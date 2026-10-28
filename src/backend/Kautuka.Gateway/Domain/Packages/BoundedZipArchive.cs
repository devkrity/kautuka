using System.Diagnostics;
using System.IO.Compression;

using Kautuka.Gateway.Domain.Assets;

namespace Kautuka.Gateway.Domain.Packages;

internal sealed record ArchiveIngestionLimits(
    long MaxCompressedBytes,
    int MaxEntries,
    long MaxEntryBytes,
    long MaxExpandedBytes,
    double MaxCompressionRatio,
    TimeSpan MaxProcessingTime);

internal sealed class BoundedZipArchive : IDisposable
{
    private const int bufferSize = 81920;
    private readonly ArchiveIngestionLimits limits;
    private readonly CancellationToken callerCancellationToken;
    private readonly CancellationTokenSource processingCancellation;
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();
    private long expandedBytes;

    public BoundedZipArchive(ArchiveIngestionLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaxCompressedBytes <= 0
            || limits.MaxEntries <= 0
            || limits.MaxEntryBytes <= 0
            || limits.MaxExpandedBytes <= 0
            || limits.MaxCompressionRatio <= 0
            || limits.MaxProcessingTime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "Archive ingestion limits must all be positive.");
        }

        this.limits = limits;
        callerCancellationToken = cancellationToken;
        processingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        processingCancellation.CancelAfter(limits.MaxProcessingTime);
    }

    public CancellationToken CancellationToken => processingCancellation.Token;

    public async Task CopyUploadAsync(Stream source, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        byte[] buffer = new byte[bufferSize];
        long total = 0;
        while (true)
        {
            int read = await ReadAsync(source, buffer).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            total = checked(total + read);
            if (total > limits.MaxCompressedBytes)
            {
                throw new InvalidOperationException($"Archive exceeds the compressed size limit of {limits.MaxCompressedBytes} bytes.");
            }

            await WriteAsync(destination, buffer.AsMemory(0, read)).ConfigureAwait(false);
        }
    }

    public IReadOnlyList<ZipArchiveEntry> Validate(ZipArchive archive, string packageKind)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ThrowIfUnavailable();

        if (archive.Entries.Count > limits.MaxEntries)
        {
            throw new InvalidOperationException($"{packageKind} has {archive.Entries.Count} entries, exceeding the limit of {limits.MaxEntries}.");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<ZipArchiveEntry>(archive.Entries.Count);
        long declaredExpandedBytes = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            ThrowIfUnavailable();
            string normalizedPath = NormalizeAndValidatePath(entry.FullName, packageKind);
            ValidateNotLink(entry, packageKind);
            if (!paths.Add(normalizedPath))
            {
                throw new InvalidOperationException($"{packageKind} contains duplicate entry path '{entry.FullName}'.");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            if (entry.Length < 0 || entry.CompressedLength < 0)
            {
                throw new InvalidOperationException($"{packageKind} entry '{entry.FullName}' has invalid size metadata.");
            }

            if (entry.Length > limits.MaxEntryBytes)
            {
                throw new InvalidOperationException($"{packageKind} entry '{entry.FullName}' exceeds the per-entry limit of {limits.MaxEntryBytes} bytes.");
            }

            declaredExpandedBytes = checked(declaredExpandedBytes + entry.Length);
            if (declaredExpandedBytes > limits.MaxExpandedBytes)
            {
                throw new InvalidOperationException($"{packageKind} exceeds the expanded size limit of {limits.MaxExpandedBytes} bytes.");
            }

            double ratio = entry.Length / (double)Math.Max(1, entry.CompressedLength);
            if (ratio > limits.MaxCompressionRatio)
            {
                throw new InvalidOperationException($"{packageKind} entry '{entry.FullName}' exceeds the compression ratio limit of {limits.MaxCompressionRatio:0.##}.");
            }

            files.Add(entry);
        }

        return files;
    }

    public async Task CopyEntryAsync(ZipArchiveEntry entry, Stream destination, string packageKind)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(destination);

#pragma warning disable CA1849, CA2007 // ZipArchiveEntry.Open has no async overload; no SynchronizationContext in ASP.NET Core
        await using Stream source = entry.Open();
#pragma warning restore CA1849, CA2007
        byte[] buffer = new byte[bufferSize];
        long entryBytes = 0;
        while (true)
        {
            int read = await ReadAsync(source, buffer).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            entryBytes = checked(entryBytes + read);
            expandedBytes = checked(expandedBytes + read);
            if (entryBytes > limits.MaxEntryBytes || entryBytes > entry.Length)
            {
                throw new InvalidOperationException($"{packageKind} entry '{entry.FullName}' exceeds its allowed expanded size.");
            }

            if (expandedBytes > limits.MaxExpandedBytes)
            {
                throw new InvalidOperationException($"{packageKind} exceeds the expanded size limit of {limits.MaxExpandedBytes} bytes.");
            }

            await WriteAsync(destination, buffer.AsMemory(0, read)).ConfigureAwait(false);
        }

        if (entryBytes != entry.Length)
        {
            throw new InvalidOperationException($"{packageKind} entry '{entry.FullName}' expanded to an unexpected size.");
        }
    }

    public string ResolveDestination(string root, string relativePath)
    {
        ThrowIfUnavailable();
        string fullRoot = Path.GetFullPath(root);
        string fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        string rootWithSeparator = Path.EndsInDirectorySeparator(fullRoot)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!fullPath.StartsWith(rootWithSeparator, comparison))
        {
            throw new InvalidOperationException($"Archive path '{relativePath}' escapes the extraction root.");
        }

        return fullPath;
    }

    public static string NormalizeEntryPath(string fullName)
        => fullName.Replace('\\', '/').TrimEnd('/');

    public void Dispose()
    {
        processingCancellation.Dispose();
        stopwatch.Stop();
    }

    private static string NormalizeAndValidatePath(string fullName, string packageKind)
    {
        string normalized = NormalizeEntryPath(fullName);
        string[] segments = normalized.Split('/');
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized[0] == '/'
            || normalized.Contains(':', StringComparison.Ordinal)
            || segments.Any(static segment => string.IsNullOrWhiteSpace(segment) || segment is "." or "..")
            || (OperatingSystem.IsWindows() && segments.Any(static segment => HasWindowsTrailingDotOrSpace(segment) || WorkspaceAttachmentUploadPolicy.IsReservedWindowsDeviceName(segment))))
        {
            throw new InvalidOperationException($"Invalid archive entry path '{fullName}' in {packageKind}.");
        }

        return normalized;
    }

    private static bool HasWindowsTrailingDotOrSpace(string segment)
    {
        string trimmed = segment.TrimEnd(' ', '.');
        return trimmed.Length != segment.Length;
    }

    private static void ValidateNotLink(ZipArchiveEntry entry, string packageKind)
    {
        const int unixFileTypeMask = 0xF000;
        const int unixSymlink = 0xA000;
        const int dosReparsePoint = 0x0400;
        int unixMode = (entry.ExternalAttributes >> 16) & unixFileTypeMask;
        int dosAttributes = entry.ExternalAttributes & 0xFFFF;
        if (unixMode == unixSymlink || (dosAttributes & dosReparsePoint) != 0)
        {
            throw new InvalidOperationException($"Symlink entry '{entry.FullName}' is not allowed in {packageKind}.");
        }
    }

    private async ValueTask<int> ReadAsync(Stream source, byte[] buffer)
    {
        ThrowIfUnavailable();
        try
        {
            return await source.ReadAsync(buffer.AsMemory(), processingCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!callerCancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Archive processing exceeded the time limit of {limits.MaxProcessingTime}.");
        }
    }

    private async ValueTask WriteAsync(Stream destination, ReadOnlyMemory<byte> buffer)
    {
        ThrowIfUnavailable();
        try
        {
            await destination.WriteAsync(buffer, processingCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!callerCancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Archive processing exceeded the time limit of {limits.MaxProcessingTime}.");
        }
    }

    private void ThrowIfUnavailable()
    {
        if (callerCancellationToken.IsCancellationRequested)
        {
            callerCancellationToken.ThrowIfCancellationRequested();
        }

        if (processingCancellation.IsCancellationRequested || stopwatch.Elapsed > limits.MaxProcessingTime)
        {
            throw new InvalidOperationException($"Archive processing exceeded the time limit of {limits.MaxProcessingTime}.");
        }
    }
}
