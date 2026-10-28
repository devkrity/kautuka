namespace Kautuka.ChannelContracts.Assets;

public enum AttachmentCategory
{
    DirectText = 0,
    DirectImage = 1,
    ParseRequiredDocument = 2,
    UnsupportedBinary = 3,
}

public static class AttachmentCategoryClassifier
{
    private static readonly HashSet<string> defaultParseRequiredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".doc", ".pptx", ".ppt", ".xlsx", ".xls",
    };

    public static AttachmentCategory Classify(string fileName, string contentType)
        => Classify(fileName, contentType, defaultParseRequiredExtensions);

    public static AttachmentCategory Classify(string fileName, string contentType, ISet<string> parseRequiredExtensions)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(contentType);
        ArgumentNullException.ThrowIfNull(parseRequiredExtensions);

        string ext = Path.GetExtension(fileName).ToUpperInvariant();

        if (ext is ".TXT" or ".MD" or ".JSON" or ".CSV" or ".LOG" or ".XML" or ".YAML" or ".YML" or ".TOML")
        {
            return AttachmentCategory.DirectText;
        }

        if (ext is ".CS" or ".TS" or ".TSX" or ".JSX" or ".JS" or ".PY" or ".RS" or ".GO" or ".RB" or ".JAVA" or ".CPP" or ".C" or ".H" or ".SQL" or ".HTML" or ".CSS" or ".SCSS" or ".LESS")
        {
            return AttachmentCategory.DirectText;
        }

        if (ext is ".JPG" or ".JPEG" or ".PNG" or ".GIF" or ".WEBP" or ".BMP" or ".SVG" or ".ICO")
        {
            return AttachmentCategory.DirectImage;
        }

        if (parseRequiredExtensions.Contains(ext))
        {
            return AttachmentCategory.ParseRequiredDocument;
        }

        if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return AttachmentCategory.DirectImage;
        }

        if (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
        {
            return AttachmentCategory.DirectText;
        }

        return AttachmentCategory.UnsupportedBinary;
    }
}

public static class AttachmentUploadPolicy
{
    public const string ExecutableUploadBlockedMessage = "Executable or installable files are blocked for safety. Upload documents, spreadsheets, archives, code, images, audio, video, and other non-executable files instead.";

    private static readonly HashSet<string> blockedUploadExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".apk", ".appimage", ".appinstaller", ".appx", ".appxbundle", ".bat", ".bash", ".bin", ".cmd", ".com", ".command",
        ".cpl", ".deb", ".dll", ".dmg", ".drv", ".exe", ".fish", ".hta", ".img", ".ipa", ".iso", ".jar", ".jse",
        ".ksh", ".lib", ".lnk", ".msc", ".msi", ".msix", ".msixbundle", ".msp", ".ocx", ".pkg", ".ps1", ".ps1xml",
        ".psd1", ".psm1", ".pyz", ".rpm", ".run", ".scr", ".sh", ".so", ".sys", ".vb", ".vbe", ".vbs", ".wsf",
        ".wsh", ".xll", ".zsh",
    };

    private static readonly HashSet<string> blockedUploadContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/java-archive", "application/vnd.android.package-archive", "application/vnd.microsoft.portable-executable", "application/x-apple-diskimage",
        "application/x-bat", "application/x-cmd", "application/x-deb", "application/x-dosexec", "application/x-executable", "application/x-iso9660-image",
        "application/x-mach-binary", "application/x-msdos-program", "application/x-msdownload", "application/x-msi", "application/x-ms-shortcut", "application/x-powershell",
        "application/x-rpm", "application/x-shellscript", "application/x-sh",
    };

    public static string NormalizeContentType(string contentType, string fileName, bool enforceBlockedUploadPolicy)
    {
        string normalized = string.IsNullOrWhiteSpace(contentType)
            || string.Equals(contentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase)
            ? InferContentType(fileName)
            : contentType.Trim();

        if (enforceBlockedUploadPolicy && IsBlockedUserUpload(fileName, normalized))
        {
            throw new InvalidOperationException(ExecutableUploadBlockedMessage);
        }

        return normalized;
    }

    public static string InferContentType(string fileName)
    {
        string extension = Path.GetExtension(fileName).ToUpperInvariant();
        return extension switch
        {
            ".TXT" => "text/plain",
            ".MD" => "text/markdown",
            ".LOG" => "text/plain",
            ".YAML" or ".YML" => "application/yaml",
            ".JSON" => "application/json",
            ".CSV" => "text/csv",
            ".TSV" => "text/tab-separated-values",
            ".HTML" => "text/html",
            ".XML" => "application/xml",
            ".PDF" => "application/pdf",
            ".RTF" => "application/rtf",
            ".DOC" => "application/msword",
            ".DOCX" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".XLS" => "application/vnd.ms-excel",
            ".XLSX" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".PPT" => "application/vnd.ms-powerpoint",
            ".PPTX" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".ODT" => "application/vnd.oasis.opendocument.text",
            ".ODS" => "application/vnd.oasis.opendocument.spreadsheet",
            ".ODP" => "application/vnd.oasis.opendocument.presentation",
            ".PNG" => "image/png",
            ".JPG" or ".JPEG" => "image/jpeg",
            ".GIF" => "image/gif",
            ".WEBP" => "image/webp",
            ".BMP" => "image/bmp",
            ".SVG" => "image/svg+xml",
            ".MP3" => "audio/mpeg",
            ".WAV" => "audio/wav",
            ".M4A" => "audio/mp4",
            ".MP4" => "video/mp4",
            ".MOV" => "video/quicktime",
            ".WEBM" => "video/webm",
            ".ZIP" => "application/zip",
            ".7Z" => "application/x-7z-compressed",
            ".TAR" => "application/x-tar",
            ".GZ" => "application/gzip",
            _ => "application/octet-stream",
        };
    }

    public static bool IsBlockedUserUpload(string fileName, string contentType)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(contentType);

        string extension = Path.GetExtension(fileName);
        if (!string.IsNullOrWhiteSpace(extension) && blockedUploadExtensions.Contains(extension))
        {
            return true;
        }

        if (blockedUploadContentTypes.Contains(contentType))
        {
            return true;
        }

        return contentType.StartsWith("application/x-ms", StringComparison.OrdinalIgnoreCase)
            || contentType.StartsWith("application/x-dosexec", StringComparison.OrdinalIgnoreCase)
            || contentType.StartsWith("text/x-shellscript", StringComparison.OrdinalIgnoreCase);
    }
}
