using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

using Microsoft.Win32.SafeHandles;

namespace Kautuka.Manager.Hosting;

internal sealed record TrustedApplianceReleaseInput(byte[] ManifestBytes, string SignatureContent,
    string PublicKeyPem, string InstalledReleaseId, string VersionDirectoryName);

/// <summary>Reads release trust inputs through protected Linux directory descriptors.</summary>
internal static class TrustedApplianceReleaseFiles
{
    private const int OpenReadOnly = 0;
    private const int OpenDirectory = 0x10000;
    private const int OpenNoFollow = 0x20000;
    private const int OpenCloseOnExec = 0x80000;
    private const int OpenNonBlock = 0x800;
    private const int DirectoryFlags = OpenReadOnly | OpenDirectory | OpenNoFollow | OpenCloseOnExec;
    private const int FileFlags = OpenReadOnly | OpenNoFollow | OpenCloseOnExec | OpenNonBlock;
    private const uint RequiredStat = 0x7ff;
    private const ushort TypeMask = 0xf000;
    private const ushort DirectoryType = 0x4000;
    private const ushort FileType = 0x8000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static void RequireVerificationTarget(string version, string recoveryDirectory)
    {
        using SafeFileHandle recovery = OpenAbsoluteDirectory(recoveryDirectory);
        using JsonDocument marker = JsonDocument.Parse(ReadFile(recovery, "maintenance.json", 16 * 1024));
        JsonElement record = marker.RootElement;
        if (record.ValueKind != JsonValueKind.Object ||
            !record.TryGetProperty("phase", out JsonElement phase) || phase.ValueKind != JsonValueKind.String || phase.GetString() != "verifying" ||
            !record.TryGetProperty("targetRelease", out JsonElement target) || target.ValueKind != JsonValueKind.String || target.GetString() != version)
            throw new InvalidDataException("The selected appliance release is not the active verification target.");
    }

    internal static TrustedApplianceReleaseInput Read(string managerRoot, string trustedKeyDirectory, string? candidateVersion = null)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        using SafeFileHandle manager = OpenAbsoluteDirectory(managerRoot);
        string version = candidateVersion ?? ReadCurrentTarget(manager, managerRoot);
        if (!IsVersionDirectoryName(version))
            throw new InvalidDataException("Invalid Manager release version.");
        using SafeFileHandle versions = OpenChecked(manager, "versions", DirectoryFlags, DirectoryType);
        using SafeFileHandle release = OpenChecked(versions, version, DirectoryFlags, DirectoryType);
        using SafeFileHandle keys = OpenAbsoluteDirectory(trustedKeyDirectory);
        byte[] manifest = ReadFile(release, "kautuka-platform-release.json", 1024 * 1024);
        string signature = Decode(ReadFile(release, "kautuka-platform-release.sig", 16 * 1024));
        string publicKey = Decode(ReadFile(keys, "manifest-public-key.pem", 64 * 1024));
        string releaseId = Decode(ReadFile(release, "release-id.txt", 1024)).Trim();
        if (manifest.Length == 0 || string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(publicKey) || releaseId.Length == 0)
            throw new InvalidDataException("A trusted appliance release input is empty.");
        return new(manifest, signature, publicKey, releaseId, version);
    }

    internal static SafeFileHandle OpenApplianceReleaseDirectory(string releaseVersionDirectory)
    {
        const string prefix = "/opt/kautuka/manager/versions/";
        if (!releaseVersionDirectory.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException("Invalid Manager release directory.");
        string version = releaseVersionDirectory[prefix.Length..];
        if (!IsVersionDirectoryName(version))
            throw new InvalidDataException("Invalid Manager release version.");
        return OpenAbsoluteDirectory(releaseVersionDirectory);
    }

    internal static SafeFileHandle OpenApplianceImagesDirectory(SafeFileHandle releaseDirectory)
        => OpenChecked(releaseDirectory, "service-images", DirectoryFlags, DirectoryType);

    private static bool IsVersionDirectoryName(string version)
        => version.Length is > 0 and <= 128 && char.IsAsciiLetterOrDigit(version[0])
            && version.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_');

    internal static SafeFileHandle OpenApplianceImageArchive(SafeFileHandle directory, string role)
    {
        if (role is not ("credentialshost" or "channelhost" or "gateway" or "webhost" or "updateservice"))
            throw new InvalidDataException("Invalid appliance service role.");
        return OpenChecked(directory, role + ".tar", FileFlags, FileType);
    }
    private static SafeFileHandle OpenAbsoluteDirectory(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/') throw new InvalidDataException("Trusted directory must be absolute.");
        string[] components = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (components.Any(static name => name is "." or "..")) throw new InvalidDataException("Invalid trusted directory path.");
        SafeFileHandle current = OpenChecked(null, "/", DirectoryFlags, DirectoryType);
        try
        {
            foreach (string component in components)
            {
                SafeFileHandle next = OpenChecked(current, component, DirectoryFlags, DirectoryType);
                current.Dispose();
                current = next;
            }
            return current;
        }
        catch { current.Dispose(); throw; }
    }

    private static string ReadCurrentTarget(SafeFileHandle manager, string managerRoot)
    {
        IntPtr buffer = Marshal.AllocHGlobal(4096);
        try
        {
            long size = ReadLinkAt(manager.DangerousGetHandle().ToInt32(), "current", buffer, (nuint)4096);
            if (size <= 0 || size >= 4096) throw new IOException("Invalid current release pointer.");
            byte[] bytes = new byte[checked((int)size)];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            string target = Decode(bytes);
            string prefix = managerRoot.TrimEnd('/') + "/versions/";
            string relative = target.StartsWith("versions/", StringComparison.Ordinal) ? target[9..]
                : target.StartsWith(prefix, StringComparison.Ordinal) ? target[prefix.Length..] : string.Empty;
            if (relative.Length == 0 || relative is "." or ".." || relative.Contains('/', StringComparison.Ordinal) ||
                relative.Any(static c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')))
                throw new InvalidDataException("Current release pointer is outside Manager versions.");
            return relative;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static byte[] ReadFile(SafeFileHandle directory, string name, int limit)
    {
        using SafeFileHandle handle = OpenChecked(directory, name, FileFlags, FileType);
        using var stream = new FileStream(handle, FileAccess.Read);
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192];
        int count;
        while ((count = stream.Read(buffer)) != 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("Trusted appliance release input exceeds its size limit.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static string Decode(byte[] value) => StrictUtf8.GetString(value);

    private static SafeFileHandle OpenChecked(SafeFileHandle? parent, string name, int flags, ushort expectedType)
    {
        int parentFd = parent is null ? -100 : parent.DangerousGetHandle().ToInt32();
        int fd = OpenAt(parentFd, name, flags);
        if (fd < 0) throw new IOException("Unable to open trusted appliance release input.");
        var handle = new SafeFileHandle((IntPtr)fd, true);
        if (Statx(fd, "", 0x1000, RequiredStat, out StatxResult stat) == 0 &&
            (stat.Mask & RequiredStat) == RequiredStat && (stat.Mode & TypeMask) == expectedType &&
            (stat.Mode & 0x12) == 0 && stat.Uid == 0)
            return handle;
        handle.Dispose();
        throw new IOException("Trusted appliance release input has unsafe ownership, mode or type.");
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
    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "readlinkat", SetLastError = true)]
    private static extern nint ReadLinkAt(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr buffer, nuint size);
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int descriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out StatxResult result);
#pragma warning restore SYSLIB1054, CA2101
}
