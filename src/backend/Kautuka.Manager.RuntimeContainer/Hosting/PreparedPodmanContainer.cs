using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

/// <summary>
/// An initialized application whose native network is prepared but whose process has not run.
/// The caller must confirm its source policy is active before starting this exact ID.
/// </summary>
internal sealed record PreparedPodmanContainer(string Id, string NetworkName, string Address, string SandboxKey)
{
    internal string[] StartArguments(bool attach)
        => attach ? ["start", "--attach", "--interactive", Id] : ["start", Id];
}

/// <summary>Native Podman create/init preparation; does not publish policy or start applications.</summary>
internal sealed class PodmanContainerPreparation(PodmanCommandRunner runner)
{
    private const string operationLabel = "kautuka.preparation";
    private const string helperLabel = "kautuka.preparation-helper";

    internal async Task<PreparedPodmanContainer> PrepareAsync(
        IReadOnlyList<string> createArguments,
        string networkName,
        string gateway,
        string routeHelperImage,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(createArguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(networkName);
        ContainerImageProvenancePolicy.EnsureTrusted(routeHelperImage, "Route helper");
        if (createArguments.Count == 0 || createArguments[0] != "create" || !createArguments.Contains("--pull=never"))
        {
            throw new ArgumentException("Preparation requires a create command with --pull=never.", nameof(createArguments));
        }

        if (!IPAddress.TryParse(gateway, out IPAddress? gatewayAddress) || gatewayAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("Preparation requires an IPv4 gateway.", nameof(gateway));
        }

        string operation = Guid.NewGuid().ToString("N");
        try
        {
            // Native labels let us recover ownership even if a timed-out create never returned its ID.
            string[] ownedCreate = ["create", "--label", operationLabel + "=" + operation, .. createArguments.Skip(1)];
            PodmanCommandResult created = await RunAsync(ownedCreate, cancellationToken, environment).ConfigureAwait(false);
            string id = created.StdOut.Trim();
            if (!IsContainerId(id))
            {
                throw new InvalidOperationException("Podman create did not return an immutable container ID.");
            }

            _ = await RunAsync(["init", id], cancellationToken).ConfigureAwait(false);
            PreparedPodmanContainer prepared = await InspectAsync(id, networkName, cancellationToken).ConfigureAwait(false);
            if (prepared.Address == gatewayAddress.ToString())
            {
                throw new InvalidOperationException("The application cannot own its egress gateway address.");
            }

            // container:<ID> starts the target as a dependency on the tested Podman runtime.
            // ns:<SandboxKey> joins only its initialized namespace and leaves the process stopped.
            string[] helper = ["run", "--rm", "--pull=never", "--label", operationLabel + "=" + operation,
                "--label", helperLabel + "=true", "--network", "ns:" + prepared.SandboxKey,
                "--user", "0:0", "--cap-drop=ALL", "--cap-add=NET_ADMIN", "--security-opt=no-new-privileges",
                "--read-only", "--entrypoint", "/usr/sbin/ip", routeHelperImage,
                "route", "replace", "default", "via", gatewayAddress.ToString()];
            _ = await RunAsync(helper, cancellationToken).ConfigureAwait(false);
            PreparedPodmanContainer after = await InspectAsync(id, networkName, cancellationToken).ConfigureAwait(false);
            if (after != prepared)
            {
                throw new InvalidOperationException("The initialized container's network changed during preparation.");
            }

            string[] readRoutes = ["run", "--rm", "--pull=never", "--label", operationLabel + "=" + operation,
                "--label", helperLabel + "=true", "--network", "ns:" + prepared.SandboxKey,
                "--user", "65532:65532", "--cap-drop=ALL", "--security-opt=no-new-privileges", "--read-only",
                "--entrypoint", "/usr/sbin/ip", routeHelperImage, "-j", "-4", "route", "show", "default"];
            PodmanCommandResult routes = await RunAsync(readRoutes, cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(routes.StdOut);
            JsonElement defaults = document.RootElement;
            if (defaults.ValueKind != JsonValueKind.Array || defaults.GetArrayLength() != 1
                || defaults[0].GetProperty("dst").GetString() != "default"
                || defaults[0].GetProperty("gateway").GetString() != gatewayAddress.ToString())
            {
                throw new InvalidOperationException("The prepared container does not have exactly the selected egress default route.");
            }

            return prepared;
        }
        catch (Exception preparationError)
        {
            // No source policy has been published by this primitive, so its allocation is safe to retire.
            // Cleanup must run even when the caller cancelled preparation.
            try
            {
                await RetireOperationAsync(operation).ConfigureAwait(false);
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException("Container preparation failed and its initialized allocation could not be removed.", preparationError, cleanupError);
            }

            throw;
        }
    }

    internal async Task RetireOperationAsync(string operation)
    {
        string[] inventory = ["ps", "--all", "--filter", "label=" + operationLabel + "=" + operation, "--no-trunc", "--format", "{{.ID}}"];
        PodmanCommandResult listed = await RunAsync(inventory, CancellationToken.None).ConfigureAwait(false);
        var owned = new List<(string Id, bool Helper)>();
        var errors = new List<Exception>();
        foreach (string id in listed.StdOut.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (!IsContainerId(id)) throw new InvalidOperationException("Preparation cleanup received an invalid container ID.");
                PodmanCommandResult inspected = await runner.RunAsync(["inspect", id], 30, CancellationToken.None).ConfigureAwait(false);
                if (inspected.ExitCode != 0 && await IsAbsentAsync(id).ConfigureAwait(false)) continue;
                if (inspected.ExitCode != 0)
                {
                    throw new InvalidOperationException(PodmanFailureFormatter.FormatFailure("Inspect preparation ownership", inspected));
                }

                using JsonDocument document = JsonDocument.Parse(inspected.StdOut);
                JsonElement container = document.RootElement[0];
                JsonElement labels = container.GetProperty("Config").GetProperty("Labels");
                if (container.GetProperty("Id").GetString() != id || !labels.TryGetProperty(operationLabel, out JsonElement owner)
                    || owner.GetString() != operation)
                {
                    throw new InvalidOperationException("Preparation cleanup could not confirm operation ownership.");
                }

                owned.Add((id, labels.TryGetProperty(helperLabel, out JsonElement helper) && helper.GetString() == "true"));
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }

        // A failed Podman CLI can leave its NET_ADMIN helper alive. Retire it before the application.
        foreach (var item in owned.OrderByDescending(static item => item.Helper))
        {
            try
            {
                // An auto-removed helper may disappear after inspection. Exact-ID absence is success.
                _ = await runner.RunAsync(["rm", "--force", item.Id], 30, CancellationToken.None).ConfigureAwait(false);
                if (!await IsAbsentAsync(item.Id).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("Preparation cleanup could not confirm container removal.");
                }
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }

        if (errors.Count > 0) throw new AggregateException("Preparation cleanup did not complete.", errors);
    }

    private async Task<bool> IsAbsentAsync(string id)
    {
        PodmanCommandResult exists = await runner.RunAsync(["container", "exists", id], 10, CancellationToken.None).ConfigureAwait(false);
        return exists.ExitCode == 1 && exists.TerminationReason == ProcessTerminationReason.NonZeroExit;
    }

    private static bool IsContainerId(string id) => id.Length == 64 && id.All(char.IsAsciiHexDigit);

    private async Task<PreparedPodmanContainer> InspectAsync(string id, string networkName, CancellationToken cancellationToken)
    {
        PodmanCommandResult result = await RunAsync(["inspect", id], cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(result.StdOut);
        JsonElement container = document.RootElement[0];
        JsonElement state = container.GetProperty("State");
        JsonElement settings = container.GetProperty("NetworkSettings");
        JsonElement networks = settings.GetProperty("Networks");
        string? sandbox = settings.GetProperty("SandboxKey").GetString();
        if (container.GetProperty("Id").GetString() != id || state.GetProperty("Status").GetString() != "initialized"
            || state.GetProperty("Running").GetBoolean()
            || container.GetProperty("Config").GetProperty("Labels").GetProperty("kautuka.managed").GetString() != "manager"
            || networks.EnumerateObject().Count() != 1 || !networks.TryGetProperty(networkName, out JsonElement network)
            || !IsRootlessNamespacePath(sandbox))
        {
            throw new InvalidOperationException("Podman did not preserve the managed application's initialized private network.");
        }

        string? address = network.GetProperty("IPAddress").GetString();
        if (!IPAddress.TryParse(address, out IPAddress? parsed) || parsed.AddressFamily != AddressFamily.InterNetwork
            || parsed.Equals(IPAddress.Any) || parsed.Equals(IPAddress.Broadcast) || IPAddress.IsLoopback(parsed))
        {
            throw new InvalidOperationException("Podman did not assign an IPv4 application address.");
        }

        return new PreparedPodmanContainer(id, networkName, parsed.ToString(), sandbox!);
    }

    private static bool IsRootlessNamespacePath(string? path)
    {
        string[] parts = (path ?? string.Empty).Split('/');
        return parts.Length == 6 && parts[0].Length == 0 && parts[1] == "run" && parts[2] == "user"
            && parts[3].Length > 0 && parts[3].All(char.IsAsciiDigit) && parts[4] == "netns"
            && parts[5].StartsWith("netns-", StringComparison.Ordinal) && parts[5].Length > 6
            && parts[5].All(static c => char.IsAsciiLetterOrDigit(c) || c == '-');
    }

    private async Task<PodmanCommandResult> RunAsync(string[] arguments, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        PodmanCommandResult result = await runner.RunAsync(arguments, 30, cancellationToken, environment).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(PodmanFailureFormatter.FormatFailure("Podman container preparation " + arguments[0], result));
        }

        return result;
    }
}
