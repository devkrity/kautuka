using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Win32.SafeHandles;

namespace Kautuka.OciHook;

/// <summary>Linux owner registry, with descriptor validation and serialized atomic writes.</summary>
internal static partial class OwnerRegistry
{
    internal sealed record Result(Dictionary<string, JsonObject> Entries, bool Valid, string? Reason)
    {
        internal IReadOnlyDictionary<string, string?> Ids => Entries.ToDictionary(static pair => pair.Key, static pair => OwnerId(pair.Value), StringComparer.Ordinal);
    }
    private const int ReadFlags = 0x20000 | 0x80000 | 0x800;
    private const int CreateLockFlags = ReadFlags | 0x40 | 0x2;
    private const int MaxBytes = 1024 * 1024;

    internal static Result Read(string path)
    {
        if (!OperatingSystem.IsLinux()) return Invalid();
        int fd = Open(path, ReadFlags, 0);
        if (fd < 0) return Marshal.GetLastPInvokeError() == 2 ? new(new(StringComparer.Ordinal), true, null) : Invalid();
        using var handle = new SafeFileHandle((IntPtr)fd, true);
        if (!IsSafeFile(fd)) return Invalid();
        try
        {
            using var file = new FileStream(handle, FileAccess.Read);
            using var bytes = new MemoryStream();
            byte[] buffer = new byte[8192];
            int count;
            while ((count = file.Read(buffer)) != 0)
            {
                if (bytes.Length + count > MaxBytes) return Invalid();
                bytes.Write(buffer, 0, count);
            }
            string json = new UTF8Encoding(false, true).GetString(bytes.GetBuffer(), 0, checked((int)bytes.Length));
            if (string.IsNullOrWhiteSpace(json)) return new(new(StringComparer.Ordinal), true, null);
            return Parse(json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or DecoderFallbackException or InvalidOperationException or FormatException or OverflowException)
        {
            return Invalid();
        }
    }

    private static Result Parse(string json)
    {
        JsonNode? root = JsonNode.Parse(json);
        var entries = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (root is JsonArray array)
        {
            foreach (JsonNode? node in array)
                if (node is JsonObject entry && entry["inode"] is JsonNode inode)
                    entries[inode.ToString()] = (JsonObject)entry.DeepClone();
        }
        else if (root is JsonObject obj)
        {
            foreach (KeyValuePair<string, JsonNode?> pair in obj)
            {
                if (pair.Value is JsonObject item)
                {
                    var entry = (JsonObject)item.DeepClone();
                    if (!entry.ContainsKey("inode"))
                        entry["inode"] = ulong.TryParse(pair.Key, NumberStyles.None, CultureInfo.InvariantCulture, out ulong parsed) ? JsonValue.Create(parsed) : JsonValue.Create(pair.Key);
                    entries[pair.Key] = entry;
                }
                // Scalar legacy entries have no PID and therefore expire.
            }
        }
        else return Invalid();
        var live = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, JsonObject> pair in entries)
        {
            JsonObject entry = pair.Value;
            if (entry["pid"] is not JsonValue pidValue || !pidValue.TryGetValue<int>(out int pid) || pid <= 0) continue;
            ulong? inode = OptionalUnsigned(entry["inode"]);
            ulong? ticks = OptionalUnsigned(entry["start_ticks"]);
            if (ProcessIdentity.IsLive(pid, inode, ticks)) live[pair.Key] = entry;
        }
        return new(live, true, null);
    }

    private static ulong? OptionalUnsigned(JsonNode? value)
    {
        if (value is null) return null;
        return ulong.Parse(value.ToString(), NumberStyles.None, CultureInfo.InvariantCulture);
    }

    internal static void MergeAndWrite(string ownersPath, string lockPath, IReadOnlyDictionary<string, JsonObject> additions)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        string directory = Path.GetDirectoryName(Path.GetFullPath(ownersPath))!;
        Directory.CreateDirectory(directory);
        int fd = Open(lockPath, CreateLockFlags, 0x180); // 0600
        if (fd < 0) throw new IOException("Unable to open owners lock.");
        using var handle = new SafeFileHandle((IntPtr)fd, true);
        if (!IsSafeFile(fd, requirePrivateMode: false) || Fchmod(fd, 0x180) != 0) throw new IOException("Unsafe owners lock.");
        var lockTimer = System.Diagnostics.Stopwatch.StartNew();
        while (Flock(fd, 2 | 4) != 0) // LOCK_EX | LOCK_NB
        {
            int error = Marshal.GetLastPInvokeError();
            if (error != 4 && error != 11) throw new IOException("Unable to lock owners registry.");
            if (lockTimer.Elapsed >= TimeSpan.FromSeconds(10)) throw new IOException("Timed out locking owners registry.");
            Thread.Sleep(10);
        }
        string temporary = Path.Combine(directory, "owners-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            Result existing = Read(ownersPath);
            if (!existing.Valid) throw new IOException("Existing owners registry is unreadable.");
            foreach (KeyValuePair<string, JsonObject> entry in additions)
            {
                if (existing.Entries.TryGetValue(entry.Key, out JsonObject? previous) && OwnerId(previous) != OwnerId(entry.Value))
                    throw new IOException("Network namespace already has a live owner.");
                existing.Entries[entry.Key] = (JsonObject)entry.Value.DeepClone();
            }
            using (var file = new FileStream(temporary, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
            {
                using (var writer = new Utf8JsonWriter(file))
                {
                    writer.WriteStartArray();
                    foreach (JsonObject entry in existing.Entries.Values) entry.WriteTo(writer);
                    writer.WriteEndArray();
                    writer.Flush();
                }
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, ownersPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            finally { _ = Flock(fd, 8); }
        }
    }

    internal static void Claim(string ownersPath, string lockPath, ulong inode, string id, int pid, ulong startTicks)
    {
        if (string.IsNullOrWhiteSpace(id) || !ProcessIdentity.IsLive(pid, inode, startTicks))
            throw new IOException("Cannot claim a namespace for an invalid process identity.");
        string key = inode.ToString(CultureInfo.InvariantCulture);
        var entry = new JsonObject { ["inode"] = inode, ["id"] = id, ["pid"] = pid, ["start_ticks"] = startTicks };
        MergeAndWrite(ownersPath, lockPath, new Dictionary<string, JsonObject>(StringComparer.Ordinal) { [key] = entry });
    }

    private static string? OwnerId(JsonObject entry) => entry["id"]?.ToString() ?? entry["cid"]?.ToString();

    private static Result Invalid() => new(new(StringComparer.Ordinal), false, "owners_unreadable");
    private static bool IsSafeFile(int fd, bool requirePrivateMode = true) => Statx(fd, "", 0x1000, 0x7ff, out StatxResult stat) == 0 &&
        (stat.Mask & 0x7ff) == 0x7ff && (stat.Mode & 0xf000) == 0x8000 && (!requirePrivateMode || (stat.Mode & 0x3f) == 0) && stat.Uid == GetEffectiveUserId();

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxResult
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(20)] public uint Uid;
        [FieldOffset(28)] public ushort Mode;
    }
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags, uint mode);
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static partial int Flock(int descriptor, int operation);
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Statx(int descriptor, string path, int flags, uint mask, out StatxResult result);
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    private static partial int Fchmod(int descriptor, uint mode);
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserId();
}
