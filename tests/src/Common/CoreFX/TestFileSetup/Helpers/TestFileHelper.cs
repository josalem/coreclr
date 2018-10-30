﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Schema;
using Newtonsoft.Json.Schema.Generation;

namespace CoreFX.TestUtils.TestFileSetup.Helpers
{
    /// <summary>
    /// Defines the set of flags that represent exit codes
    /// </summary>
    [Flags]
    public enum ExitCode : int
    {
        Success = 0,
        TestFailure = 1,
        HttpError = 2,
        IOError = 3,
        JsonSchemaValidationError = 4,
        UnknownError = 10

    }

    /// <summary>
    /// This helper class is used to fetch CoreFX tests from a specified URL, unarchive them and create a flat directory structure
    /// through which to iterate.
    /// </summary>
    public class TestFileHelper
    {
        private HttpClient httpClient;
        public HttpClient HttpClient
        {
            get
            {
                if (httpClient == null)
                {
                    httpClient = new HttpClient();
                }
                return httpClient;
            }
            set{ httpClient = value; }
        }

        private HashSet<string> disabledTests;
        
        /// <summary>
        /// Default constructor - initialize list of disabled tests
        /// </summary>
        public TestFileHelper() {
            disabledTests = new HashSet<string>();
        }

        /// <summary>
        /// Deserialize a list of JSON objects defining test assemblies
        /// </summary>
        /// <param name="testDefinitionFilePath">The path on disk to the test list. The test list must conform to a schema generated from XUnitTestAssembly</param>
        /// <returns></returns>
        public Dictionary<string, XUnitTestAssembly> DeserializeTestJson(string testDefinitionFilePath)
        {
            JSchemaGenerator jsonGenerator = new JSchemaGenerator();

            // Generate a JSON schema from the XUnitTestAssembly class against which to validate the test list
            JSchema testDefinitionSchema = jsonGenerator.Generate(typeof(IList<XUnitTestAssembly>));
            IList<XUnitTestAssembly> testAssemblies = new List<XUnitTestAssembly>();

            IList<string> validationMessages = new List<string>();

            using (var sr = new StreamReader(testDefinitionFilePath))
            using (var jsonReader = new JsonTextReader(sr))
            using (var jsonValidationReader = new JSchemaValidatingReader(jsonReader))
            {
                // Create schema validator
                jsonValidationReader.Schema = testDefinitionSchema;
                jsonValidationReader.ValidationEventHandler += (o, a) => validationMessages.Add(a.Message);

                // Deserialize json test assembly definitions
                JsonSerializer serializer = new JsonSerializer();
                try
                {
                    testAssemblies = serializer.Deserialize<List<XUnitTestAssembly>>(jsonValidationReader);
                }
                catch (JsonSerializationException ex)
                {
                    // Invalid definition
                    throw new AggregateException(ex);
                }
            }

            if (validationMessages.Count != 0)
            {
                StringBuilder aggregateExceptionMessage = new StringBuilder();
                foreach (string validationMessage in validationMessages)
                {
                    aggregateExceptionMessage.Append("JSON Validation Error: ");
                    aggregateExceptionMessage.Append(validationMessage);
                    aggregateExceptionMessage.AppendLine();
                }

                throw new AggregateException(new JSchemaValidationException(aggregateExceptionMessage.ToString()));

            }
            // Generate a map of test assembly names to their object representations - this is used to download and match them to their on-disk representations
            var nameToTestAssemblyDef = new Dictionary<string, XUnitTestAssembly>();

            // Map test names to their definitions
            foreach (XUnitTestAssembly assembly in testAssemblies)
            {
                // Filter disabled tests
                if(assembly.IsEnabled)
                    nameToTestAssemblyDef.Add(assembly.Name, assembly);
                else
                    disabledTests.Add(assembly.Name);
            }

            return nameToTestAssemblyDef;
        }

        /// <summary>
        /// Layout tests on disk. This method sets up every downloaded test as it would appear after running build-test.[cmd/sh] in CoreFX
        /// </summary>
        /// <param name="jsonUrl">URL to a test list - we expect a test list, which conforms to the Helix layout</param>
        /// <param name="destinationDirectory">Directory to which the tests are downloaded</param>
        /// <param name="testDefinitions">The mapping of tests parsed from a test definition list to their names</param>
        /// <param name="runAllTests">Optional argument, which denotes whether all tests available in the test list downloaded from jsonUrl should be run</param>
        /// <param name="alcXunitExtensionPath">Optional argument with path which denotes whether to modify test directories and binaries to use Assembly Load Context</param>
        /// <param name="ilasmPath">Optional argument to specify where ilasm/ildasm are located</param>
        /// <returns></returns>
        public async Task SetupTests(string jsonUrl, 
                                     string destinationDirectory, 
                                     Dictionary<string, XUnitTestAssembly> testDefinitions = null, 
                                     bool runAllTests = false, 
                                     string alcXunitExtensionPath = "",
                                     string ilasmPath = "")
        {
            Debug.Assert(Directory.Exists(destinationDirectory));
            // testDefinitions should not be empty unless we're running all tests with no exclusions
            Debug.Assert(runAllTests || testDefinitions != null);

            // Download archives to a temporary directory
            string tempDirPath = Path.Combine(destinationDirectory, "temp");
            if (!Directory.Exists(tempDirPath))
            {
                Directory.CreateDirectory(tempDirPath);
            }
            // Map test names to their URLs, specified by the test list found at jsonUrl
            Dictionary<string, XUnitTestAssembly> testPayloads = await GetTestUrls(jsonUrl, testDefinitions, runAllTests);

            // If none were found or the testList did not have the expected format - return
            if (testPayloads == null)
            {
                return;
            }

            // Download and unzip tests
            await GetTestArchives(testPayloads, tempDirPath);
            ExpandArchivesInDirectory(tempDirPath, destinationDirectory);

            // Generate response file for each tests
            RSPGenerator rspGenerator = new RSPGenerator();
            foreach (XUnitTestAssembly assembly in testDefinitions.Values)
            {
                rspGenerator.GenerateRSPFile(assembly, Path.Combine(destinationDirectory, assembly.Name));
                if (!string.IsNullOrEmpty(alcXunitExtensionPath))
                    await ModifyTestForAssemblyLoadContext(assembly, Path.Combine(destinationDirectory, assembly.Name), alcXunitExtensionPath, ilasmPath);
            }

            Directory.Delete(tempDirPath);
        }

        /// <summary>
        /// Modifies the test DLL by ILDASM-ing, adding an assembly attribtue, and ILASM-ing to
        /// enable test cases to be run inside their own Assembly Load Context.
        /// </summary>
        /// <param name="assembly">The assembly to modify</param>
        /// <param name="TestDirectory">The directory where the test DLLs are</param>
        /// <param name="alcXunitExtensionPath">The path to the Xunit extension DLL that needs to be next to the test DLL</param>
        /// <param name="ilasmPath">The path to ilasm.exe and ildasm.exe</param>
        private async Task ModifyTestForAssemblyLoadContext(XUnitTestAssembly assembly, string testDirectory, string alcXunitExtensionPath, string ilasmPath)
        {
            string assemblyAttributeText = @".custom instance void [xunit.core]Xunit.TestFrameworkAttribute::.ctor(string,
                string) = ( 01 00 63 4D 69 63 72 6F 73 6F 66 74 2E 44 6F 74   // ..cMicrosoft.Dot
                            4E 65 74 2E 58 75 6E 69 74 45 78 74 65 6E 73 69   // Net.XunitExtensi
                            6F 6E 73 2E 58 75 6E 69 74 41 73 73 65 6D 62 6C   // ons.XunitAssembl
                            79 4C 6F 61 64 43 6F 6E 74 65 78 74 2E 58 75 6E   // yLoadContext.Xun
                            69 74 54 65 73 74 46 72 61 6D 65 77 6F 72 6B 57   // itTestFrameworkW
                            69 74 68 41 73 73 65 6D 62 6C 79 4C 6F 61 64 43   // ithAssemblyLoadC
                            6F 6E 74 65 78 74 39 4D 69 63 72 6F 73 6F 66 74   // ontext9Microsoft
                            2E 44 6F 74 4E 65 74 2E 58 75 6E 69 74 45 78 74   // .DotNet.XunitExt
                            65 6E 73 69 6F 6E 73 2E 58 75 6E 69 74 41 73 73   // ensions.XunitAss
                            65 6D 62 6C 79 4C 6F 61 64 43 6F 6E 74 65 78 74   // emblyLoadContext
                            00 00 )";

            string originalFileName = $"{assembly.Name}.dll";
            string toModifyFileName = $"{assembly.Name}_.dll";
            string ilFileName       = $"{assembly.Name}.il";
            string tmpDirPath = Path.Combine(testDirectory, "tmpDir");

            if (!Directory.Exists(tmpDirPath))
                Directory.CreateDirectory(tmpDirPath);

            File.Move(Path.Combine(testDirectory, originalFileName), Path.Combine(tmpDirPath, toModifyFileName));

            // ILDASM
            string ildasmArguments = $"{Path.Combine(tmpDirPath, toModifyFileName)} /OUT={Path.Combine(tmpDirPath, ilFileName)}";
                        // Create and initialize the test executable process
            ProcessStartInfo ildasmStartInfo = new ProcessStartInfo(Path.Combine(ilasmPath, "ildasm.exe"), ildasmArguments)
            {
                Arguments = ildasmArguments,
                WorkingDirectory = tmpDirPath
            };


            Process ildasmExecutableProcess = new Process();
            ildasmExecutableProcess.StartInfo = ildasmStartInfo;
            ildasmExecutableProcess.EnableRaisingEvents = true;
            ildasmExecutableProcess.Start();
            ildasmExecutableProcess.WaitForExit();

            if (ildasmExecutableProcess.ExitCode != 0)
                throw new Exception($"Failed to ILDASM {assembly.Name}");

            // Inject Assembly Attribute
            var match = $".assembly {assembly.Name}\r\n{{";
            var text = await File.ReadAllTextAsync(Path.Combine(tmpDirPath, ilFileName));
            Regex.Replace(text, match, match + assemblyAttributeText);
            await File.WriteAllTextAsync(Path.Combine(tmpDirPath, ilFileName), text);

            // ILASM
            string ilasmArguments = $"{Path.Combine(tmpDirPath, ilFileName)} /NOLOGO /QUIET /DLL /OUTPUT={Path.Combine(tmpDirPath, originalFileName)}";
                        // Create and initialize the test executable process
            ProcessStartInfo ilasmStartInfo = new ProcessStartInfo(Path.Combine(ilasmPath, "ilasm.exe"), ilasmArguments)
            {
                Arguments = ilasmArguments,
                WorkingDirectory = tmpDirPath
            };


            Process ilasmExecutableProcess = new Process();
            ilasmExecutableProcess.StartInfo = ilasmStartInfo;
            ilasmExecutableProcess.EnableRaisingEvents = true;
            ilasmExecutableProcess.Start();
            ilasmExecutableProcess.WaitForExit();

            if (ilasmExecutableProcess.ExitCode != 0)
                throw new Exception($"Failed to ILASM {assembly.Name}");

            // Move things back
            File.Move(Path.Combine(tmpDirPath, originalFileName), Path.Combine(testDirectory, originalFileName));

            // add xunit extensions
            File.Copy(alcXunitExtensionPath, Path.Combine(testDirectory, Path.GetFileName(alcXunitExtensionPath)));

            Directory.Delete(tmpDirPath, true);
        }

        /// <summary>
        /// Maps test names to their respective URLs as found in the test list found at the specified URL
        /// </summary>
        /// <param name="jsonUrl">URL to a test list - we expect a test list, which conforms to the Helix layout</param>
        /// <param name="testDefinitions">The mapping of tests parsed from a test definition list to their names</param>
        /// <param name="runAllTests">Optional argument, which denotes whether all tests available in the test list downloaded from jsonUrl should be run</param>
        /// <returns></returns>
        public async Task<Dictionary<string, XUnitTestAssembly>> GetTestUrls(string jsonUrl, Dictionary<string, XUnitTestAssembly> testDefinitions = null, bool runAllTests = false)
        {
            // testDefinitions should not be empty unless we're running all tests with no exclusions
            Debug.Assert(runAllTests || testDefinitions != null);
            // Set up the json stream reader
            using (var responseStream = await HttpClient.GetStreamAsync(jsonUrl))
            using (var streamReader = new StreamReader(responseStream))
            using (var jsonReader = new JsonTextReader(streamReader))
            {
                // Manual parsing - we only need to key-value pairs from each object and this avoids deserializing all of the work items into objects
                string markedTestName = string.Empty;
                string currentPropertyName = string.Empty;

                // The expected layout is produced by regular Helix runs - this allows us to parse and run tests from any Helix test list without special considerations
                // The expected fields are
                // { "WorkItemId": "<Fully Qualified Test Name>" ,  "PayloadUri":"<Url Of Test>" }

                while (jsonReader.Read())
                {
                    if (jsonReader.Value != null)
                    {
                        switch (jsonReader.TokenType)
                        {
                            case JsonToken.PropertyName:
                                currentPropertyName = jsonReader.Value.ToString();
                                break;
                            case JsonToken.String:
                                // Test Name Value
                                if (currentPropertyName.Equals("WorkItemId"))
                                {
                                    string currentTestName = jsonReader.Value.ToString();
                                    
                                    // If the test has been marked as disabled in the test list - ignore it 
                                    if ((runAllTests || testDefinitions.ContainsKey(currentTestName)) && !disabledTests.Contains(currentTestName))
                                    {
                                        markedTestName = currentTestName;
                                    }
                                }
                                // Test URL value
                                else if (currentPropertyName.Equals("PayloadUri") && markedTestName != string.Empty)
                                {
                                    if (!testDefinitions.ContainsKey(markedTestName))
                                    {
                                        testDefinitions[markedTestName] = new XUnitTestAssembly() { Name = markedTestName };
                                    }
                                    testDefinitions[markedTestName].Url = jsonReader.Value.ToString();
                                    markedTestName = string.Empty;
                                }
                                break;
                        }
                    }
                }

            }
            return testDefinitions;
        }

        /// <summary>
        /// Download each test from its specified URL
        /// </summary>
        /// <param name="testPayloads">The mapping of tests parsed from a test definition list to their names. The test definitions are populated with test URLs</param>
        /// <param name="downloadDir">Directory to which to download tests</param>
        /// <returns></returns>
        public async Task GetTestArchives(Dictionary<string, XUnitTestAssembly> testPayloads, string downloadDir)
        {
            foreach (string testName in testPayloads.Keys)
            {
                string payloadUri = testPayloads[testName].Url;

                // Check URL for validity
                if (!Uri.IsWellFormedUriString(payloadUri, UriKind.Absolute))
                    continue;
                Console.WriteLine("Downloading " + testName + " from " + payloadUri);
                // Download tests from specified URL
                using (var response = await HttpClient.GetStreamAsync(payloadUri))
                {
                    if (response.CanRead)
                    {
                        // Create the test setup directory if it doesn't exist
                        if (!Directory.Exists(downloadDir))
                        {
                            Directory.CreateDirectory(downloadDir);
                        }

                        // CoreFX test archives are output as .zip regardless of platform
                        string archivePath = Path.Combine(downloadDir, testName + ".zip");

                        // Copy to a temp folder 
                        using (FileStream file = new FileStream(archivePath, FileMode.Create))
                        {
                            await response.CopyToAsync(file);
                        }

                    }
                }
            }
        }

        /// <summary>
        /// Expand Archives
        /// </summary>
        /// <param name="archiveDirectory">Directory containing archives</param>
        /// <param name="destinationDirectory">Directory to which to unpack archives</param>
        /// <param name="cleanup">Optional parameter stating, whether archives should be deleted once downloaded</param>
        public void ExpandArchivesInDirectory(string archiveDirectory, string destinationDirectory, bool cleanup = true)
        {
            Debug.Assert(Directory.Exists(archiveDirectory));
            Debug.Assert(Directory.Exists(destinationDirectory));

            // Get all archives in the directory
            string[] archives = Directory.GetFiles(archiveDirectory, "*.zip", SearchOption.TopDirectoryOnly);

            foreach (string archivePath in archives)
            {
                string destinationDirName = Path.Combine(destinationDirectory, Path.GetFileNameWithoutExtension(archivePath));

                ZipFile.ExtractToDirectory(archivePath, destinationDirName);

                // Delete archives if cleanup was 
                if (cleanup)
                {
                    File.Delete(archivePath);
                }
            }
        }

        /// <summary>
        /// Cleans build directory
        /// </summary>
        /// <param name="directoryToClean">Directory the contents of which to delete.</param>
        public void CleanBuild(string directoryToClean)
        {
            Debug.Assert(Directory.Exists(directoryToClean));
            DirectoryInfo dirInfo = new DirectoryInfo(directoryToClean);

            foreach (FileInfo file in dirInfo.EnumerateFiles())
            {
                file.Delete();
            }

            foreach (DirectoryInfo dir in dirInfo.EnumerateDirectories())
            {
                dir.Delete(true);
            }
        }

    }
}
