// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Microsoft.DotNet.XunitExtensions.XunitAssemblyLoadContext
{
    public class XunitTestFrameworkWithAssemblyLoadContext : XunitTestFramework
    {
        public XunitTestFrameworkWithAssemblyLoadContext(IMessageSink messageSink) : base(messageSink)
        { }

        protected override ITestFrameworkExecutor CreateExecutor(AssemblyName assemblyName)
        {
            using (var testFrameworkExecutor = new XunitTestFrameworkExecutorWithAssemblyLoadContext(assemblyName, SourceInformationProvider, DiagnosticMessageSink))
            {
                return testFrameworkExecutor;
            }
        }
    }
}
