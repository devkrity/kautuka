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
        InstallerPlatformReleaseManifest? releaseManifest = null;
        Uri? releaseManifestUri = TryCreateAbsoluteUri(request.ReleaseManifestUrl);
        bool requireVerification = !request.AllowUnsignedLocalFeeds;
        string? manifestPublicKeyPath = ResolveManifestPublicKeyPath(request);

        if (string.IsNullOrWhiteSpace(request.RootfsPath))
        {
            if (releaseManifestUri is null && string.IsNullOrWhiteSpace(request.ReleaseTag))
            {
                resolvedReleaseTag = await DiscoverLatestReleaseTagAsync(
                    request.RepositoryOwner,
                    request.RepositoryName,
                    request.ReleaseChannel,
                    log,
                    cancellationToken).ConfigureAwait(false);
            }

            releaseManifestUri ??= new Uri(
                $"https://github.com/{Uri.EscapeDataString(request.RepositoryOwner)}/{Uri.EscapeDataString(request.RepositoryName)}/releases/download/{Uri.EscapeDataString(resolvedReleaseTag ?? request.ReleaseTag ?? string.Empty)}/{releaseManifestAssetName}",
                UriKind.Absolute);
            EnsureApprovedInstallerSourceUrl(releaseManifestUri.ToString(), "Release manifest");
            releaseManifest = await DownloadReleaseManifestAsync(
                releaseManifestUri,
                request.ReleaseChannel,
                request.RepositoryOwner,
                request.RepositoryName,
                resolvedReleaseTag,
                requireVerification,
                manifestPublicKeyPath,
                cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(request.ReleaseTag)
                && !string.Equals(request.ReleaseTag, releaseManifest.ReleaseTag, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Signed release manifest tag does not match the requested exact release tag.");
            }

            resolvedReleaseTag ??= string.IsNullOrWhiteSpace(releaseManifest.ReleaseTag)
                ? null
                : releaseManifest.ReleaseTag;

            await log($"Resolved install assets from {request.ReleaseChannel} channel manifest {releaseManifestUri}.").ConfigureAwait(false);
        }

        var releaseId = !string.IsNullOrWhiteSpace(releaseManifest?.ReleaseId)
                ? releaseManifest.ReleaseId!
            : !string.IsNullOrWhiteSpace(request.ReleaseId)
                ? request.ReleaseId!
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

    private static async Task<string> DiscoverLatestReleaseTagAsync(
        string owner,
        string repository,
        string channel,
        Func<string, Task> log,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repository))
        {
            throw new InvalidOperationException("GitHub release repository is not configured.");
        }

        using HttpClient client = CreateHttpClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        var releases = new List<GitHubReleaseCandidate>();
        const int pageSize = 100;
        for (int page = 1; page <= 11; page++)
        {
            string url = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/releases?per_page={pageSize}&page={page}";
            using HttpResponseMessage response = await client.GetAsync(new Uri(url, UriKind.Absolute), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using JsonDocument document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("GitHub release discovery returned an invalid response.");
            }

            JsonElement[] rows = document.RootElement.EnumerateArray().ToArray();
            if (page == 11)
            {
                if (rows.Length != 0)
                {
                    throw new InvalidOperationException("GitHub release history exceeds the safe 1,000-release discovery limit.");
                }

                break;
            }

            foreach (JsonElement row in rows)
            {
                if (row.ValueKind != JsonValueKind.Object
                    || !row.TryGetProperty("tag_name", out JsonElement tagValue)
                    || tagValue.ValueKind != JsonValueKind.String
                    || !row.TryGetProperty("draft", out JsonElement draftValue)
                    || draftValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                    || !row.TryGetProperty("prerelease", out JsonElement prereleaseValue)
                    || prereleaseValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                    || !row.TryGetProperty("published_at", out JsonElement publishedValue)
                    || publishedValue.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                {
                    throw new InvalidOperationException("GitHub release discovery contained malformed release metadata.");
                }

                string? tag = tagValue.GetString();
                bool draft = draftValue.ValueKind == JsonValueKind.True;
                bool prerelease = prereleaseValue.ValueKind == JsonValueKind.True;
                bool published = publishedValue.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(publishedValue.GetString());
                if (draft || !published || !ReleaseChannelPolicy.TryParseTag(tag, channel, prerelease, out ReleaseTagVersion version))
                {
                    await log($"Skipping non-eligible GitHub release '{tag}'.").ConfigureAwait(false);
                    continue;
                }

                releases.Add(new GitHubReleaseCandidate(tag!, version));
            }

            if (rows.Length < pageSize)
            {
                break;
            }
        }

        GitHubReleaseCandidate? selected;
        try
        {
            selected = ReleaseChannelPolicy.SelectLatest(
                releases,
                channel,
                static release => release.Tag,
                static _ => false,
                release => release.Version.IsPreview);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }

        return selected?.Tag ?? throw new InvalidOperationException($"No published strict {channel} GitHub release was found.");
    }

    private sealed record GitHubReleaseCandidate(string Tag, ReleaseTagVersion Version);

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
        string releaseChannel,
        string repositoryOwner,
        string repositoryName,
        string? expectedTag,
        bool requireVerification,
        string? manifestPublicKeyPath,
        CancellationToken cancellationToken)
    {
        using var client = CreateHttpClient();
        if (requireVerification && !string.IsNullOrWhiteSpace(expectedTag))
        {
            string expectedManifestUrl = $"https://github.com/{Uri.EscapeDataString(repositoryOwner)}/{Uri.EscapeDataString(repositoryName)}/releases/download/{Uri.EscapeDataString(expectedTag)}/{releaseManifestAssetName}";
            if (!string.Equals(releaseManifestUri.AbsoluteUri, expectedManifestUrl, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Signed release manifest URL does not match the selected exact GitHub Release tag.");
            }
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, releaseManifestUri);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        byte[] manifestBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        InstallerPlatformReleaseManifest manifest = JsonSerializer.Deserialize(
            manifestBytes,
            InstallerJsonContext.Default.InstallerPlatformReleaseManifest)
            ?? throw new InvalidOperationException($"Failed to parse platform release manifest from '{releaseManifestUri}'.");

        if (requireVerification)
        {
            ValidateManifestMatchesChannel(manifest, releaseChannel, repositoryOwner, repositoryName, expectedTag);
        }

        await VerifyInstallerManifestSignatureAsync(
            manifestBytes,
            manifest,
            releaseManifestUri,
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

    private static Task<GitHubAssetDownload> ResolveReleaseAssetDownloadAsync(
        string releaseTag,
        string repositoryOwner,
        string repositoryName,
        string assetName,
        InstallerPlatformReleaseArtifact? manifestArtifact,
        Uri? releaseManifestUri,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Uri? manifestAssetUri = ResolveManifestAssetUri(manifestArtifact, releaseManifestUri);
        if (manifestAssetUri is not null)
        {
            return Task.FromResult(new GitHubAssetDownload(manifestAssetUri.ToString()));
        }

        string resolvedAssetName = !string.IsNullOrWhiteSpace(manifestArtifact?.Name)
            ? manifestArtifact.Name
            : assetName;
        string releaseBase = $"https://github.com/{Uri.EscapeDataString(repositoryOwner)}/{Uri.EscapeDataString(repositoryName)}/releases/download/{Uri.EscapeDataString(releaseTag)}";
        return Task.FromResult(new GitHubAssetDownload($"{releaseBase}/{Uri.EscapeDataString(resolvedAssetName)}"));
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

    private static void ValidateManifestMatchesChannel(
        InstallerPlatformReleaseManifest manifest,
        string channel,
        string owner,
        string repository,
        string? expectedTag)
    {
        if (!ReleaseChannelPolicy.TryParseTag(
                manifest.ReleaseTag,
                channel,
                string.Equals(channel, "preview", StringComparison.OrdinalIgnoreCase),
                out ReleaseTagVersion parsed)
            || !string.Equals(manifest.Version, parsed.ToVersionString(), StringComparison.Ordinal)
            || !string.Equals(manifest.Channel, channel, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(manifest.SourceSha)
            || manifest.SourceSha.Length != 40
            || !manifest.SourceSha.All(Uri.IsHexDigit)
            || (!string.IsNullOrWhiteSpace(expectedTag) && !string.Equals(manifest.ReleaseTag, expectedTag, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Signed release manifest identity does not match the selected {channel} channel and its strict release tag.");
        }

        if (!string.Equals(manifest.ReleaseId, manifest.ReleaseTag, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Signed release manifest release ID does not match its release tag.");
        }

        string releaseBase = $"https://github.com/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/releases/download/{Uri.EscapeDataString(manifest.ReleaseTag!)}";
        string expectedManifestUrl = $"{releaseBase}/{releaseManifestAssetName}";
        string expectedSignatureUrl = $"{releaseBase}/{releaseManifestAssetName}.sig";
        string expectedRootfsUrl = $"{releaseBase}/{rootfsAssetName}";
        string expectedLinuxUrl = $"{releaseBase}/kautuka-linux-amd64.tar.gz";
        InstallerPlatformReleaseArtifact? rootfs = manifest.Artifacts.SingleOrDefault(artifact =>
            string.Equals(artifact.Name, rootfsAssetName, StringComparison.Ordinal));
        InstallerPlatformReleaseArtifact? linux = manifest.Artifacts.SingleOrDefault(artifact =>
            string.Equals(artifact.Name, "kautuka-linux-amd64.tar.gz", StringComparison.Ordinal));
        if (!string.Equals(manifest.Signature?.Algorithm, "ecdsa-p256-sha256", StringComparison.Ordinal)
            || !string.Equals(manifest.Signature?.Format, "base64", StringComparison.Ordinal)
            || !string.Equals(manifest.Signature?.Path, $"{releaseManifestAssetName}.sig", StringComparison.Ordinal)
            || !string.Equals(manifest.Signature?.Url, expectedSignatureUrl, StringComparison.Ordinal)
            || rootfs is null
            || !string.Equals(rootfs.Url, expectedRootfsUrl, StringComparison.Ordinal)
            || linux is null
            || !string.Equals(linux.Url, expectedLinuxUrl, StringComparison.Ordinal)
            || !Uri.TryCreate(expectedManifestUrl, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("Signed release manifest URLs do not bind the required artifacts and signature to the exact GitHub Release tag.");
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

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("KautukaInstaller", "1.0"));
        return client;
    }

    private sealed record GitHubAssetDownload(string Url);
}
