// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Microsoft.DotNet.XunitExtensions.XunitAssemblyLoadContext
{
    public class XunitAssemblyLoadContext : AssemblyLoadContext
    {
        private readonly string _rootSearchPath;
        private readonly IMessageSink _diagnosticMessageSink;

        /// <summary>
        /// AssemblyLoadContextOverride for use with Xunit
        /// </summary>
        /// <param name="rootSearchPath">The directory to look for dependency binaries in</param>
        public XunitAssemblyLoadContext(string rootSearchPath, IMessageSink diagnosticMessageSink) : base(true)
        {
            _rootSearchPath = rootSearchPath;
            _diagnosticMessageSink = diagnosticMessageSink;
        }

        /// <summary>
        /// Look for dependencies in the local directory, then in a specified sesarch root
        /// </summary>
        /// <param name="assemblyName">The dependency the AssemblyLoadContext is attempting to load</param>
        /// <returns></returns>
        protected override Assembly Load(AssemblyName assemblyName)
        {
            // prevent xunit DLLs and this DLL from loading since comparison of types across context bounds prevents xunit from functioning properly
            if (assemblyName.Name == typeof(XunitAssemblyLoadContext).Assembly.GetName().Name || assemblyName.Name.Contains("xunit", StringComparison.OrdinalIgnoreCase))
                return null;

            // First search the local directory for the executing DLL
            try
            {
                var asm = LoadFromAssemblyPath(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), $"{assemblyName.Name}.dll"));
                return asm;
            }
            catch (Exception)
            {
                // Then check the search path 
                try
                {
                    var asm = LoadFromAssemblyPath(Path.Combine(_rootSearchPath, $"{assemblyName.Name}.dll"));
                    return asm;
                }
                catch (Exception)
                {
                    // Nest additional search locations here
                    // If we can't find the DLL, try to use the one from the parent context (return null)
                    return null;
                }
            }
        }
    }
}
