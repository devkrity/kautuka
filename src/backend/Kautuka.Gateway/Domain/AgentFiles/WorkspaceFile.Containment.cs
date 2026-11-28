using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

namespace Kautuka.Gateway.Domain.AgentFiles;

internal static partial class WorkspaceFile
{
    private static int OpenUnixNoLinks(string path, int flags)
        => OperatingSystem.IsMacOS() ? OpenMacNoLinks(path, flags) : OpenLinuxNoLinks(path, flags);

    private static int OpenMacNoLinks(string path, int flags)
    {
        if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
            throw new IOException("Secure workspace file access is unavailable on this device.");
        const int directoryFlags = 0x100000 | 0x100 | 0x1000000; // O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC
        int fd = OpenUnix("/", directoryFlags, 0);
        if (fd < 0) return -1;
        using var root = new SafeFileHandle(fd, ownsHandle: true);
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new IOException("Requested path is not accessible.");
        return OpenMacRelative(root, parts, 0, flags);
    }

    private static int OpenMacRelative(SafeFileHandle parent, string[] parts, int index, int flags)
    {
        if (index == parts.Length - 1)
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? OpenMacAtArm(parent, parts[index], flags | 4, 0, 0, 0, 0, 0, (flags & oCreat) != 0 ? 0x180u : 0u)
                : OpenMacAt(parent, parts[index], flags | 4, (flags & oCreat) != 0 ? 0x180u : 0u);
        int fd = OpenMacAt(parent, parts[index], 0x100000 | 0x100 | 0x1000000);
        if (fd < 0) return -1;
        using var next = new SafeFileHandle(fd, ownsHandle: true);
        return OpenMacRelative(next, parts, index + 1, flags);
    }

    private static int ReadMacStat(SafeFileHandle handle, out DarwinStat stat)
        => RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? FStatMacIntel(handle, out stat) : FStatMacArm(handle, out stat);

    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct DarwinStat
    {
        [FieldOffset(4)] public ushort Mode;
        [FieldOffset(6)] public ushort NumLinks;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenMacAt(SafeFileHandle parent, string name, int flags, uint mode = 0);
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenMacAtArm(SafeFileHandle parent, string name, int flags,
        nint pad3, nint pad4, nint pad5, nint pad6, nint pad7, uint mode);
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static partial int FStatMacIntel(SafeFileHandle handle, out DarwinStat stat);
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static partial int FStatMacArm(SafeFileHandle handle, out DarwinStat stat);

    // Unlike O_NOFOLLOW, openat2 rejects links in every component in one kernel lookup.
    // Open the filesystem root first: even ancestors of the authorized workspace must
    // not be redirected. There is deliberately no path-based fallback on older kernels.
    private static int OpenLinuxNoLinks(string path, int flags)
    {
        if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
        {
            throw new IOException("Secure workspace file access is unsupported on this architecture.");
        }

        int rootDescriptor = OpenUnix("/", oRdonly | oCloexec | (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x4000 : 0x10000), 0);
        if (rootDescriptor < 0)
        {
            throw new IOException("Requested path is not accessible.");
        }

        using var root = new SafeFileHandle(rootDescriptor, ownsHandle: true);
        var how = new LinuxOpenHow
        {
            Flags = (ulong)flags,
            Mode = (flags & oCreat) != 0 ? 0x180UL : 0,
            Resolve = 0x08 | 0x04 | 0x02, // BENEATH | NO_SYMLINKS | NO_MAGICLINKS
        };
        long descriptor = OpenAt2(437, root, path.TrimStart('/'), ref how, (nuint)Marshal.SizeOf<LinuxOpenHow>());
        return descriptor < 0 ? -1 : checked((int)descriptor);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxOpenHow
    {
        public ulong Flags;
        public ulong Mode;
        public ulong Resolve;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial long OpenAt2(long number, SafeFileHandle root, string path, ref LinuxOpenHow how, nuint size);

    // A single NT lookup rejects reparse points in every component. The volume
    // root is held as a directory handle; no workspace ancestor is locked or reopened.
    private static SafeFileHandle OpenWindowsNoLinks(string path, bool write, FileMode mode, out bool truncate)
    {
        truncate = false;
        string rootPath = Path.GetPathRoot(path) ?? throw new IOException("Requested path is not accessible.");
        using SafeFileHandle root = CreateFileW(rootPath, genericRead, 7, 0, openExisting,
            0x02000000 | fileFlagOpenReparsePoint, 0);
        if (root.IsInvalid || !GetFileInformationByHandle(root, out ByHandleFileInformation rootInformation)
            || (rootInformation.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Requested path is not accessible.");
        }

        string relative = Path.GetRelativePath(rootPath, path);
        nint text = Marshal.StringToHGlobalUni(relative);
        nint namePointer = Marshal.AllocHGlobal(Marshal.SizeOf<NtUnicodeString>());
        try
        {
            var name = new NtUnicodeString
            {
                Length = checked((ushort)(relative.Length * 2)),
                MaximumLength = checked((ushort)(relative.Length * 2)),
                Buffer = text,
            };
            Marshal.StructureToPtr(name, namePointer, fDeleteOld: false);
            var attributes = new NtObjectAttributes
            {
                Length = Marshal.SizeOf<NtObjectAttributes>(),
                RootDirectory = root.DangerousGetHandle(),
                ObjectName = namePointer,
                Attributes = 0x40 | 0x1000, // CASE_INSENSITIVE | DONT_REPARSE
            };
            uint access = (write ? genericWrite : genericRead) | 0x100000 | 0x80; // SYNCHRONIZE | READ_ATTRIBUTES
            uint share = write ? 0u : 1u;
            uint disposition = mode == FileMode.CreateNew ? 2u : 1u; // FILE_CREATE / FILE_OPEN
            int status = NtCreateFile(out SafeFileHandle handle, access, ref attributes, out _, 0,
                fileAttributeNormal, share, disposition, 0x20 | 0x40 | 0x00200000, 0, 0);
            // SYNCHRONOUS_IO_NONALERT | NON_DIRECTORY_FILE | OPEN_REPARSE_POINT
            if (status < 0 && mode == FileMode.Create && status is unchecked((int)0xC0000034) or unchecked((int)0xC000003A))
            {
                handle.Dispose();
                status = NtCreateFile(out handle, access, ref attributes, out _, 0,
                    fileAttributeNormal, share, 2, 0x20 | 0x40 | 0x00200000, 0, 0);
            }
            else if (status >= 0 && mode == FileMode.Create)
            {
                truncate = true;
            }

            if (status < 0 || handle.IsInvalid)
            {
                handle.Dispose();
                throw new IOException("Requested path is not accessible.");
            }

            return handle;
        }
        finally
        {
            // Keep the SafeHandle alive across the native call using its raw value.
            GC.KeepAlive(root);
            Marshal.FreeHGlobal(namePointer);
            Marshal.FreeHGlobal(text);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NtUnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NtObjectAttributes
    {
        public int Length;
        public nint RootDirectory;
        public nint ObjectName;
        public uint Attributes;
        public nint SecurityDescriptor;
        public nint SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NtIoStatusBlock
    {
        public nint Status;
        public nuint Information;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("ntdll.dll")]
    private static partial int NtCreateFile(out SafeFileHandle handle, uint access, ref NtObjectAttributes attributes,
        out NtIoStatusBlock ioStatus, nint allocationSize, uint fileAttributes, uint share, uint disposition,
        uint options, nint eaBuffer, uint eaLength);
}
