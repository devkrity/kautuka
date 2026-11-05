using System.Net;

using Grpc.Core;

using Kautuka.Gateway.Domain.Connectors;
using Kautuka.Gateway.Infrastructure.Credentials;

namespace Kautuka.IntegrationTests;

public sealed class RemoteMcpRequestHandlerTests
{
    [Theory]
    [InlineData(401, "authentication was rejected")]
    [InlineData(403, "access was denied")]
    [InlineData(429, "usage limit was reached")]
    public async Task ProviderFailuresAreActionableWithoutBodyEchoOrAnonymousRetry(int status, string expected)
    {
        var inner = new RejectedHandler(status);
        var context = CreateContext((keys, _) => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { [keys[0]] = "synthetic-secret" }));
        using var client = new HttpClient(new RemoteMcpRequestHandler(context, inner));
        using var request = new HttpRequestMessage(HttpMethod.Get, context.Endpoint);
        RemoteMcpConfigurationException error = await Assert.ThrowsAsync<RemoteMcpConfigurationException>(() => client.SendAsync(request, TestContext.Current.CancellationToken));
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-secret", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("provider-body", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, inner.Sends);
        Assert.False(request.Headers.Contains("Authorization"));
    }

    private sealed class RejectedHandler(int status) : HttpMessageHandler
    {
        public int Sends { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sends++;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("provider-body synthetic-secret") });
        }
    }

    [Fact]
    public async Task ResolvesOnlyAtSendScopesKeyAndRemovesInjectedHeaders()
    {
        int resolves = 0;
        string expectedKey = ConnectorCredentialKeys.BuildRemoteCredentialKey("namespace-one", new Uri("https://example.com/mcp"), "api-key");
        var inner = new RecordingHandler();
        var context = CreateContext((keys, _) =>
        {
            resolves++;
            Assert.Equal([expectedKey], keys);
            return Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { [expectedKey] = "secret-value" });
        });
        using var client = new HttpClient(new RemoteMcpRequestHandler(context, inner));
        Assert.Equal(0, resolves);
        using var request = new HttpRequestMessage(HttpMethod.Post, context.Endpoint);
        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal("Bearer secret-value", inner.Authorization);
        Assert.Equal("free", inner.Mode);
        Assert.Equal(1, resolves);
        Assert.False(request.Headers.Contains("Authorization"));
        Assert.False(request.Headers.Contains("X-Mode"));
        Assert.NotEqual(expectedKey, ConnectorCredentialKeys.BuildRemoteCredentialKey("namespace-two", new Uri("https://example.com/mcp"), "api-key"));
    }

    [Fact]
    public async Task OptionalMissingOmittedRequiredMissingAndVaultFailureBlock()
    {
        var inner = new RecordingHandler();
        RemoteMcpRequestContext optional = CreateContext((_, _) => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>()));
        using (var client = new HttpClient(new RemoteMcpRequestHandler(optional, inner)))
        using (HttpResponseMessage response = await client.GetAsync(optional.Endpoint, TestContext.Current.CancellationToken))
            Assert.Null(inner.Authorization);
        int sends = inner.Sends;
        var required = optional with { Credentials = [new("api-key", "API key", true)] };
        using (var client = new HttpClient(new RemoteMcpRequestHandler(required, inner)))
            await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync(required.Endpoint, TestContext.Current.CancellationToken));
        var failed = optional with { Resolve = (_, _) => throw new RpcException(new Status(StatusCode.Unavailable, "sensitive-detail")) };
        using (var client = new HttpClient(new RemoteMcpRequestHandler(failed, inner)))
        {
            HttpRequestException error = await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync(failed.Endpoint, TestContext.Current.CancellationToken));
            Assert.DoesNotContain("sensitive-detail", error.Message, StringComparison.Ordinal);
        }
        Assert.Equal(sends, inner.Sends);
    }

    [Fact]
    public async Task RejectsForeignDestinationAndOversizedResolvedValueBeforeSend()
    {
        int calls = 0;
        var inner = new RecordingHandler();
        var context = CreateContext((keys, _) => { calls++; return Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { [keys[0]] = new string('x', 8193) }); });
        using var client = new HttpClient(new RemoteMcpRequestHandler(context, inner));
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync(new Uri("https://other.example/mcp"), TestContext.Current.CancellationToken));
        Assert.Equal(0, calls);
        HttpRequestException error = await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync(context.Endpoint, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(new string('x', 20), error.Message, StringComparison.Ordinal);
        Assert.Equal(0, inner.Sends);
    }

    [Fact]
    public async Task RejectsRedirectAndCleansHeadersWhenInnerSendFails()
    {
        var context = CreateContext((keys, _) => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { [keys[0]] = "secret" }));
        var inner = new FailureHandler();
        using var client = new HttpClient(new RemoteMcpRequestHandler(context, inner));
        using var redirect = new HttpRequestMessage(HttpMethod.Get, context.Endpoint);
        HttpRequestException error = await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.SendAsync(redirect, TestContext.Current.CancellationToken));
        Assert.Contains("redirect", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(redirect.Headers.Contains("Authorization"));
        inner.Throw = true;
        using var failure = new HttpRequestMessage(HttpMethod.Get, context.Endpoint);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.SendAsync(failure, TestContext.Current.CancellationToken));
        Assert.False(failure.Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task WhitespaceOptionalValueIsOmittedAndResolvedLimitsApplyToAggregate()
    {
        var inner = new RecordingHandler();
        var optional = CreateContext((keys, _) => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { [keys[0]] = "   " }));
        using (var client = new HttpClient(new RemoteMcpRequestHandler(optional, inner)))
        using (HttpResponseMessage response = await client.GetAsync(optional.Endpoint, TestContext.Current.CancellationToken))
            Assert.Null(inner.Authorization);
        int count = inner.Sends;
        RemoteMcpHeader[] headers = [.. Enumerable.Range(0, 5).Select(i => new RemoteMcpHeader($"X-{i}", CredentialKey: "api-key"))];
        var oversized = optional with { Headers = headers, Resolve = (keys, _) => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { [keys[0]] = new string('x', 8192) }) };
        using var rejected = new HttpClient(new RemoteMcpRequestHandler(oversized, inner));
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => rejected.GetAsync(oversized.Endpoint, TestContext.Current.CancellationToken));
        Assert.Equal(count, inner.Sends);
    }

    private sealed class FailureHandler : HttpMessageHandler
    {
        public bool Throw { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Throw ? throw new HttpRequestException("failure") : Task.FromResult(new HttpResponseMessage(HttpStatusCode.TemporaryRedirect));
    }

    [Fact]
    public async Task RevalidatesPolicyOnEverySendAndAfterResolution()
    {
        bool allowed = true;
        int checks = 0;
        var inner = new RecordingHandler();
        var context = CreateContext((_, _) => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>())) with
        {
            Revalidate = _ => { checks++; if (!allowed) throw new HttpRequestException("Policy changed."); return Task.CompletedTask; },
        };
        using var client = new HttpClient(new RemoteMcpRequestHandler(context, inner));
        using HttpResponseMessage first = await client.GetAsync(context.Endpoint, TestContext.Current.CancellationToken);
        Assert.Equal(2, checks);
        allowed = false;
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync(context.Endpoint, TestContext.Current.CancellationToken));
        Assert.Equal(1, inner.Sends);

        allowed = true;
        var duringResolution = context with
        {
            Resolve = (_, _) => { allowed = false; return Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>()); },
        };
        using var second = new HttpClient(new RemoteMcpRequestHandler(duringResolution, inner));
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => second.GetAsync(context.Endpoint, TestContext.Current.CancellationToken));
        Assert.Equal(1, inner.Sends);
    }

    [Fact]
    public async Task ScreensCredentialEchoFromActualResponse()
    {
        var context = CreateContext((keys, _) => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { [keys[0]] = "secret\"value" }));
        using var client = new HttpClient(new RemoteMcpRequestHandler(context, new EchoHandler()));
        using HttpResponseMessage response = await client.GetAsync(context.Endpoint, TestContext.Current.CancellationToken);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("secret", body, StringComparison.Ordinal);
        Assert.Contains("[redacted]", body, StringComparison.Ordinal);
    }

    private sealed class EchoHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"value\":\"secret\\\"value\"}") });
    }

    [Fact]
    public async Task CredentialMutationCancelsBlockedResponseReadAndReleasesLease()
    {
        var tracker = new RemoteMcpCredentialRevisionTracker();
        var context = CreateContext((_, _) => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>())) with
        {
            CreateLease = () => tracker.Acquire("one", "user", "agent", includeSystem: false),
        };
        var handler = new RemoteMcpRequestHandler(context, new StreamingHandler());
        using var client = new HttpClient(handler);
        using HttpResponseMessage response = await client.GetAsync(context.Endpoint, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        using Stream stream = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        Task<int> read = stream.ReadAsync(new byte[32], TestContext.Current.CancellationToken).AsTask();
        Assert.False(read.IsCompleted);
        tracker.BeginChange("one", "user:user");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        tracker.EndChange("one", "user:user");
        client.Dispose();
        Assert.Equal(0, tracker.ActiveLeaseCount);
    }

    private sealed class StreamingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BlockingReadStream()) });
    }
    private sealed class BlockingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static RemoteMcpRequestContext CreateContext(Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyDictionary<string, string>>> resolve)
        => new(new Uri("https://example.com/mcp"), "one", "user", false,
            [new("api-key", "API key", false)], [new("X-Mode", "free"), new("Authorization", CredentialKey: "api-key", Prefix: "Bearer ")], resolve, CredentialNamespace: "namespace-one");

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? Authorization { get; private set; }
        public string? Mode { get; private set; }
        public int Sends { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sends++;
            Authorization = request.Headers.TryGetValues("Authorization", out IEnumerable<string>? auth) ? Assert.Single(auth) : null;
            Mode = request.Headers.TryGetValues("X-Mode", out IEnumerable<string>? mode) ? Assert.Single(mode) : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
