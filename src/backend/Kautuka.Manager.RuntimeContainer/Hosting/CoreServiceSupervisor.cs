using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Kautuka.Hosting.Hosting;
using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Sandbox;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

public sealed record CoreServicePaths(string StateRoot, string SocketRoot, string WorkspaceRoot,
    string ConfigurationRoot, string WriterLock, string MaintenanceRoot);

/// <summary>Fixed application services, reconciled by the existing Manager worker before Gateway's desired state.</summary>
public sealed class CoreServiceSupervisor(PodmanCommandRunner runner, ContainerEgressRuntime egress,
    RuntimeImageAllowlist inventory, CoreServicePaths paths, CoreServiceOptions options) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private const string containerSocketRoot = "/run/kautuka";
    private const string containerStateRoot = "/data";
    private sealed record Service(string Id, string Executable, string Image, bool Networkless,
        IReadOnlyList<WorkspaceSidecarVolumeBinding> Mounts, IReadOnlyDictionary<string, string> Environment, string Fingerprint)
    {
        internal string Name => "kautuka-" + Id;
    }

    public void Dispose() { gate.Dispose(); GC.SuppressFinalize(this); }

    public async Task<bool> EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled) return true;
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Core service containers require the Linux appliance.");
        if (!egress.Enabled) throw new InvalidOperationException("Core service containers require mandatory egress.");
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string maintenance = Path.Combine(paths.MaintenanceRoot, "maintenance.json");
            if (File.Exists(maintenance) && KautukaMaintenanceMode.ShouldSuppressWorkers(await File.ReadAllTextAsync(maintenance, cancellationToken).ConfigureAwait(false)))
                return false;
            if (!File.Exists(paths.WriterLock)) throw new InvalidOperationException("The installer must provision the host database-writer lock before starting services.");
            string gatewayData = Path.Combine(paths.StateRoot, "gateway-data");
            if (File.Exists(Path.Combine(paths.StateRoot, "kautuka.db")))
                throw new InvalidOperationException("The release installer must relocate the legacy Gateway database before enabling container services.");
            await ValidateWorkspaceRootAsync(gatewayData, cancellationToken).ConfigureAwait(false);
            foreach (string directory in new[] { gatewayData, paths.WorkspaceRoot, paths.MaintenanceRoot, Path.Combine(paths.StateRoot, "credentialsHost") })
                Directory.CreateDirectory(directory);
            foreach (string service in new[] { "credentialshost", "gateway", "manager", "updateservice" })
                Directory.CreateDirectory(Path.Combine(paths.SocketRoot, service));
            await egress.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
            Service credentials = BuildService("credentialshost", "Kautuka.CredentialsHost", networkless: true);
            string credentialsId = await EnsureServiceAsync(credentials, cancellationToken).ConfigureAwait(false);
            if (!await WaitForSocketAsync(credentialsId, "credentialshost", cancellationToken).ConfigureAwait(false)) return false;
            foreach ((string service, string identity) in new[] { ("manager", Kautuka.Manager.Protos.ManagerClientIdentities.GatewayCertificateFileName),
                ("updateService", ".updateservice-client.pfx"), ("credentialsHost/transport", ".credentials-client.pfx") })
            {
                string root = Path.Combine(paths.StateRoot, service);
                string published = Path.Combine(root, "clients", "gateway");
                if (!Directory.Exists(published) || !File.Exists(Path.Combine(published, ".ca.cer")) || !File.Exists(Path.Combine(published, identity))) return false;
            }
            Service gateway = BuildService("gateway", "Kautuka.Gateway", networkless: false);
            string gatewayId = await EnsureServiceAsync(gateway, cancellationToken).ConfigureAwait(false);
            return await WaitForSocketAsync(gatewayId, "gateway", cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (string service in new[] { "gateway", "credentialshost" })
            {
                string? json = await PodmanContainerInspector.ReadAsync(runner, "kautuka-" + service, cancellationToken).ConfigureAwait(false);
                if (json is null) continue;
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement container = document.RootElement[0];
                RequireOwnership(container, service);
                await RetireAsync(container.GetProperty("Id").GetString()!, service == "credentialshost", cancellationToken).ConfigureAwait(false);
            }
        }
        finally { gate.Release(); }
    }

    private async Task ValidateWorkspaceRootAsync(string gatewayData, CancellationToken ct)
    {
        string workspace = Path.GetFullPath(paths.WorkspaceRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (workspace.Length == 0) throw new InvalidOperationException("The Gateway workspace mount cannot be the filesystem root.");
        string store = (await CommandAsync(["info", "--format", "{{.Store.GraphRoot}}\n{{.Store.RunRoot}}"], ct).ConfigureAwait(false)).StdOut;
        string[] storage = store.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (storage.Length != 2 || storage.Any(path => !Path.IsPathFullyQualified(path)))
            throw new InvalidOperationException("Cannot confirm the runtime's private storage paths.");
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        IEnumerable<string> privateRoots = new[] { paths.StateRoot, gatewayData, Path.Combine(paths.StateRoot, "credentialsHost"),
            Path.Combine(paths.StateRoot, "manager"), Path.Combine(paths.StateRoot, "updateService"), Path.Combine(paths.StateRoot, "security"), Path.Combine(paths.StateRoot, "oci-images"),
            paths.ConfigurationRoot, paths.SocketRoot, paths.MaintenanceRoot, Path.Combine(home, ".config", "containers") }
            .Concat(storage);
        string? runtimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(runtimeDirectory)) privateRoots = privateRoots.Append(runtimeDirectory);
        foreach (string root in privateRoots)
        {
            string canonical = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            if (canonical == workspace || canonical.StartsWith(workspace + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || root != paths.StateRoot && workspace.StartsWith(canonical + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidOperationException("The Gateway workspace mount overlaps private service state.");
        }
    }

    private Service BuildService(string id, string executable, bool networkless)
    {
        string image = inventory.Images.SingleOrDefault(entry => entry.Id == "core-" + id)?.Image
            ?? throw new InvalidOperationException("The verified release inventory is missing core-" + id + ".");
        ContainerImageProvenancePolicy.EnsureTrusted(image, "Core service " + id, inventory.ImageReferences, requireListed: true);
        var mounts = new List<WorkspaceSidecarVolumeBinding>
        {
            new(paths.WriterLock, KautukaDatabaseWriterLease.LockPath, true),
            new(paths.MaintenanceRoot, Path.GetDirectoryName(KautukaMaintenanceMode.MarkerPath)!, true),
            new(Path.Combine(paths.SocketRoot, id), containerSocketRoot + "/" + id, false),
        };
        var environment = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["Kautuka__Mode"] = "prod",
            ["DOTNET_ENVIRONMENT"] = "Production",
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["Kautuka__ContainerStateRoot"] = containerStateRoot,
            ["Kautuka__Transport"] = "unix",
            ["Kautuka__SocketRoot"] = containerSocketRoot,
            ["HOME"] = "/tmp",
            ["Kautuka__Telemetry__Otlp__LogsEndpoint"] = "",
            ["Kautuka__Telemetry__Otlp__MetricsEndpoint"] = "",
            ["Kautuka__Telemetry__Otlp__TracesEndpoint"] = "",
        };
        if (networkless) mounts.Add(new(Path.Combine(paths.StateRoot, "credentialsHost"), containerStateRoot + "/credentialsHost", false));
        else
        {
            mounts.Add(new(Path.Combine(paths.StateRoot, "gateway-data"), containerStateRoot, false));
            mounts.Add(new(paths.WorkspaceRoot, paths.WorkspaceRoot, false));
            environment["Kautuka__Workspace__BaseRoot"] = paths.WorkspaceRoot;
            foreach (string client in new[] { "manager", "updateservice", "credentialshost" })
                mounts.Add(new(Path.Combine(paths.SocketRoot, client), containerSocketRoot + "/" + client, true));
            foreach (string service in new[] { "manager", "updateService", "credentialsHost/transport" })
                mounts.Add(new(Path.Combine(paths.StateRoot, service, "clients", "gateway"), containerStateRoot + "/" + service + "/clients/gateway", true));
            foreach (string kind in new[] { "gateway-private", "gateway-trust" })
            {
                string source = Path.Combine(paths.StateRoot, "security", "channel", kind);
                if (!Directory.Exists(source)) continue;
                string destination = "/security/channel/" + kind;
                mounts.Add(new(source, destination, true));
                environment["Kautuka__ChannelGateway__ProductionMaterial__" + (kind == "gateway-private" ? "GatewayPrivateRoot" : "GatewayTrustRoot")] = destination;
            }
        }
        string configuration = Path.Combine(paths.ConfigurationRoot, id + ".json");
        string configurationHash = "";
        if (File.Exists(configuration))
        {
            mounts.Add(new(configuration, "/app/appsettings.Production.json", true));
            configurationHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(configuration)));
        }
        IEnumerable<string> specification = new[] { id, executable, image, configurationHash }
            .Concat(mounts.Select(mount => mount.HostPath + ":" + mount.ContainerPath + ":" + mount.ReadOnly))
            .Concat(environment.Select(pair => pair.Key + "=" + pair.Value));
        string fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', specification))));
        return new Service(id, executable, image, networkless, mounts, environment, fingerprint);
    }

    private static List<string> CreateArguments(Service service, string network, string? dns)
    {
        var args = new List<string> { "create", "--pull=never", "--restart=no", "--name", service.Name,
            "--label", "kautuka.managed=manager", "--label", "kautuka.role=core", "--label", "kautuka.core-service=" + service.Id,
            "--label", "kautuka.core-spec=" + service.Fingerprint, "--network", network,
            "--userns=keep-id:uid=1000,gid=1000", "--user=1000:1000", "--cap-drop=ALL", "--security-opt=no-new-privileges",
            "--read-only", "--pids-limit", "512", "--memory", "1073741824", "--tmpfs", "/tmp:rw,noexec,nosuid",
            "--workdir", "/app", "--entrypoint", "/app/" + service.Executable };
        if (dns is not null) args.AddRange(["--dns", dns, "--sysctl", "net.ipv6.conf.all.disable_ipv6=1"]);
        foreach (WorkspaceSidecarVolumeBinding mount in service.Mounts)
            args.AddRange(["--volume", mount.HostPath + ":" + mount.ContainerPath + (mount.ReadOnly ? ":ro" : ":rw")]);
        foreach ((string key, string value) in service.Environment) args.AddRange(["--env", key + "=" + value]);
        args.Add(service.Image);
        return args;
    }

    private async Task<string> EnsureServiceAsync(Service service, CancellationToken ct)
    {
        using JsonDocument image = JsonDocument.Parse((await CommandAsync(["image", "inspect", service.Image], ct).ConfigureAwait(false)).StdOut);
        string imageId = image.RootElement[0].GetProperty("Id").GetString()!;
        string? inspected = await PodmanContainerInspector.ReadAsync(runner, service.Name, ct).ConfigureAwait(false);
        if (inspected is not null)
        {
            using JsonDocument document = JsonDocument.Parse(inspected);
            JsonElement container = document.RootElement[0];
            RequireOwnership(container, service.Id);
            string id = container.GetProperty("Id").GetString()!;
            PreparedPodmanContainer? admitted = service.Networkless ? null : await egress.FindSourceAsync(service.Name, ct).ConfigureAwait(false);
            if (Matches(container, service, imageId, admitted)) return id;
            await RetireAsync(id, service.Networkless, ct).ConfigureAwait(false);
        }
        if (!service.Networkless)
        {
            var identity = new ContainerEgressRuntime.SourceIdentity(new ContainerEgressRuntime.Scope("system", service.Id, "core"), "core", service.Id);
            PreparedPodmanContainer prepared = await egress.PrepareAndAuthorizeAsync(identity, service.Name,
                (network, dns) => CreateArguments(service, network, dns), ct).ConfigureAwait(false);
            try
            {
                await egress.StartWithAdmissionAsync(prepared, token => CommandAsync(prepared.StartArguments(false), token), ct).ConfigureAwait(false);
                return prepared.Id;
            }
            catch { await egress.RetireAsync(prepared.Id, CancellationToken.None).ConfigureAwait(false); throw; }
        }
        string operation = Guid.NewGuid().ToString("N");
        List<string> create = CreateArguments(service, "none", null);
        create.InsertRange(1, ["--label", "kautuka.preparation=" + operation]);
        try
        {
            string id = (await CommandAsync(create, ct).ConfigureAwait(false)).StdOut.Trim();
            if (id.Length != 64 || !id.All(char.IsAsciiHexDigit)) throw new InvalidOperationException("Core service create did not return an immutable ID.");
            await CommandAsync(["start", id], ct).ConfigureAwait(false);
            return id;
        }
        catch { await new PodmanContainerPreparation(runner).RetireOperationAsync(operation).ConfigureAwait(false); throw; }
    }

    private static void RequireOwnership(JsonElement container, string service)
    {
        JsonElement labels = container.GetProperty("Config").GetProperty("Labels");
        if (!labels.TryGetProperty("kautuka.managed", out JsonElement owner) || owner.GetString() != "manager"
            || !labels.TryGetProperty("kautuka.role", out JsonElement role) || role.GetString() != "core"
            || !labels.TryGetProperty("kautuka.core-service", out JsonElement id) || id.GetString() != service)
            throw new InvalidOperationException("The core service name belongs to an unrecognized container; refusing to replace it.");
    }

    private static bool Matches(JsonElement container, Service service, string imageId, PreparedPodmanContainer? admitted)
    {
        JsonElement config = container.GetProperty("Config"), host = container.GetProperty("HostConfig");
        if (!container.GetProperty("State").GetProperty("Running").GetBoolean() || container.GetProperty("Image").GetString() != imageId
            || !config.GetProperty("Labels").TryGetProperty("kautuka.core-spec", out JsonElement fingerprint) || fingerprint.GetString() != service.Fingerprint
            || config.GetProperty("User").GetString() != "1000:1000" || !host.GetProperty("ReadonlyRootfs").GetBoolean()
            || host.GetProperty("RestartPolicy").GetProperty("Name").GetString() is not ("no" or "")
            || !host.GetProperty("SecurityOpt").EnumerateArray().Any(value => value.GetString() == "no-new-privileges")
            || !config.GetProperty("CreateCommand").EnumerateArray().Any(value => value.GetString() == "--userns=keep-id:uid=1000,gid=1000")
            || !Empty(container.GetProperty("EffectiveCaps")) || !Empty(container.GetProperty("BoundingCaps"))
            || host.GetProperty("PidsLimit").GetInt64() != 512 || host.GetProperty("Memory").GetInt64() != 1073741824
            || !config.GetProperty("Entrypoint").EnumerateArray().Select(value => value.GetString()).SequenceEqual(new[] { "/app/" + service.Executable })
            || !Empty(config.GetProperty("Cmd"))) return false;
        if (container.TryGetProperty("Pod", out JsonElement pod) && !string.IsNullOrEmpty(pod.GetString())) return false;
        JsonElement tmpfs = host.GetProperty("Tmpfs");
        if (tmpfs.EnumerateObject().Count() != 1 || !tmpfs.TryGetProperty("/tmp", out JsonElement temporary)
            || !temporary.GetString()!.Split(',').Contains("noexec", StringComparer.Ordinal)
            || !temporary.GetString()!.Split(',').Contains("nosuid", StringComparer.Ordinal)) return false;
        foreach (JsonElement mount in container.GetProperty("Mounts").EnumerateArray())
        {
            string? kind = mount.GetProperty("Type").GetString();
            if (kind == "tmpfs" && mount.GetProperty("Destination").GetString() == "/tmp") continue;
            if (kind != "bind") return false;
            WorkspaceSidecarVolumeBinding? expected = service.Mounts.SingleOrDefault(item => item.ContainerPath == mount.GetProperty("Destination").GetString());
            if (expected is null || Path.GetFullPath(expected.HostPath) != mount.GetProperty("Source").GetString()
                || mount.GetProperty("RW").GetBoolean() == expected.ReadOnly) return false;
        }
        string json = container.GetRawText();
        string expectedNetwork = service.Networkless ? "none" : "kautuka-core";
        if (LiveContainerSpec.DescribeSidecarDrift(json, service.Image, [], imageId, service.Mounts, service.Environment, expectedNetwork) is not null) return false;
        string[] environment = [.. config.GetProperty("Env").EnumerateArray().Select(value => value.GetString()!)];
        if (service.Environment.Any(pair => !environment.Contains(pair.Key + "=" + pair.Value, StringComparer.Ordinal))) return false;
        if (service.Networkless) return true;
        return admitted is not null && admitted.Id == container.GetProperty("Id").GetString()
            && container.GetProperty("NetworkSettings").GetProperty("SandboxKey").GetString() == admitted.SandboxKey
            && container.GetProperty("NetworkSettings").GetProperty("Networks").GetProperty("kautuka-core").GetProperty("IPAddress").GetString() == admitted.Address;
    }

    private static bool Empty(JsonElement value) => value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0;

    private async Task RetireAsync(string id, bool networkless, CancellationToken ct)
    {
        if (!networkless) { await egress.RetireAsync(id, ct).ConfigureAwait(false); return; }
        await CommandAsync(["rm", "--force", id], ct).ConfigureAwait(false);
        PodmanCommandResult exists = await runner.RunAsync(["container", "exists", id], 10, ct).ConfigureAwait(false);
        if (exists.ExitCode != 1 || exists.TerminationReason != ProcessTerminationReason.NonZeroExit)
            throw new InvalidOperationException("Could not confirm core service retirement.");
    }

    private async Task<bool> WaitForSocketAsync(string id, string service, CancellationToken ct)
    {
        string socketPath = Path.Combine(paths.SocketRoot, service, service + ".sock");
        for (int attempt = 0; attempt < 40; attempt++)
        {
            PodmanCommandResult state = await CommandAsync(["inspect", "--format", "{{.State.Running}}", id], ct).ConfigureAwait(false);
            if (state.StdOut.Trim() != "true") return false;
            try
            {
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(1));
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), deadline.Token).ConfigureAwait(false);
                return true;
            }
            catch (SocketException) { }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
        return false;
    }

    private async Task<PodmanCommandResult> CommandAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        PodmanCommandResult result = await runner.RunAsync(args, 30, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (result.ExitCode != 0) throw new InvalidOperationException(PodmanFailureFormatter.FormatFailure("Core service lifecycle", result));
        return result;
    }
}
