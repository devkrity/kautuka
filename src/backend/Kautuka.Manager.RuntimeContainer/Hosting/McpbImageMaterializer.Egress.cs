using System.Globalization;
using System.Text;

using Kautuka.Manager.RuntimeContainer.Sandbox.Models;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

public sealed partial class McpbImageMaterializer
{
    private const string egressRecipeVersion = "mcpb-egress-v3";

    internal async Task<WorkspaceMcpContainerConnectorDefinition> ResolveControlledExecutionAsync(
        WorkspaceMcpContainerConnectorDefinition connector, bool materialize, ContainerContext? workspace, CancellationToken ct)
    {
        connector = ResolveGatewayBundle(connector);
        if (egressRuntime?.Enabled != true) throw new InvalidOperationException("Controlled execution is not configured.");
        string? serverType = ResolveEgressServerType(connector);
        if (serverType is null)
            return connector with { ResolvedImage = await egressRuntime.ResolveCachedRuntimeImageAsync(connector.Image, ct).ConfigureAwait(false) };
        WorkspaceMcpContainerConnectorDefinition expected = ResolveControlledRuntimeConnector(connector);
        if (materialize)
        {
            string built = await EnsureEgressImageAsync(connector, BundleWorkdir, false, workspace, ct).ConfigureAwait(false);
            if (built != expected.Image) throw new InvalidOperationException("Materialization returned an image outside its owned cache identity.");
        }
        // Only the materializer may bind its own derived local tag. Arbitrary
        // local images never enter the release/registry resolver through this path.
        PodmanCommandResult inspected = await podmanCommandRunner.RunAsync(["image", "inspect", expected.Image], 15, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (inspected.ExitCode != 0) throw new ContainerInspectionUnavailableException("The materialized connector image is not ready.");
        using System.Text.Json.JsonDocument image = System.Text.Json.JsonDocument.Parse(inspected.StdOut);
        if (image.RootElement.GetArrayLength() != 1) throw new InvalidOperationException("Materialized image inspection is ambiguous.");
        return expected with { ResolvedImage = ResolvedRuntimeImage.FromInspection(expected.Image, image.RootElement[0]) };
    }

    private async Task<string> EnsureEgressImageAsync(WorkspaceMcpContainerConnectorDefinition connector, string installRoot,
        bool artifact, ContainerContext? workspace, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Controlled materialization requires the Linux appliance.");
        string? serverType = ResolveEgressServerType(connector);
        if (string.IsNullOrWhiteSpace(serverType)) return connector.Image;
        if (serverType is not ("node" or "python" or "uv" or "binary")) throw new InvalidOperationException("Unsupported MCPB server type.");
        connector = connector with { ServerType = serverType };
        if (artifact) ValidateBundleConnector(connector);
        bool hasBundle = !string.IsNullOrWhiteSpace(connector.BundlePath);
        connector = UseReleasedSandboxBase(connector, serverType);
        if (artifact && workspace is not null) connector = connector with { Image = workspace.Image };
        ContainerEgressRuntime.Scope scope = ContainerEgressRuntime.ConnectorScope(connector.ConnectorId, workspace);
        await egressRuntime!.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        if (!(allowlist ?? RuntimeImageAllowlist.Empty).ImageReferences.Contains(connector.Image))
            throw new InvalidOperationException("Controlled materialization requires a sandbox base from the verified release inventory.");
        ResolvedRuntimeImage baseImage = await egressRuntime.ResolveCachedRuntimeImageAsync(connector.Image, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<KeyValuePair<string, string>> indexes = ResolveEgressIndexEnvironment(connector.Environment, serverType);
        string digest = hasBundle ? ComputeSha256Hex(connector.BundlePath!) : ComputeSha256HexFromString(connector.Image);
        string target = EgressImageReference(serverType, digest, connector.Image, installRoot, artifact, hasBundle);
        if (await EgressImageExistsAsync(target, cancellationToken).ConfigureAwait(false)) return target;
        await imageBuildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string context = Path.Combine(egressRuntime!.MaterializationRoot, Guid.NewGuid().ToString("N"));
        string systemImage = target + "-sys";
        string installedImage = artifact ? target + "-deps" : target;
        PreparedPodmanContainer? prepared = null;
        try
        {
            if (await EgressImageExistsAsync(target, cancellationToken).ConfigureAwait(false)) return target;
            Directory.CreateDirectory(egressRuntime!.MaterializationRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.CreateDirectory(context);
            if (hasBundle) ExtractBundle(connector.BundlePath!, Path.Combine(context, "bundle"));
            await File.WriteAllTextAsync(Path.Combine(context, "contract.json"), "{\"installRoot\":\"" + System.Text.Json.JsonEncodedText.Encode(installRoot) + "\"}", cancellationToken).ConfigureAwait(false);
            string containerfile = BuildEgressSystemContainerfile(baseImage.ForSource(connector.Image), serverType, installRoot, hasBundle);
            await File.WriteAllTextAsync(Path.Combine(context, "Containerfile"), containerfile, cancellationToken).ConfigureAwait(false);
            await EgressCommandAsync(["build", "--pull=never", "--network=none", "--cap-drop=ALL", "--cap-add=CHOWN", "--cap-add=DAC_OVERRIDE", "--security-opt=no-new-privileges",
                "-t", systemImage, "-f", Path.Combine(context, "Containerfile"), context], 300, cancellationToken).ConfigureAwait(false);
            var source = new ContainerEgressRuntime.SourceIdentity(scope, "materializer", connector.ConnectorId, PackageEligible: true);
            string name = "kautuka-mcpb-install-" + Guid.NewGuid().ToString("N");
            prepared = await egressRuntime!.PrepareAndAuthorizeAsync(source, name, (network, dns) =>
            {
                var args = new List<string> { "create", "--pull=never", "--restart=no", "--name", name, "--label", "kautuka.managed=manager",
                    "--label", "kautuka.role=materializer", "--network", network, "--dns", dns, "--sysctl", "net.ipv6.conf.all.disable_ipv6=1",
                    "--cap-drop=ALL", "--security-opt=no-new-privileges", "--user", "1000:1000", "--pids-limit", "256", "--memory", "536870912",
                    "--tmpfs", "/tmp:rw,noexec,nosuid", "--workdir", installRoot };
                foreach ((string key, string value) in indexes) args.AddRange(["--env", key + "=" + value]);
                args.AddRange([systemImage, "/bin/sh", "-lc", BuildEgressInstallScript(serverType)]);
                return args;
            }, cancellationToken).ConfigureAwait(false);
            PodmanCommandResult started = await egressRuntime.StartWithAdmissionAsync(prepared,
                token => podmanCommandRunner.RunAsync(prepared.StartArguments(attach: false), 15, token), cancellationToken).ConfigureAwait(false);
            if (started.ExitCode != 0) throw new InvalidOperationException(PodmanFailureFormatter.FormatFailure("Start MCPB installer", started));
            // Installation may take time; it must not hold the short network/publication gate.
            PodmanCommandResult waited = await EgressCommandAsync(["wait", prepared.Id], 900, cancellationToken).ConfigureAwait(false);
            if (!int.TryParse(waited.StdOut.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int exit) || exit != 0)
            {
                PodmanCommandResult logs = await EgressCommandAsync(["logs", prepared.Id], 15, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException(PodmanFailureFormatter.FormatFailure("Install MCPB packages", exit == 0 ? 1 : exit, logs.StdOut, logs.StdErr));
            }
            await EgressCommandAsync(["commit", "--change", "USER 1000:1000", "--change", "WORKDIR " + installRoot,
                "--change", "ENTRYPOINT []", "--change", "CMD [\"tail\",\"-f\",\"/dev/null\"]", prepared.Id, installedImage], 120, cancellationToken).ConfigureAwait(false);
            if (artifact)
            {
                await File.WriteAllTextAsync(Path.Combine(context, "Containerfile.scratch"), BuildSandboxScratchContainerfile(installedImage, installRoot), cancellationToken).ConfigureAwait(false);
                await EgressCommandAsync(["build", "--pull=never", "--network=none", "-t", target, "-f", Path.Combine(context, "Containerfile.scratch"), context], 300, cancellationToken).ConfigureAwait(false);
            }
            return target;
        }
        finally
        {
            try
            {
                if (prepared is not null) await egressRuntime!.RetireAsync(prepared.Id, CancellationToken.None).ConfigureAwait(false);
                // A forced image removal can remove dependent containers. Never bypass retirement.
                _ = await podmanCommandRunner.RunAsync(["rmi", systemImage], 30, CancellationToken.None).ConfigureAwait(false);
                if (artifact) _ = await podmanCommandRunner.RunAsync(["rmi", installedImage], 30, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                try { if (Directory.Exists(context)) Directory.Delete(context, recursive: true); }
                finally { imageBuildGate.Release(); }
            }
        }
    }

    internal WorkspaceMcpContainerConnectorDefinition ResolveControlledRuntimeConnector(WorkspaceMcpContainerConnectorDefinition connector)
    {
        connector = ResolveGatewayBundle(connector);
        string? serverType = ResolveEgressServerType(connector);
        if (serverType is null) return connector;
        connector = UseReleasedSandboxBase(connector, serverType) with { ServerType = serverType };
        bool hasBundle = !string.IsNullOrWhiteSpace(connector.BundlePath);
        string digest = hasBundle ? ComputeSha256Hex(connector.BundlePath!) : ComputeSha256HexFromString(connector.Image);
        string image = EgressImageReference(serverType, digest, connector.Image, BundleWorkdir, false, hasBundle);
        return hasBundle ? AgentSandboxInstallContract.ApplyRuntime(connector, image, true) : connector with { Image = image };
    }

    private static string? ResolveEgressServerType(WorkspaceMcpContainerConnectorDefinition connector)
    {
        string? value = string.IsNullOrWhiteSpace(connector.ServerType) ? InferServerType(connector.Command) : connector.ServerType;
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (string.Equals(value, "node", StringComparison.OrdinalIgnoreCase)) return "node";
        if (string.Equals(value, "python", StringComparison.OrdinalIgnoreCase)) return "python";
        if (string.Equals(value, "uv", StringComparison.OrdinalIgnoreCase)) return "uv";
        if (string.Equals(value, "binary", StringComparison.OrdinalIgnoreCase)) return "binary";
        throw new InvalidOperationException("Unsupported MCPB server type.");
    }

    private static string EgressImageReference(string serverType, string digest, string image, string root, bool artifact, bool bundle)
    {
        string identity = egressRecipeVersion + "\n" + digest + "\n" + image + "\n" + root;
        return artifact ? BuildSandboxArtifactImageReference(serverType, identity) : BuildLocalImageReference(serverType, identity, bundle);
    }

    internal static IReadOnlyList<KeyValuePair<string, string>> ResolveEgressIndexEnvironment(IReadOnlyList<KeyValuePair<string, string>> environment, string serverType)
    {
        Dictionary<string, string> indexes = ResolveLanguagePackageIndexEnvironment(environment, serverType)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        if (serverType == "python") indexes.TryAdd("UV_INDEX_URL", indexes["PIP_INDEX_URL"]);
        foreach ((string key, string value) in indexes)
        {
            string expected = key switch
            {
                "NPM_CONFIG_REGISTRY" => "http://kautuka-pkgs-proxy:20101/npm/",
                "PIP_TRUSTED_HOST" => "kautuka-pkgs-proxy",
                _ => "http://kautuka-pkgs-proxy:20101/pypi/simple/",
            };
            if (value != expected) throw new InvalidOperationException("Controlled materialization requires the designated package endpoint for " + key + ".");
        }
        return [.. indexes.OrderBy(static pair => pair.Key, StringComparer.Ordinal)];
    }

    internal static string BuildEgressSystemContainerfile(string image, string serverType, string installRoot, bool hasBundle)
    {
        ContainerImageProvenancePolicy.EnsureSingleLine(image, "offline materialization base");
        if (!installRoot.StartsWith('/') || installRoot.Contains("..", StringComparison.Ordinal)
            || installRoot.Any(static value => !char.IsAsciiLetterOrDigit(value) && value is not ('/' or '-' or '_' or '.')))
            throw new ArgumentException("Materialization requires a safe absolute install directory.", nameof(installRoot));
        string root = AgentSandboxInstallContract.QuoteShell(installRoot);
        var text = new StringBuilder().AppendLine("FROM " + image).AppendLine("USER root")
            .AppendLine("WORKDIR " + installRoot)
            .AppendLine("RUN chown 1000:1000 " + root);
        if (hasBundle) text.AppendLine("COPY --chown=1000:1000 bundle/ " + installRoot + "/");
        text.AppendLine("COPY contract.json /contract.json").AppendLine("USER 1000:1000")
            .AppendLine("ENV HOME=/tmp NPM_CONFIG_CACHE=/tmp/.npm PIP_CACHE_DIR=/tmp/.pip UV_CACHE_DIR=/tmp/.uv")
            .AppendLine("ENV UV_PROJECT_ENVIRONMENT=" + installRoot + "/.venv UV_PYTHON_INSTALL_DIR=" + installRoot + "/.python")
            .AppendLine("ENTRYPOINT []").AppendLine("CMD [\"tail\",\"-f\",\"/dev/null\"]");
        if (serverType == "binary") text.AppendLine("RUN python3 -I -c \"import json,pathlib; r=pathlib.Path('.').resolve(); p=(r/json.load(open('manifest.json'))['server']['entry_point']).resolve(); assert p.is_relative_to(r) and p.is_file(); p.chmod(0o755)\"");
        return text.ToString();
    }

    internal static string BuildEgressInstallScript(string serverType)
    {
        if (serverType is "python" or "uv")
            return "set -eu; uv venv --python python3 \"$UV_PROJECT_ENVIRONMENT\"; " + BuildLanguagePackageInstallScript("uv", forSandboxArtifact: true);
        return BuildLanguagePackageInstallScript(serverType, forSandboxArtifact: true);
    }

    private async Task<bool> EgressImageExistsAsync(string image, CancellationToken cancellationToken)
    {
        PodmanCommandResult result = await podmanCommandRunner.RunAsync(["image", "exists", image], 15, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.ExitCode is 0 or 1) return result.ExitCode == 0;
        throw new ContainerInspectionUnavailableException(PodmanFailureFormatter.FormatFailure("Inspect materialization image", result));
    }

    private async Task<PodmanCommandResult> EgressCommandAsync(string[] arguments, int timeout, CancellationToken cancellationToken)
    {
        PodmanCommandResult result = await podmanCommandRunner.RunAsync(arguments, timeout, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.ExitCode != 0) throw new InvalidOperationException(PodmanFailureFormatter.FormatFailure("MCPB materialization", result));
        return result;
    }
}
