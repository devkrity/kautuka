using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

internal static class DescriptorFiles
{
    private const int OpenReadOnly = 0;
    private const int OpenWriteOnly = 1;
    private const int OpenCreateExclusive = 0xc0;
    private const int OpenNonBlock = 0x800;
    private const int OpenDirectory = 0x10000;
    private const int OpenNoFollow = 0x20000;
    private const int OpenCloseOnExec = 0x80000;
    internal const int DirectoryFlags = OpenReadOnly | OpenDirectory | OpenNoFollow | OpenCloseOnExec;
    internal const int FileFlags = OpenReadOnly | OpenNoFollow | OpenCloseOnExec | OpenNonBlock;
    private const uint RequiredStat = 0x7ff;
    private const ushort TypeMask = 0xf000;
    internal const ushort DirectoryType = 0x4000;
    internal const ushort FileType = 0x8000;

    internal static bool IsSupportedPlatform()
        => OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private static void EnsureSupportedPlatform()
    {
        if (!IsSupportedPlatform())
            throw new PlatformNotSupportedException("Descriptor file operations are supported only on Linux x64.");
    }

    internal static SafeFileHandle OpenAbsoluteDirectory(string path, bool requireRootOwnership = true)
    {
        EnsureSupportedPlatform();
        if (string.IsNullOrEmpty(path) || path[0] != '/') throw new InvalidDataException("Directory path must be absolute.");
        string[] components = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (components.Any(static name => name is "." or "..")) throw new InvalidDataException("Invalid directory path.");
        SafeFileHandle current = OpenChecked(null, "/", DirectoryFlags, DirectoryType, requireRootOwnership);
        try
        {
            foreach (string component in components)
            {
                SafeFileHandle next = OpenChecked(current, component, DirectoryFlags, DirectoryType, requireRootOwnership);
                current.Dispose();
                current = next;
            }
            return current;
        }
        catch { current.Dispose(); throw; }
    }

    internal static SafeFileHandle OpenChecked(SafeFileHandle? parent, string name, int flags, ushort expectedType, bool requireRootOwnership = true)
    {
        EnsureSupportedPlatform();
        int parentFd = parent is null ? -100 : parent.DangerousGetHandle().ToInt32();
        int fd = OpenAt(parentFd, name, flags, 0);
        GC.KeepAlive(parent);
        if (fd < 0)
        {
            if (Marshal.GetLastPInvokeError() == 2) // ENOENT
                throw new FileNotFoundException("Path was not found.", name);
            throw new IOException("Unable to open path.");
        }
        var handle = new SafeFileHandle((IntPtr)fd, true);
        if (Statx(fd, "", 0x1000, RequiredStat, out StatxResult stat) == 0 &&
            (stat.Mask & RequiredStat) == RequiredStat && (stat.Mode & TypeMask) == expectedType &&
            (!requireRootOwnership || ((stat.Mode & 0x12) == 0 && stat.Uid == 0)))
            return handle;
        handle.Dispose();
        throw new IOException("Path has unsafe ownership, permissions, or file type.");
    }

    internal static void WriteViewFile(string workspace, string folder, string name, ReadOnlySpan<byte> bytes)
    {
        EnsureSupportedPlatform();
        using SafeFileHandle root = OpenAbsoluteDirectory(workspace, requireRootOwnership: false);
        using SafeFileHandle views = OpenOrCreateDirectory(root, "view-files");
        using SafeFileHandle directory = OpenOrCreateDirectory(views, folder);
        const int exclusiveCreate = OpenWriteOnly | OpenCreateExclusive | OpenNoFollow | OpenCloseOnExec;
        int parent = directory.DangerousGetHandle().ToInt32();
        int fd = CreateExclusive(parent, name, exclusiveCreate); // 0640
        if (fd < 0 && Marshal.GetLastPInvokeError() == 17) // EEXIST
        {
            using SafeFileHandle existing = OpenChecked(directory, name, FileFlags, FileType, requireRootOwnership: false);
            if (UnlinkAt(directory, name, 0) != 0)
                throw new IOException("Unable to replace view file.");
            fd = CreateExclusive(parent, name, exclusiveCreate);
        }
        GC.KeepAlive(directory);
        if (fd < 0) throw new IOException("Unable to create view file.");
        using var handle = new SafeFileHandle(fd, ownsHandle: true);
        if (ChangeFileMode(fd, 0x1a0) != 0) // Ensure 0640 regardless of service umask so sandbox GID 0 can read.
            throw new IOException("Unable to set view file permissions.");
        RandomAccess.Write(handle, bytes, 0);
    }

    private static int CreateExclusive(int directory, string name, int flags)
        => OpenAt(directory, name, flags, 0x1a0);

    private static SafeFileHandle OpenOrCreateDirectory(SafeFileHandle parent, string name)
    {
        if (MakeDirectoryAt(parent, name, 0x1e8) != 0 && Marshal.GetLastPInvokeError() != 17) // 0750, EEXIST
            throw new IOException("Unable to create view file directory.");
        SafeFileHandle directory = OpenChecked(parent, name, DirectoryFlags, DirectoryType, requireRootOwnership: false);
        try
        {
            int fd = directory.DangerousGetHandle().ToInt32();
            if (Statx(fd, "", 0x1000, RequiredStat, out StatxResult stat) == 0
                && stat.Uid == GetEffectiveUserId()
                && (stat.Mode & 0xfff) != 0x1e8
                && ChangeFileMode(fd, 0x1e8) != 0)
            {
                throw new IOException("Unable to set view file directory permissions.");
            }
            return directory;
        }
        catch
        {
            directory.Dispose();
            throw;
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxResult
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(20)] public uint Uid;
        [FieldOffset(28)] public ushort Mode;
    }

#pragma warning disable SYSLIB1054, CA2101 // Avoid enabling unsafe code in the Manager project for these fixed libc signatures.
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)]
    private static extern int MakeDirectoryAt(SafeFileHandle directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint mode);
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    private static extern int UnlinkAt(SafeFileHandle directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags);
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    private static extern int ChangeFileMode(int descriptor, uint mode);
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int descriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out StatxResult result);
#pragma warning restore SYSLIB1054, CA2101
}
