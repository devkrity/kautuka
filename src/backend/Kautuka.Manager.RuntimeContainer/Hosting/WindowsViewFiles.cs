using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

internal static class WindowsViewFiles
{
    private const uint Directory = 1;
    private const uint NonDirectory = 0x40;
    private const uint Open = 1;
    private const uint Create = 2;
    private const uint OpenIf = 3;
    private const uint ReadDirectory = 0x20 | 0x80 | 0x100000;

    internal static void Write(string workspace, string folder, string name, byte[] bytes)
    {
        if (!Path.IsPathFullyQualified(workspace) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.EndsWith('.') || name.EndsWith(' '))
            throw new IOException("Invalid view file path.");
        string volume = Path.GetPathRoot(workspace)!;
        using SafeFileHandle root = CreateFile(volume, ReadDirectory, 7, 0, 3, 0x02000000 | 0x00200000, 0);
        if (root.IsInvalid || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Invalid workspace volume.");
        using SafeFileHandle workspaceHandle = OpenChild(root, Path.GetRelativePath(volume, workspace), ReadDirectory, Directory, Open);
        using SafeFileHandle views = OpenChild(workspaceHandle, "view-files", ReadDirectory, Directory, OpenIf);
        using SafeFileHandle directory = OpenChild(views, folder, ReadDirectory, Directory, OpenIf);
        using SafeFileHandle output = OpenChild(directory, name, 0x40000000 | 0x100000, NonDirectory, Create, replace: true);
        RandomAccess.Write(output, bytes, 0);
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    private static SafeFileHandle OpenChild(SafeFileHandle root, string relative, uint access, uint options, uint disposition, bool replace = false)
    {
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
            uint share = options == Directory ? 7u : 0u;
            int status = NtCreateFile(out SafeFileHandle handle, access, ref attributes, out _, 0, 0, share,
                disposition, options | 0x20, 0, 0);
            if (replace && status == unchecked((int)0xC0000035)) // STATUS_OBJECT_NAME_COLLISION
            {
                handle.Dispose();
                using (OpenChild(root, relative, 0x10000 | 0x100000, NonDirectory | 0x1000, Open)) { } // DELETE_ON_CLOSE
                return OpenChild(root, relative, access, options, disposition);
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
    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref NtObjectAttributes attributes,
        out NtIoStatusBlock ioStatus, nint allocationSize, uint fileAttributes, uint share, uint disposition,
        uint options, nint eaBuffer, uint eaLength);
}
