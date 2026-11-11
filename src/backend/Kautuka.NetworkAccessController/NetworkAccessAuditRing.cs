using System.Text.Json;
using System.Threading.Channels;

namespace Kautuka.NetworkAccessController;

/// <summary>
/// Bounded in-memory activity ring (last 500 per user + 500 global) and rotated JSONL audit log
/// (rotated at 10 MB, 5 files kept) for network access decisions.
/// All file persistence is handled asynchronously via a background Channel writer so the
/// decision hot path never blocks on disk I/O.
/// </summary>
public sealed class NetworkAccessAuditRing : IAsyncDisposable, IDisposable
{
    private const int maxEntriesPerBucket = 500;
    private const long defaultMaxFileBytes = 10 * 1024 * 1024;
    private const int maxRotatedFiles = 5;

    private readonly object gate = new();
    private readonly LinkedList<NetworkAccessActivityEntry> globalEntries = new();
    private readonly Dictionary<string, LinkedList<NetworkAccessActivityEntry>> entriesByUser = new(StringComparer.Ordinal);
    private readonly string? logFilePath;
    private readonly long maxFileBytes;
    private readonly Channel<NetworkAccessActivityEntry>? asyncWriterChannel;
    private readonly Task? asyncWriterTask;
    private readonly CancellationTokenSource cts = new();

    public NetworkAccessAuditRing(string? logFilePath = null, long maxFileBytes = defaultMaxFileBytes)
    {
        this.logFilePath = string.IsNullOrWhiteSpace(logFilePath) ? null : Path.GetFullPath(logFilePath);
        this.maxFileBytes = maxFileBytes > 0 ? maxFileBytes : defaultMaxFileBytes;

        if (this.logFilePath is not null)
        {
            var options = new BoundedChannelOptions(10000)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            };
            asyncWriterChannel = Channel.CreateBounded<NetworkAccessActivityEntry>(options);
            asyncWriterTask = Task.Run(() => ProcessAsyncWrites(asyncWriterChannel.Reader, cts.Token));
        }
    }

    public void Record(NetworkAccessActivityEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (gate)
        {
            AppendBounded(globalEntries, entry);
            if (!string.IsNullOrWhiteSpace(entry.UserId))
            {
                if (!entriesByUser.TryGetValue(entry.UserId, out LinkedList<NetworkAccessActivityEntry>? userList))
                {
                    userList = new LinkedList<NetworkAccessActivityEntry>();
                    entriesByUser[entry.UserId] = userList;
                }

                AppendBounded(userList, entry);
            }
        }

        // Asynchronous non-blocking file append
        asyncWriterChannel?.Writer.TryWrite(entry);
    }

    public void RecordPrivilegeChange(string actorUserId, string targetUserId, bool canApproveOwn, bool canAllowAllAgents)
    {
        Record(new NetworkAccessActivityEntry(
            TimestampUtc: DateTimeOffset.UtcNow,
            SourceIp: null,
            UserId: targetUserId,
            AgentId: null,
            ConnectorId: null,
            Destination: "user-privileges:" + targetUserId,
            Port: 0,
            Decision: "privilege_updated",
            Reason: $"canApproveOwn={canApproveOwn},canAllowAllAgents={canAllowAllAgents};actor={actorUserId}",
            Cluster: null,
            OpenInternetBadge: false,
            CompatibilityMode: false));
    }

    public void RecordBrakeChange(string actorUserId, string scope, string? targetUserId, bool on)
    {
        string dest = scope == "system" ? "brake:system" : "brake:user:" + (targetUserId ?? string.Empty);
        Record(new NetworkAccessActivityEntry(
            TimestampUtc: DateTimeOffset.UtcNow,
            SourceIp: null,
            UserId: targetUserId ?? "system",
            AgentId: null,
            ConnectorId: null,
            Destination: dest,
            Port: 0,
            Decision: on ? "brake_engaged" : "brake_released",
            Reason: $"scope={scope};actor={actorUserId}",
            Cluster: null,
            OpenInternetBadge: false,
            CompatibilityMode: false));
    }

    public IReadOnlyList<NetworkAccessActivityEntry> List(NetworkAccessActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (string.IsNullOrWhiteSpace(actor.UserId))
        {
            throw new ArgumentException("Actor userId is required.", nameof(actor));
        }

        lock (gate)
        {
            if (actor.IsAdmin)
            {
                return [.. globalEntries.Reverse()];
            }

            if (!entriesByUser.TryGetValue(actor.UserId, out LinkedList<NetworkAccessActivityEntry>? userList))
            {
                return [];
            }

            return [.. userList.Where(static e => e.UserId != "system").Reverse()];
        }
    }

    public async Task FlushAsync()
    {
        if (asyncWriterChannel is null || asyncWriterTask is null)
        {
            return;
        }

        // Give background queue up to 500ms to drain
        for (int i = 0; i < 50; i++)
        {
            if (asyncWriterChannel.Reader.Count == 0)
            {
                break;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (asyncWriterChannel is not null)
        {
            asyncWriterChannel.Writer.TryComplete();
            try
            {
                if (asyncWriterTask is not null)
                {
                    await asyncWriterTask.ConfigureAwait(false);
                }
            }
            catch
            {
                // Suppress background writer termination errors
            }
        }

        await cts.CancelAsync().ConfigureAwait(false);
        cts.Dispose();
    }

    public void Dispose()
    {
        if (asyncWriterChannel is not null)
        {
            asyncWriterChannel.Writer.TryComplete();
            try
            {
                asyncWriterTask?.Wait(TimeSpan.FromSeconds(1));
            }
            catch
            {
            }
        }

        cts.Cancel();
        cts.Dispose();
    }

    private static void AppendBounded(LinkedList<NetworkAccessActivityEntry> list, NetworkAccessActivityEntry entry)
    {
        list.AddLast(entry);
        while (list.Count > maxEntriesPerBucket)
        {
            list.RemoveFirst();
        }
    }

    private async Task ProcessAsyncWrites(ChannelReader<NetworkAccessActivityEntry> reader, CancellationToken token)
    {
        try
        {
            while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (reader.TryRead(out NetworkAccessActivityEntry? entry))
                {
                    if (entry is not null)
                    {
                        TryWriteJsonLine(entry);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Drain remaining
            while (reader.TryRead(out NetworkAccessActivityEntry? entry))
            {
                if (entry is not null)
                {
                    TryWriteJsonLine(entry);
                }
            }
        }
    }

    private void TryWriteJsonLine(NetworkAccessActivityEntry entry)
    {
        try
        {
            string dir = Path.GetDirectoryName(logFilePath!)!;
            Directory.CreateDirectory(dir);
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            RotateIfNeeded();
            byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(entry, NetworkAccessJsonContext.Default.NetworkAccessActivityEntry);
            using var stream = new FileStream(logFilePath!, FileMode.Append, FileAccess.Write, FileShare.Read);
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(logFilePath!, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            stream.Write(jsonBytes);
            stream.WriteByte((byte)'\n');
        }
        catch (IOException)
        {
            // Decision path never fails on audit log disk trouble.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(logFilePath!))
        {
            return;
        }

        var info = new FileInfo(logFilePath!);
        if (info.Length < maxFileBytes)
        {
            return;
        }

        for (int i = maxRotatedFiles - 1; i >= 1; i--)
        {
            string source = i == 1 ? logFilePath! : $"{logFilePath}.{i - 1}";
            string target = $"{logFilePath}.{i}";
            if (File.Exists(source))
            {
                File.Move(source, target, overwrite: true);
            }
        }
    }
}
