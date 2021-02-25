// <copyright file="GrpcClientDiagnosticListener.cs" company="OpenTelemetry Authors">
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
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Instrumentation.Http;
using OpenTelemetry.Trace;

namespace OpenTelemetry.Instrumentation.GrpcNetClient.Implementation
{
    internal class GrpcClientDiagnosticListener : ListenerHandler
    {
        internal static readonly AssemblyName AssemblyName = typeof(GrpcClientDiagnosticListener).Assembly.GetName();
        internal static readonly string ActivitySourceName = AssemblyName.Name;
        internal static readonly Version Version = AssemblyName.Version;
        internal static readonly ActivitySource ActivitySource = new ActivitySource(ActivitySourceName, Version.ToString());

        private readonly GrpcClientInstrumentationOptions options;
        private readonly PropertyFetcher<HttpRequestMessage> startRequestFetcher = new PropertyFetcher<HttpRequestMessage>("Request");
        private readonly PropertyFetcher<HttpResponseMessage> stopRequestFetcher = new PropertyFetcher<HttpResponseMessage>("Response");

        public GrpcClientDiagnosticListener(GrpcClientInstrumentationOptions options)
            : base("Grpc.Net.Client")
        {
            this.options = options;
        }

        public override void OnStartActivity(Activity activity, object payload)
        {
            if (!this.startRequestFetcher.TryFetch(payload, out HttpRequestMessage request) || request == null)
            {
                GrpcInstrumentationEventSource.Log.NullPayload(nameof(GrpcClientDiagnosticListener), nameof(this.OnStartActivity));
                return;
            }

            if (this.options.SuppressDownstreamInstrumentation)
            {
                SuppressInstrumentationScope.Enter();

                // If we are suppressing downstream instrumentation then inject
                // context here. Grpc.Net.Client uses HttpClient, so
                // SuppressDownstreamInstrumentation means that the
                // OpenTelemetry instrumentation for HttpClient will not be
                // invoked.

                // Note that HttpClient natively generates its own activity and
                // propagates W3C trace context headers regardless of whether
                // OpenTelemetry HttpClient instrumentation is invoked.
                // Therefore, injecting here preserves more intuitive span
                // parenting - i.e., the entry point span of a downstream
                // service would be parented to the span generated by
                // Grpc.Net.Client rather than the span generated natively by
                // HttpClient. Injecting here also ensures that baggage is
                // propagated to downstream services.
                // Injecting context here also ensures that the configured
                // propagator is used, as HttpClient by itself will only
                // do TraceContext propagation.
                var textMapPropagator = Propagators.DefaultTextMapPropagator;
                textMapPropagator.Inject(
                    new PropagationContext(activity.Context, Baggage.Current),
                    request,
                    HttpRequestMessageContextPropagation.HeaderValueSetter);
            }

            var grpcMethod = GrpcTagHelper.GetGrpcMethodFromActivity(activity);

            activity.DisplayName = grpcMethod?.Trim('/');

            ActivityInstrumentationHelper.SetActivitySourceProperty(activity, ActivitySource);
            ActivityInstrumentationHelper.SetKindProperty(activity, ActivityKind.Client);

            if (activity.IsAllDataRequested)
            {
                activity.SetTag(SemanticConventions.AttributeRpcSystem, GrpcTagHelper.RpcSystemGrpc);

                if (GrpcTagHelper.TryParseRpcServiceAndRpcMethod(grpcMethod, out var rpcService, out var rpcMethod))
                {
                    activity.SetTag(SemanticConventions.AttributeRpcService, rpcService);
                    activity.SetTag(SemanticConventions.AttributeRpcMethod, rpcMethod);

                    // Remove the grpc.method tag added by the gRPC .NET library
                    activity.SetTag(GrpcTagHelper.GrpcMethodTagName, null);
                }

                var uriHostNameType = Uri.CheckHostName(request.RequestUri.Host);
                if (uriHostNameType == UriHostNameType.IPv4 || uriHostNameType == UriHostNameType.IPv6)
                {
                    activity.SetTag(SemanticConventions.AttributeNetPeerIp, request.RequestUri.Host);
                }
                else
                {
                    activity.SetTag(SemanticConventions.AttributeNetPeerName, request.RequestUri.Host);
                }

                activity.SetTag(SemanticConventions.AttributeNetPeerPort, request.RequestUri.Port);

                try
                {
                    this.options.Enrich?.Invoke(activity, "OnStartActivity", request);
                }
                catch (Exception ex)
                {
                    GrpcInstrumentationEventSource.Log.EnrichmentException(ex);
                }
            }
        }

        public override void OnStopActivity(Activity activity, object payload)
        {
            if (activity.IsAllDataRequested)
            {
                bool validConversion = GrpcTagHelper.TryGetGrpcStatusCodeFromActivity(activity, out int status);
                if (validConversion)
                {
                    if (activity.GetStatus().StatusCode == StatusCode.Unset)
                    {
                        activity.SetStatus(GrpcTagHelper.ResolveSpanStatusForGrpcStatusCode(status));
                    }

                    // setting rpc.grpc.status_code
                    activity.SetTag(SemanticConventions.AttributeRpcGrpcStatusCode, status);
                }

                // Remove the grpc.status_code tag added by the gRPC .NET library
                activity.SetTag(GrpcTagHelper.GrpcStatusCodeTagName, null);

                if (this.stopRequestFetcher.TryFetch(payload, out HttpResponseMessage response) && response != null)
                {
                    try
                    {
                        this.options.Enrich?.Invoke(activity, "OnStopActivity", response);
                    }
                    catch (Exception ex)
                    {
                        GrpcInstrumentationEventSource.Log.EnrichmentException(ex);
                    }
                }
            }
        }
    }
}
