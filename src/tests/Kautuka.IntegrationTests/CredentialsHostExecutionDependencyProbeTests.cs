using Grpc.Core;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Infrastructure.Credentials;
using Kautuka.Protos;

namespace Kautuka.IntegrationTests;

public sealed class CredentialsHostExecutionDependencyProbeTests
{
    private static readonly DateTimeOffset now = new(2026, 7, 16, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CheckAsyncReportsReadyHostAsync()
    {
        CredentialsHostExecutionDependencyProbe probe = CreateProbe(_ => new CredentialHealthResponse
        {
            State = CredentialHealthState.Ready,
            ActiveClientCertificateNotAfterUnixSeconds = now.AddDays(30).ToUnixTimeSeconds()
        });

        var status = await probe.CheckAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(status.Ready);
        Assert.Equal(CredentialsHostExecutionDependencyProbe.DependencyName, status.Dependency);
        Assert.Equal(CredentialsHostExecutionDependencyProbe.ReadyCode, status.Code);
    }

    [Fact]
    public async Task CheckAsyncClassifiesUnreachableHostAsync()
    {
        CredentialsHostExecutionDependencyProbe probe = CreateProbe(_ =>
            new RpcException(new Status(StatusCode.Unavailable, "connection refused")));

        var status = await probe.CheckAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(status.Ready);
        Assert.Equal(CredentialsHostExecutionDependencyProbe.UnavailableCode, status.Code);
        Assert.Contains("cannot start", status.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("connection refused", status.Detail, StringComparison.Ordinal);
        Assert.Contains("could not reach", status.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsyncClassifiesIntegrityFailureResponseAsync()
    {
        CredentialsHostExecutionDependencyProbe probe = CreateProbe(_ => new CredentialHealthResponse
        {
            State = CredentialHealthState.IntegrityFailed,
            ActiveClientCertificateNotAfterUnixSeconds = now.AddDays(30).ToUnixTimeSeconds()
        });

        var status = await probe.CheckAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(status.Ready);
        Assert.Equal(CredentialsHostExecutionDependencyProbe.IntegrityFailedCode, status.Code);
        Assert.Contains("integrity", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsyncClassifiesDataLossAsIntegrityFailureAsync()
    {
        CredentialsHostExecutionDependencyProbe probe = CreateProbe(_ =>
            new RpcException(new Status(StatusCode.DataLoss, "database corrupt")));

        var status = await probe.CheckAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(status.Ready);
        Assert.Equal(CredentialsHostExecutionDependencyProbe.IntegrityFailedCode, status.Code);
        Assert.DoesNotContain("database corrupt", status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAsyncClassifiesDegradedHostAsync()
    {
        CredentialsHostExecutionDependencyProbe probe = CreateProbe(_ => new CredentialHealthResponse
        {
            State = CredentialHealthState.Degraded,
            ActiveClientCertificateNotAfterUnixSeconds = now.AddDays(30).ToUnixTimeSeconds()
        });

        var status = await probe.CheckAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(status.Ready);
        Assert.Equal(CredentialsHostExecutionDependencyProbe.DegradedCode, status.Code);
        Assert.Contains("not ready", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsyncClassifiesExpiredClientIdentityAsDegradedAsync()
    {
        CredentialsHostExecutionDependencyProbe probe = CreateProbe(_ => new CredentialHealthResponse
        {
            State = CredentialHealthState.Ready,
            ActiveClientCertificateNotAfterUnixSeconds = now.AddMinutes(-1).ToUnixTimeSeconds()
        });

        var status = await probe.CheckAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(status.Ready);
        Assert.Equal(CredentialsHostExecutionDependencyProbe.DegradedCode, status.Code);
        Assert.Contains("expired", status.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckAsyncPropagatesCallerCancellationAsync()
    {
        CredentialsHostExecutionDependencyProbe probe = CreateProbe(_ => new OperationCanceledException());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(true);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => probe.CheckAsync(cancellation.Token)).ConfigureAwait(true);
    }

    [Fact]
    public async Task CheckAsyncBoundsUnresponsiveHostAsync()
    {
        CredentialsHostExecutionDependencyProbe probe = CreateProbe(
            _ => HangingResponse.Instance,
            TimeSpan.FromMilliseconds(25));

        AgentExecutionDependencyStatus status = await probe.CheckAsync(
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(status.Ready);
        Assert.Equal(CredentialsHostExecutionDependencyProbe.UnavailableCode, status.Code);
        Assert.Contains("timed out", status.Detail, StringComparison.OrdinalIgnoreCase);
    }

    private static CredentialsHostExecutionDependencyProbe CreateProbe(
        Func<IMethod, object> responseFactory,
        TimeSpan? probeTimeout = null)
    {
        var client = new CredentialsService.CredentialsServiceClient(new StubCallInvoker(responseFactory));
        var resolver = new CredentialResolverService(
            new Lazy<CredentialsService.CredentialsServiceClient>(() => client),
            "https://127.0.0.1:8181");
        return probeTimeout is null
            ? new CredentialsHostExecutionDependencyProbe(resolver, new FixedTimeProvider(now))
            : new CredentialsHostExecutionDependencyProbe(resolver, new FixedTimeProvider(now), probeTimeout.Value);
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class StubCallInvoker(Func<IMethod, object> responseFactory) : CallInvoker
    {
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request)
        {
            object response = responseFactory(method);
            Task<TResponse> responseTask;
            if (ReferenceEquals(response, HangingResponse.Instance))
            {
                var completion = new TaskCompletionSource<TResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                options.CancellationToken.Register(static state =>
                    ((TaskCompletionSource<TResponse>)state!).TrySetCanceled(), completion);
                responseTask = completion.Task;
            }
            else
            {
                responseTask = response is Exception exception
                    ? Task.FromException<TResponse>(exception)
                    : Task.FromResult((TResponse)response);
            }

            return new AsyncUnaryCall<TResponse>(
                responseTask,
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
    }


    private sealed class HangingResponse
    {
        public static HangingResponse Instance { get; } = new();
    }
}
