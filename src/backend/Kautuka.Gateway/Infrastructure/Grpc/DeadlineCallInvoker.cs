using Grpc.Core;

namespace Kautuka.Gateway.Infrastructure.Grpc;

/// <summary>
/// Ensures every outgoing gRPC call has a deadline. The 30-second default is injected
/// only when the caller has not already set one; propagated deadlines from upstream are preserved.
/// </summary>
internal sealed class DeadlineCallInvoker(CallInvoker inner) : CallInvoker
{
    private static readonly TimeSpan defaultDeadline = TimeSpan.FromSeconds(30);

    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options)
        => inner.AsyncClientStreamingCall(method, host, EnsureDeadline(options));

    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options)
        => inner.AsyncDuplexStreamingCall(method, host, EnsureDeadline(options));

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        => inner.AsyncServerStreamingCall(method, host, EnsureDeadline(options), request);

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        => inner.AsyncUnaryCall(method, host, EnsureDeadline(options), request);

    public override TResponse BlockingUnaryCall<TRequest, TResponse>(
        Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        => inner.BlockingUnaryCall(method, host, EnsureDeadline(options), request);

    private static CallOptions EnsureDeadline(CallOptions options)
        => options.Deadline.HasValue ? options : options.WithDeadline(DateTime.UtcNow + defaultDeadline);
}
