using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

namespace Kautuka.OciHook;

/// <summary>Reads the allowlist using one verified Linux file descriptor.</summary>
internal static partial class AllowlistFile
{
    private const int ReadOnlyNoFollowCloseOnExec = 0x20000 | 0x80000 | 0x800; // O_NOFOLLOW | O_CLOEXEC | O_NONBLOCK
    private const int AtEmptyPath = 0x1000;
    private const uint BasicStats = 0x7ff;
    private const int NotFound = 2;
    private const int MaxAllowlistBytes = 1024 * 1024;
    private const ushort RegularFile = 0x8000;
    private const ushort FileTypeMask = 0xf000;

    internal sealed record Result(Dictionary<string, HookPolicy.Infrastructure> Entries, bool Valid, string? Reason);

    internal static Result Read(string path)
    {
        if (!OperatingSystem.IsLinux()) return Invalid("allowlist_unsupported_platform");
        int descriptor = Open(path, ReadOnlyNoFollowCloseOnExec);
        if (descriptor < 0)
            return Invalid(Marshal.GetLastPInvokeError() == NotFound ? "missing_allowlist" : "allowlist_read_error");
        using var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        if (Statx(descriptor, "", AtEmptyPath, BasicStats, out StatxResult stat) != 0 ||
            (stat.Mask & BasicStats) != BasicStats || (stat.Mode & FileTypeMask) != RegularFile)
            return Invalid("allowlist_read_error");
        if ((stat.Mode & 0x3f) != 0) return Invalid("bad_allowlist_mode");
        if (stat.Uid != GetEffectiveUserId()) return Invalid("bad_allowlist_owner");
        try
        {
            using var stream = new FileStream(handle, FileAccess.Read);
            using var data = new MemoryStream();
            byte[] buffer = new byte[8192];
            int count;
            while ((count = stream.Read(buffer)) != 0)
            {
                if (data.Length + count > MaxAllowlistBytes) return Invalid("allowlist_too_large");
                data.Write(buffer, 0, count);
            }
            string json = new System.Text.UTF8Encoding(false, true).GetString(data.GetBuffer(), 0, checked((int)data.Length));
            return new(HookInput.ParseAllowlist(json), true, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or System.Text.DecoderFallbackException)
        {
            return Invalid("allowlist_read_error");
        }
    }

    private static Result Invalid(string reason) => new(new(StringComparer.Ordinal), false, reason);

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxResult
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(20)] public uint Uid;
        [FieldOffset(28)] public ushort Mode;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Statx(int descriptor, string path, int flags, uint mask, out StatxResult result);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserId();
}
