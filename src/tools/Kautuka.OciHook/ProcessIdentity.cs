using System.Globalization;
using System.Runtime.InteropServices;

namespace Kautuka.OciHook;

/// <summary>Reads Linux procfs identity for namespace owner liveness checks.</summary>
internal static partial class ProcessIdentity
{
    internal sealed record Identity(int Pid, ulong NetworkNamespaceInode, ulong StartTicks);

    internal static Identity? TryRead(int pid)
    {
        if (!OperatingSystem.IsLinux() || pid <= 0) return null;
        try
        {
            // Reading ticks on both sides of statx rejects a PID reused during lookup.
            ulong? first = ParseStartTicks(File.ReadAllText($"/proc/{pid}/stat"));
            if (first is null || Statx(-100, $"/proc/{pid}/ns/net", 0, 0x100, out StatxResult stat) != 0 || (stat.Mask & 0x100) == 0)
                return null;
            ulong? second = ParseStartTicks(File.ReadAllText($"/proc/{pid}/stat"));
            return first == second ? new(pid, stat.Inode, first.Value) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static bool IsLive(int pid, ulong? inode, ulong? startTicks)
    {
        Identity? current = TryRead(pid);
        return current is not null && (!inode.HasValue || inode == current.NetworkNamespaceInode) &&
            (!startTicks.HasValue || startTicks == current.StartTicks);
    }

    internal static ulong? ParseStartTicks(string stat)
    {
        int end = stat.LastIndexOf(')');
        if (end < 0 || end + 1 >= stat.Length || stat[end + 1] != ' ') return null;
        string[] fields = stat[(end + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length > 19 && ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out ulong ticks) ? ticks : null;
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxResult
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(32)] public ulong Inode;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Statx(int descriptor, string path, int flags, uint mask, out StatxResult result);
}
