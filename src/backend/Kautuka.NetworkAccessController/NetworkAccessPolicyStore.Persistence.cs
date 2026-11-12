using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;

namespace Kautuka.NetworkAccessController;

public sealed partial class NetworkAccessPolicyStore : IAsyncDisposable, IDisposable
{
    public const int MaxPendingRequests = 500;
    public const int MaxPendingProbeRequestsPerContainer = 64;
    public const int MaxRules = 10000;
    private static readonly TimeSpan resolvedRequestRetention = TimeSpan.FromDays(30);

    private readonly object gate = new();
    private readonly object diskWriteLock = new();
    private long lastRequestActivityWatermark = -1;
    private readonly string path;
    private readonly Action<string> syncDirectory;
    private readonly Action<string>? beforeRenameHook;
    private readonly Action? beforeBackgroundDiskLockHook;
    private readonly Action? beforeBackgroundDrainHook;
    private readonly IReadOnlyList<NetworkAccessRule> seedRules;
    private readonly NetworkAccessAuditRing? auditRing;

    private NetworkAccessPolicyState? state;
    private long diskWriteCount;
    private long backgroundWriteErrorCount;
    private long inMemoryMutationSeq;
    private long lastWrittenMutationSeq;
    private int pendingWriteRequested;
    private int backgroundWriteInProgress;

    // Background coalescing writer for pending requests (v7-10)
    private readonly Channel<byte>? pendingRequestWriterChannel;
    private readonly Task? pendingRequestWriterTask;
    private readonly CancellationTokenSource cts = new();

    public NetworkAccessPolicyStore(
        string path,
        IReadOnlyList<NetworkAccessRule>? seedRules = null,
        NetworkAccessAuditRing? auditRing = null)
        : this(path, FlushDirectory, beforeRenameHook: null, seedRules, auditRing)
    {
    }

    internal NetworkAccessPolicyStore(
        string path,
        Action<string> syncDirectory,
        Action<string>? beforeRenameHook = null,
        IReadOnlyList<NetworkAccessRule>? seedRules = null,
        NetworkAccessAuditRing? auditRing = null,
        Action? beforeBackgroundDiskLockHook = null,
        Action? beforeBackgroundDrainHook = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(syncDirectory);
        this.path = Path.GetFullPath(path);
        this.syncDirectory = syncDirectory;
        this.beforeRenameHook = beforeRenameHook;
        this.beforeBackgroundDiskLockHook = beforeBackgroundDiskLockHook;
        this.beforeBackgroundDrainHook = beforeBackgroundDrainHook;
        this.seedRules = seedRules ?? NetworkAccessDefaultSeeds.DefaultRules;
        this.auditRing = auditRing;

        // Bounded channel of size 1 with DropOldest for waking the coalescing writer.
        var channelOptions = new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        };
        pendingRequestWriterChannel = Channel.CreateBounded<byte>(channelOptions);
        pendingRequestWriterTask = Task.Run(() => ProcessPendingRequestWrites(pendingRequestWriterChannel.Reader, cts.Token));
    }

    public long DiskWriteCount => Interlocked.Read(ref diskWriteCount);

    public long BackgroundWriteErrorCount => Interlocked.Read(ref backgroundWriteErrorCount);

    public IReadOnlyList<NetworkAccessRule> SeedRules => seedRules;

    public void EnsureLoaded()
    {
        lock (gate)
        {
            _ = Load();
        }
    }

    public NetworkAccessPolicyState GetRawStateSnapshot()
    {
        lock (gate)
        {
            return Load();
        }
    }

    public async Task FlushPendingWritesAsync()
    {
        if (pendingRequestWriterChannel is not null)
        {
            for (int i = 0; i < 200; i++)
            {
                if (pendingRequestWriterChannel.Reader.Count == 0
                    && Volatile.Read(ref pendingWriteRequested) == 0
                    && Volatile.Read(ref backgroundWriteInProgress) == 0)
                {
                    break;
                }

                await Task.Delay(10).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (pendingRequestWriterChannel is not null)
        {
            pendingRequestWriterChannel.Writer.TryComplete();
            try
            {
                if (pendingRequestWriterTask is not null)
                {
                    await pendingRequestWriterTask.ConfigureAwait(false);
                }
            }
            catch
            {
            }
        }

        await cts.CancelAsync().ConfigureAwait(false);
        cts.Dispose();
    }

    public void Dispose()
    {
        if (pendingRequestWriterChannel is not null)
        {
            pendingRequestWriterChannel.Writer.TryComplete();
            try
            {
                pendingRequestWriterTask?.Wait(TimeSpan.FromSeconds(1));
            }
            catch
            {
            }
        }

        cts.Cancel();
        cts.Dispose();
    }

    private void EnqueuePendingRequestSave(NetworkAccessPolicyState next)
    {
        state = next;
        inMemoryMutationSeq++;
        Volatile.Write(ref pendingWriteRequested, 1);
        pendingRequestWriterChannel?.Writer.TryWrite(0);
    }

    private async Task ProcessPendingRequestWrites(ChannelReader<byte> reader, CancellationToken token)
    {
        try
        {
            while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (reader.TryRead(out _))
                {
                }

                DrainPendingWrites(token);
            }
        }
        catch (OperationCanceledException)
        {
            while (reader.TryRead(out _))
            {
            }

            if (Volatile.Read(ref pendingWriteRequested) != 0)
            {
                try
                {
                    PersistLatestSnapshotIfNewer();
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref backgroundWriteErrorCount);
                }
            }
        }
    }

    private void DrainPendingWrites(CancellationToken token)
    {
        beforeBackgroundDrainHook?.Invoke();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Exchange(ref backgroundWriteInProgress, 1);
            if (Interlocked.Exchange(ref pendingWriteRequested, 0) == 0)
            {
                Interlocked.Exchange(ref backgroundWriteInProgress, 0);
                if (Volatile.Read(ref pendingWriteRequested) == 1)
                {
                    continue;
                }

                break;
            }

            try
            {
                PersistLatestSnapshotIfNewer();
            }
            catch (Exception)
            {
                Interlocked.Increment(ref backgroundWriteErrorCount);
            }
            finally
            {
                Interlocked.Exchange(ref backgroundWriteInProgress, 0);
            }

            if (Volatile.Read(ref pendingWriteRequested) == 0)
            {
                break;
            }
        }
    }

    private void PersistLatestSnapshotIfNewer()
    {
        NetworkAccessPolicyState latestSnapshot;
        long seq;
        lock (gate)
        {
            latestSnapshot = Load();
            seq = inMemoryMutationSeq;
        }

        // Allow tests to pause after snapshot capture and before disk lock acquisition
        // so SaveSync concurrency ordering can be exercised deterministically.
        beforeBackgroundDiskLockHook?.Invoke();

        lock (diskWriteLock)
        {
            if (seq > lastWrittenMutationSeq)
            {
                WriteStateToDisk(latestSnapshot, syncDir: false);
                lastWrittenMutationSeq = seq;
            }
        }
    }

    private NetworkAccessPolicyState Load()
    {
        if (state is not null)
        {
            return state;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (FileNotFoundException)
        {
            return state = new NetworkAccessPolicyState(1, 0, [], [], [], BrakeState.Empty);
        }
        catch (DirectoryNotFoundException)
        {
            return state = new NetworkAccessPolicyState(1, 0, [], [], [], BrakeState.Empty);
        }

        if (bytes.Length == 0)
        {
            return state = new NetworkAccessPolicyState(1, 0, [], [], [], BrakeState.Empty);
        }

        try
        {
            NetworkAccessPolicyState loaded = JsonSerializer.Deserialize(bytes, NetworkAccessJsonContext.Default.NetworkAccessPolicyState)
                ?? throw new InvalidDataException($"Corrupt state file: deserialization returned null for {path}.");

            return state = loaded;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Corrupt state file at {path}: {ex.Message}", ex);
        }
    }

    private void SaveSync(NetworkAccessPolicyState next)
    {
        state = next;
        long seq = ++inMemoryMutationSeq;
        lock (diskWriteLock)
        {
            if (seq > lastWrittenMutationSeq)
            {
                WriteStateToDisk(next, syncDir: true);
                lastWrittenMutationSeq = seq;
            }
        }
    }

    private void WriteStateToDisk(NetworkAccessPolicyState next, bool syncDir)
    {
        string dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        string tempPath = Path.Combine(dir, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                if (OperatingSystem.IsLinux())
                {
                    File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                JsonSerializer.Serialize(stream, next, NetworkAccessJsonContext.Default.NetworkAccessPolicyState);
                stream.Flush(flushToDisk: true);
            }

            beforeRenameHook?.Invoke(tempPath);
            for (int retry = 0; retry < 10; retry++)
            {
                try
                {
                    File.Move(tempPath, path, overwrite: true);
                    break;
                }
                catch (Exception ex) when (retry < 9 && (ex is IOException || ex is UnauthorizedAccessException))
                {
                    Thread.Sleep(10 * (retry + 1));
                }
            }

            if (syncDir)
            {
                syncDirectory(dir);
            }

            Interlocked.Increment(ref diskWriteCount);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                }
            }
        }
    }

    internal static void FlushDirectory(string directory)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        int handle = open(directory, 0x10000 | 0x80000); // O_RDONLY | O_DIRECTORY | O_CLOEXEC
        if (handle < 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not open network policy directory for durability.");
        }

        try
        {
            if (fsync(handle) != 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not persist network policy directory.");
            }
        }
        finally
        {
            _ = close(handle);
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", SetLastError = true)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA2101", Justification = "Called only on Linux; libc open requires an explicitly marshaled UTF-8 pathname.")]
    private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", SetLastError = true)]
    private static extern int fsync(int handle);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", SetLastError = true)]
    private static extern int close(int handle);
}
