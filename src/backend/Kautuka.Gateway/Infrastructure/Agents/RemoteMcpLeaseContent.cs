using System.Net;

namespace Kautuka.Gateway.Infrastructure.Agents;

// Bind response reads to the operation lease even after SendAsync has returned.
internal sealed class RemoteMcpLeaseContent : HttpContent
{
    private readonly HttpContent source;
    private readonly CancellationToken leaseToken;
    public RemoteMcpLeaseContent(HttpContent source, CancellationToken leaseToken)
    {
        this.source = source;
        this.leaseToken = leaseToken;
        foreach (var header in source.Headers)
            Headers.TryAddWithoutValidation(header.Key, header.Value);
    }

    protected override bool TryComputeLength(out long length) { length = 0; return false; }
    protected override async Task<Stream> CreateContentReadStreamAsync()
        => new LeaseStream(await source.ReadAsStreamAsync(leaseToken).ConfigureAwait(false), leaseToken);
    protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(leaseToken, cancellationToken);
        return new LeaseStream(await source.ReadAsStreamAsync(linked.Token).ConfigureAwait(false), leaseToken);
    }
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        Stream sourceStream = await CreateContentReadStreamAsync().ConfigureAwait(false);
        await using (sourceStream.ConfigureAwait(false))
            await sourceStream.CopyToAsync(stream, leaseToken).ConfigureAwait(false);
    }
    protected override void Dispose(bool disposing) { if (disposing) source.Dispose(); base.Dispose(disposing); }

    private sealed class LeaseStream(Stream source, CancellationToken leaseToken) : Stream
    {
        public override bool CanRead => source.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(leaseToken, cancellationToken);
            return await source.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) source.Dispose(); base.Dispose(disposing); }
    }
}
