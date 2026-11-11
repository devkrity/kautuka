using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;

namespace Kautuka.NetworkAccessController;

public sealed partial class NetworkAccessPolicyStore : IAsyncDisposable, IDisposable
{
    public const int MaxPendingRequests = 500;
    public const int MaxRules = 10000;
    private static readonly TimeSpan resolvedRequestRetention = TimeSpan.FromDays(30);

    private readonly object gate = new();
    private readonly object diskWriteLock = new();
    private long lastRequestActivityWatermark = -1;
    private readonly string path;
    private readonly Action<string> syncDirectory;
    private readonly Action<string>? beforeRenameHook;
    private readonly IReadOnlyList<NetworkAccessRule> seedRules;
    private readonly NetworkAccessAuditRing? auditRing;

    private NetworkAccessPolicyState? state;
    private long diskWriteCount;
    private int backgroundWritesPendingOrActive;

    // Background coalescing writer for pending requests (v7-10)
    private readonly Channel<NetworkAccessPolicyState>? pendingRequestWriterChannel;
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
        NetworkAccessAuditRing? auditRing = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(syncDirectory);
        this.path = Path.GetFullPath(path);
        this.syncDirectory = syncDirectory;
        this.beforeRenameHook = beforeRenameHook;
        this.seedRules = seedRules ?? NetworkAccessDefaultSeeds.DefaultRules;
        this.auditRing = auditRing;

        // Bounded channel of size 1 with DropOldest for coalescing pending request writes
        var channelOptions = new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        };
        pendingRequestWriterChannel = Channel.CreateBounded<NetworkAccessPolicyState>(channelOptions);
        pendingRequestWriterTask = Task.Run(() => ProcessPendingRequestWrites(pendingRequestWriterChannel.Reader, cts.Token));
    }

    public long DiskWriteCount => Interlocked.Read(ref diskWriteCount);

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
                if (pendingRequestWriterChannel.Reader.Count == 0 && Volatile.Read(ref backgroundWritesPendingOrActive) == 0)
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
        Interlocked.Increment(ref backgroundWritesPendingOrActive);
        if (pendingRequestWriterChannel?.Writer.TryWrite(next) != true)
        {
            Interlocked.Decrement(ref backgroundWritesPendingOrActive);
        }
    }

    private async Task ProcessPendingRequestWrites(ChannelReader<NetworkAccessPolicyState> reader, CancellationToken token)
    {
        try
        {
            while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                int drained = 0;
                while (reader.TryRead(out NetworkAccessPolicyState? _))
                {
                    drained++;
                }

                try
                {
                    NetworkAccessPolicyState latestSnapshot;
                    lock (gate)
                    {
                        latestSnapshot = Load();
                    }

                    lock (diskWriteLock)
                    {
                        WriteStateToDisk(latestSnapshot, syncDir: false);
                    }
                }
                finally
                {
                    if (drained > 0)
                    {
                        Interlocked.Add(ref backgroundWritesPendingOrActive, -drained);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            int drained = 0;
            while (reader.TryRead(out NetworkAccessPolicyState? _))
            {
                drained++;
            }

            try
            {
                NetworkAccessPolicyState finalSnapshot;
                lock (gate)
                {
                    finalSnapshot = Load();
                }

                lock (diskWriteLock)
                {
                    WriteStateToDisk(finalSnapshot, syncDir: false);
                }
            }
            finally
            {
                if (drained > 0)
                {
                    Interlocked.Add(ref backgroundWritesPendingOrActive, -drained);
                }
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
        lock (diskWriteLock)
        {
            WriteStateToDisk(next, syncDir: true);
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
