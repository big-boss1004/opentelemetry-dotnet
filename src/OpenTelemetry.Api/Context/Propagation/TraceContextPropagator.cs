// <copyright file="TraceContextPropagator.cs" company="OpenTelemetry Authors">
// Copyright The OpenTelemetry Authors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// </copyright>

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using OpenTelemetry.Internal;

namespace OpenTelemetry.Context.Propagation
{
    /// <summary>
    /// A text map propagator for W3C trace context. See https://w3c.github.io/trace-context/.
    /// </summary>
    public class TraceContextPropagator : TextMapPropagator
    {
        private const string TraceParent = "traceparent";
        private const string TraceState = "tracestate";

        private static readonly int VersionPrefixIdLength = "00-".Length;
        private static readonly int TraceIdLength = "0af7651916cd43dd8448eb211c80319c".Length;
        private static readonly int VersionAndTraceIdLength = "00-0af7651916cd43dd8448eb211c80319c-".Length;
        private static readonly int SpanIdLength = "00f067aa0ba902b7".Length;
        private static readonly int VersionAndTraceIdAndSpanIdLength = "00-0af7651916cd43dd8448eb211c80319c-00f067aa0ba902b7-".Length;
        private static readonly int OptionsLength = "00".Length;
        private static readonly int TraceparentLengthV0 = "00-0af7651916cd43dd8448eb211c80319c-00f067aa0ba902b7-00".Length;

        /// <inheritdoc/>
        public override ISet<string> Fields => new HashSet<string> { TraceState, TraceParent };

        /// <inheritdoc/>
        public override PropagationContext Extract<T>(PropagationContext context, T carrier, Func<T, string, IEnumerable<string>> getter)
        {
            if (context.ActivityContext.IsValid())
            {
                // If a valid context has already been extracted, perform a noop.
                return context;
            }

            if (carrier == null)
            {
                OpenTelemetryApiEventSource.Log.FailedToExtractActivityContext(nameof(TraceContextPropagator), "null carrier");
                return context;
            }

            if (getter == null)
            {
                OpenTelemetryApiEventSource.Log.FailedToExtractActivityContext(nameof(TraceContextPropagator), "null getter");
                return context;
            }

            try
            {
                var traceparentCollection = getter(carrier, TraceParent);

                // There must be a single traceparent
                if (traceparentCollection == null || traceparentCollection.Count() != 1)
                {
                    return context;
                }

                var traceparent = traceparentCollection.First();
                var traceparentParsed = TryExtractTraceparent(traceparent, out var traceId, out var spanId, out var traceoptions);

                if (!traceparentParsed)
                {
                    return context;
                }

                string tracestate = null;
                var tracestateCollection = getter(carrier, TraceState);
                if (tracestateCollection?.Any() ?? false)
                {
                    TryExtractTracestate(tracestateCollection.ToArray(), out tracestate);
                }

                return new PropagationContext(
                    new ActivityContext(traceId, spanId, traceoptions, tracestate, isRemote: true),
                    context.Baggage);
            }
            catch (Exception ex)
            {
                OpenTelemetryApiEventSource.Log.ActivityContextExtractException(nameof(TraceContextPropagator), ex);
            }

            // in case of exception indicate to upstream that there is no parseable context from the top
            return context;
        }

        /// <inheritdoc/>
        public override void Inject<T>(PropagationContext context, T carrier, Action<T, string, string> setter)
        {
            if (context.ActivityContext.TraceId == default || context.ActivityContext.SpanId == default)
            {
                OpenTelemetryApiEventSource.Log.FailedToInjectActivityContext(nameof(TraceContextPropagator), "Invalid context");
                return;
            }

            if (carrier == null)
            {
                OpenTelemetryApiEventSource.Log.FailedToInjectActivityContext(nameof(TraceContextPropagator), "null carrier");
                return;
            }

            if (setter == null)
            {
                OpenTelemetryApiEventSource.Log.FailedToInjectActivityContext(nameof(TraceContextPropagator), "null setter");
                return;
            }

            var traceparent = string.Concat("00-", context.ActivityContext.TraceId.ToHexString(), "-", context.ActivityContext.SpanId.ToHexString());
            traceparent = string.Concat(traceparent, (context.ActivityContext.TraceFlags & ActivityTraceFlags.Recorded) != 0 ? "-01" : "-00");

            setter(carrier, TraceParent, traceparent);

            string tracestateStr = context.ActivityContext.TraceState;
            if (tracestateStr?.Length > 0)
            {
                setter(carrier, TraceState, tracestateStr);
            }
        }

        internal static bool TryExtractTraceparent(string traceparent, out ActivityTraceId traceId, out ActivitySpanId spanId, out ActivityTraceFlags traceOptions)
        {
            // from https://github.com/w3c/distributed-tracing/blob/master/trace_context/HTTP_HEADER_FORMAT.md
            // traceparent: 00-0af7651916cd43dd8448eb211c80319c-00f067aa0ba902b7-01

            traceId = default;
            spanId = default;
            traceOptions = default;
            var bestAttempt = false;

            if (string.IsNullOrWhiteSpace(traceparent) || traceparent.Length < TraceparentLengthV0)
            {
                return false;
            }

            // if version does not end with delimiter
            if (traceparent[VersionPrefixIdLength - 1] != '-')
            {
                return false;
            }

            // or version is not a hex (will throw)
            var version0 = HexCharToByte(traceparent[0]);
            var version1 = HexCharToByte(traceparent[1]);

            if (version0 == 0xf && version1 == 0xf)
            {
                return false;
            }

            if (version0 > 0)
            {
                // expected version is 00
                // for higher versions - best attempt parsing of trace id, span id, etc.
                bestAttempt = true;
            }

            if (traceparent[VersionAndTraceIdLength - 1] != '-')
            {
                return false;
            }

            try
            {
                traceId = ActivityTraceId.CreateFromString(traceparent.AsSpan().Slice(VersionPrefixIdLength, TraceIdLength));
            }
            catch (ArgumentOutOfRangeException)
            {
                // it's ok to still parse tracestate
                return false;
            }

            if (traceparent[VersionAndTraceIdAndSpanIdLength - 1] != '-')
            {
                return false;
            }

            byte options1;
            try
            {
                spanId = ActivitySpanId.CreateFromString(traceparent.AsSpan().Slice(VersionAndTraceIdLength, SpanIdLength));
                options1 = HexCharToByte(traceparent[VersionAndTraceIdAndSpanIdLength + 1]);
            }
            catch (ArgumentOutOfRangeException)
            {
                // it's ok to still parse tracestate
                return false;
            }

            if ((options1 & 1) == 1)
            {
                traceOptions |= ActivityTraceFlags.Recorded;
            }

            if ((!bestAttempt) && (traceparent.Length != VersionAndTraceIdAndSpanIdLength + OptionsLength))
            {
                return false;
            }

            if (bestAttempt)
            {
                if ((traceparent.Length > TraceparentLengthV0) && (traceparent[TraceparentLengthV0] != '-'))
                {
                    return false;
                }
            }

            return true;
        }

        internal static bool TryExtractTracestate(string[] tracestateCollection, out string tracestateResult)
        {
            tracestateResult = string.Empty;

            if (tracestateCollection != null)
            {
                var result = new StringBuilder();

                // Iterate in reverse order because when call builder set the elements is added in the
                // front of the list.
                for (int i = tracestateCollection.Length - 1; i >= 0; i--)
                {
                    if (string.IsNullOrEmpty(tracestateCollection[i]))
                    {
                        return false;
                    }

                    result.Append(tracestateCollection[i]);
                }

                tracestateResult = result.ToString();
            }

            return true;
        }

        private static byte HexCharToByte(char c)
        {
            if (((c >= '0') && (c <= '9'))
                || ((c >= 'a') && (c <= 'f'))
                || ((c >= 'A') && (c <= 'F')))
            {
                return Convert.ToByte(c);
            }

            throw new ArgumentOutOfRangeException(nameof(c), c, $"Invalid character: {c}.");
        }
    }
}
