// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Xunit.Abstractions;
using Xunit.Sdk;

namespace Microsoft.DotNet.XunitExtensions.XunitAssemblyLoadContext
{
    internal class XunitTestCaseWithAssemblyLoadContext : XunitTestCase
    {
        public XunitTestCaseWithAssemblyLoadContext(IMessageSink diagnosticMessageSink, TestMethodDisplay defaultMethodDisplay, TestMethodDisplayOptions defaultMethodDisplayOptions, ITestMethod testMethod, object[] testMethodArguments = null) 
            : base(diagnosticMessageSink, defaultMethodDisplay, defaultMethodDisplayOptions, testMethod, testMethodArguments)
        { }
    }

    public class XunitTestMethodWithAssemblyLoadContext : TestMethod
    {
        public XunitTestMethodWithAssemblyLoadContext(ITestClass @class, IMethodInfo method) : base(@class, method)
        {
        }
    }
}