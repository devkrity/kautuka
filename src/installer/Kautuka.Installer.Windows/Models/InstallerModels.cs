using System.Text.Json.Serialization;

namespace Kautuka.Installer.Windows.Models;

internal sealed record WindowsApplianceInstallRequest
{
    public string? ReleaseId { get; init; }
    public string? RootfsPath { get; init; }
    public string? ReleaseManifestUrl { get; init; }
    public string? ManifestPublicKeyPath { get; init; }
    /// <summary>
    /// Development-only. When false (default), network installs require a signed
    /// manifest and artifact SHA-256. Local RootfsPath without a remote manifest
    /// remains allowed.
    /// </summary>
    public bool AllowUnsignedLocalFeeds { get; init; }
    public string ReleaseChannel { get; init; } = "stable";
    public string RepositoryOwner { get; init; } = "devkrity";
    public string RepositoryName { get; init; } = "kautuka";
    public string? ReleaseTag { get; init; }
    public string DistroName { get; init; } = "kautuka";
    public string? KautukaRoot { get; init; }
    public string InstallDir { get; init; }
        = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kautuka", "WSL");
    public bool Resume { get; init; }
    /// <summary>
    /// When true, the installer opens TCP 20229 to the Windows Private network
    /// profile and forwards it to the localhost appliance. Default is off.
    /// </summary>
    public bool AllowLanAccess { get; init; }
}

internal sealed record WindowsApplianceUninstallRequest
{
    public string DistroName { get; init; } = "kautuka";
    public string? InstallDir { get; init; }
    public string? KautukaRoot { get; init; }
    public bool DeleteData { get; init; }
}

internal sealed record ApplianceSuperviseRequest
{
    public string DistroName { get; init; } = "kautuka";
    public required string InstallDir { get; init; }
}

internal sealed record WindowsApplianceStatusResponse
{
    public required bool IsWindows { get; init; }
    public required bool IsAdministrator { get; init; }
    public required bool WslAvailable { get; init; }
    public required bool WslFeaturesEnabled { get; init; }
    public required bool WslReady { get; init; }
    public required bool HasResumeState { get; init; }
    public required string DefaultKautukaRoot { get; init; }
    public required string DefaultInstallDir { get; init; }
    public required IReadOnlyDictionary<string, string> FeatureStates { get; init; }
}

internal sealed record WindowsApplianceBootstrapResponse
{
    public required WindowsApplianceStatusResponse Status { get; init; }
    public required WindowsApplianceInstallRequest InstallDefaults { get; init; }
    public required WindowsApplianceUninstallRequest UninstallDefaults { get; init; }
    public required string ActionToken { get; init; }
}

internal sealed record WindowsApplianceLaunchOptions
{
    public required WindowsApplianceInstallRequest InstallDefaults { get; init; }
    public required WindowsApplianceUninstallRequest UninstallDefaults { get; init; }
}

internal sealed record GitHubReleaseInfo
{
    public long Id { get; init; }

    [JsonPropertyName("tag_name")]
    public string TagName { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public bool Draft { get; init; }

    public bool Prerelease { get; init; }

    public List<GitHubReleaseAssetInfo> Assets { get; init; } = [];
}

internal sealed record GitHubReleaseAssetInfo
{
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("url")]
    public string ApiUrl { get; init; } = string.Empty;

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; init; } = string.Empty;
}

internal sealed record InstallerPlatformReleaseManifest
{
    public int SchemaVersion { get; init; }
    public string? ReleaseId { get; init; }
    public string? ReleaseTag { get; init; }
    public string? Channel { get; init; }
    public string? Version { get; init; }
    public string? PublishedAt { get; init; }
    public string? MinimumUpdaterVersion { get; init; }
    public InstallerPlatformReleaseSignature? Signature { get; init; }
    public List<InstallerPlatformReleaseArtifact> Artifacts { get; init; } = [];
}

internal sealed record InstallerPlatformReleaseSignature
{
    public string Algorithm { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string? Url { get; init; }
    public string? Format { get; init; }
}

internal sealed record InstallerPlatformReleaseArtifact
{
    public string Name { get; init; } = string.Empty;
    public string Platform { get; init; } = string.Empty;
    public string? Path { get; init; }
    public string? Url { get; init; }
    public string Sha256 { get; init; } = string.Empty;
    public long Size { get; init; }
}

internal sealed record WindowsApplianceInstallState
{
    public required string ReleaseId { get; init; }
    public string? ReleaseTag { get; init; }
    public required string RootfsPath { get; init; }
    public required string DistroName { get; init; }
    public required string KautukaRoot { get; init; }
    public required string InstallDir { get; init; }
    public required string InstallerExecutablePath { get; init; }
    public bool AllowLanAccess { get; init; }
}

// ── JSON context ─────────────────────────────────────────────────────────────

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WindowsApplianceInstallRequest))]
[JsonSerializable(typeof(WindowsApplianceUninstallRequest))]
[JsonSerializable(typeof(WindowsApplianceStatusResponse))]
[JsonSerializable(typeof(WindowsApplianceBootstrapResponse))]
[JsonSerializable(typeof(WindowsApplianceInstallState))]
[JsonSerializable(typeof(GitHubReleaseInfo))]
[JsonSerializable(typeof(List<GitHubReleaseInfo>))]
[JsonSerializable(typeof(GitHubReleaseAssetInfo))]
[JsonSerializable(typeof(InstallerPlatformReleaseManifest))]
[JsonSerializable(typeof(InstallerPlatformReleaseSignature))]
[JsonSerializable(typeof(InstallerPlatformReleaseArtifact))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(string))]
internal sealed partial class InstallerJsonContext : JsonSerializerContext;
