using System.Net;
using System.Text;
using System.Text.Json;

namespace Kautuka.IntegrationTests;

public sealed class RemoteMcpResponseScreeningTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(4096)]
    public async Task ScreensJsonEscapedAndRawEchoesAcrossStreamBoundaries(int chunkSize)
    {
        const string secret = "synthetic-<&-credential";
        string json = "{\"result\":\"" + JsonEncodedText.Encode(secret) + "\"}";
        string body = $"data: {json}\n\ndata: {{\"text\":\"{secret}\"}}\n\n";
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ChunkStream(Encoding.UTF8.GetBytes(body), chunkSize)),
        };
        response.Headers.TryAddWithoutValidation("X-Provider-Error", $"Echo {secret}");
        response.Content.Headers.TryAddWithoutValidation("X-Content-Diagnostic", secret);
        RemoteMcpResponseScreening.Apply(response, [secret]);
        string screened = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal("data: {\"result\":\"[redacted]\"}\n\ndata: {\"text\":\"[redacted]\"}\n\n", screened);
        Assert.Equal("Echo [redacted]", Assert.Single(response.Headers.GetValues("X-Provider-Error")));
        Assert.Equal("[redacted]", Assert.Single(response.Content.Headers.GetValues("X-Content-Diagnostic")));
    }

    [Fact]
    public async Task ScreensShortJsonQuoteEscapesAcrossSingleByteReads()
    {
        const string secret = "synthetic-\"-credential";
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ChunkStream("{\"text\":\"synthetic-\\\"-credential\"}"u8.ToArray(), 1)),
        };
        RemoteMcpResponseScreening.Apply(response, [secret]);
        Assert.Equal("{\"text\":\"[redacted]\"}", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StreamsAnEventWithoutWaitingForEofOrAnotherEvent()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new OneEventStream()),
        };
        RemoteMcpResponseScreening.Apply(response, ["synthetic-very-long-credential-that-is-longer-than-the-event"]);
        await using Stream stream = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        var buffer = new byte[1024];
        int count = await stream.ReadAsync(buffer, TestContext.Current.CancellationToken);
        Assert.Equal("data: {}\n\n", Encoding.UTF8.GetString(buffer, 0, count));
    }

    [Fact]
    public async Task HandlesOverlappingPatternsAndIncompletePrefixAtEof()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ChunkStream("prefix-long prefix pre"u8.ToArray(), 1)),
        };
        RemoteMcpResponseScreening.Apply(response, ["prefix", "prefix-long"]);
        Assert.Equal("[redacted] [redacted] pre", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PreservesBodyWhenNoCredentialWasUsed()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: {}\n\n") };
        HttpContent original = response.Content;
        RemoteMcpResponseScreening.Apply(response, []);
        Assert.Same(original, response.Content);
        Assert.Equal("data: {}\n\n", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private sealed class ChunkStream(byte[] bytes, int chunkSize) : MemoryStream(bytes, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(chunkSize, buffer.Length)], cancellationToken);
    }

    private sealed class OneEventStream : MemoryStream
    {
        private bool read;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (read) throw new InvalidOperationException("The next SSE event has not arrived.");
            read = true;
            ReadOnlySpan<byte> bytes = "data: {}\n\n"u8;
            bytes.CopyTo(buffer.Span);
            return ValueTask.FromResult(bytes.Length);
        }
    }
}
