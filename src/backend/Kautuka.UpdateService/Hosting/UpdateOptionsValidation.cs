using Microsoft.Extensions.Options;

namespace Kautuka.UpdateService.Hosting;

internal sealed class UpdateOptionsValidation : IValidateOptions<UpdateOptions>
{
    public ValidateOptionsResult Validate(string? name, UpdateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        if (string.IsNullOrWhiteSpace(options.StateRootPath))
        {
            return ValidateOptionsResult.Fail("Update:StateRootPath must be configured when updates are enabled.");
        }

        if (options.DiscoveryInterval <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail("Update:DiscoveryInterval must be greater than zero.");
        }

        if (options.CatalogRefreshInterval <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail("Update:CatalogRefreshInterval must be greater than zero.");
        }

        if (!string.IsNullOrWhiteSpace(options.ReleaseManifestUrl)
            && !Uri.TryCreate(options.ReleaseManifestUrl, UriKind.Absolute, out _))
        {
            return ValidateOptionsResult.Fail("Update:ReleaseManifestUrl must be an absolute URI when configured.");
        }

        if (!string.IsNullOrWhiteSpace(options.PreviewReleaseManifestUrl)
            && !Uri.TryCreate(options.PreviewReleaseManifestUrl, UriKind.Absolute, out _))
        {
            return ValidateOptionsResult.Fail("Update:PreviewReleaseManifestUrl must be an absolute URI when configured.");
        }

        if (!string.IsNullOrWhiteSpace(options.StableReleaseManifestUrl)
            && !Uri.TryCreate(options.StableReleaseManifestUrl, UriKind.Absolute, out _))
        {
            return ValidateOptionsResult.Fail("Update:StableReleaseManifestUrl must be an absolute URI when configured.");
        }

        if (!string.IsNullOrWhiteSpace(options.CatalogManifestUrl)
            && !Uri.TryCreate(options.CatalogManifestUrl, UriKind.Absolute, out _))
        {
            return ValidateOptionsResult.Fail("Update:CatalogManifestUrl must be an absolute URI when configured.");
        }

        bool hasReleaseManifestUrl = !string.IsNullOrWhiteSpace(options.ReleaseManifestUrl)
            || !string.IsNullOrWhiteSpace(options.PreviewReleaseManifestUrl)
            || !string.IsNullOrWhiteSpace(options.StableReleaseManifestUrl);
        bool hasRepositorySource = !string.IsNullOrWhiteSpace(options.RepositoryOwner)
            && !string.IsNullOrWhiteSpace(options.RepositoryName);
        bool hasCatalogManifestUrl = !string.IsNullOrWhiteSpace(options.CatalogManifestUrl);

        if (!hasReleaseManifestUrl && !hasRepositorySource && !hasCatalogManifestUrl)
        {
            return ValidateOptionsResult.Success;
        }

        if (!string.IsNullOrWhiteSpace(options.ManifestPublicKeyPath)
            && !File.Exists(options.ManifestPublicKeyPath))
        {
            return ValidateOptionsResult.Fail($"Update:ManifestPublicKeyPath was configured but the file was not found: {options.ManifestPublicKeyPath}");
        }

        if (!string.IsNullOrWhiteSpace(options.CatalogManifestPublicKeyPath)
            && !File.Exists(options.CatalogManifestPublicKeyPath))
        {
            return ValidateOptionsResult.Fail($"Update:CatalogManifestPublicKeyPath was configured but the file was not found: {options.CatalogManifestPublicKeyPath}");
        }

        if (!options.AllowUnsignedLocalFeeds)
        {
            if ((hasReleaseManifestUrl || hasRepositorySource)
                && (string.IsNullOrWhiteSpace(options.ManifestPublicKeyPath)
                    || !File.Exists(options.ManifestPublicKeyPath)))
            {
                return ValidateOptionsResult.Fail(
                    "Update:ManifestPublicKeyPath must point to an existing trusted public key when AllowUnsignedLocalFeeds is false.");
            }

            if (hasCatalogManifestUrl
                && (string.IsNullOrWhiteSpace(options.CatalogManifestPublicKeyPath)
                    || !File.Exists(options.CatalogManifestPublicKeyPath)))
            {
                return ValidateOptionsResult.Fail(
                    "Update:CatalogManifestPublicKeyPath must point to an existing trusted public key when a catalog manifest URL is configured and AllowUnsignedLocalFeeds is false.");
            }
        }

        if ((!string.IsNullOrWhiteSpace(options.HostServiceActivationCommand)
                || !string.IsNullOrWhiteSpace(options.ApplyReleaseHelperPath))
            && options.HostServiceActivationTimeout <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail("Update:HostServiceActivationTimeout must be greater than zero when an apply helper is configured.");
        }

        return ValidateOptionsResult.Success;
    }
}
