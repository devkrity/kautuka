using System.Runtime.InteropServices;

namespace Kautuka.Manager.Hosting;

// Development launcher opt-in requires a root-controlled marker as well as
// the Manager unit environment; an environment variable alone is insufficient.
internal static class DevelopmentLocalImageGate
{
    internal const string MarkerPath = "/etc/kautuka/development-local-images";

    internal static bool Enabled(string? optIn, string markerPath = MarkerPath)
    {
        if (!OperatingSystem.IsLinux() || optIn != "1") return false;
        string? parent = Path.GetDirectoryName(markerPath);
        if (parent is null || !Trusted(parent, isDirectory: true) || !Trusted(markerPath, isDirectory: false)) return false;
        try { return File.ReadAllText(markerPath).TrimEnd('\r', '\n') == "kautuka-dev-local-images-v1"; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool Trusted(string path, bool isDirectory)
    {
        if (statx(-100, path, 0x100, 0x7ff, out Statx result) != 0) return false; // AT_FDCWD, AT_SYMLINK_NOFOLLOW
        int type = result.Mode & 0xf000;
        return (result.Mask & 0xa) == 0xa && result.UserId == 0 && (result.Mode & 0x12) == 0 && type == (isDirectory ? 0x4000 : 0x8000);
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Statx
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(20)] internal uint UserId;
        [FieldOffset(28)] internal ushort Mode;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", SetLastError = true)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA2101", Justification = "Linux statx requires an explicitly marshaled UTF-8 pathname.")]
    private static extern int statx(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out Statx result);
}
