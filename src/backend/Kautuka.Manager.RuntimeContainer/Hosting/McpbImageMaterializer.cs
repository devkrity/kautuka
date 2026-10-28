using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

public sealed class McpbImageMaterializer(
    PodmanCommandRunner podmanCommandRunner,
    ILogger<McpbImageMaterializer> logger,
    RuntimeImageAllowlist? allowlist = null,
    PodmanImageArchiveStore? imageArchives = null)
{
    internal const string BundleWorkdir = "/opt/kautuka/mcpb";
    internal const string SandboxArtifactRoot = "/artifact";
    internal const string SandboxArtifactContractPath = "/contract.json";
    private const string materializationRecipeVersion = "mcpb-materializer-v8";
    private static readonly SemaphoreSlim imageBuildGate = new(1, 1);
    private const string pipTrustedHostShellInit =
        "pip_trusted=\"\"; if [ -n \"${PIP_TRUSTED_HOST:-}\" ]; then pip_trusted=\"--trusted-host $PIP_TRUSTED_HOST\"; fi; ";
    private const string nodeTarballUrl = "https://nodejs.org/dist/v26.3.0/node-v26.3.0-linux-x64.tar.xz";
    private const string nodeTarballSha256 = "0ce210831380ab75a5fec0d3fb6b5af0958898016b2cae51cc2fa11057d2f77e";
    private static readonly string[] languagePackageIndexKeys =
    [
        "NPM_CONFIG_REGISTRY",
        "PIP_INDEX_URL",
        "PIP_TRUSTED_HOST",
        "UV_INDEX_URL",
        "UV_DEFAULT_INDEX",
    ];

    public async Task<string> EnsureImageAsync(
        WorkspaceMcpContainerConnectorDefinition connector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connector);

        string? serverType = connector.ServerType;
        if (string.IsNullOrWhiteSpace(serverType))
        {
            serverType = InferServerType(connector.Command);
        }

        if (!string.IsNullOrWhiteSpace(connector.Image))
        {
            connector = UseReleasedSandboxBase(connector, serverType);
        }

        if (string.IsNullOrWhiteSpace(serverType))
        {
            return connector.Image;
        }

        if (string.IsNullOrWhiteSpace(connector.Image))
        {
            throw new InvalidOperationException($"MCPB connector '{connector.ConnectorId}' is missing a base image.");
        }
        await AdmitInfrastructureBaseAsync(connector, cancellationToken).ConfigureAwait(false);

        bool hasBundle = !string.IsNullOrWhiteSpace(connector.BundlePath);
        if (hasBundle && !File.Exists(connector.BundlePath!))
        {
            throw new InvalidOperationException($"MCPB bundle archive '{connector.BundlePath}' does not exist.");
        }

        string sourceDigest = hasBundle
            ? ComputeSha256Hex(connector.BundlePath!)
            : ComputeSha256HexFromString(connector.Image);

        string localImage = BuildLocalImageReference(serverType, sourceDigest + "\n" + connector.Image, hasBundle);

        PodmanCommandResult existsResult = await podmanCommandRunner
            .RunAsync(["image", "exists", localImage], 15, cancellationToken)
            .ConfigureAwait(false);

        if (existsResult.ExitCode == 0)
        {
            return localImage;
        }

        string buildContextPath = CreateBuildContextPath(connector.ConnectorId, sourceDigest);
        await imageBuildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(buildContextPath);

            if (hasBundle)
            {
                string bundleSourcePath = Path.Combine(buildContextPath, "bundle");
                ExtractBundle(connector.BundlePath!, bundleSourcePath);
            }

            string containerfilePath = Path.Combine(buildContextPath, "Containerfile");
            await File.WriteAllTextAsync(
                containerfilePath,
                BuildContainerfile(connector.Image, serverType, hasBundle),
                cancellationToken).ConfigureAwait(false);

            string systemImage = $"{localImage}-sys";
            PodmanCommandResult buildResult = await podmanCommandRunner
                .RunAsync(
                    [
                        "build",
                        "-t", systemImage,
                        "-f", containerfilePath,
                        buildContextPath,
                    ],
                    900,
                    cancellationToken)
                .ConfigureAwait(false);

            if (buildResult.ExitCode != 0)
            {
                throw new InvalidOperationException(PodmanFailureFormatter.FormatFailure(
                    $"Build MCPB runtime image for connector '{connector.ConnectorId}'",
                    buildResult));
            }

            try
            {
                await CommitLanguagePackagesAsync(
                    systemImage,
                    localImage,
                    connector,
                    serverType,
                    forSandboxArtifact: false,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await TryRemoveImageAsync(systemImage, cancellationToken).ConfigureAwait(false);
            }

            return localImage;
        }
        finally
        {
            try
            {
                if (Directory.Exists(buildContextPath))
                {
                    Directory.Delete(buildContextPath, recursive: true);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to clean MCPB build context '{BuildContextPath}'.", buildContextPath);
            }

            imageBuildGate.Release();
        }
    }

    public async Task<string> EnsureSandboxArtifactImageAsync(
        WorkspaceMcpContainerConnectorDefinition connector,
        string installRoot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);

        ValidateBundleConnector(connector);
        connector = UseReleasedSandboxBase(connector, connector.ServerType);
        await AdmitInfrastructureBaseAsync(connector, cancellationToken).ConfigureAwait(false);

        string sourceDigest = ComputeSha256Hex(connector.BundlePath!);
        string localImage = BuildSandboxArtifactImageReference(connector.ServerType!, sourceDigest + "\n" + connector.Image);

        PodmanCommandResult existsResult = await podmanCommandRunner
            .RunAsync(["image", "exists", localImage], 15, cancellationToken)
            .ConfigureAwait(false);

        if (existsResult.ExitCode == 0)
        {
            return localImage;
        }

        string buildContextPath = CreateBuildContextPath(connector.ConnectorId, sourceDigest + "-sandbox");
        await imageBuildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(buildContextPath);

            string bundleSourcePath = Path.Combine(buildContextPath, "bundle");
            ExtractBundle(connector.BundlePath!, bundleSourcePath);

            string contractPath = Path.Combine(buildContextPath, "contract.json");
            string contractJson = string.Concat(
                "{\"installRoot\":\"",
                JsonEncodedText.Encode(installRoot).ToString(),
                "\"}");
            await File.WriteAllTextAsync(contractPath, contractJson, cancellationToken).ConfigureAwait(false);

            string containerfilePath = Path.Combine(buildContextPath, "Containerfile");
            await File.WriteAllTextAsync(
                containerfilePath,
                BuildSandboxSystemContainerfile(connector.Image!, connector.ServerType!, installRoot),
                cancellationToken).ConfigureAwait(false);

            string systemImage = $"{localImage}-sys";
            string depsImage = $"{localImage}-deps";
            PodmanCommandResult buildResult = await podmanCommandRunner
                .RunAsync(
                    [
                        "build",
                        "-t", systemImage,
                        "-f", containerfilePath,
                        buildContextPath,
                    ],
                    900,
                    cancellationToken)
                .ConfigureAwait(false);

            if (buildResult.ExitCode != 0)
            {
                throw new InvalidOperationException(PodmanFailureFormatter.FormatFailure(
                    $"Build MCPB sandbox artifact image for connector '{connector.ConnectorId}'",
                    buildResult));
            }

            try
            {
                await CommitLanguagePackagesAsync(
                    systemImage,
                    depsImage,
                    connector,
                    connector.ServerType!,
                    forSandboxArtifact: true,
                    cancellationToken).ConfigureAwait(false);

                string scratchPath = Path.Combine(buildContextPath, "Containerfile.scratch");
                await File.WriteAllTextAsync(
                    scratchPath,
                    BuildSandboxScratchContainerfile(depsImage, installRoot),
                    cancellationToken).ConfigureAwait(false);

                PodmanCommandResult scratchResult = await podmanCommandRunner
                    .RunAsync(
                        [
                            "build",
                            "--pull=never",
                            "-t", localImage,
                            "-f", scratchPath,
                            buildContextPath,
                        ],
                        300,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (scratchResult.ExitCode != 0)
                {
                    throw new InvalidOperationException(PodmanFailureFormatter.FormatFailure(
                        $"Pack MCPB sandbox artifact image for connector '{connector.ConnectorId}'",
                        scratchResult));
                }
            }
            finally
            {
                await TryRemoveImageAsync(systemImage, cancellationToken).ConfigureAwait(false);
                await TryRemoveImageAsync(depsImage, cancellationToken).ConfigureAwait(false);
            }

            return localImage;
        }
        finally
        {
            try
            {
                if (Directory.Exists(buildContextPath))
                {
                    Directory.Delete(buildContextPath, recursive: true);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to clean MCPB sandbox artifact build context '{BuildContextPath}'.", buildContextPath);
            }

            imageBuildGate.Release();
        }
    }

    internal static string BuildLocalImageReference(string serverType, string sourceDigest, bool hasBundle = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverType);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDigest);

        string normalizedServerType = NormalizeIdentifier(serverType);
        string normalizedDigest = BuildMaterializedDigest(sourceDigest);
        string prefix = hasBundle ? "kautuka-mcpb" : "kautuka-runtime";
        return $"localhost/{prefix}-{normalizedServerType}:{normalizedDigest}";
    }

    internal static string BuildSandboxArtifactImageReference(string serverType, string sourceDigest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverType);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDigest);

        string normalizedServerType = NormalizeIdentifier(serverType);
        string normalizedDigest = BuildMaterializedDigest(sourceDigest);
        return $"localhost/kautuka-mcpb-sandbox-{normalizedServerType}:{normalizedDigest}";
    }

    internal static string RewriteBundlePlaceholder(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? value ?? string.Empty
            : value.Replace("${__dirname}", BundleWorkdir, StringComparison.Ordinal);

    internal static string RewriteBundlePlaceholder(string? value, string replacementRoot)
        => string.IsNullOrWhiteSpace(value)
            ? value ?? string.Empty
            : value.Replace("${__dirname}", replacementRoot, StringComparison.Ordinal);

    private WorkspaceMcpContainerConnectorDefinition UseReleasedSandboxBase(
        WorkspaceMcpContainerConnectorDefinition connector,
        string? serverType)
    {
        string image = (allowlist ?? RuntimeImageAllowlist.Empty).SelectReleasedSandboxImage(connector.Image, serverType);
        return string.Equals(image, connector.Image, StringComparison.Ordinal)
            ? connector
            : connector with { Image = image };
    }

    private async Task AdmitInfrastructureBaseAsync(
        WorkspaceMcpContainerConnectorDefinition connector,
        CancellationToken cancellationToken)
    {
        if (imageArchives is not null)
        {
            await imageArchives.TryEnsureImageAsync(connector.Image, cancellationToken).ConfigureAwait(false);
        }

        ContainerImageProvenancePolicy.EnsureTrusted(
            connector.Image,
            $"MCPB connector '{connector.ConnectorId}' base",
            (allowlist ?? RuntimeImageAllowlist.Empty).ImageReferences,
            requireListed: true);
    }

    private static string CreateBuildContextPath(string connectorId, string sourceDigest)
        => Path.Combine(Path.GetTempPath(), "kautuka-mcpb-build", $"{SanitizeSegment(connectorId)}-{sourceDigest[..12]}");

    internal static string BuildContainerfile(string baseImage, string serverType, bool hasBundle)
    {
        ContainerImageProvenancePolicy.EnsureSingleLine(baseImage, "container build");
        var lines = new List<string>
        {
            $"FROM {baseImage}",
            "ENV PATH=/usr/local/bin:$PATH",
            "ENV HOME=/tmp",
            "ENV NPM_CONFIG_CACHE=/tmp/.npm",
            "ENV PIP_CACHE_DIR=/tmp/.pip",
            "ENV UV_CACHE_DIR=/tmp/.uv",
            $"WORKDIR {BundleWorkdir}"
        };

        if (hasBundle)
        {
            lines.Add($"COPY bundle/ {BundleWorkdir}/");
        }

        lines.Add(BuildSystemRuntimeLayer(serverType, BundleWorkdir));
        lines.Add(string.Empty);

        return string.Join('\n', lines);
    }

    internal static string BuildSandboxSystemContainerfile(string baseImage, string serverType, string installRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseImage);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverType);
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        ContainerImageProvenancePolicy.EnsureSingleLine(baseImage, "container build");

        return string.Join('\n',
        [
            $"FROM {baseImage}",
            "ENV PATH=/usr/local/bin:$PATH",
            "ENV HOME=/tmp",
            "ENV NPM_CONFIG_CACHE=/tmp/.npm",
            "ENV PIP_CACHE_DIR=/tmp/.pip",
            "ENV UV_CACHE_DIR=/tmp/.uv",
            $"WORKDIR {installRoot}",
            $"COPY bundle/ {installRoot}/",
            $"COPY contract.json {SandboxArtifactContractPath}",
            BuildSystemRuntimeLayer(serverType, installRoot),
            string.Empty,
        ]);
    }

    internal static string BuildSandboxScratchContainerfile(string depsImage, string installRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(depsImage);
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        ContainerImageProvenancePolicy.EnsureSingleLine(depsImage, "container build");

        return string.Join('\n',
        [
            $"FROM {depsImage} AS builder",
            "FROM scratch",
            $"COPY --from=builder {installRoot}/ {SandboxArtifactRoot}/",
            $"COPY --from=builder {SandboxArtifactContractPath} {SandboxArtifactContractPath}",
            "CMD [\"/contract.json\"]",
            string.Empty,
        ]);
    }

    internal static string BuildLanguagePackageInstallScript(string serverType, bool forSandboxArtifact)
    {
        if (string.Equals(serverType, "node", StringComparison.OrdinalIgnoreCase))
        {
            return "set -eu; if [ -z \"${NPM_CONFIG_REGISTRY:-}\" ]; then echo 'NPM_CONFIG_REGISTRY is required' >&2; exit 1; fi; "
                + "if [ -f package-lock.json ]; then npm ci --omit=dev --registry=\"$NPM_CONFIG_REGISTRY\"; "
                + "elif [ -f package.json ]; then npm install --omit=dev --registry=\"$NPM_CONFIG_REGISTRY\"; fi";
        }

        if (string.Equals(serverType, "python", StringComparison.OrdinalIgnoreCase))
        {
            return "set -eu; if [ -z \"${PIP_INDEX_URL:-}\" ]; then echo 'PIP_INDEX_URL is required' >&2; exit 1; fi; "
                + pipTrustedHostShellInit
                + "if [ -f requirements.txt ]; then PIP_CONFIG_FILE=/dev/null pip install --no-cache-dir --index-url \"$PIP_INDEX_URL\" $pip_trusted -r requirements.txt; "
                + "elif [ -f pyproject.toml ]; then PIP_CONFIG_FILE=/dev/null pip install --no-cache-dir --index-url \"$PIP_INDEX_URL\" $pip_trusted .; fi";
        }

        if (string.Equals(serverType, "uv", StringComparison.OrdinalIgnoreCase))
        {
            string uvSync = forSandboxArtifact
                ? "if [ -f pyproject.toml ]; then uv sync --no-dev --no-editable --index-url \"$UV_INDEX_URL\"; elif [ -f requirements.txt ]; then uv pip install --python .venv/bin/python --index-url \"$UV_INDEX_URL\" -r requirements.txt; fi"
                : "if [ -f pyproject.toml ]; then uv sync --no-dev --index-url \"$UV_INDEX_URL\"; elif [ -f requirements.txt ]; then uv pip install --system --index-url \"$UV_INDEX_URL\" -r requirements.txt; fi";
            return string.Concat(
                "set -eu; ",
                "if [ -z \"${PIP_INDEX_URL:-}\" ] || [ -z \"${UV_INDEX_URL:-}\" ]; then echo 'PIP_INDEX_URL and UV_INDEX_URL are required' >&2; exit 1; fi; ",
                pipTrustedHostShellInit,
                "if ! command -v uv >/dev/null 2>&1 || ! uv --version | grep -q '0.11.19'; then ",
                "PIP_CONFIG_FILE=/dev/null python3 -m pip install --break-system-packages --no-cache-dir --index-url \"$PIP_INDEX_URL\" $pip_trusted uv==0.11.19; ",
                "fi; ",
                uvSync);
        }

        if (string.Equals(serverType, "binary", StringComparison.OrdinalIgnoreCase))
        {
            return "exit 0";
        }

        throw new InvalidOperationException($"Unsupported MCPB server type '{serverType}'.");
    }

    internal static IReadOnlyList<KeyValuePair<string, string>> ResolveLanguagePackageIndexEnvironment(
        IReadOnlyList<KeyValuePair<string, string>> environment,
        string serverType)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverType);

        Dictionary<string, string> provided = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> pair in environment)
        {
            if (string.IsNullOrWhiteSpace(pair.Key)
                || string.IsNullOrWhiteSpace(pair.Value)
                || !languagePackageIndexKeys.Contains(pair.Key, StringComparer.Ordinal))
            {
                continue;
            }

            provided[pair.Key] = pair.Value;
        }

        foreach (string requiredKey in RequiredLanguagePackageIndexKeys(serverType))
        {
            if (!provided.ContainsKey(requiredKey))
            {
                throw new InvalidOperationException(
                    $"MCPB language-package install requires '{requiredKey}' so npm/pip/uv cannot fall back to public indexes.");
            }
        }

        return [.. provided.OrderBy(static pair => pair.Key, StringComparer.Ordinal)];
    }

    private static IReadOnlyList<string> RequiredLanguagePackageIndexKeys(string serverType)
    {
        if (string.Equals(serverType, "node", StringComparison.OrdinalIgnoreCase))
        {
            return ["NPM_CONFIG_REGISTRY"];
        }

        if (string.Equals(serverType, "python", StringComparison.OrdinalIgnoreCase))
        {
            return ["PIP_INDEX_URL"];
        }

        if (string.Equals(serverType, "uv", StringComparison.OrdinalIgnoreCase))
        {
            return ["PIP_INDEX_URL", "UV_INDEX_URL"];
        }

        if (string.Equals(serverType, "binary", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        throw new InvalidOperationException($"Unsupported MCPB server type '{serverType}'.");
    }

    private static string BuildSystemRuntimeLayer(string serverType, string workdirRoot)
    {
        if (string.Equals(serverType, "node", StringComparison.OrdinalIgnoreCase))
        {
            return AsRootBuildLayer(string.Join('\n',
            [
                "RUN microdnf install -y --setopt=install_weak_deps=0 curl tar xz libatomic && microdnf clean all",
                $"RUN curl -fSL {nodeTarballUrl} -o /tmp/node.tar.xz && echo \"{nodeTarballSha256}  /tmp/node.tar.xz\" | sha256sum -c - && tar -C /usr/local --strip-components=1 -xJf /tmp/node.tar.xz && rm -f /tmp/node.tar.xz",
            ]), workdirRoot);
        }

        if (string.Equals(serverType, "python", StringComparison.OrdinalIgnoreCase))
        {
            return AsRootBuildLayer(
                "RUN microdnf install -y --setopt=install_weak_deps=0 python3 python3-pip && microdnf clean all",
                workdirRoot);
        }

        if (string.Equals(serverType, "uv", StringComparison.OrdinalIgnoreCase))
        {
            return AsRootBuildLayer(string.Join('\n',
            [
                "RUN microdnf install -y --setopt=install_weak_deps=0 python3 python3-pip && microdnf clean all",
                $"ENV UV_PYTHON_INSTALL_DIR={workdirRoot}/.python",
                $"ENV UV_PROJECT_ENVIRONMENT={workdirRoot}/.venv",
            ]), workdirRoot);
        }

        if (string.Equals(serverType, "binary", StringComparison.OrdinalIgnoreCase))
        {
            // kautuka-base already has python3. The native executable is copied with the bundle
            // and must be executable for uid 65532. No language package install.
            return AsRootBuildLayer(
                "RUN python3 -c \"import json,os; p=json.load(open('manifest.json'))['server']['entry_point']; os.chmod(p, 0o755)\"",
                workdirRoot);
        }

        throw new InvalidOperationException($"Unsupported MCPB server type '{serverType}'.");
    }

    private static string AsRootBuildLayer(string layer, string workdir)
        => "USER root\n" + layer + "\nRUN chown -R node:node " + workdir + "\nUSER node";

    internal static List<string> BuildLanguagePackageRunArguments(
        string containerName,
        string systemImage,
        IReadOnlyList<KeyValuePair<string, string>> indexEnvironment,
        string installScript)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(systemImage);
        ArgumentNullException.ThrowIfNull(indexEnvironment);
        ArgumentException.ThrowIfNullOrWhiteSpace(installScript);

        var arguments = new List<string>
        {
            "run",
            "--replace",
            "--name", containerName,
            "--network", ConnectorContainerSpecBuilder.SandboxNetworkName,
        };

        foreach (KeyValuePair<string, string> pair in indexEnvironment)
        {
            arguments.Add("--env");
            arguments.Add($"{pair.Key}={pair.Value}");
        }

        // Do not use --entrypoint: podman commit snapshots it. A baked
        // ENTRYPOINT /bin/sh turns connector `sh -lc` into `sh sh -lc` (exit 126),
        // then probe exec fails with 125 on the stopped container.
        arguments.Add(systemImage);
        arguments.Add("/bin/sh");
        arguments.Add("-lc");
        arguments.Add(installScript);
        return arguments;
    }

    private async Task CommitLanguagePackagesAsync(
        string systemImage,
        string targetImage,
        WorkspaceMcpContainerConnectorDefinition connector,
        string serverType,
        bool forSandboxArtifact,
        CancellationToken cancellationToken)
    {
        if (string.Equals(serverType, "binary", StringComparison.OrdinalIgnoreCase))
        {
            PodmanCommandResult tagResult = await podmanCommandRunner
                .RunAsync(["tag", systemImage, targetImage], 30, cancellationToken)
                .ConfigureAwait(false);
            if (tagResult.ExitCode != 0)
            {
                throw new InvalidOperationException(PodmanFailureFormatter.FormatFailure(
                    $"Tag MCPB binary image for connector '{connector.ConnectorId}'",
                    tagResult));
            }

            return;
        }

        await EnsureSandboxNetworkExistsAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<KeyValuePair<string, string>> indexEnvironment = ResolveLanguagePackageIndexEnvironment(
            connector.Environment,
            serverType);
        string installScript = BuildLanguagePackageInstallScript(serverType, forSandboxArtifact);
        string containerName = "kautuka-mcpb-pkg-" + Guid.NewGuid().ToString("N")[..12];
        List<string> arguments = BuildLanguagePackageRunArguments(
            containerName,
            systemImage,
            indexEnvironment,
            installScript);

        try
        {
            PodmanCommandResult runResult = await podmanCommandRunner
                .RunAsync(arguments, 900, cancellationToken)
                .ConfigureAwait(false);
            if (runResult.ExitCode != 0)
            {
                throw new InvalidOperationException(PodmanFailureFormatter.FormatFailure(
                    $"Install MCPB language packages for connector '{connector.ConnectorId}' via {ConnectorContainerSpecBuilder.SandboxNetworkName}",
                    runResult));
            }

            PodmanCommandResult commitResult = await podmanCommandRunner
                .RunAsync(["commit", containerName, targetImage], 120, cancellationToken)
                .ConfigureAwait(false);
            if (commitResult.ExitCode != 0)
            {
                throw new InvalidOperationException(PodmanFailureFormatter.FormatFailure(
                    $"Commit MCPB language packages for connector '{connector.ConnectorId}'",
                    commitResult));
            }
        }
        finally
        {
            await TryRemoveContainerAsync(containerName, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EnsureSandboxNetworkExistsAsync(CancellationToken cancellationToken)
    {
        PodmanCommandResult existsResult = await podmanCommandRunner
            .RunAsync(["network", "exists", ConnectorContainerSpecBuilder.SandboxNetworkName], 10, cancellationToken)
            .ConfigureAwait(false);
        if (existsResult.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"MCPB language-package install requires Podman network '{ConnectorContainerSpecBuilder.SandboxNetworkName}' so npm/pip/uv can reach kautuka-pkgs-proxy. Refusing public-index fallback.");
        }
    }

    private async Task TryRemoveImageAsync(string image, CancellationToken cancellationToken)
    {
        try
        {
            _ = await podmanCommandRunner
                .RunAsync(["rmi", "-f", image], 30, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to remove temporary MCPB image '{Image}'.", image);
        }
    }

    private async Task TryRemoveContainerAsync(string containerName, CancellationToken cancellationToken)
    {
        try
        {
            _ = await podmanCommandRunner
                .RunAsync(["rm", "-f", containerName], 30, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to remove temporary MCPB package container '{Container}'.", containerName);
        }
    }

    private static void ExtractBundle(string archivePath, string destinationPath)
    {
        Directory.CreateDirectory(destinationPath);

        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            ValidateEntryPath(entry.FullName);
            ValidateNotSymlink(entry);

            string relativePath = NormalizeEntryPath(entry.FullName);
            string fullPath = Path.GetFullPath(Path.Combine(destinationPath, relativePath));
            string destinationRoot = Path.GetFullPath(destinationPath);
            if (!fullPath.StartsWith(destinationRoot, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Archive entry path '{entry.FullName}' resolves outside destination root.");
            }

            string? parent = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }

            using Stream source = entry.Open();
            using FileStream target = File.Create(fullPath);
            source.CopyTo(target);
        }
    }

    private static string ComputeSha256Hex(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexStringLower(hash);
    }

    private static void ValidateBundleConnector(WorkspaceMcpContainerConnectorDefinition connector)
    {
        if (string.IsNullOrWhiteSpace(connector.BundlePath))
        {
            throw new InvalidOperationException($"MCPB connector '{connector.ConnectorId}' is missing bundle_path.");
        }

        if (!File.Exists(connector.BundlePath))
        {
            throw new InvalidOperationException($"MCPB bundle archive '{connector.BundlePath}' does not exist.");
        }

        if (string.IsNullOrWhiteSpace(connector.ServerType))
        {
            throw new InvalidOperationException($"MCPB connector '{connector.ConnectorId}' is missing server_type.");
        }

        if (string.IsNullOrWhiteSpace(connector.Image))
        {
            throw new InvalidOperationException($"MCPB connector '{connector.ConnectorId}' is missing a base image.");
        }
    }

    private static void ValidateEntryPath(string fullName)
    {
        string normalized = NormalizeEntryPath(fullName);
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized[0] == '/'
            || normalized.Contains("../", StringComparison.Ordinal)
            || normalized.Contains(':', StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Invalid archive entry path '{fullName}'.");
        }
    }

    private static string NormalizeEntryPath(string fullName)
        => fullName.Replace('\\', '/').Trim('/');

    private static void ValidateNotSymlink(ZipArchiveEntry entry)
    {
        const int UnixFileTypeMask = 0xF000;
        const int UnixSymlink = 0xA000;
        int unixMode = (entry.ExternalAttributes >> 16) & UnixFileTypeMask;
        if (unixMode == UnixSymlink)
        {
            throw new InvalidOperationException($"Symlink entry '{entry.FullName}' is not allowed in MCPB bundles.");
        }
    }

    private static string NormalizeIdentifier(string value)
        => SanitizeSegment(value);

    private static string SanitizeSegment(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char ch in value)
        {
            builder.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-');
        }

        return builder.ToString().Trim('-');
    }

    internal static string? InferServerType(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        string normalizedCommand = command.Trim();
        if (string.Equals(normalizedCommand, "npx", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedCommand, "node", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedCommand, "nodejs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedCommand, "npm", StringComparison.OrdinalIgnoreCase))
        {
            return "node";
        }

        if (string.Equals(normalizedCommand, "python", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedCommand, "python3", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedCommand, "python3.12", StringComparison.OrdinalIgnoreCase))
        {
            return "python";
        }

        if (string.Equals(normalizedCommand, "uv", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedCommand, "uvx", StringComparison.OrdinalIgnoreCase))
        {
            return "uv";
        }

        return null;
    }

    private static string ComputeSha256HexFromString(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(hash);
    }

    private static string BuildMaterializedDigest(string sourceDigest)
        => ComputeSha256HexFromString($"{sourceDigest.Trim()}\n{materializationRecipeVersion}");
}
