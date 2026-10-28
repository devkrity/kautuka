using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

namespace Kautuka.Gateway.Domain.AgentFiles;

internal static partial class WorkspaceFile
{
    // Unlike O_NOFOLLOW, openat2 rejects links in every component in one kernel lookup.
    // Open the filesystem root first: even ancestors of the authorized workspace must
    // not be redirected. There is deliberately no path-based fallback on older kernels.
    private static int OpenLinuxNoLinks(string path, int flags)
    {
        if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
        {
            throw new IOException("Secure workspace file access is unsupported on this architecture.");
        }

        int rootDescriptor = OpenUnix("/", oRdonly | oCloexec | 0x10000, 0);
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
