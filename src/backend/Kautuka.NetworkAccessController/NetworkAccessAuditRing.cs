using System.Text.Json;

namespace Kautuka.NetworkAccessController;

/// <summary>
/// Bounded in-memory activity ring (last 500 per user + 500 global) and rotated JSONL audit log
/// (rotated at 10 MB, 5 files kept) for network access decisions.
/// </summary>
public sealed class NetworkAccessAuditRing
{
    private const int maxEntriesPerBucket = 500;
    private const long defaultMaxFileBytes = 10 * 1024 * 1024;
    private const int maxRotatedFiles = 5;

    private readonly object gate = new();
    private readonly LinkedList<NetworkAccessActivityEntry> globalEntries = new();
    private readonly Dictionary<string, LinkedList<NetworkAccessActivityEntry>> entriesByUser = new(StringComparer.Ordinal);
    private readonly string? logFilePath;
    private readonly long maxFileBytes;

    public NetworkAccessAuditRing(string? logFilePath = null, long maxFileBytes = defaultMaxFileBytes)
    {
        this.logFilePath = string.IsNullOrWhiteSpace(logFilePath) ? null : Path.GetFullPath(logFilePath);
        this.maxFileBytes = maxFileBytes > 0 ? maxFileBytes : defaultMaxFileBytes;
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

            if (logFilePath is not null)
            {
                TryWriteJsonLine(entry);
            }
        }
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

    private static void AppendBounded(LinkedList<NetworkAccessActivityEntry> list, NetworkAccessActivityEntry entry)
    {
        list.AddLast(entry);
        while (list.Count > maxEntriesPerBucket)
        {
            list.RemoveFirst();
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
