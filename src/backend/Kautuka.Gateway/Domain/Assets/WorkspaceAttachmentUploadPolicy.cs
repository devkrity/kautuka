using Kautuka.ChannelContracts.Assets;

namespace Kautuka.Gateway.Domain.Assets;

internal static class WorkspaceAttachmentUploadPolicy
{
    public const string ExecutableUploadBlockedMessage = AttachmentUploadPolicy.ExecutableUploadBlockedMessage;

    private static readonly string[] reservedWindowsNames = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    public static string NormalizeLeafFileName(string fileName, string label = "File name", IReadOnlySet<string>? reservedNames = null)
    {
        string rawName = string.IsNullOrWhiteSpace(fileName) ? string.Empty : fileName;
        int separator = Math.Max(rawName.LastIndexOf('/'), rawName.LastIndexOf('\\'));
        string rawLeaf = separator >= 0 ? rawName[(separator + 1)..] : rawName;
        if (OperatingSystem.IsWindows() && rawLeaf.Length > 0 && rawLeaf.TrimEnd(' ', '.').Length != rawLeaf.Length)
        {
            throw new InvalidOperationException($"{label} is invalid.");
        }

        string safeFileName = string.IsNullOrWhiteSpace(fileName) ? string.Empty : Path.GetFileName(fileName.Trim());
        if (string.IsNullOrWhiteSpace(safeFileName))
        {
            throw new InvalidOperationException($"{label} is required.");
        }

        if (safeFileName.Length > 128)
        {
            throw new InvalidOperationException($"{label}s must be 128 characters or fewer.");
        }

        if (safeFileName is "." or "..")
        {
            throw new InvalidOperationException($"{label} is invalid.");
        }

        if (safeFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException($"{label} contains invalid characters.");
        }

        if (reservedNames?.Contains(safeFileName) is true)
        {
            throw new InvalidOperationException($"{label} is reserved and cannot be used.");
        }

        if (OperatingSystem.IsWindows() && IsReservedWindowsDeviceName(safeFileName))
        {
            throw new InvalidOperationException($"{label} is reserved and cannot be used.");
        }

        return safeFileName;
    }

    public static bool IsReservedWindowsDeviceName(string fileName)
    {
        string name = fileName.TrimEnd(' ', '.');
        int colon = name.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
        {
            name = name[..colon];
        }

        string withoutExtension = Path.GetFileNameWithoutExtension(name);
        return reservedWindowsNames.Contains(withoutExtension, StringComparer.OrdinalIgnoreCase);
    }

    public static string NormalizeContentType(string contentType, string fileName, bool enforceBlockedUploadPolicy)
        => AttachmentUploadPolicy.NormalizeContentType(contentType, fileName, enforceBlockedUploadPolicy);

    public static void SetSafeHostFilePermissions(string fullPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(fullPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    public static string InferContentType(string fileName)
        => AttachmentUploadPolicy.InferContentType(fileName);
}

internal sealed record WorkspaceAttachmentReferenceSummary(
    string RelativePath,
    string FileName,
    int ReferenceCount);

internal sealed record WorkspaceAttachmentFileSummary(
    string RelativePath,
    string FileName,
    long SizeBytes,
    DateTimeOffset ModifiedAt);
