using System.Runtime.InteropServices;
using System.Text.Json;

using Microsoft.Win32.SafeHandles;

namespace Kautuka.OciHook;

/// <summary>Writes the JSONL records consumed by Manager's startup canary.</summary>
internal static partial class HookAuditLog
{
    private const int LogLimit = 5 * 1024 * 1024;
    private static readonly int OpenFlags = 1 | 0x40 | 0x400 | 0x800 | (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x8000 : 0x20000) | 0x80000;
    private const int LockExclusiveNonblocking = 2 | 4;
    private const uint OwnerReadWrite = 0x180;

    internal static void Write(string stateDirectory, HookPolicy.State? state, IReadOnlyList<string> addresses,
        ulong? inode, bool allowed, string reason, string hash, DateTimeOffset startedAt, TimeSpan elapsed)
    {
        Directory.CreateDirectory(stateDirectory);
        string path = Path.Combine(stateDirectory, "hook.log");
        using SafeFileHandle gate = OpenPrivate(path + ".lock");
        for (int attempt = 0; Flock(gate, LockExclusiveNonblocking) != 0; attempt++)
        {
            if (attempt == 19 || Marshal.GetLastPInvokeError() is not (4 or 11))
                throw new IOException("Could not lock the OCI hook audit log.");

            Thread.Sleep(25);
        }

        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteNumber("ts_ns", (startedAt.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100);
            json.WriteNumber("duration_ms", Math.Round(elapsed.TotalMilliseconds, 3));
            json.WriteString("id", state?.Id ?? string.Empty);
            json.WriteNumber("pid", state?.Pid ?? 0);
            json.WriteString("hook_version", "7.0.0-native");
            json.WriteStartArray("ips");
            foreach (string address in addresses)
                json.WriteStringValue(address);
            json.WriteEndArray();
            if (inode is ulong value)
                json.WriteNumber("netns_inode", value);
            else
                json.WriteNull("netns_inode");
            json.WriteString("action", allowed ? "allow" : "refuse");
            json.WriteString("reason", reason);
            json.WriteString("rules_sha256", hash);
            json.WriteStartObject("annotations");
            if (state?.Role is string role)
                json.WriteString("io.kautuka.role", role);
            if (state?.ImageId is string image)
                json.WriteString("io.kautuka.image-id", image);
            json.WriteEndObject();
            json.WriteEndObject();
        }
        buffer.WriteByte((byte)'\n');

        using (SafeFileHandle currentHandle = OpenPrivate(path))
        using (var current = new FileStream(currentHandle, FileAccess.Write))
        {
            if (current.Length + buffer.Length <= LogLimit)
            {
                buffer.WriteTo(current);
                return;
            }
        }

        for (int generation = 3; generation >= 1; generation--)
        {
            string previous = generation == 1 ? path : FormattableString.Invariant($"{path}.{generation - 1}");
            if (File.Exists(previous))
                File.Move(previous, FormattableString.Invariant($"{path}.{generation}"), overwrite: true);
        }
        using SafeFileHandle activeHandle = OpenPrivate(path);
        using var active = new FileStream(activeHandle, FileAccess.Write);
        buffer.WriteTo(active);
    }

    private static SafeFileHandle OpenPrivate(string path)
    {
        int descriptor = Open(path, OpenFlags, OwnerReadWrite);
        if (descriptor < 0)
            throw new IOException("Could not open the OCI hook audit file.");

        var handle = new SafeFileHandle(descriptor, ownsHandle: true);
        if (Statx(handle, string.Empty, 0x1000, 0x0a, out StatxResult stat) == 0
            && (stat.Mask & 0x0a) == 0x0a && (stat.Mode & 0xf000) == 0x8000
            && (stat.Mode & 0x3f) == 0 && stat.Uid == GetEffectiveUserId())
        {
            return handle;
        }

        handle.Dispose();
        throw new IOException("The OCI hook audit file is not private.");
    }

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
    private static partial int Flock(SafeFileHandle descriptor, int operation);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Statx(SafeFileHandle descriptor, string path, int flags, uint mask, out StatxResult result);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserId();
}
