using System.Diagnostics;
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
    private static readonly TimeSpan defaultEnqueueWaitTimeout = TimeSpan.FromMilliseconds(5);

    private readonly object gate = new();
    private readonly LinkedList<NetworkAccessActivityEntry> globalEntries = new();
    private readonly Dictionary<string, LinkedList<NetworkAccessActivityEntry>> entriesByUser = new(StringComparer.Ordinal);
    private readonly string? logFilePath;
    private readonly long maxFileBytes;
    private readonly Channel<NetworkAccessActivityEntry>? asyncWriterChannel;
    private readonly Task? asyncWriterTask;
    private readonly Action<NetworkAccessActivityEntry>? writeEntry;
    private long pendingAuditLossCount;
    private DateTimeOffset? pendingAuditLossFrom;
    private DateTimeOffset? pendingAuditLossTo;
    private bool writerCompleting;
    private bool isDisposed;
    private int pendingEnqueueAttempts;
    private TaskCompletionSource producersDrained = CompletedSignal();

    public NetworkAccessAuditRing(string? logFilePath = null, long maxFileBytes = defaultMaxFileBytes)
        : this(logFilePath, maxFileBytes, 10000, null)
    {
    }

    internal NetworkAccessAuditRing(
        string? logFilePath,
        long maxFileBytes,
        int queueCapacity,
        Action<NetworkAccessActivityEntry>? writeEntry)
    {
        this.logFilePath = string.IsNullOrWhiteSpace(logFilePath) ? null : Path.GetFullPath(logFilePath);
        this.maxFileBytes = maxFileBytes > 0 ? maxFileBytes : defaultMaxFileBytes;
        this.writeEntry = writeEntry;

        if (this.logFilePath is not null)
        {
            var options = new BoundedChannelOptions(queueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            };
            asyncWriterChannel = Channel.CreateBounded<NetworkAccessActivityEntry>(options);
            asyncWriterTask = Task.Run(() => ProcessAsyncWrites(asyncWriterChannel.Reader));
        }
    }

    public void Record(NetworkAccessActivityEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        bool waitForCapacity = false;
        lock (gate)
        {
            if (isDisposed)
            {
                return;
            }

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

            if (asyncWriterChannel is not null && !writerCompleting)
            {
                if (pendingAuditLossCount > 0)
                {
                    NetworkAccessActivityEntry loss = CreateAuditLossEntry();
                    if (!asyncWriterChannel.Writer.TryWrite(loss))
                    {
                        RegisterPendingEnqueueAttempt();
                        waitForCapacity = true;
                    }
                    else
                    {
                        ClearPendingAuditLoss();
                    }
                }

                if (!waitForCapacity && !asyncWriterChannel.Writer.TryWrite(entry))
                {
                    RegisterPendingEnqueueAttempt();
                    waitForCapacity = true;
                }
            }
        }

        if (waitForCapacity)
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                while (true)
                {
                    bool canWrite = WaitForCapacityWithShortTimeout(started);
                    if (!canWrite)
                    {
                        lock (gate)
                        {
                            NoteAuditLoss(entry.TimestampUtc);
                        }

                        break;
                    }

                    bool retry = false;
                    lock (gate)
                    {
                        if (writerCompleting)
                        {
                            NoteAuditLoss(entry.TimestampUtc);
                            break;
                        }

                        if (pendingAuditLossCount > 0)
                        {
                            NetworkAccessActivityEntry loss = CreateAuditLossEntry();
                            if (asyncWriterChannel!.Writer.TryWrite(loss))
                            {
                                ClearPendingAuditLoss();
                            }
                            else
                            {
                                retry = true;
                            }
                        }

                        if (!retry && !asyncWriterChannel!.Writer.TryWrite(entry))
                        {
                            retry = true;
                        }
                    }

                    if (!retry)
                    {
                        break;
                    }
                }
            }
            finally
            {
                lock (gate)
                {
                    CompletePendingEnqueueAttempt();
                }
            }
        }
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
            lock (gate)
            {
                isDisposed = true;
            }

            await WaitForPendingEnqueueAttemptsAsync().ConfigureAwait(false);
            lock (gate)
            {
                writerCompleting = true;
                asyncWriterChannel.Writer.TryComplete();
            }
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
        else
        {
            lock (gate)
            {
                isDisposed = true;
            }
        }

    }

    public void Dispose()
    {
        if (asyncWriterChannel is not null)
        {
            lock (gate)
            {
                isDisposed = true;
            }

            WaitForPendingEnqueueAttempts();
            lock (gate)
            {
                writerCompleting = true;
                asyncWriterChannel.Writer.TryComplete();
            }
            try
            {
                asyncWriterTask?.Wait(TimeSpan.FromSeconds(1));
            }
            catch
            {
            }
        }
        else
        {
            lock (gate)
            {
                isDisposed = true;
            }
        }

    }

    private static void AppendBounded(LinkedList<NetworkAccessActivityEntry> list, NetworkAccessActivityEntry entry)
    {
        list.AddLast(entry);
        while (list.Count > maxEntriesPerBucket)
        {
            list.RemoveFirst();
        }
    }

    private bool WaitForCapacityWithShortTimeout(long started)
    {
        ChannelWriter<NetworkAccessActivityEntry> writer = asyncWriterChannel!.Writer;
        try
        {
            TimeSpan remaining = defaultEnqueueWaitTimeout - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            using var timeout = new CancellationTokenSource(remaining);
            return writer.WaitToWriteAsync(timeout.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (ChannelClosedException)
        {
            return false;
        }
    }

    private void RegisterPendingEnqueueAttempt()
    {
        if (pendingEnqueueAttempts++ == 0)
        {
            producersDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private void CompletePendingEnqueueAttempt()
    {
        if (--pendingEnqueueAttempts == 0)
        {
            producersDrained.TrySetResult();
        }
    }

    private void WaitForPendingEnqueueAttempts()
    {
        while (true)
        {
            Task waitForProducers;
            lock (gate)
            {
                if (pendingEnqueueAttempts == 0)
                {
                    return;
                }

                waitForProducers = producersDrained.Task;
            }

            waitForProducers.GetAwaiter().GetResult();
        }
    }

    private async Task WaitForPendingEnqueueAttemptsAsync()
    {
        while (true)
        {
            Task waitForProducers;
            lock (gate)
            {
                if (pendingEnqueueAttempts == 0)
                {
                    return;
                }

                waitForProducers = producersDrained.Task;
            }

            await waitForProducers.ConfigureAwait(false);
        }
    }

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }

    private void NoteAuditLoss(DateTimeOffset timestamp)
    {
        pendingAuditLossCount++;
        pendingAuditLossFrom = pendingAuditLossFrom is null || timestamp < pendingAuditLossFrom
            ? timestamp
            : pendingAuditLossFrom;
        pendingAuditLossTo = pendingAuditLossTo is null || timestamp > pendingAuditLossTo
            ? timestamp
            : pendingAuditLossTo;

        if (!writerCompleting && asyncWriterChannel is not null)
        {
            NetworkAccessActivityEntry loss = CreateAuditLossEntry();
            if (asyncWriterChannel.Writer.TryWrite(loss))
            {
                ClearPendingAuditLoss();
            }
        }
    }

    private NetworkAccessActivityEntry CreateAuditLossEntry()
        => new(
            TimestampUtc: DateTimeOffset.UtcNow,
            SourceIp: null,
            UserId: "system",
            AgentId: null,
            ConnectorId: null,
            Destination: "network-access-audit",
            Port: 0,
            Decision: "audit_loss",
            Reason: $"count={pendingAuditLossCount};from={pendingAuditLossFrom:O};to={pendingAuditLossTo:O}",
            Cluster: null,
            OpenInternetBadge: false,
            CompatibilityMode: false);

    private void ClearPendingAuditLoss()
    {
        pendingAuditLossCount = 0;
        pendingAuditLossFrom = null;
        pendingAuditLossTo = null;
    }

    private async Task ProcessAsyncWrites(ChannelReader<NetworkAccessActivityEntry> reader)
    {
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out NetworkAccessActivityEntry? entry))
            {
                if (entry is not null)
                {
                    WriteEntry(entry);
                    TryEnqueuePendingLossFromConsumer();
                }
            }
        }

        NetworkAccessActivityEntry? pendingLoss = null;
        lock (gate)
        {
            if (pendingAuditLossCount > 0)
            {
                pendingLoss = CreateAuditLossEntry();
                ClearPendingAuditLoss();
            }
        }

        if (pendingLoss is not null)
        {
            WriteEntry(pendingLoss);
        }
    }

    private void WriteEntry(NetworkAccessActivityEntry entry)
    {
        if (writeEntry is not null)
        {
            writeEntry(entry);
        }

        TryWriteJsonLine(entry);
    }

    private void TryEnqueuePendingLossFromConsumer()
    {
        lock (gate)
        {
            if (writerCompleting || pendingAuditLossCount == 0)
            {
                return;
            }

            NetworkAccessActivityEntry loss = CreateAuditLossEntry();
            if (asyncWriterChannel!.Writer.TryWrite(loss))
            {
                ClearPendingAuditLoss();
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
