using System.IO.Compression;

using Kautuka.Gateway.Domain.Packages;
using Kautuka.Gateway.Domain.Skills;

using Microsoft.Extensions.Configuration;

namespace Kautuka.IntegrationTests;

public sealed class PackageIngestionBoundaryTests
{
    [Fact]
    public async Task CopyUploadRejectsCompressedPayloadBeyondLimit()
    {
        ArchiveIngestionLimits limits = CreateLimits(maxCompressedBytes: 32);
        using var ingestion = new BoundedZipArchive(limits, TestContext.Current.CancellationToken);
        await using var source = new MemoryStream(new byte[64]);
        await using var destination = new MemoryStream();

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ingestion.CopyUploadAsync(source, destination));

        Assert.Contains("compressed size limit", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateRejectsExcessiveEntries()
    {
        using MemoryStream stream = CreateArchive(archive =>
        {
            archive.CreateEntry("one.txt");
            archive.CreateEntry("two.txt");
            archive.CreateEntry("three.txt");
        });
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        using var ingestion = new BoundedZipArchive(CreateLimits(maxEntries: 2), TestContext.Current.CancellationToken);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ingestion.Validate(archive, "test package"));

        Assert.Contains("entries", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateRejectsCompressionBomb()
    {
        using MemoryStream stream = CreateArchive(archive =>
        {
            ZipArchiveEntry entry = archive.CreateEntry("zeros.bin", CompressionLevel.SmallestSize);
            using Stream output = entry.Open();
            output.Write(new byte[64 * 1024]);
        });
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        using var ingestion = new BoundedZipArchive(CreateLimits(maxCompressionRatio: 20), TestContext.Current.CancellationToken);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ingestion.Validate(archive, "test package"));

        Assert.Contains("compression ratio", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("folder/../../escape.txt")]
    [InlineData("C:/escape.txt")]
    [InlineData("/absolute.txt")]
    public void ValidateRejectsTraversalAndAbsolutePaths(string path)
    {
        using MemoryStream stream = CreateArchive(archive => archive.CreateEntry(path));
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        using var ingestion = new BoundedZipArchive(CreateLimits(), TestContext.Current.CancellationToken);

        Assert.Throws<InvalidOperationException>(() => ingestion.Validate(archive, "test package"));
    }

    [Theory]
    [InlineData("NUL.txt")]
    [InlineData("folder/COM1")]
    [InlineData("LPT1.txt")]
    public void ValidateRejectsWindowsDeviceNames(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using MemoryStream stream = CreateArchive(archive => archive.CreateEntry(path));
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        using var ingestion = new BoundedZipArchive(CreateLimits(), TestContext.Current.CancellationToken);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ingestion.Validate(archive, "test package"));

        Assert.Contains("Invalid archive entry path", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("notes.txt.")]
    [InlineData("folder/notes.txt ")]
    public void ValidateRejectsWindowsTrailingDotOrSpace(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using MemoryStream stream = CreateArchive(archive => archive.CreateEntry(path));
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        using var ingestion = new BoundedZipArchive(CreateLimits(), TestContext.Current.CancellationToken);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ingestion.Validate(archive, "test package"));

        Assert.Contains("Invalid archive entry path", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateAcceptsAnOrdinaryFileName()
    {
        using MemoryStream stream = CreateArchive(archive => archive.CreateEntry("notes.txt"));
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        using var ingestion = new BoundedZipArchive(CreateLimits(), TestContext.Current.CancellationToken);

        IReadOnlyList<ZipArchiveEntry> files = ingestion.Validate(archive, "test package");

        Assert.Equal("notes.txt", Assert.Single(files).FullName);
    }

    [Fact]
    public void ValidateRejectsSymlink()
    {
        using MemoryStream stream = CreateArchive(archive =>
        {
            ZipArchiveEntry entry = archive.CreateEntry("link");
            entry.ExternalAttributes = 0xA000 << 16;
        });
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        using var ingestion = new BoundedZipArchive(CreateLimits(), TestContext.Current.CancellationToken);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ingestion.Validate(archive, "test package"));

        Assert.Contains("Symlink", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CopyUploadEnforcesProcessingDeadline()
    {
        using var ingestion = new BoundedZipArchive(
            CreateLimits(maxProcessingTime: TimeSpan.FromMilliseconds(25)),
            TestContext.Current.CancellationToken);
        await using var source = new BlockingReadStream();
        await using var destination = new MemoryStream();

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ingestion.CopyUploadAsync(source, destination));

        Assert.Contains("time limit", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SkillImportCancellationRemovesPartialExtractionDirectory()
    {
        string testRoot = Path.Combine(Path.GetTempPath(), "kautuka-package-boundary-tests", Guid.NewGuid().ToString("N"));
        string stateRoot = Path.Combine(testRoot, "state");
        string temporaryRoot = Path.Combine(testRoot, "temp");
        Directory.CreateDirectory(temporaryRoot);

        try
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Kautuka:ContainerStateRoot"] = stateRoot,
                })
                .Build();
            var database = new KautukaDatabase(configuration);
            database.EnsureReady();
            var service = new SkillPackageService(database, temporaryRoot);
            await using var archive = new MemoryStream(new byte[128]);
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ValidateImportOutcomeAsync(
                archive,
                new SkillPackageImportRequest("cancelled.zip", null),
                cancellation.Token));

            Assert.Empty(Directory.EnumerateFileSystemEntries(temporaryRoot));
        }
        finally
        {
            SqliteTestPoolCleanup.ClearPoolsUnderDirectory(testRoot);
            if (Directory.Exists(testRoot))
            {
                await TestDirectoryCleanup.DeleteDirectoryWithRetryAsync(testRoot).ConfigureAwait(false);
            }
        }
    }

    private static ArchiveIngestionLimits CreateLimits(
        long maxCompressedBytes = 1024 * 1024,
        int maxEntries = 32,
        long maxEntryBytes = 128 * 1024,
        long maxExpandedBytes = 256 * 1024,
        double maxCompressionRatio = 100,
        TimeSpan? maxProcessingTime = null)
        => new(
            maxCompressedBytes,
            maxEntries,
            maxEntryBytes,
            maxExpandedBytes,
            maxCompressionRatio,
            maxProcessingTime ?? TimeSpan.FromSeconds(5));

    private static MemoryStream CreateArchive(Action<ZipArchive> configure)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            configure(archive);
        }

        stream.Position = 0;
        return stream;
    }

    private sealed class BlockingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
