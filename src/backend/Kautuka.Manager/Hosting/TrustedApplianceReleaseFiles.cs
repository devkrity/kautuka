using static Kautuka.Manager.RuntimeContainer.Hosting.DescriptorFiles;

using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

using Kautuka.Manager.Protos;

using Microsoft.Win32.SafeHandles;

namespace Kautuka.Manager.Hosting;

internal sealed record TrustedApplianceReleaseInput(byte[] ManifestBytes, string SignatureContent,
    string PublicKeyPem, string InstalledReleaseId, string VersionDirectoryName);

/// <summary>Reads release trust inputs through protected Linux directory descriptors.</summary>
internal static class TrustedApplianceReleaseFiles
{
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

    internal static ApplianceRecoveryStatus ReadRecoveryStatusOrRequireRecovery(string recoveryDirectory)
    {
        try { return ReadRecoveryStatus(recoveryDirectory); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return new() { RecoveryRequired = true };
        }
    }

    internal static ApplianceRecoveryStatus ReadRecoveryStatus(string recoveryDirectory)
    {
        using SafeFileHandle recovery = OpenAbsoluteDirectory(recoveryDirectory);
        bool fenced = ReadOptionalFile(recovery, "maintenance.json") is not null;
        byte[]? journalBytes = ReadOptionalFile(recovery, "operation.json");
        fenced |= ReadOptionalFile(recovery, "maintenance.json") is not null;
        if (journalBytes is null)
            return new() { RecoveryRequired = fenced };

        using JsonDocument journal = JsonDocument.Parse(journalBytes);
        JsonElement record = journal.RootElement;
        string phase = record.GetProperty("phase").GetString() ?? string.Empty;
        string pruneStatus = string.Empty;
        if (record.TryGetProperty("snapshotPruneStatus", out JsonElement prune))
        {
            string? reportedStatus = prune.ValueKind == JsonValueKind.String ? prune.GetString() : null;
            pruneStatus = reportedStatus is "pending" or "succeeded" or "failed" ? reportedStatus : "unverified";
        }
        return new()
        {
            RecoveryRequired = fenced || phase is not ("committed" or "completed" or "aborted"),
            Phase = phase,
            OperationId = record.GetProperty("operationId").GetString() ?? string.Empty,
            SourceVersion = record.GetProperty("sourceRelease").GetString() ?? string.Empty,
            TargetVersion = record.GetProperty("targetRelease").GetString() ?? string.Empty,
            UpdatedUnixMilliseconds = record.TryGetProperty("updatedUnixMilliseconds", out JsonElement updated) && updated.TryGetInt64(out long value) ? value : 0,
            SnapshotPruneStatus = pruneStatus,
        };
    }

    // A malformed optional receipt is ignored; it must never alter recovery fencing.
    internal static AppliancePreflightStatus? ReadPreflightStatus(string recoveryDirectory)
    {
        try
        {
            using SafeFileHandle recovery = OpenAbsoluteDirectory(recoveryDirectory);
            byte[]? bytes = ReadOptionalFile(recovery, "activation-preflight.json");
            if (bytes is null) return null;
            using JsonDocument document = JsonDocument.Parse(bytes);
            JsonElement root = document.RootElement;
            string? request = root.GetProperty("requestId").GetString();
            string? source = root.GetProperty("sourceVersion").GetString();
            string? target = root.GetProperty("targetVersion").GetString();
            string? code = root.GetProperty("code").GetString();
            if (request is not { Length: 32 } || request.Any(static c => !char.IsAsciiDigit(c) && c is not (>= 'a' and <= 'f'))
                || source is null || !IsVersionDirectoryName(source) || target is null || !IsVersionDirectoryName(target)
                || code != "insufficient_space") return null;
            return new() { RequestId = request, SourceVersion = source, TargetVersion = target, Code = code };
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }

    private static byte[]? ReadOptionalFile(SafeFileHandle directory, string name)
    {
        try { return ReadFile(directory, name, 16 * 1024); }
        catch (FileNotFoundException) { return null; }
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

#pragma warning disable SYSLIB1054, CA2101
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "readlinkat", SetLastError = true)]
    private static extern nint ReadLinkAt(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr buffer, nuint size);
#pragma warning restore SYSLIB1054, CA2101
}
