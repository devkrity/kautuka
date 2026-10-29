namespace Kautuka.UpdateService.Hosting;

public sealed class UpdateOptions
{
    public const string SectionName = "Update";

    public bool Enabled { get; set; } = true;

    public string? RepositoryOwner { get; set; }

    public string? RepositoryName { get; set; }

    // Runtime policy store uses this as the initial/default channel only.
    public string ReleaseChannel { get; set; } = "stable";

    public string PlatformAssetName { get; set; } = "kautuka-platform-linux-x64.tar.gz";

    public string PlatformReleaseAssetName { get; set; } = "kautuka-platform-release.json";

    public string PlatformReleaseSignatureAssetName { get; set; } = "kautuka-platform-release.sig";

    public string? ReleaseManifestUrl { get; set; }

    public string? CatalogManifestUrl { get; set; }

    public string StateRootPath { get; set; } = string.Empty;

    public string? ManifestPublicKeyPath { get; set; }

    public string? CatalogManifestPublicKeyPath { get; set; }

    /// <summary>
    /// Development-only escape hatch. When true, missing signature/hash are
    /// permitted. Production forces this false; see <c>Program.cs</c>.
    /// </summary>
    public bool AllowUnsignedLocalFeeds { get; set; }

    public string? HostServiceActivationCommand { get; set; }

    public string ApplyReleaseHelperPath { get; set; } = "/usr/local/lib/kautuka/apply-release";

    public bool ApplyReleaseHelperUseSudo { get; set; } = true;

    public TimeSpan HostServiceActivationTimeout { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan DiscoveryInterval { get; set; } = TimeSpan.FromDays(1);

    public TimeSpan CatalogRefreshInterval { get; set; } = TimeSpan.FromDays(1);
}
