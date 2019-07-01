// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Diagnostics.Tools.RuntimeClient;
using Tracing.Tests.Common;

namespace Tracing.Tests.BufferValidation
{
    public sealed class MyEventSource : EventSource
    {
        private MyEventSource() {}
        public static MyEventSource Log = new MyEventSource();
        public void MyEvent() { WriteEvent(1, "MyEvent"); }
    }

    public class BufferValidation
    {
        public static int Main(string[] args)
        {
            // This tests the resilience of message sending with
            // smaller buffers, specifically 1MB and 4MB

            var providers = new List<Provider>()
            {
                new Provider("MyEventSource")
            };

            var tests = new int[] { 0, 2 }
                .Select(x => (uint)Math.Pow(2, x))
                .Select(bufferSize => new SessionConfiguration(circularBufferSizeMB: bufferSize, format: EventPipeSerializationFormat.NetTrace, providers: providers))
                .Select<SessionConfiguration, Func<int>>(configuration => () => IpcTraceTest.RunAndValidateEventCounts(_expectedEventCounts, _eventGeneratingAction, configuration));

            foreach (var test in tests)
            {
                var ret = test();
                if (ret < 0)
                    return ret;
            }

            return 100;
        }

        private static Dictionary<string, ExpectedEventCount> _expectedEventCounts = new Dictionary<string, ExpectedEventCount>()
        {
            // We're testing small buffer sizes, so we expect some dropped events
            // especially on the resource strapped CI machines.
            { "MyEventSource", new ExpectedEventCount(1000, 0.40f) }
        };

        private static Action _eventGeneratingAction = () => 
        {
            foreach (var _ in Enumerable.Range(0,1000))
            {
                MyEventSource.Log.MyEvent();
            }
        };
    }
}