// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Microsoft.DotNet.XunitExtensions.XunitAssemblyLoadContext
{
    public class XunitTestMethodRunnerWithAssemblyLoadContext : XunitTestMethodRunner
    {
        private readonly IMessageSink diagnosticMessageSink;
        private readonly object[] constructorArguments;

        public XunitTestMethodRunnerWithAssemblyLoadContext(ITestMethod testMethod, IReflectionTypeInfo @class, IReflectionMethodInfo method, IEnumerable<IXunitTestCase> testCases, IMessageSink diagnosticMessageSink, IMessageBus messageBus, ExceptionAggregator aggregator, CancellationTokenSource cancellationTokenSource, object[] constructorArguments) 
                : base(testMethod, @class, method, testCases, diagnosticMessageSink, messageBus, aggregator, cancellationTokenSource, constructorArguments)
        {
            this.diagnosticMessageSink = diagnosticMessageSink;
            this.constructorArguments = constructorArguments;
        }

        // Do no inline this function, to ensure the JIT doesn't accidentally keep a reference 
        // to the ALC after we want it to be collected
        [MethodImpl(MethodImplOptions.NoInlining)]
        private async Task<(WeakReference, RunSummary)> RunTestCaseInAssemblyLoadContextAsync(IXunitTestCase testCase)
        {
            // Determine shared library location for AssemblyLoadContext to load from
            var assumedHostLocation = Path.GetDirectoryName(typeof(System.Runtime.Loader.AssemblyLoadContext).Assembly.Location);

            // Load Assembly the TestMethod came from into an AssemblyLoadContext
            var assemblyLoadContext = new XunitAssemblyLoadContext(assumedHostLocation, diagnosticMessageSink);
            var assembly = assemblyLoadContext.LoadFromAssemblyPath(testCase.TestMethod.TestClass.Class.ToRuntimeType().Assembly.Location);

            // Reflect through the context to find the same class and method (including generics) as was passed in
            var classToMask = new ReflectionTypeInfo(assembly.DefinedTypes.Where(t => t.IsClass).Where(t => t.FullName == Class.Name).FirstOrDefault().AsType());
            var maskedTestCollection = new TestCollection(new TestAssembly(new ReflectionAssemblyInfo(assembly)), classToMask, "Masked_Test_Collection");
            var testClass = new TestClass(maskedTestCollection, classToMask);
            var methodToMask = classToMask.GetMethod(testCase.Method.Name, true);
            var maskedTestMethod = new TestMethod(testClass, methodToMask);

            // We choose to only apply this logic to the standard Xunit TestCase types so we don't override custom behavior
            var maskedTestCase = testCase.GetType() == typeof(XunitTheoryTestCase) ?
                new XunitTheoryTestCase(diagnosticMessageSink, TestMethodDisplay.ClassAndMethod, TestMethodDisplayOptions.All, maskedTestMethod) :
                new XunitTestCase(diagnosticMessageSink, TestMethodDisplay.ClassAndMethod, TestMethodDisplayOptions.All, maskedTestMethod, testCase.TestMethodArguments);

            // Run the test case
            RunSummary summary = null;
            try
            {
                summary = await maskedTestCase.RunAsync(diagnosticMessageSink, MessageBus, constructorArguments, new ExceptionAggregator(Aggregator), CancellationTokenSource);
            }
            catch (Exception e)
            {
                diagnosticMessageSink.OnMessage(new DiagnosticMessage($"FAILURE - {e}"));
                throw;
            }

            // Unload the ALC

            return (new WeakReference(assemblyLoadContext), summary);
        }

        protected override async Task<RunSummary> RunTestCaseAsync(IXunitTestCase testCase)
        {
            // filter out other XunitTestCase implementations so we don't override custom behavior
            if (testCase.GetType() != typeof(XunitTheoryTestCase) && testCase.GetType() != typeof(XunitTestCase))
            {
                diagnosticMessageSink.OnMessage(new DiagnosticMessage($"Custom or non-executing testcase type, not loading into Assembly Load Context."));
                return await testCase.RunAsync(diagnosticMessageSink, MessageBus, constructorArguments, new ExceptionAggregator(Aggregator), CancellationTokenSource);
            }

            var (alcReference, summary) = await RunTestCaseInAssemblyLoadContextAsync(testCase);

            // Wait and ensure all finalizers are called so the AssemblyLoadContext gets unloaded
            for (int i = 0;  alcReference.IsAlive && i < 10; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }

            if (alcReference.IsAlive)
            {
                summary.Failed += 10000;
                diagnosticMessageSink.OnMessage(new DiagnosticMessage($"AssemblyLoadContext was not properly unloaded for test: {testCase.DisplayName}"));
            }

            return summary;
        }
    }
}