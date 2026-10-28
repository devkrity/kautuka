using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Kautuka.WebHost;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class WebHostEdgeAndProxyTests
{
    [Fact]
    public void DevelopmentDefaultsToLoopbackHttp()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();
        using WebHostEdgeBinding binding = WebHostEdgePolicy.Resolve(configuration, 20229);

        Assert.True(IPAddress.IsLoopback(binding.Address));
        Assert.False(binding.UsesTls);
    }

    [Fact]
    public void NonLoopbackBindingWithoutCertificateIsRejected()
    {
        IConfiguration configuration = Configuration(new Dictionary<string, string?>
        {
            ["WebHost:ListenAddress"] = "0.0.0.0",
        });

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => WebHostEdgePolicy.Resolve(configuration, 20229));
        Assert.Contains("requires a TLS certificate", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NonLoopbackBindingLoadsConfiguredCertificate()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-webhost-edge-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string certificatePath = Path.Combine(root, "edge.pfx");
        try
        {
            using RSA key = RSA.Create(2048);
            var request = new CertificateRequest("CN=Kautuka WebHost Test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            File.WriteAllBytes(certificatePath, certificate.Export(X509ContentType.Pkcs12, "test-password"));

            IConfiguration configuration = Configuration(new Dictionary<string, string?>
            {
                ["WebHost:ListenAddress"] = "0.0.0.0",
                ["WebHost:Tls:CertificatePath"] = certificatePath,
                ["WebHost:Tls:CertificatePassword"] = "test-password",
            });
            using WebHostEdgeBinding binding = WebHostEdgePolicy.Resolve(configuration, 20229);

            Assert.True(binding.UsesTls);
            Assert.Equal(IPAddress.Any, binding.Address);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProxyFiltersSpoofedAndHopByHopHeaders()
    {
        HttpRequestMessage? forwardedRequest = null;
        using var client = new HttpClient(new DelegateHandler(request =>
        {
            forwardedRequest = request;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok"),
            };
            response.Headers.TryAddWithoutValidation("Connection", "X-Response-Hop");
            response.Headers.TryAddWithoutValidation("X-Response-Hop", "secret-hop");
            response.Headers.TryAddWithoutValidation("X-Response-End", "visible");
            return response;
        }))
        {
            BaseAddress = new Uri("http://127.0.0.1:20000"),
        };
        DefaultHttpContext context = Context("/api/test?value=1");
        context.Request.Headers.Connection = "X-Request-Hop";
        context.Request.Headers["X-Request-Hop"] = "secret-hop";
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.10";
        context.Request.Headers["X-Forwarded-Port"] = "4444";
        context.Request.Headers["X-Forwarded-Prefix"] = "/spoofed";
        context.Request.Headers["X-Original-For"] = "203.0.113.11";
        context.Request.Headers["X-Original-Host"] = "spoofed.example";
        context.Request.Headers["X-Original-Proto"] = "http";
        context.Request.Headers["X-Real-IP"] = "203.0.113.12";
        context.Request.Headers["X-End-To-End"] = "visible";
        context.Request.Scheme = "https";
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.20");

        await GatewayProxy.ForwardAsync(context, client, NullLogger.Instance);

        Assert.NotNull(forwardedRequest);
        Assert.False(forwardedRequest.Headers.Contains("X-Request-Hop"));
        Assert.Equal("visible", Assert.Single(forwardedRequest.Headers.GetValues("X-End-To-End")));
        Assert.Equal("192.0.2.20", Assert.Single(forwardedRequest.Headers.GetValues("X-Forwarded-For")));
        Assert.Equal("https", Assert.Single(forwardedRequest.Headers.GetValues("X-Forwarded-Proto")));
        Assert.False(forwardedRequest.Headers.Contains("X-Forwarded-Port"));
        Assert.False(forwardedRequest.Headers.Contains("X-Forwarded-Prefix"));
        Assert.False(forwardedRequest.Headers.Contains("X-Original-For"));
        Assert.False(forwardedRequest.Headers.Contains("X-Original-Host"));
        Assert.False(forwardedRequest.Headers.Contains("X-Original-Proto"));
        Assert.False(forwardedRequest.Headers.Contains("X-Real-IP"));
        Assert.False(context.Response.Headers.ContainsKey("Connection"));
        Assert.False(context.Response.Headers.ContainsKey("X-Response-Hop"));
        Assert.Equal("visible", context.Response.Headers["X-Response-End"]);
    }

    [Theory]
    [InlineData("/api/internal/shutdown")]
    [InlineData("/api/foo/../internal/shutdown")]
    [InlineData("/api/./internal/shutdown")]
    [InlineData("/api/foo/bar/../../internal/shutdown")]
    [InlineData("/api/foo/%2e%2e/internal/shutdown")]
    [InlineData("/api/%2e/internal/shutdown")]
    public void DotSegmentsCannotReachInternalGatewayRoutes(string requestPath)
    {
        Assert.True(GatewayProxy.TargetsInternalRoute(requestPath));
    }

    [Fact]
    public void OrdinaryApiPathsAreForwarded()
    {
        Assert.False(GatewayProxy.TargetsInternalRoute("/api/connectors"));
    }

    [Fact]
    public void SecurityHeadersCoverFramingAndContentType()
    {
        var headers = new HeaderDictionary();
        WebHostSecurityHeaders.Apply(headers);

        Assert.Equal("nosniff", headers["X-Content-Type-Options"].ToString());
        Assert.Equal("DENY", headers["X-Frame-Options"].ToString());
        Assert.Equal("no-referrer", headers["Referrer-Policy"].ToString());
        Assert.Equal("frame-ancestors 'none'", headers["Content-Security-Policy"].ToString());
    }

    [Fact]
    public async Task ProxyStreamsResponseContentBeforeSourceCompletes()
    {
        var stream = new GatedResponseStream("first-", "second");
        var client = new HttpClient(new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream),
        }))
        {
            BaseAddress = new Uri("http://127.0.0.1:20000"),
        };
        DefaultHttpContext context = Context("/api/stream");

        Task forwarding = GatewayProxy.ForwardAsync(context, client, NullLogger.Instance);
        try
        {
            await stream.FirstChunkRead.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal("first-", Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));
            stream.ReleaseSecondChunk();
            await forwarding;
            Assert.Equal("first-second", Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));
        }
        finally
        {
            stream.ReleaseSecondChunk();
            await forwarding;
            client.Dispose();
        }
    }

    [Fact]
    public async Task ProxyFailureBodyDoesNotExposeTransportException()
    {
        using var client = new HttpClient(new DelegateHandler(_ => throw new HttpRequestException("secret downstream path and token")))
        {
            BaseAddress = new Uri("http://127.0.0.1:20000"),
        };
        DefaultHttpContext context = Context("/api/fail");

        await GatewayProxy.ForwardAsync(context, client, NullLogger.Instance);

        string body = Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
        Assert.Equal("{\"error\":\"gateway_unavailable\"}", body);
        Assert.DoesNotContain("secret", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static DefaultHttpContext Context(string pathAndQuery)
    {
        var context = new DefaultHttpContext();
        Uri uri = new("http://localhost" + pathAndQuery);
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = uri.AbsolutePath;
        context.Request.QueryString = new QueryString(uri.Query);
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }

    private sealed class GatedResponseStream(string first, string second) : Stream
    {
        private readonly byte[] firstBytes = Encoding.UTF8.GetBytes(first);
        private readonly byte[] secondBytes = Encoding.UTF8.GetBytes(second);
        private readonly TaskCompletionSource firstChunkRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseSecond = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int readCount;

        public Task FirstChunkRead => firstChunkRead.Task;

        public void ReleaseSecondChunk() => releaseSecond.TrySetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int current = Interlocked.Increment(ref readCount);
            if (current == 1)
            {
                firstBytes.CopyTo(buffer);
                firstChunkRead.TrySetResult();
                return firstBytes.Length;
            }
            if (current == 2)
            {
                await releaseSecond.Task.WaitAsync(cancellationToken);
                secondBytes.CopyTo(buffer);
                return secondBytes.Length;
            }
            return 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
