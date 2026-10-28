using System.Runtime.InteropServices;

using Kautuka.Gateway.Domain.Assets;

using Microsoft.Win32.SafeHandles;

namespace Kautuka.Gateway.Domain.AgentFiles;

internal static partial class WorkspaceFile
{
    private const uint genericRead = 0x80000000;
    private const uint genericWrite = 0x40000000;
    private const uint openExisting = 3;
    private const uint fileAttributeNormal = 0x80;
    private const uint fileFlagOpenReparsePoint = 0x00200000;
    private const int oRdonly = 0;
    private const int oWronly = 1;
    private const int oCreat = 0x40;
    private const int oExcl = 0x80;
    private const int oNofollow = 0x20000;
    private const int oCloexec = 0x80000;
    private const int atEmptyPath = 0x1000;
    private const uint statxNlink = 0x4;

    public static FileStream OpenRead(string path)
        => OpenStream(path, write: false, FileMode.Open, FileAccess.Read);

    public static FileStream OpenWrite(string path, FileMode mode)
        => OpenStream(path, write: true, mode, FileAccess.Write);

    private static FileStream OpenStream(string path, bool write, FileMode mode, FileAccess access)
    {
        path = Path.GetFullPath(path);
        SafeFileHandle? handle = Open(path, write, mode, out bool truncateAfterValidation);
        try
        {
            if (HasUnsafeFileAttributes(handle) || HasMultipleLinks(handle))
            {
                throw new IOException("Requested path is not accessible.");
            }

            FileStream stream = new(handle, access);
            handle = null;
            if (truncateAfterValidation)
            {
                stream.SetLength(0);
            }

            return stream;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private static bool HasUnsafeFileAttributes(SafeFileHandle handle)
    {
        return OperatingSystem.IsWindows()
            && (!GetFileInformationByHandle(handle, out ByHandleFileInformation information)
                || (information.FileAttributes & (uint)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0);
    }

    private static SafeFileHandle Open(string path, bool write, FileMode mode, out bool truncateAfterValidation)
    {
        truncateAfterValidation = false;
        if (OperatingSystem.IsWindows())
        {
            return OpenWindowsNoLinks(path, write, mode, out truncateAfterValidation);
        }

        if (!OperatingSystem.IsLinux())
        {
            throw new IOException("Secure workspace file access is unsupported on this platform.");
        }

        int flags = (write ? oWronly : oRdonly) | oNofollow | oCloexec;
        if (mode == FileMode.CreateNew)
        {
            flags |= oCreat | oExcl;
        }

        int descriptor = OpenLinuxNoLinks(path, flags);
        if (descriptor < 0 && mode == FileMode.Create && Marshal.GetLastPInvokeError() == 2)
        {
            descriptor = OpenLinuxNoLinks(path, flags | oCreat | oExcl);
        }
        else if (descriptor >= 0 && mode == FileMode.Create)
        {
            truncateAfterValidation = true;
        }

        if (descriptor < 0)
        {
            throw new IOException("Requested path is not accessible.");
        }

        return new SafeFileHandle(descriptor, ownsHandle: true);
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenUnix(string path, int flags, uint mode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Statx(int dirfd, string pathname, int flags, uint mask, ref LinuxStatx information);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    public static bool TryResolveUnderRoot(string? rootPath, string? rawPath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(rawPath))
        {
            return false;
        }

        string root = Path.GetFullPath(rootPath);
        string candidate = rawPath.Trim();
        if (candidate.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        if (Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri))
        {
            if (!uri.IsFile)
            {
                return false;
            }

            candidate = uri.LocalPath;
        }

        fullPath = Path.IsPathRooted(candidate)
            ? Path.GetFullPath(candidate)
            : Path.GetFullPath(Path.Combine(root, candidate.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsUnderRoot(root, fullPath)
            || HasUnsafeSegment(root, fullPath)
            || NamesExistingWindowsAlias(root, fullPath)
            || WorkspaceReparseGuard.ContainsReparsePoint(root, fullPath))
        {
            fullPath = string.Empty;
            return false;
        }

        return true;
    }

    public static bool TryReadUnderRoot(string? rootPath, string? rawPath, long maxBytes, out byte[]? bytes, out string? fileName, bool rejectEmpty = true)
    {
        bytes = null;
        fileName = null;
        if (maxBytes <= 0 || !TryResolveUnderRoot(rootPath, rawPath, out string fullPath))
        {
            return false;
        }

        FileStream? stream = null;
        try
        {
            stream = OpenRead(fullPath);
            if (stream.Length > maxBytes || (rejectEmpty && stream.Length <= 0))
            {
                return false;
            }

            bytes = new byte[stream.Length];
            if (bytes.Length > 0)
            {
                stream.ReadExactly(bytes);
            }

            fileName = Path.GetFileName(fullPath);
            return true;
        }
        catch (IOException)
        {
            bytes = null;
            fileName = null;
            return false;
        }
        finally
        {
            stream?.Dispose();
        }
    }

    public static bool NamesExistingWindowsAlias(string rootPath, string resolvedPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        string current = Path.GetFullPath(rootPath);
        string relative = Path.GetRelativePath(current, Path.GetFullPath(resolvedPath));
        if (relative is "." or "")
        {
            return false;
        }

        foreach (string segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Path.Exists(current))
            {
                return false;
            }

            string? actualName = ReadWindowsLongFileName(current);
            if (actualName is null || !string.Equals(actualName, segment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ReadWindowsLongFileName(string path)
    {
        char[] buffer = new char[32 * 1024];
        uint length = GetLongPathNameW(path, buffer, (uint)buffer.Length);
        if (length == 0 || length >= buffer.Length)
        {
            return null;
        }

        return Path.GetFileName(new string(buffer, 0, (int)length).TrimEnd('\\', '/'));
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", EntryPoint = "GetLongPathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetLongPathNameW(string shortPath, char[] longPath, uint bufferLength);

    private static bool IsUnderRoot(string rootPath, string resolvedPath)
    {
        string normalizedRoot = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(normalizedRoot, resolvedPath, StringComparison.Ordinal)
            || resolvedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool HasUnsafeSegment(string rootPath, string resolvedPath)
    {
        string relative = Path.GetRelativePath(rootPath, resolvedPath);
        foreach (string segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or ".." || segment.Equals(".kautuka", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!OperatingSystem.IsWindows())
            {
                continue;
            }

            if (segment.Contains(':', StringComparison.Ordinal) || WorkspaceAttachmentUploadPolicy.IsReservedWindowsDeviceName(segment.TrimEnd(' ', '.')))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasMultipleLinks(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
        {
            return !GetFileInformationByHandle(handle, out ByHandleFileInformation information)
                || information.NumberOfLinks != 1;
        }

        return !TryGetLinuxLinkCount(handle, out uint links) || links != 1;
    }

    private static bool TryGetLinuxLinkCount(SafeFileHandle handle, out uint links)
    {
        links = 0;
        bool added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            int descriptor = (int)handle.DangerousGetHandle();
            LinuxStatx information = default;
            if (Statx(descriptor, string.Empty, atEmptyPath, statxNlink, ref information) != 0
                || (information.Mask & statxNlink) == 0)
            {
                return false;
            }

            links = information.NumLinks;
            return true;
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    /// <summary>
    /// Kernel <c>struct statx</c>. The layout is the syscall ABI, so <c>stx_nlink</c> stays at offset 16 on every architecture.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct LinuxStatx
    {
        public uint Mask;
        public uint BlockSize;
        public ulong Attributes;
        public uint NumLinks;
        public uint Uid;
        public uint Gid;
        public ushort Mode;
        public ushort Spare0;
        public ulong Inode;
        public ulong Size;
        public ulong Blocks;
        public ulong AttributesMask;
        public long AccessTimeSeconds;
        public uint AccessTimeNanoseconds;
        public int AccessTimeReserved;
        public long BirthTimeSeconds;
        public uint BirthTimeNanoseconds;
        public int BirthTimeReserved;
        public long ChangeTimeSeconds;
        public uint ChangeTimeNanoseconds;
        public int ChangeTimeReserved;
        public long ModificationTimeSeconds;
        public uint ModificationTimeNanoseconds;
        public int ModificationTimeReserved;
        public uint SpecialDeviceMajor;
        public uint SpecialDeviceMinor;
        public uint ContainingDeviceMajor;
        public uint ContainingDeviceMinor;
        public ulong MountId;
        public uint DirectIoMemoryAlign;
        public uint DirectIoOffsetAlign;
        public ulong Spare3A;
        public ulong Spare3B;
        public ulong Spare3C;
        public ulong Spare3D;
        public ulong Spare3E;
        public ulong Spare3F;
        public ulong Spare3G;
        public ulong Spare3H;
        public ulong Spare3I;
        public ulong Spare3J;
        public ulong Spare3K;
        public ulong Spare3L;
    }
}
