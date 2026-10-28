using Grpc.Core;
using Grpc.Net.Client;

namespace Kautuka.Gateway.Infrastructure.Grpc;

/// <summary>
/// Defers <see cref="GrpcChannel"/> creation (and its associated cert loading) to the first
/// actual gRPC call. A certificate that is not on disk yet throws and is not remembered, so
/// the next call loads it again after the peer has written the files.
///
/// Compose with <see cref="DeadlineCallInvoker"/> to get both deferred init and default deadlines:
/// <c>new DeadlineCallInvoker(new LazyCallInvoker(lazyChannel))</c>
/// </summary>
internal sealed class LazyCallInvoker : CallInvoker
{
    private readonly DeferredSuccess<CallInvoker> inner;

    public LazyCallInvoker(DeferredSuccess<GrpcChannel> lazyChannel)
    {
        ArgumentNullException.ThrowIfNull(lazyChannel);
        inner = new DeferredSuccess<CallInvoker>(() => lazyChannel.Value.CreateCallInvoker());
    }

    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options)
        => inner.Value.AsyncClientStreamingCall(method, host, options);

    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options)
        => inner.Value.AsyncDuplexStreamingCall(method, host, options);

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        => inner.Value.AsyncServerStreamingCall(method, host, options, request);

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        => inner.Value.AsyncUnaryCall(method, host, options, request);

    public override TResponse BlockingUnaryCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        => inner.Value.BlockingUnaryCall(method, host, options, request);
}
