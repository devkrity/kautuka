using Grpc.Core;

using Kautuka.Gateway.Infrastructure.Channels;
using Kautuka.Gateway.Infrastructure.Sandbox;
using Kautuka.Manager.Protos;
using Kautuka.Protos;

namespace Kautuka.ContractTests;

public sealed class GrpcCorrelationPropagationTests
{
    [Fact]
    public async Task ChannelHostClientAttachesCorrelationMetadataOnUnaryCall()
    {
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

        Assert.NotNull(invoker.LastHeaders);
        Assert.NotNull(GetHeaderValue(invoker.LastHeaders!, "x-correlation-id"));
    }

    [Fact]
    public async Task WorkspaceClientAttachesCorrelationMetadataOnUnaryCall()
    {
        RecordingCallInvoker invoker = new(static method => method.Name switch
        {
            "Ping" => new WorkspaceBoolResponse { Ok = true },
            _ => throw new InvalidOperationException($"Unexpected method {method.Name}")
        });

        var client = new ManagerGrpcServiceClient(invoker, "http://127.0.0.1:8100");

        _ = await client.PingAsync(new WorkspacePingRequest { Message = "ping" }, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.NotNull(invoker.LastHeaders);
        Assert.NotNull(GetHeaderValue(invoker.LastHeaders!, "x-correlation-id"));
    }

    private static string? GetHeaderValue(Metadata headers, string key)
        => headers.FirstOrDefault(entry => string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;

    private sealed class RecordingCallInvoker(Func<IMethod, object> responseFactory) : CallInvoker
    {
        private readonly Func<IMethod, object> responseFactory = responseFactory;

        public Metadata? LastHeaders { get; private set; }

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request)
        {
            LastHeaders = options.Headers;
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
        {
            LastHeaders = options.Headers;
            return new AsyncServerStreamingCall<TResponse>(
                new EmptyAsyncStreamReader<TResponse>(),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { });
        }

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
    }

    private sealed class EmptyAsyncStreamReader<T> : IAsyncStreamReader<T>
    {
        public T Current => throw new InvalidOperationException();

        public Task<bool> MoveNext(CancellationToken cancellationToken)
            => Task.FromResult(false);
    }
}
