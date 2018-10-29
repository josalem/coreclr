// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Microsoft.DotNet.XunitExtensions.XunitAssemblyLoadContext
{
    public class XunitTestAssemblyRunnerWithAssemblyLoadContext : XunitTestAssemblyRunner
    {
        public XunitTestAssemblyRunnerWithAssemblyLoadContext(ITestAssembly testAssembly, IEnumerable<IXunitTestCase> testCases, IMessageSink diagnosticMessageSink, IMessageSink executionMessageSink, ITestFrameworkExecutionOptions executionOptions) 
            : base(testAssembly, testCases, diagnosticMessageSink, executionMessageSink, executionOptions)
        {
        }

        protected override Task<RunSummary> RunTestCollectionAsync(IMessageBus messageBus,
                                                                   ITestCollection testCollection,
                                                                   IEnumerable<IXunitTestCase> testCases,
                                                                   CancellationTokenSource cancellationTokenSource)
            => new XunitTestCollectionRunnerWithAssemblyLoadContext(testCollection, testCases, DiagnosticMessageSink, messageBus, TestCaseOrderer, new ExceptionAggregator(Aggregator), cancellationTokenSource).RunAsync();
        
    }
}
