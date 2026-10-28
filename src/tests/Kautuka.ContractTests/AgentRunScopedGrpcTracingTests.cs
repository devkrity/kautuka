using System.Diagnostics;

using Grpc.Core;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Infrastructure.Channels;
using Kautuka.Gateway.Infrastructure.Credentials;
using Kautuka.Gateway.Infrastructure.Observability;
using Kautuka.Gateway.Infrastructure.Sandbox;
using Kautuka.Manager.Protos;
using Kautuka.Protos;
using Kautuka.Telemetry.Telemetry;

namespace Kautuka.ContractTests;

public sealed class AgentRunScopedGrpcTracingTests
{
    [Fact]
    public async Task CredentialResolverOnlyCreatesGrpcSpanWithinAgentRunAsync()
    {
        using ActivityListener listener = CreateGatewayAgentRunListener();
        RecordingCallInvoker invoker = new(static method => method.Name switch
        {
            "GetResolvedCredentials" => new CredentialMapResponse
            {
                Entries = { ["provider.openai.api_key"] = "secret" }
            },
            _ => throw new InvalidOperationException($"Unexpected method {method.Name}")
        });

        var resolver = new CredentialResolverService(
            new Lazy<CredentialsService.CredentialsServiceClient>(() => new CredentialsService.CredentialsServiceClient(invoker)),
            "https://127.0.0.1:8181");

        _ = await resolver.GetResolvedAsync("user-1", ["provider.openai.api_key"], TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Null(invoker.CapturedActivityDisplayName);

        invoker.Reset();
        using Activity? runActivity = StartAgentRun("run-credentials");

        _ = await resolver.GetResolvedAsync("user-1", ["provider.openai.api_key"], TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal("credentialshost grpc GetResolvedCredentials", invoker.CapturedActivityDisplayName);
        Assert.Equal("credentialshost", invoker.CapturedHostKind);
        Assert.Equal("GetResolvedCredentials", invoker.CapturedRpcMethod);
        Assert.Equal("run-credentials", invoker.CapturedRunId);
    }

    [Fact]
    public async Task ManagerClientOnlyCreatesGrpcSpanWithinAgentRunAsync()
    {
        using ActivityListener listener = CreateGatewayAgentRunListener();
        RecordingCallInvoker invoker = new(static method => method.Name switch
        {
            "Ping" => new WorkspaceBoolResponse { Ok = true },
            _ => throw new InvalidOperationException($"Unexpected method {method.Name}")
        });

        var client = new ManagerGrpcServiceClient(invoker, "http://127.0.0.1:8100");

        _ = await client.PingAsync(new WorkspacePingRequest { Message = "ping" }, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Null(invoker.CapturedActivityDisplayName);

        invoker.Reset();
        using Activity? runActivity = StartAgentRun("run-manager");

        _ = await client.PingAsync(new WorkspacePingRequest { Message = "ping" }, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal("manager grpc Ping", invoker.CapturedActivityDisplayName);
        Assert.Equal("manager", invoker.CapturedHostKind);
        Assert.Equal("Ping", invoker.CapturedRpcMethod);
        Assert.Equal("run-manager", invoker.CapturedRunId);
    }

    [Fact]
    public async Task ChannelHostClientOnlyCreatesGrpcSpanWithinAgentRunAsync()
    {
        using ActivityListener listener = CreateGatewayAgentRunListener();
        RecordingCallInvoker invoker = new(static method => method.Name switch
        {
            "SendOutboundMessage" => new SendOutboundMessageResponse { Accepted = true },
            _ => throw new InvalidOperationException($"Unexpected method {method.Name}")
        });

        var client = new ChannelHostGrpcServiceClient(invoker, "http://127.0.0.1:8098");

        _ = await client.SendOutboundMessageAsync(new SendOutboundMessageRequest
        {
            AttachmentId = "attachment-1",
            MessageId = "message-1",
            CorrelationId = "correlation-1",
            Content = new OutboundContentEnvelope { ActionType = "send_text", PayloadJson = "{}" }
        }, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Null(invoker.CapturedActivityDisplayName);

        invoker.Reset();
        using Activity? runActivity = StartAgentRun("run-channel");

        _ = await client.SendOutboundMessageAsync(new SendOutboundMessageRequest
        {
            AttachmentId = "attachment-1",
            MessageId = "message-1",
            CorrelationId = "correlation-1",
            Content = new OutboundContentEnvelope { ActionType = "send_text", PayloadJson = "{}" }
        }, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal("channelhost grpc SendOutboundMessage", invoker.CapturedActivityDisplayName);
        Assert.Equal("channelhost", invoker.CapturedHostKind);
        Assert.Equal("SendOutboundMessage", invoker.CapturedRpcMethod);
        Assert.Equal("run-channel", invoker.CapturedRunId);
    }

    [Fact]
    public async Task ChannelHostOwnerTargetSyncAssignsOneStableIdempotencyKeyBeforeSigningAsync()
    {
        RecordingCallInvoker invoker = new(static method => method.Name == "SyncOwnerTargets"
            ? new SyncOwnerTargetsResponse { Accepted = true }
            : throw new InvalidOperationException($"Unexpected method {method.Name}"));
        var client = new ChannelHostGrpcServiceClient(invoker, "http://127.0.0.1:8098");
        var request = new SyncOwnerTargetsRequest
        {
            Context = new ScopedRequestContext
            {
                RequestId = "owner-sync-request-1",
                ActorUserId = "owner-1",
                TenantId = "tenant-1",
                SubmittedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            },
            OwnerUserId = "owner-1",
        };

        _ = await client.SyncOwnerTargetsAsync(request, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string key = request.IdempotencyKey;
        Assert.False(string.IsNullOrWhiteSpace(key));

        _ = await client.SyncOwnerTargetsAsync(request, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(key, request.IdempotencyKey);
    }

    [Fact]
    public void CapturedRequestTraceContextParentsChildActivity()
    {
        using ActivityListener listener = CreateGatewayAgentRunListener();
        using Activity? parentActivity = StartAgentRun("run-parent");
        Assert.NotNull(parentActivity);

        AgentRequestContext? requestContext = AgentRunHostCallTracer.CaptureCurrentRequestContext();
        Assert.NotNull(requestContext?.Trace);
        Assert.True(AgentRunHostCallTracer.TryParseRequestTraceContext(requestContext.Trace, out ActivityContext parentContext));

        using var source = new ActivitySource(KautukaActivitySources.GatewayAgentRunSource);
        using Activity? childActivity = source.StartActivity("agent (child) run [agent_tool]", ActivityKind.Internal, parentContext);
        Assert.NotNull(childActivity);
        Assert.Equal(parentActivity.TraceId, childActivity.TraceId);
        Assert.Equal(parentActivity.SpanId, childActivity.ParentSpanId);
    }

    [Fact]
    public void AgentRunCarriesApprovedIdentityAsBaggageAndAttributes()
    {
        using ActivityListener listener = CreateGatewayAgentRunListener();
        using var source = new ActivitySource(KautukaActivitySources.GatewayAgentRunSource);
        using Activity? runActivity = source.StartActivity("agent (test-agent) run [chat]", ActivityKind.Internal);

        AgentRunHostCallTracer.MarkAgentRun(runActivity, "run-identity", "tenant-1", "user-1");

        Assert.NotNull(runActivity);
        Assert.Equal("tenant-1", runActivity.GetBaggageItem(KautukaActivitySources.TenantIdAttribute));
        Assert.Equal("user-1", runActivity.GetBaggageItem(KautukaActivitySources.EndUserIdAttribute));
        Assert.Equal("tenant-1", runActivity.GetTagItem(KautukaActivitySources.TenantIdAttribute));
        Assert.Equal("user-1", runActivity.GetTagItem(KautukaActivitySources.EndUserIdAttribute));
    }

    private static Activity? StartAgentRun(string runId)
    {
        using var source = new ActivitySource(KautukaActivitySources.GatewayAgentRunSource);
        Activity? runActivity = source.StartActivity("agent (test-agent) run [chat]", ActivityKind.Internal);
        AgentRunHostCallTracer.MarkAgentRun(runActivity, runId);
        return runActivity;
    }

    private static ActivityListener CreateGatewayAgentRunListener()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = static source => string.Equals(source.Name, KautukaActivitySources.GatewayAgentRunSource, StringComparison.Ordinal),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private sealed class RecordingCallInvoker(Func<IMethod, object> responseFactory) : CallInvoker
    {
        private readonly Func<IMethod, object> responseFactory = responseFactory;

        public string? CapturedActivityDisplayName { get; private set; }
        public string? CapturedHostKind { get; private set; }
        public string? CapturedRpcMethod { get; private set; }
        public string? CapturedRunId { get; private set; }

        public void Reset()
        {
            CapturedActivityDisplayName = null;
            CapturedHostKind = null;
            CapturedRpcMethod = null;
            CapturedRunId = null;
        }

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request)
        {
            CaptureCurrentActivity();

            TResponse response = (TResponse)responseFactory(method);
            return new AsyncUnaryCall<TResponse>(
                Task.FromResult(response),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { });
        }

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request)
            => throw new NotSupportedException();

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options)
            => throw new NotSupportedException();

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options)
            => throw new NotSupportedException();

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request)
            => throw new NotSupportedException();

        private void CaptureCurrentActivity()
        {
            CapturedActivityDisplayName = Activity.Current?.DisplayName;
            CapturedHostKind = Activity.Current?.GetTagItem("kautuka.host.kind")?.ToString();
            CapturedRpcMethod = Activity.Current?.GetTagItem("rpc.method")?.ToString();
            CapturedRunId = Activity.Current?.GetTagItem("kautuka.run.id")?.ToString();
        }
    }
}
