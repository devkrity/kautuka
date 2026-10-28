using System.Net.Http.Headers;
using System.Text.Json;

using Kautuka.Telemetry.Security;

namespace Kautuka.Installer.Windows.Installation;

internal static partial class WindowsApplianceInstaller
{
    private static async Task<WindowsApplianceInstallState> ResolveStateAsync(
        WindowsApplianceInstallRequest request,
        Func<string, Task> log,
        CancellationToken cancellationToken)
    {
        var kautukaRoot = string.IsNullOrWhiteSpace(request.KautukaRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Kautuka")
            : request.KautukaRoot;
        var installDir = string.IsNullOrWhiteSpace(request.InstallDir)
            ? DeriveInstallDir(string.IsNullOrWhiteSpace(request.DistroName) ? defaultDistroName : request.DistroName)
            : request.InstallDir;
        var resolvedReleaseTag = string.IsNullOrWhiteSpace(request.ReleaseTag) ? null : request.ReleaseTag;
        GitHubResolvedRelease? resolvedRelease = null;
        InstallerPlatformReleaseManifest? releaseManifest = null;
        Uri? releaseManifestUri = TryCreateAbsoluteUri(request.ReleaseManifestUrl);
        bool requireVerification = !request.AllowUnsignedLocalFeeds;
        string? manifestPublicKeyPath = ResolveManifestPublicKeyPath(request);

        if (string.IsNullOrWhiteSpace(request.RootfsPath) && releaseManifestUri is not null)
        {
            EnsureApprovedInstallerSourceUrl(releaseManifestUri.ToString(), "Release manifest");
            releaseManifest = await DownloadReleaseManifestAsync(
                releaseManifestUri,
                requireVerification,
                manifestPublicKeyPath,
                cancellationToken).ConfigureAwait(false);

            resolvedReleaseTag ??= string.IsNullOrWhiteSpace(releaseManifest.ReleaseTag)
                ? null
                : releaseManifest.ReleaseTag;

            await log($"Resolved install assets from local manifest {releaseManifestUri}.").ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(resolvedReleaseTag)
            && string.IsNullOrWhiteSpace(request.RootfsPath)
            && releaseManifest is null)
        {
            resolvedRelease = await ResolveReleaseForChannelAsync(
                request.ReleaseChannel,
                request.RepositoryOwner,
                request.RepositoryName,
                cancellationToken).ConfigureAwait(false);
            resolvedReleaseTag = resolvedRelease.TagName;
            await log($"Resolved {request.ReleaseChannel} channel to {resolvedRelease.TagName}.").ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(request.RootfsPath) && !string.IsNullOrWhiteSpace(resolvedReleaseTag))
        {
            if (releaseManifest is null)
            {
                releaseManifest = await DownloadReleaseManifestAsync(
                    resolvedReleaseTag,
                    request.RepositoryOwner,
                    request.RepositoryName,
                    requireVerification,
                    manifestPublicKeyPath,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        var releaseId = !string.IsNullOrWhiteSpace(request.ReleaseId)
            ? request.ReleaseId!
            : !string.IsNullOrWhiteSpace(releaseManifest?.ReleaseId)
                ? releaseManifest.ReleaseId!
            : !string.IsNullOrWhiteSpace(resolvedReleaseTag)
                ? resolvedReleaseTag!
                : throw new InvalidOperationException("The installer could not determine which release assets to use. Publish the installer with embedded release metadata or provide explicit asset paths.");
        var downloadDirectory = Path.Combine(Path.GetTempPath(), "kautuka-installer");
        Directory.CreateDirectory(downloadDirectory);

        var rootfsPath = await ResolveAssetPathAsync(
            request.RootfsPath,
            resolvedReleaseTag,
            request.RepositoryOwner,
            request.RepositoryName,
            rootfsAssetName,
            releaseManifest,
            releaseManifestUri,
            downloadDirectory,
            requireVerification,
            log,
            cancellationToken).ConfigureAwait(false);

        return new WindowsApplianceInstallState
        {
            ReleaseId = releaseId,
            ReleaseTag = resolvedReleaseTag,
            RootfsPath = rootfsPath,
            DistroName = string.IsNullOrWhiteSpace(request.DistroName) ? defaultDistroName : request.DistroName,
            KautukaRoot = ResolveWindowsPathWithStableCasing(kautukaRoot),
            InstallDir = ResolveWindowsPathWithStableCasing(installDir),
            InstallerExecutablePath = string.Empty,
            AllowLanAccess = request.AllowLanAccess,
        };
    }

    private static string ResolveWindowsPathWithStableCasing(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows())
            return fullPath;

        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException($"Invalid Windows path: {path}");
        var relativePath = fullPath[root.Length..];
        if (string.IsNullOrEmpty(relativePath))
            return root;

        var segments = relativePath.Split(
            ['\\', '/'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
            return root;

        var resolvedSegments = new List<string>(segments.Length);
        var currentPath = root;
        var preserveRemainingSegments = false;

        foreach (var segment in segments)
        {
            if (preserveRemainingSegments || !Directory.Exists(currentPath))
            {
                preserveRemainingSegments = true;
                resolvedSegments.Add(segment);
                currentPath = Path.Combine(currentPath, segment);
                continue;
            }

            var matchingEntry = Directory
                .EnumerateFileSystemEntries(currentPath)
                .Select(Path.GetFileName)
                .FirstOrDefault(name => string.Equals(name, segment, StringComparison.OrdinalIgnoreCase));

            var resolvedSegment = string.IsNullOrWhiteSpace(matchingEntry) ? segment : matchingEntry;
            if (matchingEntry is null)
                preserveRemainingSegments = true;

            resolvedSegments.Add(resolvedSegment);
            currentPath = Path.Combine(currentPath, resolvedSegment);
        }

        return Path.Combine(root, Path.Combine(resolvedSegments.ToArray()));
    }

    private static async Task<string> ResolveAssetPathAsync(
        string? providedPath,
        string? releaseTag,
        string repositoryOwner,
        string repositoryName,
        string assetName,
        InstallerPlatformReleaseManifest? releaseManifest,
        Uri? releaseManifestUri,
        string downloadDirectory,
        bool requireVerification,
        Func<string, Task> log,
        CancellationToken cancellationToken)
    {
        InstallerPlatformReleaseArtifact? manifestArtifact = ResolveManifestArtifact(releaseManifest, assetName);

        if (!string.IsNullOrWhiteSpace(providedPath))
        {
            if (!File.Exists(providedPath))
                throw new InvalidOperationException($"Asset not found: {providedPath}");

            string fullPath = Path.GetFullPath(providedPath);
            // Local explicit paths only enforce hash when the manifest supplies one or
            // fail-closed verification is on and a remote manifest was resolved.
            ValidateDownloadedAsset(fullPath, manifestArtifact, requireVerification && releaseManifest is not null);
            return fullPath;
        }

        if (string.IsNullOrWhiteSpace(releaseTag))
            throw new InvalidOperationException($"Either a local asset path or ReleaseTag must be provided for {assetName}.");

        var destinationPath = Path.Combine(
            downloadDirectory,
            SanitizeCacheSegment(releaseTag),
            assetName);
        if (File.Exists(destinationPath))
        {
            if (TryValidateExistingAsset(destinationPath, manifestArtifact, requireVerification, out string? validationError))
            {
                await log($"Using cached {assetName} for release {releaseTag}.").ConfigureAwait(false);
                return destinationPath;
            }

            await log($"Discarding cached {assetName} for release {releaseTag}: {validationError}").ConfigureAwait(false);
            File.Delete(destinationPath);
        }

        GitHubAssetDownload assetUrl = await ResolveReleaseAssetDownloadAsync(
            releaseTag,
            repositoryOwner,
            repositoryName,
            assetName,
            manifestArtifact,
            releaseManifestUri,
            cancellationToken).ConfigureAwait(false);

        EnsureApprovedInstallerSourceUrl(assetUrl.Url, $"Release asset '{assetName}'");
        await DownloadFileAsync(assetUrl, destinationPath, log, cancellationToken).ConfigureAwait(false);
        ValidateDownloadedAsset(destinationPath, manifestArtifact, requireVerification);
        return destinationPath;
    }

    private static bool TryValidateExistingAsset(
        string destinationPath,
        InstallerPlatformReleaseArtifact? manifestArtifact,
        bool requireVerification,
        out string? validationError)
    {
        try
        {
            ValidateDownloadedAsset(destinationPath, manifestArtifact, requireVerification);
            validationError = null;
            return true;
        }
        catch (InvalidOperationException ex)
        {
            validationError = ex.Message;
            return false;
        }
    }

    private static InstallerPlatformReleaseArtifact? ResolveManifestArtifact(InstallerPlatformReleaseManifest? releaseManifest, string assetName)
    {
        if (releaseManifest is null || releaseManifest.Artifacts.Count == 0)
            return null;

        InstallerPlatformReleaseArtifact? byName = releaseManifest.Artifacts.FirstOrDefault(item =>
            string.Equals(item.Name, assetName, StringComparison.OrdinalIgnoreCase));
        if (byName is not null)
            return byName;

        return releaseManifest.Artifacts.FirstOrDefault(item =>
            string.Equals(item.Platform, "wsl-amd64", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<InstallerPlatformReleaseManifest> DownloadReleaseManifestAsync(
        Uri releaseManifestUri,
        bool requireVerification,
        string? manifestPublicKeyPath,
        CancellationToken cancellationToken)
    {
        using var client = CreateHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, releaseManifestUri);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        byte[] manifestBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        InstallerPlatformReleaseManifest manifest = JsonSerializer.Deserialize(
            manifestBytes,
            InstallerJsonContext.Default.InstallerPlatformReleaseManifest)
            ?? throw new InvalidOperationException($"Failed to parse platform release manifest from '{releaseManifestUri}'.");

        await VerifyInstallerManifestSignatureAsync(
            manifestBytes,
            manifest,
            releaseManifestUri,
            requireVerification,
            manifestPublicKeyPath,
            cancellationToken).ConfigureAwait(false);

        return manifest;
    }

    private static async Task<InstallerPlatformReleaseManifest> DownloadReleaseManifestAsync(
        string releaseTag,
        string repositoryOwner,
        string repositoryName,
        bool requireVerification,
        string? manifestPublicKeyPath,
        CancellationToken cancellationToken)
    {
        GitHubAssetDownload manifestDownload = await GetReleaseAssetUrlAsync(
            releaseTag,
            repositoryOwner,
            repositoryName,
            releaseManifestAssetName,
            cancellationToken).ConfigureAwait(false);

        EnsureApprovedInstallerSourceUrl(manifestDownload.Url, "Release manifest");

        using var client = CreateHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(manifestDownload.Url));

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        byte[] manifestBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        InstallerPlatformReleaseManifest manifest = JsonSerializer.Deserialize(
            manifestBytes,
            InstallerJsonContext.Default.InstallerPlatformReleaseManifest)
            ?? throw new InvalidOperationException($"Failed to parse platform release manifest for tag '{releaseTag}'.");

        Uri manifestUri = new(manifestDownload.Url);
        await VerifyInstallerManifestSignatureAsync(
            manifestBytes,
            manifest,
            manifestUri,
            requireVerification,
            manifestPublicKeyPath,
            cancellationToken).ConfigureAwait(false);

        return manifest;
    }

    private static async Task VerifyInstallerManifestSignatureAsync(
        byte[] manifestBytes,
        InstallerPlatformReleaseManifest manifest,
        Uri releaseManifestUri,
        bool requireVerification,
        string? manifestPublicKeyPath,
        CancellationToken cancellationToken)
    {
        if (manifest.Signature is null)
        {
            if (requireVerification)
            {
                throw new InvalidOperationException(
                    "Platform release manifest is missing signature metadata. Fail-closed verification rejects unsigned manifests.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(manifestPublicKeyPath) || !File.Exists(manifestPublicKeyPath))
        {
            throw new InvalidOperationException(
                "Platform release signature was published, but no trusted manifest public key path was configured.");
        }

        string? signatureUrl = !string.IsNullOrWhiteSpace(manifest.Signature.Url)
            ? manifest.Signature.Url
            : !string.IsNullOrWhiteSpace(manifest.Signature.Path)
                ? new Uri(releaseManifestUri, manifest.Signature.Path).ToString()
                : null;

        if (string.IsNullOrWhiteSpace(signatureUrl))
        {
            throw new InvalidOperationException(
                "Platform release signature metadata was present but no signature URL could be resolved.");
        }

        EnsureApprovedInstallerSourceUrl(signatureUrl, "Release signature");

        using var client = CreateHttpClient();
        string signatureContent = await client.GetStringAsync(new Uri(signatureUrl), cancellationToken).ConfigureAwait(false);
        string publicKeyPem = await File.ReadAllTextAsync(manifestPublicKeyPath, cancellationToken).ConfigureAwait(false);
        if (!ReleaseManifestSignatureVerifier.TryVerify(manifestBytes, signatureContent, publicKeyPem, out string? verificationError))
        {
            throw new InvalidOperationException(verificationError ?? "Platform release signature verification failed.");
        }
    }

    private static void ValidateDownloadedAsset(
        string destinationPath,
        InstallerPlatformReleaseArtifact? manifestArtifact,
        bool requireVerification)
    {
        if (manifestArtifact is null)
        {
            if (requireVerification)
            {
                throw new InvalidOperationException(
                    "Platform release artifact metadata was missing. Fail-closed verification requires artifact SHA-256.");
            }

            return;
        }

        ReleaseArtifactHashVerifier.Validate(
            destinationPath,
            manifestArtifact.Sha256,
            manifestArtifact.Size,
            requireHash: requireVerification,
            artifactName: manifestArtifact.Name);
    }

    private static string? ResolveManifestPublicKeyPath(WindowsApplianceInstallRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ManifestPublicKeyPath))
        {
            return request.ManifestPublicKeyPath;
        }

        string? fromEnv = Environment.GetEnvironmentVariable("KAUTUKA_MANIFEST_PUBLIC_KEY_PATH");
        return string.IsNullOrWhiteSpace(fromEnv) ? null : fromEnv;
    }

    private static void EnsureApprovedInstallerSourceUrl(string assetUrl, string purpose)
    {
        if (!Uri.TryCreate(assetUrl, UriKind.Absolute, out Uri? uri))
        {
            throw new InvalidOperationException($"{purpose} URL is not an absolute URI: {assetUrl}");
        }

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (uri.IsLoopback
            && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException(
            $"{purpose} URL must be https or loopback http. Rejected: {assetUrl}");
    }

    private static async Task<GitHubAssetDownload> ResolveReleaseAssetDownloadAsync(
        string releaseTag,
        string repositoryOwner,
        string repositoryName,
        string assetName,
        InstallerPlatformReleaseArtifact? manifestArtifact,
        Uri? releaseManifestUri,
        CancellationToken cancellationToken)
    {
        Uri? manifestAssetUri = ResolveManifestAssetUri(manifestArtifact, releaseManifestUri);
        if (manifestAssetUri is not null)
        {
            return new GitHubAssetDownload(manifestAssetUri.ToString());
        }

        string resolvedAssetName = !string.IsNullOrWhiteSpace(manifestArtifact?.Name)
            ? manifestArtifact.Name
            : assetName;

        return await GetReleaseAssetUrlAsync(
            releaseTag,
            repositoryOwner,
            repositoryName,
            resolvedAssetName,
            cancellationToken).ConfigureAwait(false);
    }

    private static Uri? ResolveManifestAssetUri(InstallerPlatformReleaseArtifact? manifestArtifact, Uri? releaseManifestUri)
    {
        if (manifestArtifact is null)
        {
            return null;
        }

        if (TryCreateAbsoluteUri(manifestArtifact.Url) is Uri absoluteUrl)
        {
            return absoluteUrl;
        }

        if (!string.IsNullOrWhiteSpace(manifestArtifact.Url) && releaseManifestUri is not null)
        {
            return new Uri(releaseManifestUri, manifestArtifact.Url);
        }

        if (!string.IsNullOrWhiteSpace(manifestArtifact.Path) && releaseManifestUri is not null)
        {
            return new Uri(releaseManifestUri, manifestArtifact.Path);
        }

        return null;
    }

    private static Uri? TryCreateAbsoluteUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            ? uri
            : null;
    }

    private static async Task<GitHubResolvedRelease> ResolveReleaseForChannelAsync(
        string releaseChannel,
        string repositoryOwner,
        string repositoryName,
        CancellationToken cancellationToken)
    {
        using var client = CreateHttpClient();
        var releasesUri = $"https://api.github.com/repos/{repositoryOwner}/{repositoryName}/releases?per_page=20";
        using var response = await client.GetAsync(new Uri(releasesUri), cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Failed to resolve the {releaseChannel} release channel from {repositoryOwner}/{repositoryName}. GitHub returned {(int)response.StatusCode} ({response.ReasonPhrase}).");
        }

        Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var releases = await JsonSerializer.DeserializeAsync(
                stream,
                InstallerJsonContext.Default.ListGitHubReleaseInfo,
                cancellationToken).ConfigureAwait(false)
                ?? [];

            var selected = SelectReleaseForChannel(releases, releaseChannel);
            if (selected is null)
            {
                throw new InvalidOperationException(
                    $"No release could be resolved for the {releaseChannel} channel in {repositoryOwner}/{repositoryName}.");
            }

            return new GitHubResolvedRelease(
                selected.TagName,
                selected.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static GitHubReleaseInfo? SelectReleaseForChannel(IReadOnlyList<GitHubReleaseInfo> releases, string releaseChannel)
    {
        var normalizedChannel = (releaseChannel ?? string.Empty).Trim();

        foreach (var release in releases)
        {
            if (release.Draft)
                continue;

            if (string.Equals(normalizedChannel, "stable", StringComparison.OrdinalIgnoreCase))
            {
                if (!release.Prerelease)
                    return release;

                continue;
            }

            if (!release.Prerelease)
                continue;

            if (ContainsChannelToken(release, normalizedChannel))
                return release;
        }

        return null;
    }

    private static async Task<GitHubAssetDownload> GetReleaseAssetUrlAsync(
        string releaseTag,
        string repositoryOwner,
        string repositoryName,
        string assetName,
        CancellationToken cancellationToken)
    {
        using var client = CreateHttpClient();
        var releaseUri = $"https://api.github.com/repos/{repositoryOwner}/{repositoryName}/releases/tags/{releaseTag}";
        using var response = await client.GetAsync(new Uri(releaseUri), cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                $"GitHub release tag '{releaseTag}' could not be resolved in {repositoryOwner}/{repositoryName}. The tag may be missing, or the repository/release may not be accessible anonymously from this machine. Use a published public release or provide a local rootfs path.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Failed to resolve release tag '{releaseTag}' from {repositoryOwner}/{repositoryName}. GitHub returned {(int)response.StatusCode} ({response.ReasonPhrase}).");
        }

        Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var release = await JsonSerializer.DeserializeAsync(
                stream,
                InstallerJsonContext.Default.GitHubReleaseInfo,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Failed to parse release metadata for tag '{releaseTag}'.");
            var asset = release.Assets.FirstOrDefault(item =>
                string.Equals(item.Name, assetName, StringComparison.OrdinalIgnoreCase));

            if (asset is null)
                throw new InvalidOperationException($"Release asset '{assetName}' not found in tag '{releaseTag}'.");

            return new GitHubAssetDownload(asset.BrowserDownloadUrl);
        }
    }

    private static async Task DownloadFileAsync(
        GitHubAssetDownload assetDownload,
        string destinationPath,
        Func<string, Task> log,
        CancellationToken cancellationToken)
    {
        await log($"Downloading {Path.GetFileName(destinationPath)}...").ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        using var client = CreateHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(assetDownload.Url));

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            FileStream destination = File.Create(destinationPath);
            await using (destination.ConfigureAwait(false))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static string SanitizeCacheSegment(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(value.Length);

        foreach (var character in value)
        {
            builder.Append(Array.IndexOf(invalidCharacters, character) >= 0 ? '_' : character);
        }

        return builder.Length == 0 ? "unknown-release" : builder.ToString();
    }

    private static bool ContainsChannelToken(GitHubReleaseInfo release, string channel)
        => release.TagName.Contains(channel, StringComparison.OrdinalIgnoreCase)
            || release.Name.Contains(channel, StringComparison.OrdinalIgnoreCase);

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("KautukaInstaller", "1.0"));
        return client;
    }

    private sealed record GitHubAssetDownload(string Url);

    private sealed record GitHubResolvedRelease(string TagName, string ReleaseId);
}
