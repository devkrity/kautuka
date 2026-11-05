using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Kautuka.Gateway.Infrastructure.Agents;

/// <summary>
/// Best-effort screening of known credential echoes, scoped to one HTTP response.
/// Preserves streaming: only an unfinished possible match is held between reads.
/// This cannot detect arbitrary transformations made by a malicious credential recipient.
/// </summary>
internal static class RemoteMcpResponseScreening
{
    public static void Apply(HttpResponseMessage response, IReadOnlyCollection<string> secrets)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(secrets);
        string[] values = [.. secrets.Where(static value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal)];
        if (values.Length == 0) return;

        foreach (var header in response.Headers.ToArray())
        {
            string[] screened = [.. header.Value.Select(value => Screen(value, values))];
            response.Headers.Remove(header.Key);
            response.Headers.TryAddWithoutValidation(header.Key, screened);
        }
        response.ReasonPhrase = response.ReasonPhrase is null ? null : Screen(response.ReasonPhrase, values);
        byte[][] patterns = [.. values.SelectMany(static value => new[]
        {
            Encoding.UTF8.GetBytes(value),
            JsonEncodedText.Encode(value).EncodedUtf8Bytes.ToArray(),
            JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).EncodedUtf8Bytes.ToArray(),
        }).OrderByDescending(static bytes => bytes.Length)];
        response.Content = new ScreenedContent(response.Content, patterns, values);
    }

    private static string Screen(string value, string[] secrets)
    {
        foreach (string secret in secrets.OrderByDescending(static item => item.Length))
            value = value.Replace(secret, "[redacted]", StringComparison.Ordinal);
        return value;
    }

    private static void Clear(byte[][] patterns)
    {
        foreach (byte[] pattern in patterns) CryptographicOperations.ZeroMemory(pattern);
    }

    private sealed class ScreenedContent : HttpContent
    {
        private readonly HttpContent source;
        private readonly byte[][] patterns;

        public ScreenedContent(HttpContent source, byte[][] patterns, string[] secrets)
        {
            this.source = source;
            this.patterns = patterns;
            foreach (var header in source.Headers)
                if (!header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    Headers.TryAddWithoutValidation(header.Key, header.Value.Select(value => Screen(value, secrets)));
        }

        protected override bool TryComputeLength(out long length) { length = 0; return false; }

        protected override async Task<Stream> CreateContentReadStreamAsync()
            => new ScreeningStream(await source.ReadAsStreamAsync().ConfigureAwait(false), patterns);

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
            => new ScreeningStream(await source.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), patterns);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            Stream screened = await CreateContentReadStreamAsync().ConfigureAwait(false);
            await using (screened.ConfigureAwait(false))
                await screened.CopyToAsync(stream).ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { source.Dispose(); Clear(patterns); }
            base.Dispose(disposing);
        }
    }

    private sealed class ScreeningStream(Stream source, byte[][] patterns) : Stream
    {
        private static readonly byte[] replacement = "[redacted]"u8.ToArray();
        private readonly byte[] incoming = new byte[4096];
        private byte[] pending = [];
        private byte[] ready = [];
        private int readyOffset;
        private bool ended;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty) return 0;
            while (readyOffset == ready.Length)
            {
                if (ended && pending.Length == 0) return 0;
                int received = await source.ReadAsync(incoming, cancellationToken).ConfigureAwait(false);
                ended = received == 0;
                byte[] combined = new byte[pending.Length + received];
                pending.CopyTo(combined, 0);
                incoming.AsSpan(0, received).CopyTo(combined.AsSpan(pending.Length));
                CryptographicOperations.ZeroMemory(pending);
                CryptographicOperations.ZeroMemory(incoming);
                Transform(combined);
                CryptographicOperations.ZeroMemory(combined);
            }
            int count = Math.Min(buffer.Length, ready.Length - readyOffset);
            ready.AsMemory(readyOffset, count).CopyTo(buffer);
            readyOffset += count;
            if (readyOffset == ready.Length) CryptographicOperations.ZeroMemory(ready);
            return count;
        }

        private void Transform(byte[] bytes)
        {
            var output = new List<byte>(bytes.Length);
            int position = 0;
            while (position < bytes.Length)
            {
                ReadOnlySpan<byte> rest = bytes.AsSpan(position);
                int matched = 0;
                bool unfinished = false;
                foreach (byte[] pattern in patterns)
                {
                    if (!ended && rest.Length < pattern.Length && pattern.AsSpan().StartsWith(rest))
                    { unfinished = true; break; }
                    if (rest.StartsWith(pattern)) { matched = pattern.Length; break; }
                }
                if (unfinished) break;
                if (matched != 0) { output.AddRange(replacement); position += matched; }
                else output.Add(bytes[position++]);
            }
            pending = bytes.AsSpan(position).ToArray();
            ready = [.. output];
            readyOffset = 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                source.Dispose();
                Clear(patterns);
                CryptographicOperations.ZeroMemory(incoming);
                CryptographicOperations.ZeroMemory(pending);
                CryptographicOperations.ZeroMemory(ready);
            }
            base.Dispose(disposing);
        }
    }
}
