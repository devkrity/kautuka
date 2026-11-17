using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using Grpc.Core;

using Kautuka.Manager.Grpc;
using Kautuka.Manager.Protos;
using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.Manager.RuntimeContainer.Sandbox;
using Kautuka.Manager.RuntimeContainer.Sandbox.Models;
using Kautuka.NetworkAccessController;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;

using NacController = Kautuka.NetworkAccessController.NetworkAccessController;

namespace Kautuka.NetworkAccess.Acceptance;

public sealed record AcceptanceObservation(
    [property: JsonPropertyName("testId")] string TestId,
    [property: JsonPropertyName("observedValue")] string ObservedValue,
    [property: JsonPropertyName("expectedValue")] string ExpectedValue,
    [property: JsonPropertyName("ncObservedValue")] string NcObservedValue,
    [property: JsonPropertyName("ncExpectedValue")] string NcExpectedValue,
    [property: JsonPropertyName("ncFlipped")] bool NcFlipped,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("timestampUtc")] DateTimeOffset TimestampUtc);

[JsonSerializable(typeof(AcceptanceObservation))]
[JsonSerializable(typeof(List<AcceptanceObservation>))]
[JsonSerializable(typeof(InfraAllowlistDocument))]
[JsonSerializable(typeof(ProductionProofReport))]
internal sealed partial class AcceptanceJsonContext : JsonSerializerContext;

public sealed record ProductionProofReport(
    [property: JsonPropertyName("generatedAtUtc")] DateTimeOffset GeneratedAtUtc,
    [property: JsonPropertyName("gitSha")] string GitSha,
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("podmanVersion")] string PodmanVersion,
    [property: JsonPropertyName("envoyVersion")] string EnvoyVersion,
    [property: JsonPropertyName("corednsVersion")] string CoreDnsVersion,
    [property: JsonPropertyName("kernelVersion")] string KernelVersion,
    [property: JsonPropertyName("egressManagerGateOpen")] bool EgressManagerGateOpen,
    [property: JsonPropertyName("canaryVerificationPassed")] bool CanaryVerificationPassed,
    [property: JsonPropertyName("upstreamDnsRule")] string UpstreamDnsRule,
    [property: JsonPropertyName("pendingCardId")] string PendingCardId,
    [property: JsonPropertyName("pendingCardDestination")] string PendingCardDestination,
    [property: JsonPropertyName("createdRuleId")] string CreatedRuleId,
    [property: JsonPropertyName("createdRuleDestination")] string CreatedRuleDestination,
    [property: JsonPropertyName("egressDefaultRoutes")] string EgressDefaultRoutes,
    [property: JsonPropertyName("observations")] IReadOnlyList<AcceptanceObservation> Observations,
    [property: JsonPropertyName("summary")] string Summary);

public static class AcceptanceHarness
{
    private static readonly List<AcceptanceObservation> observations = [];

    /// <summary>
    /// Records an acceptance test result adhering strictly to Ground Rule G9 (Section 7.0):
    /// 1. Compares observed values read during execution (never hard-coded literals).
    /// 2. Checks the negative-control result against its expected observation.
    /// 3. For structured observations, requires at least one shared field to change.
    /// A changed observation alone does not prove that a mutation disabled the intended check.
    /// </summary>
    public static AcceptanceObservation Record(
        string testId,
        string observedValue,
        string expectedValue,
        string ncObservedValue,
        string ncExpectedValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(testId);
        ArgumentException.ThrowIfNullOrWhiteSpace(observedValue);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedValue);
        ArgumentException.ThrowIfNullOrWhiteSpace(ncObservedValue);
        ArgumentException.ThrowIfNullOrWhiteSpace(ncExpectedValue);

        bool effectiveNcFlipped =
            string.Equals(ncObservedValue.Trim(), ncExpectedValue.Trim(), StringComparison.Ordinal) &&
            ObservationsDiffer(observedValue, ncObservedValue);

        if (!effectiveNcFlipped)
        {
            var failRecord = new AcceptanceObservation(
                testId,
                observedValue,
                expectedValue,
                ncObservedValue,
                ncExpectedValue,
                false,
                "FAIL (cannot fail: NC did not flip)",
                DateTimeOffset.UtcNow);
            observations.Add(failRecord);
            return failRecord;
        }

        bool valuesMatch = string.Equals(observedValue.Trim(), expectedValue.Trim(), StringComparison.Ordinal);
        string outcome = valuesMatch ? "PASS" : "FAIL (mismatch)";

        var record = new AcceptanceObservation(
            testId,
            observedValue,
            expectedValue,
            ncObservedValue,
            ncExpectedValue,
            true,
            outcome,
            DateTimeOffset.UtcNow);

        observations.Add(record);
        return record;
    }

    public static IReadOnlyList<AcceptanceObservation> GetAll() => observations.AsReadOnly();

    private static bool ObservationsDiffer(string observed, string negativeControl)
    {
        Dictionary<string, string>? left = ParseFields(observed);
        Dictionary<string, string>? right = ParseFields(negativeControl);
        if (left is null || right is null)
            return left is null && right is null && !string.Equals(observed.Trim(), negativeControl.Trim(), StringComparison.Ordinal);

        return left.Any(pair => right.TryGetValue(pair.Key, out string? value)
            && !string.Equals(pair.Value, value, StringComparison.Ordinal));
    }

    private static Dictionary<string, string>? ParseFields(string value)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string part in value.Split(':'))
        {
            int separator = part.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0 || !fields.TryAdd(part[..separator].Trim(), part[(separator + 1)..].Trim()))
                return null;
        }
        return fields;
    }

    public static IReadOnlyList<AcceptanceObservation> ProductionObservations(
        IReadOnlyList<AcceptanceObservation> packageProxy,
        params AcceptanceObservation[] remaining) => [.. packageProxy, .. remaining];
}

public static class Program
{
    private static Func<string, IReadOnlyList<string>, Task<(int ExitCode, string StdOut, string StdErr)>>? execOverride;
    private sealed class AcceptanceHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static async Task<IReadOnlyList<AcceptanceObservation>> VerifyManagedPackageProxyPathAsync(
        string repoRoot,
        string tempRoot,
        EgressTopologyManager topology,
        NacController controller,
        WorkloadRegistry registry,
        PodmanCommandRunner podman,
        WorkloadPostStartVerifier verifier,
        ILoggerFactory loggerFactory)
    {
        const string proxyName = "kautuka-pkgs-proxy";
        const string proxyImage = "localhost/kautuka-pkgs-proxy:acceptance";
        string cachePath = Path.Combine(tempRoot, "package-proxy-cache");
        string dockerfilePath = Path.Combine(repoRoot, "src", "images", "kautuka-pkgs-proxy");
        var proxy = new WorkspaceSidecarDefinition(
            proxyName,
            proxyImage,
            [],
            [new WorkspaceSidecarVolumeBinding(cachePath, "/data")],
            new Dictionary<string, string>
            {
                ["PROXY_LISTEN"] = ":20101",
                ["PROXY_BASE_URL"] = "http://kautuka-pkgs-proxy:20101",
                ["PROXY_CACHE_METADATA"] = "true",
            },
            [],
            dockerfilePath,
            null,
            null,
            RequiredForReady: true,
            ManagerInfrastructure: true,
            Policy: "ALWAYS");
        var sidecarSupervisor = new WorkspaceSidecarSupervisor(
            new WorkspaceSidecarCatalog([proxy]),
            new AcceptanceHttpClientFactory(),
            podman,
            Options.Create(new ManagerContainerStartupOptions()),
            loggerFactory.CreateLogger<WorkspaceSidecarSupervisor>(),
            RuntimeImageAllowlist.Empty,
            imageArchives: null,
            egressTopologyManager: topology);
        var runtimeSettings = new ManagerRuntimeSettings(new WorkspaceDefinition(), new SandboxRuntimeDefinition(), new WorkspaceTelemetryDefinition());
        if (!await sidecarSupervisor.EnsureReadyAsync(runtimeSettings, CancellationToken.None).ConfigureAwait(false))
        {
            PodmanCommandResult proxyState = await podman.RunAsync(["inspect", "--format", "{{json .State}}", proxyName], 10, CancellationToken.None).ConfigureAwait(false);
            PodmanCommandResult proxyLogs = await podman.RunAsync(["logs", proxyName], 10, CancellationToken.None).ConfigureAwait(false);
            await Console.Error.WriteLineAsync($"Package proxy acceptance startup diagnostics: inspect rc={proxyState.ExitCode} state={proxyState.StdOut.Trim()} inspectErr={proxyState.StdErr.Trim()}; logs rc={proxyLogs.ExitCode} logs={proxyLogs.StdOut.Trim()} logsErr={proxyLogs.StdErr.Trim()}").ConfigureAwait(false);
            _ = await podman.RunAsync(["rm", "-f", proxyName], 15, CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException("Production package proxy sidecar did not become healthy and pass its NAC ruleset verification.");
        }

        await topology.RebuildRegistryFromLiveContainersAsync(registry, CancellationToken.None).ConfigureAwait(false);
        if (!topology.IsPackageProxyReady || !topology.IsPackageProxyRegistered)
        {
            throw new InvalidOperationException("The package proxy was not retained as a healthy, verified Manager identity after production registry rebuild.");
        }

        const string builderName = "kautuka-pkgs-client";
        const string builderIp = "10.247.1.250";
        const string serviceHost = "kautuka-pkgs-proxy";
        const string npmRegistry = "http://kautuka-pkgs-proxy:20101/npm/";
        var builderSpec = new WorkloadContainerSpec(
            builderName,
            "ghcr.io/devkrity/kautuka-base@sha256:675400832f2ced5cf802db66eb067ef5022f65f0551c665d87ae7edc58dc0839",
            builderIp)
        {
            Kind = "builder",
            ExecContext = "builder",
            Egress = "proxy-only",
            Persistent = true,
            StartupGateOpen = topology.IsStartupGateOpen,
            IsBraked = false,
            ManagerRole = "builder",
            AddHosts = [$"{serviceHost}:{NetworkAddressClassifier.PackageProxyServiceAddress}"],
            Command = "/bin/sh",
            Args = ["-lc", "sleep 300"],
        };
        registry.Register(new RegistryEntry(builderIp, null, null, builderName, "builder", null, "builder", "proxy-only", "none", null, DateTimeOffset.UtcNow));
        PodmanCommandResult start = await podman.RunAsync(builderSpec.Render(), 60, CancellationToken.None).ConfigureAwait(false);
        if (start.ExitCode != 0)
        {
            registry.Deregister(builderIp, builderName);
            throw new InvalidOperationException($"Production package client failed to start: {PodmanFailureFormatter.FormatDetails(start)}");
        }

        PodmanCommandResult inspect = await podman.RunAsync(["inspect", "--format", "{{.Id}}", builderName], 10, CancellationToken.None).ConfigureAwait(false);
        string builderId = inspect.StdOut.Trim();
        if (inspect.ExitCode != 0 || !WorkloadPostStartVerifier.IsCanonicalContainerId(builderId))
        {
            throw new InvalidOperationException("Production package client did not yield a canonical Podman identity.");
        }

        registry.UpdateContainerId(builderIp, builderName, builderId);
        WorkloadVerificationResult verified = await verifier.VerifyWorkloadAsync(
            builderId, builderIp, registry: registry, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        if (!verified.Passed)
        {
            throw new InvalidOperationException("Production package client failed its NAC post-start verification.");
        }

        var npmRequest = new[]
        {
            "exec", builderName, "npm", "view", "is-number", "version", "--registry", npmRegistry,
            "--cache", "/tmp/nac-allow-cache", "--fetch-retries=0", "--fetch-timeout=10000", "--loglevel=verbose"
        };
        PodmanCommandResult packageFetch = await podman.RunAsync(npmRequest, 30, CancellationToken.None).ConfigureAwait(false);
        if (packageFetch.ExitCode != 0 || string.IsNullOrWhiteSpace(packageFetch.StdOut))
        {
            PodmanCommandResult clientHosts = await podman.RunAsync(["exec", builderName, "cat", "/etc/hosts"], 10, CancellationToken.None).ConfigureAwait(false);
            PodmanCommandResult clientResolve = await podman.RunAsync(["exec", builderName, "getent", "hosts", serviceHost], 10, CancellationToken.None).ConfigureAwait(false);
            PodmanCommandResult proxyLogs = await podman.RunAsync(["logs", proxyName], 10, CancellationToken.None).ConfigureAwait(false);
            PodmanCommandResult envoyLogs = await podman.RunAsync(["logs", "kautuka-egress-envoy"], 10, CancellationToken.None).ConfigureAwait(false);
            int builderPid = await InspectContainerPidAsync(builderName).ConfigureAwait(false);
            int proxyPid = await InspectContainerPidAsync(proxyName).ConfigureAwait(false);
            int envoyPid = await InspectContainerPidAsync("kautuka-egress-envoy").ConfigureAwait(false);
            var builderRoutes = await ExecAsync("podman", ["unshare", "nsenter", "-t", builderPid.ToString(CultureInfo.InvariantCulture), "-n", "ip", "-4", "route"]).ConfigureAwait(false);
            var builderRouteGet = await ExecAsync("podman", ["unshare", "nsenter", "-t", builderPid.ToString(CultureInfo.InvariantCulture), "-n", "ip", "-4", "route", "get", NetworkAddressClassifier.PackageProxyServiceAddress]).ConfigureAwait(false);
            var builderNft = await ExecAsync("podman", ["unshare", "nsenter", "-t", builderPid.ToString(CultureInfo.InvariantCulture), "-n", "nft", "-j", "list", "table", "inet", "kautuka_workload"]).ConfigureAwait(false);
            var proxyRoutes = await ExecAsync("podman", ["unshare", "nsenter", "-t", proxyPid.ToString(CultureInfo.InvariantCulture), "-n", "ip", "-4", "route"]).ConfigureAwait(false);
            var boundaryNft = await ExecAsync("podman", ["unshare", "nsenter", "-t", envoyPid.ToString(CultureInfo.InvariantCulture), "-n", "nft", "-j", "list", "table", "ip", "kautuka_egress"]).ConfigureAwait(false);
            string recentDecisions = string.Join("; ", controller.AuditRing.List(new NetworkAccessActor("acceptance-admin", IsAdmin: true))
                .Take(8)
                .Select(static entry => $"{entry.SourceIp} {entry.Destination}:{entry.Port} {entry.Decision}/{entry.Reason} cluster={entry.Cluster}"));
            string envoyNacLogs = string.Join("\n", (envoyLogs.StdOut + "\n" + envoyLogs.StdErr)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static line => line.Contains("[nac]", StringComparison.OrdinalIgnoreCase) || line.Contains("error", StringComparison.OrdinalIgnoreCase))
                .TakeLast(20));
            await Console.Error.WriteLineAsync($"Package proxy fetch diagnostics: hosts rc={clientHosts.ExitCode} {clientHosts.StdOut.Trim()}; resolve rc={clientResolve.ExitCode} {clientResolve.StdOut.Trim()} {clientResolve.StdErr.Trim()}; builderPid={builderPid} routes rc={builderRoutes.ExitCode} {builderRoutes.StdOut.Trim()}; routeGet rc={builderRouteGet.ExitCode} {builderRouteGet.StdOut.Trim()}; builderNft rc={builderNft.ExitCode} {builderNft.StdOut.Trim()}; proxyPid={proxyPid} routes rc={proxyRoutes.ExitCode} {proxyRoutes.StdOut.Trim()}; boundaryNft rc={boundaryNft.ExitCode} {boundaryNft.StdOut.Trim()}; proxyLogs rc={proxyLogs.ExitCode} {proxyLogs.StdOut.Trim()} {proxyLogs.StdErr.Trim()}; envoyNacLogs={envoyNacLogs}; managerDecisions={recentDecisions}").ConfigureAwait(false);
            throw new InvalidOperationException($"Package fetch did not complete through the managed proxy: {PodmanFailureFormatter.FormatDetails(packageFetch)}");
        }

        var admin = new NetworkAccessActor("acceptance-admin", IsAdmin: true);
        (_, NetworkAccessRuleView serviceDeny) = controller.PolicyStore.SetRule(
            admin,
            new RuleWriteSpec("system", null, null, null, NetworkAddressClassifier.PackageProxyServiceAddress, "ip", NetworkAddressClassifier.PackageProxyServicePort, Allowed: false),
            controller.PolicyStore.Revision);
        PodmanCommandResult deniedFetch;
        try
        {
            deniedFetch = await podman.RunAsync(
                ["exec", builderName, "npm", "view", "is-number", "version", "--registry", npmRegistry, "--cache", "/tmp/nac-deny-cache"],
                20, CancellationToken.None).ConfigureAwait(false);
            if (deniedFetch.ExitCode == 0)
            {
                throw new InvalidOperationException("An explicit Manager system deny did not block package-proxy service access.");
            }
        }
        finally
        {
            controller.PolicyStore.DeleteRule(admin, serviceDeny.Rule.Id, controller.PolicyStore.Revision);
        }

        PodmanCommandResult recoveredFetch = await podman.RunAsync(npmRequest, 45, CancellationToken.None).ConfigureAwait(false);
        if (recoveredFetch.ExitCode != 0)
        {
            throw new InvalidOperationException($"Package request did not recover after removing the explicit system deny: {PodmanFailureFormatter.FormatDetails(recoveredFetch)}");
        }

        var upstreamRequest = new[]
        {
            "exec", builderName, "npm", "view", "is-odd", "version", "--registry", npmRegistry,
            "--cache", "/tmp/nac-upstream-deny-cache"
        };
        (_, NetworkAccessRuleView upstreamDeny) = controller.PolicyStore.SetRule(
            admin,
            new RuleWriteSpec("system", null, null, null, "registry.npmjs.org", "host", 443, Allowed: false),
            controller.PolicyStore.Revision);
        PodmanCommandResult deniedUpstreamFetch;
        try
        {
            // The policy applies when Envoy authorizes a new TCP connection. Restart the
            // proxy so the request cannot reuse a connection opened before this deny.
            PodmanCommandResult restart = await podman.RunAsync(["restart", proxyName], 30, CancellationToken.None).ConfigureAwait(false);
            if (restart.ExitCode != 0 || !await sidecarSupervisor.EnsureReadyAsync(runtimeSettings, CancellationToken.None).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"Could not restart and verify the package proxy before the upstream deny check: {PodmanFailureFormatter.FormatDetails(restart)}");
            }

            deniedUpstreamFetch = await podman.RunAsync(upstreamRequest, 30, CancellationToken.None).ConfigureAwait(false);
            if (deniedUpstreamFetch.ExitCode == 0)
            {
                string decisions = string.Join("; ", controller.AuditRing.List(admin)
                    .Where(static entry => entry.UserId == "system" && entry.AgentId == "package-proxy")
                    .Take(10)
                    .Select(static entry => $"{entry.TimestampUtc:O} {entry.Destination}:{entry.Port} {entry.Decision}/{entry.Reason}"));
                PodmanCommandResult proxyLogs = await podman.RunAsync(["logs", "--tail", "30", proxyName], 10, CancellationToken.None).ConfigureAwait(false);
                throw new InvalidOperationException($"An explicit Manager system deny did not block the package proxy's upstream connection through Envoy. Recent proxy decisions: {decisions}; proxy logs: {proxyLogs.StdOut} {proxyLogs.StdErr}");
            }
        }
        finally
        {
            controller.PolicyStore.DeleteRule(admin, upstreamDeny.Rule.Id, controller.PolicyStore.Revision);
        }

        PodmanCommandResult recoveredUpstreamFetch = await podman.RunAsync(upstreamRequest, 45, CancellationToken.None).ConfigureAwait(false);
        if (recoveredUpstreamFetch.ExitCode != 0)
        {
            throw new InvalidOperationException($"Proxy upstream did not recover after removing the explicit system deny: {PodmanFailureFormatter.FormatDetails(recoveredUpstreamFetch)}");
        }

        return [
        AcceptanceHarness.Record(
            "PROD_PACKAGE_PROXY_TWO_HOP_FETCH",
            $"allowed={packageFetch.ExitCode == 0 && !string.IsNullOrWhiteSpace(packageFetch.StdOut)}",
            "allowed=True",
            $"allowed={deniedFetch.ExitCode == 0}",
            "allowed=False"),
        AcceptanceHarness.Record(
            "PROD_PACKAGE_PROXY_EXPLICIT_DENY",
            $"blocked={deniedFetch.ExitCode != 0}",
            "blocked=True",
            $"blocked={recoveredFetch.ExitCode != 0}",
            "blocked=False"),
        AcceptanceHarness.Record(
            "PROD_PACKAGE_PROXY_UPSTREAM_DENY",
            $"blocked={deniedUpstreamFetch.ExitCode != 0}",
            "blocked=True",
            $"blocked={recoveredUpstreamFetch.ExitCode != 0}",
            "blocked=False")];
    }
    private const string UserBrakeDisabledHookObservation =
        "existingStopped=False:otherUserRunning=True:newLaunchRejected=True:releaseLaunchRunning=True:responseContainsUser=True";
    public static async Task<int> Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        Console.WriteLine("Kautuka NAC v7 Acceptance Suite");
        Console.WriteLine("Enforcing Ground Rule G9: observed values and computed negative-control flips only.");

        var badNc = AcceptanceHarness.Record("SELFTEST_BAD_NC", "42", "42", "42", "0");
        if (badNc.Outcome != "FAIL (cannot fail: NC did not flip)")
        {
            await Console.Error.WriteLineAsync("CRITICAL: Harness self-test failed to reject invalid NC!").ConfigureAwait(false);
            return 1;
        }

        AcceptanceHarness.GetAll();
        int prodIdx = Array.FindIndex(args, a => string.Equals(a, "--prod-run", StringComparison.OrdinalIgnoreCase));
        if (prodIdx >= 0)
        {
            if (!OperatingSystem.IsLinux())
            {
                await Console.Error.WriteLineAsync("--prod-run mode requires Linux with rootless Podman.").ConfigureAwait(false);
                return 1;
            }

            string? outPath = prodIdx + 1 < args.Length && !args[prodIdx + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[prodIdx + 1]
                : null;
            int brakeNcReportIdx = Array.FindIndex(args, a => string.Equals(a, "--brake-nc-report", StringComparison.OrdinalIgnoreCase));
            string? brakeNcReportPath = brakeNcReportIdx >= 0 && brakeNcReportIdx + 1 < args.Length
                ? args[brakeNcReportIdx + 1]
                : null;
            if (brakeNcReportIdx >= 0 && string.IsNullOrWhiteSpace(brakeNcReportPath))
            {
                await Console.Error.WriteLineAsync("--brake-nc-report requires the report emitted by a separate run with WorkspaceGrpcService.SetBrake's EngageBrakeAsync call disabled.").ConfigureAwait(false);
                return 1;
            }
            return await RunProductionTopologyProofAsync(outPath, brakeNcReportPath).ConfigureAwait(false);
        }

        if (!args.Contains("--live", StringComparer.OrdinalIgnoreCase))
        {
            if (args.Contains("--self-test-conntrack-delete-result", StringComparer.OrdinalIgnoreCase))
            {
                VerifyConntrackDeleteResultParsing();
                Console.WriteLine("Conntrack delete result self-test passed (recognized zero-match and successful deletion accepted; command and permission errors rejected).");
                return 0;
            }

            if (args.Contains("--self-test-routed-lan-fixture-cleanup", StringComparer.OrdinalIgnoreCase))
            {
                await VerifyRoutedLanFixtureCleanupAsync().ConfigureAwait(false);
                Console.WriteLine("Routed LAN fixture cleanup self-test passed (pre-existing interface preserved; owned resources cleaned after later setup failure; namespace cleanup continued after veth cleanup failure and retained the original error).");
                return 0;
            }

            if (args.Contains("--self-test-isolation-cleanup", StringComparer.OrdinalIgnoreCase))
            {
                await VerifyProductionIsolationCleanupAsync().ConfigureAwait(false);
                Console.WriteLine("Production cleanup self-test passed (mismatched Podman isolation suppressed all Podman mutations; verified cleanup control allowed).");
                return 0;
            }

            Console.WriteLine("Harness self-test verified G9 enforcement. Pass --live on Linux rootless Podman to execute live container & Envoy v1.40.2 proofs.");
            return 0;
        }

        if (!OperatingSystem.IsLinux())
        {
            await Console.Error.WriteLineAsync("--live mode requires Linux with rootless Podman.").ConfigureAwait(false);
            return 1;
        }

        int liveIdx = Array.FindIndex(args, static arg => string.Equals(arg, "--live", StringComparison.OrdinalIgnoreCase));
        string? liveOutPath = liveIdx >= 0 && liveIdx + 1 < args.Length && !args[liveIdx + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[liveIdx + 1]
            : null;
        int liveBrakeNcReportIdx = Array.FindIndex(args, static arg => string.Equals(arg, "--brake-nc-report", StringComparison.OrdinalIgnoreCase));
        string? liveBrakeNcReportPath = liveBrakeNcReportIdx >= 0 && liveBrakeNcReportIdx + 1 < args.Length
            ? args[liveBrakeNcReportIdx + 1]
            : null;
        if (liveBrakeNcReportIdx >= 0 && string.IsNullOrWhiteSpace(liveBrakeNcReportPath))
        {
            await Console.Error.WriteLineAsync("--brake-nc-report requires the report emitted by a separate run with WorkspaceGrpcService.SetBrake's EngageBrakeAsync call disabled.").ConfigureAwait(false);
            return 1;
        }
        string? fedoraOutPath = liveOutPath is null
            ? null
            : Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(liveOutPath))!,
                $"{Path.GetFileNameWithoutExtension(liveOutPath)}.fedora{Path.GetExtension(liveOutPath)}");
        int fedoraResult = await RunLiveFedoraAcceptanceAsync(
            fedoraOutPath is null ? ["--live"] : ["--live", fedoraOutPath]).ConfigureAwait(false);
        int productionResult = await RunProductionTopologyProofAsync(liveOutPath, liveBrakeNcReportPath).ConfigureAwait(false);
        return fedoraResult == 0 && productionResult == 0 ? 0 : 1;
    }

    private static bool TryParseConntrackDeleteResult(int exitCode, string stdout, string stderr, out long removedFlowCount)
    {
        removedFlowCount = 0;
        string[] lines = (stdout + "\n" + stderr)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
        {
            return false;
        }

        Match match = Regex.Match(
            lines[^1],
            @"^(?:conntrack v[0-9.]+ \(conntrack-tools\):\s*)?(?<count>[0-9]+)\s+flow\s+(?:entries\s+have|entry\s+has)\s+been deleted\.?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !long.TryParse(match.Groups["count"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out removedFlowCount))
        {
            return false;
        }

        if (lines.Length > 1
            && (exitCode != 0 || removedFlowCount != lines.Length - 1
                || lines[..^1].Any(static line => !Regex.IsMatch(line,
                    @"^udp\s+\d+\s+\d+\s+src=\S+\s+dst=\S+\s+sport=\d+\s+dport=\d+\s+src=\S+\s+dst=\S+\s+sport=\d+\s+dport=\d+(?:\s+.*)?$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))))
        {
            return false;
        }

        // conntrack returns nonzero for its documented no-match result on some versions.
        // Accept only a recognized summary and, when present, matching UDP flow records.
        return exitCode == 0 || (exitCode != 0 && removedFlowCount == 0);
    }

    private static void VerifyConntrackDeleteResultParsing()
    {
        if (!TryParseConntrackDeleteResult(1, "0 flow entries have been deleted.\n", string.Empty, out long zeroCount) || zeroCount != 0
            || !TryParseConntrackDeleteResult(0, "2 flow entries have been deleted.\n", string.Empty, out long positiveCount) || positiveCount != 2
            || !TryParseConntrackDeleteResult(0, "udp 17 7 src=10.248.0.2 dst=10.248.0.1 sport=35857 dport=53 src=10.248.0.1 dst=10.248.0.2 sport=53 dport=35857 mark=0 use=1\n", "conntrack v1.4.9 (conntrack-tools): 1 flow entries have been deleted.\n", out long listedCount) || listedCount != 1
            || TryParseConntrackDeleteResult(0, "unexpected output\n", "conntrack v1.4.9 (conntrack-tools): 1 flow entries have been deleted.\n", out _)
            || TryParseConntrackDeleteResult(1, "0 flow entries have been deleted.\n", "conntrack: Operation failed: Permission denied\n", out _)
            || TryParseConntrackDeleteResult(127, string.Empty, "conntrack: command not found\n", out _)
            || TryParseConntrackDeleteResult(1, "1 flow entry has been deleted.\n", string.Empty, out _))
        {
            throw new InvalidOperationException("Conntrack delete result parser accepted an invalid result or rejected a recognized result.");
        }
    }

    private static async Task<int> RunLiveFedoraAcceptanceAsync(string[] args)
    {
        string repoRoot = FindRepoRoot();
        string baseTmp = Directory.Exists("/var/tmp") ? "/var/tmp" : Path.GetTempPath();
        string tempRoot = Path.Combine(baseTmp, $"kautuka-nac-live-{Guid.NewGuid():N}"[..24]);
        string homeDir = Path.Combine(tempRoot, "home");
        string xdgState = Path.Combine(tempRoot, "xdg-state");
        string stateDir = Path.Combine(tempRoot, "manager-nac");
        string socketDir = Path.Combine(tempRoot, "sock");
        string certsDir = Path.Combine(tempRoot, "certs");
        Directory.CreateDirectory(homeDir);
        Directory.CreateDirectory(xdgState);
        Directory.CreateDirectory(stateDir);
        Directory.CreateDirectory(socketDir);
        Directory.CreateDirectory(certsDir);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(socketDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        string runSuffix = Guid.NewGuid().ToString("N")[..6];
        string wlNet = $"kautuka-wl-{runSuffix}";
        string pubNet = $"kautuka-pub-{runSuffix}";
        string lanNet = $"kautuka-lan-{runSuffix}";
        string podName = $"kautuka-egress-{runSuffix}";
        string upstreamPubName = $"nac-up-pub-{runSuffix}";
        string upstreamLanName = $"nac-up-lan-{runSuffix}";
        string dnsUpName = $"nac-up-dns-{runSuffix}";
        string workloadName = $"nac-wl-{runSuffix}";
        string workload2Name = $"nac-wl2-{runSuffix}";
        int liveIndex = Array.FindIndex(args, static arg => string.Equals(arg, "--live", StringComparison.OrdinalIgnoreCase));
        string reportPath = liveIndex >= 0 && liveIndex + 1 < args.Length && !args[liveIndex + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[liveIndex + 1]
            : Path.Combine(tempRoot, "nac-live-report.json");

        Environment.SetEnvironmentVariable("XDG_STATE_HOME", xdgState);
        string nacStateDir = Path.Combine(xdgState, "kautuka", "nac");
        Directory.CreateDirectory(nacStateDir);

        string hookSrc = Path.Combine(repoRoot, "scripts", "nac", "kautuka_hook.py");
        string hookTemplateSrc = Path.Combine(repoRoot, "scripts", "nac", "kautuka_workload.nft.json.tmpl");
        string hooksDir = Path.Combine(tempRoot, "hooks.d");
        Directory.CreateDirectory(hooksDir);
        string hookPath = Path.Combine(hooksDir, "kautuka_hook.py");
        File.Copy(hookSrc, hookPath, overwrite: true);
        File.Copy(hookTemplateSrc, Path.Combine(hooksDir, "kautuka_workload.nft.json.tmpl"), overwrite: true);
        File.Copy(Path.Combine(repoRoot, "scripts", "nac", "kautuka_package_proxy.nft.json.tmpl"),
            Path.Combine(hooksDir, "kautuka_package_proxy.nft.json.tmpl"), overwrite: true);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(hookPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // Configure hook JSON referencing our staged hook script with XDG_STATE_HOME preserved
        string hookWrapper = Path.Combine(hooksDir, "kautuka_hook_wrapper.sh");
        await File.WriteAllTextAsync(hookWrapper, $"#!/bin/bash\nexport XDG_STATE_HOME='{xdgState}'\nexec python3 '{hookPath}' \"$@\"\n").ConfigureAwait(false);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(hookWrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        string hookJson = $$"""
        {
          "version": "1.0.0",
          "hook": { "path": "{{hookWrapper}}" },
          "when": { "always": true },
          "stages": ["createRuntime"]
        }
        """;
        await File.WriteAllTextAsync(Path.Combine(hooksDir, "kautuka_hook.json"), hookJson).ConfigureAwait(false);

        var podman = new PodmanCommandRunner();
        var liveResults = new List<AcceptanceObservation>();

        // Start in-process Controller + EnvoyAuthorizationHost on Linux UDS
        var registry = new WorkloadRegistry();
        using var policyStore = new NetworkAccessPolicyStore(Path.Combine(stateDir, "policy-state.json"));
        using var auditRing = new NetworkAccessAuditRing(Path.Combine(stateDir, "audit.jsonl"));
        var controller = new NacController(policyStore, registry, auditRing);
        string sockPath = Path.Combine(socketDir, "envoy.sock");
        string lockPath = Path.Combine(socketDir, "networkaccess.lock");

        var authHost = EnvoyAuthorizationHost.Create(
            controller,
            new EnvoyAuthorizationHostOptions
            {
                TransportMode = EnvoyListenerTransportMode.UnixDomainSocket,
                SocketPath = sockPath,
                LockFilePath = lockPath,
            });
        await using var authHostScope = authHost.ConfigureAwait(false);
        await authHost.StartAsync(CancellationToken.None).ConfigureAwait(false);

        var allocator = new WorkloadAddressAllocator(Path.Combine(stateDir, "ip-cursor.json"));
        var verifier = new WorkloadPostStartVerifier(podman);
        using ILoggerFactory loggerFactory = LoggerFactory.Create(static b => b.AddSimpleConsole());
        var topology = new EgressTopologyManager(
            podman,
            allocator,
            verifier,
            registry,
            stateDir,
            loggerFactory.CreateLogger<EgressTopologyManager>(),
            envoySocketHostDir: socketDir,
            hookStateDirectory: nacStateDir);
        await using var topologyScope = topology.ConfigureAwait(false);

        try
        {
            // Generate self-signed cert for upstream HTTPS servers
            await RunBashAsync($"openssl req -x509 -newkey rsa:2048 -nodes -keyout '{certsDir}/srv.key' -out '{certsDir}/srv.crt' -days 1 -subj '/CN=allowed.example.test' -addext 'subjectAltName=DNS:allowed.example.test,DNS:rebind.example.test,IP:203.0.113.10,IP:192.168.100.50'").ConfigureAwait(false);

            // Build local boundary init image if missing
            await EnsureBoundaryImageAsync(repoRoot).ConfigureAwait(false);

            // Create isolated test networks matching production spec (203.0.113.0/24 public test, 192.168.100.0/24 LAN test, 10.247.0.0/16 workload)
            await ExecPodmanAsync(["network", "create", "--internal", "--disable-dns", "--subnet", "10.247.0.0/16", "--gateway", "10.247.0.1", "--route", "0.0.0.0/0,10.247.0.2", wlNet]).ConfigureAwait(false);
            await ExecPodmanAsync(["network", "create", "--subnet", "203.0.113.0/24", "--gateway", "203.0.113.1", pubNet]).ConfigureAwait(false);
            await ExecPodmanAsync(["network", "create", "--subnet", "192.168.100.0/24", "--gateway", "192.168.100.1", lanNet]).ConfigureAwait(false);

            string allowlistPath = Path.Combine(nacStateDir, "infra-allowlist.json");
            var infraEntries = new List<InfraAllowlistEntry>();
            void WriteAllowlist()
            {
                string tmp = allowlistPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(new InfraAllowlistDocument(1, infraEntries), AcceptanceJsonContext.Default.InfraAllowlistDocument));
                if (OperatingSystem.IsLinux())
                {
                    File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                File.Move(tmp, allowlistPath, overwrite: true);
                if (OperatingSystem.IsLinux())
                {
                    File.SetUnixFileMode(allowlistPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
            WriteAllowlist();

            // Write mock TLS upstream & DNS server python scripts (non-allowlisted/outside 10.247.x so hook skips outside-boundary!)
            string pyUp = Path.Combine(tempRoot, "up_srv.py");
            await File.WriteAllTextAsync(pyUp, """
                import http.server, ssl, sys, os
                count_file = sys.argv[1]
                tag = sys.argv[2]
                class H(http.server.BaseHTTPRequestHandler):
                    def do_GET(self):
                        with open(count_file, 'a') as f:
                            f.write('1\n')
                        self.send_response(200)
                        body = tag.encode()
                        self.send_header('Content-Length', str(len(body)))
                        self.end_headers()
                        self.wfile.write(body)
                    def log_message(self, *a): pass
                srv = http.server.HTTPServer(('0.0.0.0', 443), H)
                ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
                ctx.load_cert_chain('/certs/srv.crt', '/certs/srv.key')
                srv.socket = ctx.wrap_socket(srv.socket, server_side=True)
                srv.serve_forever()
                """).ConfigureAwait(false);

            string pyDns = Path.Combine(tempRoot, "dns_up.py");
            await File.WriteAllTextAsync(pyDns, """
                import socket, struct
                # Tiny authoritative DNS responder for CoreDNS upstream on 203.0.113.53:5353
                RECORDS = {
                    'allowed.example.test': '203.0.113.10',
                    'rebind.example.test': '192.168.100.50',
                }
                sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
                sock.bind(('0.0.0.0', 5353))
                while True:
                    data, addr = sock.recvfrom(512)
                    if len(data) < 12: continue
                    tid = data[:2]
                    flags = b'\x81\x80'
                    qdcount = data[4:6]
                    idx = 12
                    labels = []
                    while idx < len(data) and data[idx] != 0:
                        l = data[idx]; labels.append(data[idx+1:idx+1+l].decode('ascii', 'ignore')); idx += 1 + l
                    name = '.'.join(labels).lower()
                    qtype = struct.unpack('!H', data[idx+1:idx+3])[0]
                    qsec = data[12:idx+5]
                    if qtype == 1 and name in RECORDS:
                        ip_bytes = socket.inet_aton(RECORDS[name])
                        ans = b'\xc0\x0c\x00\x01\x00\x01\x00\x00\x00\x05\x00\x04' + ip_bytes
                        sock.sendto(tid + flags + qdcount + b'\x00\x01\x00\x00\x00\x00' + qsec + ans, addr)
                    else:
                        sock.sendto(tid + b'\x81\x83' + qdcount + b'\x00\x00\x00\x00\x00\x00' + qsec, addr)
                """).ConfigureAwait(false);

            string pubCount = Path.Combine(tempRoot, "pub_hits.txt");
            string lanCount = Path.Combine(tempRoot, "lan_hits.txt");
            await File.WriteAllTextAsync(pubCount, string.Empty).ConfigureAwait(false);
            await File.WriteAllTextAsync(lanCount, string.Empty).ConfigureAwait(false);

            string toolsImg = "localhost/spike-tools:latest";
            await ExecPodmanAsync(["--hooks-dir", hooksDir, "run", "-d", "--name", upstreamPubName, "--network", $"{pubNet}:ip=203.0.113.10", "-v", $"{certsDir}:/certs:ro", "-v", $"{pyUp}:/up.py:ro", "-v", $"{tempRoot}:/cnt:z", toolsImg, "python3", "/up.py", "/cnt/pub_hits.txt", "kautuka-envoy-allow-ok"]).ConfigureAwait(false);
            await ExecPodmanAsync(["--hooks-dir", hooksDir, "run", "-d", "--name", upstreamLanName, "--cap-add=NET_ADMIN", "--network", $"{lanNet}:ip=192.168.100.50", "-v", $"{certsDir}:/certs:ro", "-v", $"{pyUp}:/up.py:ro", "-v", $"{tempRoot}:/cnt:z", toolsImg, "sh", "-c", "ip route replace 192.0.0.8/32 via 192.168.100.2 dev eth0; exec python3 /up.py /cnt/lan_hits.txt kautuka-lan-rebind-ok"]).ConfigureAwait(false);
            await ExecPodmanAsync(["--hooks-dir", hooksDir, "run", "-d", "--name", dnsUpName, "--network", $"{pubNet}:ip=203.0.113.53", "-v", $"{pyDns}:/dns.py:ro", toolsImg, "python3", "/dns.py"]).ConfigureAwait(false);

            // Render production templates (envoy.yaml, Corefile pointing forward to 203.0.113.53:5353, boundary.nft)
            string renderedBoundary = BoundaryRulesetRenderer.RenderBoundaryNft(includeLanGrants: true, decisionEndpointIp: null);
            string renderedEnvoy = BoundaryRulesetRenderer.RenderEnvoyConfig(adminSocketPath: "/run/kautuka-admin/admin.sock", isWindowsMtls: false);
            string renderedCorefile = """
            .:53 {
                bind 10.247.0.2
                template IN A {
                    answer "{{ .Name }} 5 IN A 198.18.250.250"
                }
                template IN AAAA {
                    rcode NOERROR
                }
            }
            .:10053 {
                bind 127.0.0.1
                forward . 203.0.113.53:5353
            }
            """;

            string boundaryPath = Path.Combine(tempRoot, "boundary.nft");
            string envoyConfigDir = Path.Combine(tempRoot, "envoy");
            string dnsConfigDir = Path.Combine(tempRoot, "dns");
            Directory.CreateDirectory(envoyConfigDir);
            Directory.CreateDirectory(dnsConfigDir);
            string envoyPath = Path.Combine(envoyConfigDir, "envoy.yaml");
            string corefilePath = Path.Combine(dnsConfigDir, "Corefile");
            await File.WriteAllTextAsync(boundaryPath, renderedBoundary).ConfigureAwait(false);
            await File.WriteAllTextAsync(envoyPath, renderedEnvoy).ConfigureAwait(false);
            await File.WriteAllTextAsync(corefilePath, renderedCorefile).ConfigureAwait(false);

            // Create egress pod (container UID 0 in default rootless userns maps to Manager host UID for 0700/0600 UDS access) and create all pod containers BEFORE pod start
            await ExecPodmanAsync(["--hooks-dir", hooksDir, "pod", "create", "--name", podName, "--network", $"{wlNet}:ip=10.247.0.2", "--network", $"{pubNet}:ip=203.0.113.2", "--network", $"{lanNet}:ip=192.168.100.2", "--sysctl", "net.ipv4.ip_forward=0", "--sysctl", "net.ipv6.conf.all.disable_ipv6=1"]).ConfigureAwait(false);
            string[] podExpectedIps = ["10.247.0.2", "203.0.113.2", "192.168.100.2"];
            string infraCid = await InspectPodInfraIdAsync(podName).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(infraCid))
            {
                infraEntries.Add(new(infraCid, "egress-pod", podExpectedIps, "pinned"));
                WriteAllowlist();
            }

            async Task CreateAndAllowlistAsync(string name, string role, string img, IReadOnlyList<string> createArgs)
            {
                await ExecPodmanAsync(["--hooks-dir", hooksDir, .. createArgs]).ConfigureAwait(false);
                string cid = await InspectContainerIdAsync(name).ConfigureAwait(false);
                infraEntries.Add(new(cid, role, podExpectedIps, img));
                WriteAllowlist();
            }

            await CreateAndAllowlistAsync($"{podName}-bnd", "egress-boundary", EgressTopologyManager.BoundaryInitImage,
                ["create", "--pod", podName, "--name", $"{podName}-bnd", "--init-ctr=always", "--cap-add", "NET_ADMIN", "--user", "0:0", "--entrypoint", "/bin/sh", "-v", $"{boundaryPath}:/etc/nftables.conf:ro", EgressTopologyManager.BoundaryInitImage, "-c", "ip route replace local 192.0.0.8 dev lo 2>/dev/null || true; exec /usr/sbin/nft -f /etc/nftables.conf"]).ConfigureAwait(false);
            await CreateAndAllowlistAsync($"{podName}-dns", "egress-dns", EgressTopologyManager.CoreDnsPinnedImage,
                ["create", "--pod", podName, "--name", $"{podName}-dns", "--cap-add", "NET_BIND_SERVICE", "--user", "0:0", "-v", $"{dnsConfigDir}:/etc/coredns:ro,z", EgressTopologyManager.CoreDnsPinnedImage, "-conf", "/etc/coredns/Corefile"]).ConfigureAwait(false);
            string adminSockDir = Path.Combine(tempRoot, "admin-sock");
            Directory.CreateDirectory(adminSockDir);
            await CreateAndAllowlistAsync($"{podName}-env", "egress-envoy", EgressTopologyManager.EnvoyPinnedImage,
                ["create", "--pod", podName, "--name", $"{podName}-env", "--user", "0:0", "-v", $"{envoyConfigDir}:/etc/kautuka-envoy:ro,z", "-v", $"{socketDir}:/run/kautuka-nac:z", "-v", $"{adminSockDir}:/run/kautuka-admin:z", EgressTopologyManager.EnvoyPinnedImage, "-c", "/etc/kautuka-envoy/envoy.yaml"]).ConfigureAwait(false);

            await ExecPodmanAsync(["--hooks-dir", hooksDir, "pod", "start", podName]).ConfigureAwait(false);

            await Task.Delay(1500).ConfigureAwait(false);

            // Start real hardened workload container at 10.247.1.10 with hook enforced
            string wlIp = "10.247.1.10";
            await ExecPodmanAsync(["--hooks-dir", hooksDir, "run", "-d", "--name", workloadName, "--cap-drop=ALL", "--security-opt=no-new-privileges", "--read-only", "--user=65532:65532", "--pids-limit=256", "--memory=536870912", "--tmpfs", "/tmp:rw,noexec,nosuid,size=64m", "--network", $"{wlNet}:ip={wlIp}", "--dns=10.247.0.2", "--sysctl", "net.ipv6.conf.all.disable_ipv6=1", "--annotation", "io.kautuka.role=workload", toolsImg, "sleep", "300"]).ConfigureAwait(false);
            string wlCid = await InspectContainerIdAsync(workloadName).ConfigureAwait(false);

            registry.Register(new RegistryEntry(wlIp, "u1", "a1", wlCid, "agent-sandbox", null, "agent-sandbox", "normal", "user-agent", null, DateTimeOffset.UtcNow));

            // -----------------------------------------------------------------
            // LIVE PROOF 1 (v7-1): Real Envoy Allowed-Reply through top-level dynamic_metadata["cluster"]
            // -----------------------------------------------------------------
            var admin = new NetworkAccessActor("admin-1", IsAdmin: true);
            long rev = policyStore.SetRule(admin, new RuleWriteSpec("agent", "u1", "a1", null, "allowed.example.test", "host", 443, Allowed: true, AllowLan: false), expectedRevision: policyStore.Revision).Revision;
            string allowBody = await ExecInContainerAsync(workloadName, ["curl", "-sS", "--max-time", "5", "-k", "https://allowed.example.test/"]).ConfigureAwait(false);

            // NC: Deny rule -> Envoy denies connection (non-zero curl / empty body)
            rev = policyStore.SetRule(admin, new RuleWriteSpec("agent", "u1", "a1", null, "allowed.example.test", "host", 443, Allowed: false, AllowLan: false), expectedRevision: rev).Revision;
            string ncAllowBody = await ExecInContainerExpectFailAsync(workloadName, ["curl", "-sS", "--max-time", "4", "-k", "https://allowed.example.test/"]).ConfigureAwait(false);
            liveResults.Add(AcceptanceHarness.Record("E_ENVOY_ALLOW_V7_1", allowBody.Trim(), "kautuka-envoy-allow-ok", ncAllowBody, "BLOCKED"));
            Console.WriteLine($"[E_ENVOY_ALLOW_V7_1] {liveResults[^1].Outcome} (allow='{allowBody.Trim()}', nc='{ncAllowBody}')");

            // -----------------------------------------------------------------
            // LIVE PROOF 2 (E-rebind + v7-2 / Phase 9 LAN check): Public grant resolving to 192.168.100.50 MUST be blocked
            // -----------------------------------------------------------------
            rev = policyStore.SetRule(admin, new RuleWriteSpec("agent", "u1", "a1", null, "rebind.example.test", "host", 443, Allowed: true, AllowLan: false), expectedRevision: rev).Revision;
            int hitsBeforeRebind = CountLines(lanCount);
            string rebindBlocked = await ExecInContainerExpectFailAsync(workloadName, ["curl", "-sS", "--max-time", "5", "-k", "https://rebind.example.test/"]).ConfigureAwait(false);
            int hitsAfterRebind = CountLines(lanCount);
            string observedRebind = $"{rebindBlocked}:hits={hitsAfterRebind - hitsBeforeRebind}";

            // NC: Change rule to allowLan = true -> routes via dfp_lan to 192.168.100.50 -> succeeds!
            rev = policyStore.SetRule(admin, new RuleWriteSpec("agent", "u1", "a1", null, "rebind.example.test", "host", 443, Allowed: true, AllowLan: true), expectedRevision: rev).Revision;
            string ncRebindBody = await ExecInContainerExpectFailAsync(workloadName, ["curl", "-sS", "--max-time", "5", "-k", "https://rebind.example.test/"]).ConfigureAwait(false);
            int hitsAfterLan = CountLines(lanCount);
            string ncObservedRebind = hitsAfterLan > hitsAfterRebind ? "ALLOWED_LAN" : ncRebindBody;
            liveResults.Add(AcceptanceHarness.Record("E_REBIND", observedRebind, "BLOCKED:hits=0", ncObservedRebind, "ALLOWED_LAN"));
            Console.WriteLine($"[E_REBIND] {liveResults[^1].Outcome} (publicRebind='{observedRebind}', lanNc='{ncObservedRebind}' hits={hitsAfterLan - hitsAfterRebind})");

            // -----------------------------------------------------------------
            // LIVE PROOF 3 (E_BRAKE_LOCK, v7-5): Admin locks user brake -> user On/Off cannot release -> container stopped
            // -----------------------------------------------------------------
            var nonAdminUser = new NetworkAccessActor("u1", IsAdmin: false);
            policyStore.SetBrake(admin, "user", "u1", on: true);
            bool userBypassFailed = false;
            try
            {
                policyStore.SetBrake(nonAdminUser, "user", "u1", on: true);
                policyStore.SetBrake(nonAdminUser, "user", "u1", on: false);
            }
            catch (UnauthorizedAccessException)
            {
                userBypassFailed = true;
            }
            bool brakeStillActive = policyStore.GetBrake(nonAdminUser).IsUserBraked("u1");
            await topology.EngageBrakeAsync("u1", registry).ConfigureAwait(false);
            bool stoppedWhileLocked = !(await IsContainerRunningAsync(workloadName).ConfigureAwait(false));
            string brakeObserved = $"{userBypassFailed}:{brakeStillActive}:{stoppedWhileLocked}";

            // NC: Admin turns brake off -> released cleanly!
            policyStore.SetBrake(admin, "user", "u1", on: false);
            bool brakeReleasedByAdmin = !policyStore.GetBrake(nonAdminUser).IsUserBraked("u1");
            string ncBrakeObserved = brakeReleasedByAdmin ? "UNLOCKED_BY_ADMIN" : "STILL_LOCKED";
            liveResults.Add(AcceptanceHarness.Record("E_BRAKE_LOCK_V7_5", brakeObserved, "True:True:True", ncBrakeObserved, "UNLOCKED_BY_ADMIN"));
            Console.WriteLine($"[E_BRAKE_LOCK_V7_5] {liveResults[^1].Outcome} (locked='{brakeObserved}', adminNc='{ncBrakeObserved}')");

            // -----------------------------------------------------------------
            // LIVE PROOF 4 (C6_LIVE, MF6): Post-start verifier passes on clean container and kills tampered container
            // -----------------------------------------------------------------
            string wl2Ip = "10.247.1.11";
            await ExecPodmanAsync(["--hooks-dir", hooksDir, "run", "-d", "--name", workload2Name, "--cap-drop=ALL", "--security-opt=no-new-privileges", "--read-only", "--user=65532:65532", "--pids-limit=256", "--memory=536870912", "--tmpfs", "/tmp:rw,noexec,nosuid,size=64m", "--network", $"{wlNet}:ip={wl2Ip}", "--dns=10.247.0.2", "--sysctl", "net.ipv6.conf.all.disable_ipv6=1", "--annotation", "io.kautuka.role=workload", toolsImg, "sleep", "300"]).ConfigureAwait(false);
            string wl2Cid = await InspectContainerIdAsync(workload2Name).ConfigureAwait(false);
            registry.Register(new RegistryEntry(wl2Ip, "u1", "a1", wl2Cid, "agent-sandbox", null, "agent-sandbox", "normal", "user-agent", null, DateTimeOffset.UtcNow));

            // Verify C6 passes on untampered container (note: verifier expects network name "kautuka-workload"; check netns canonical hash directly via nsenter using BoundaryRulesetRenderer)
            int pid2 = await InspectContainerPidAsync(workload2Name).ConfigureAwait(false);
            string liveNftJson = await RunNsenterNftListAsync(pid2).ConfigureAwait(false);
            string hashClean = BoundaryRulesetRenderer.ComputeCanonicalNftJsonHash(liveNftJson, WorkloadRulesetRenderer.RequiredWorkloadComments, wl2Ip);
            string hashExpected = WorkloadRulesetRenderer.ComputeExpectedCanonicalRulesetHash(wl2Ip);
            string cleanHashMatch = string.Equals(hashClean, hashExpected, StringComparison.Ordinal) ? "MATCH" : $"MISMATCH:{hashClean}";

            // Inject extra accept rule inside live container's netns (C6.NC)
            await RunBashAsync($"podman unshare nsenter -t {pid2} -n nft add rule inet kautuka_workload output accept").ConfigureAwait(false);
            string tamperedNftJson = await RunNsenterNftListAsync(pid2).ConfigureAwait(false);
            string hashTampered = BoundaryRulesetRenderer.ComputeCanonicalNftJsonHash(tamperedNftJson, WorkloadRulesetRenderer.RequiredWorkloadComments, wl2Ip);
            string ncHashMatch = string.Equals(hashTampered, hashExpected, StringComparison.Ordinal) ? "MATCH" : "TAMPER_DETECTED";
            liveResults.Add(AcceptanceHarness.Record("C6_LIVE_MF6_HASH", cleanHashMatch, "MATCH", ncHashMatch, "TAMPER_DETECTED"));
            Console.WriteLine($"[C6_LIVE_MF6_HASH] {liveResults[^1].Outcome} (clean='{cleanHashMatch}', nc='{ncHashMatch}')");

            // -----------------------------------------------------------------
            // LIVE PROOF 5 (E_DNS_SINKHOLE): Workload DNS returns synthetic 198.18.250.250; NC: valid external query via Envoy resolves real IP
            // -----------------------------------------------------------------
            string dnsReply = await ExecInContainerAsync(workload2Name, ["python3", "-c", "import socket; print(socket.gethostbyname('allowed.example.test'))"]).ConfigureAwait(false);
            string ncDnsReply = await ExecInContainerAsync(upstreamPubName, ["python3", "-c", "import socket,struct; s=socket.socket(socket.AF_INET,socket.SOCK_DGRAM); s.settimeout(3); q=b'\\xab\\xcd\\x01\\x00\\x00\\x01\\x00\\x00\\x00\\x00\\x00\\x00\\x07allowed\\x07example\\x04test\\x00\\x00\\x01\\x00\\x01'; s.sendto(q,('203.0.113.53',5353)); r,_=s.recvfrom(512); print('.'.join(str(b) for b in r[-4:]))"]).ConfigureAwait(false);
            liveResults.Add(AcceptanceHarness.Record("E_DNS_SINKHOLE", dnsReply.Trim(), "198.18.250.250", ncDnsReply.Trim(), "203.0.113.10"));
            Console.WriteLine($"[E_DNS_SINKHOLE] {liveResults[^1].Outcome} (workloadDns='{dnsReply.Trim()}', upstreamDnsNc='{ncDnsReply.Trim()}')");

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(liveResults, AcceptanceJsonContext.Default.ListAcceptanceObservation)).ConfigureAwait(false);
            Console.WriteLine($"Live acceptance report written to {reportPath}");

            int failedCount = liveResults.Count(r => r.Outcome != "PASS");
            Console.WriteLine($"SUMMARY: {liveResults.Count - failedCount}/{liveResults.Count} PASS, {failedCount} FAIL");
            return failedCount == 0 ? 0 : 1;
        }
        finally
        {
            await BestEffortCleanupAsync(podName, [workloadName, workload2Name, upstreamPubName, upstreamLanName, dnsUpName], [wlNet, pubNet, lanNet]).ConfigureAwait(false);
        }
    }

    private static async Task<(string NamespaceName, string HostDevice, string Address)> CreateRoutedLanFixtureAsync()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        string namespaceName = $"nac-lan-{suffix}";
        string hostDevice = $"nch{suffix}";
        string lanDevice = $"ncl{suffix}";
        const string hostAddress = "192.168.248.1/24";
        const string fixtureAddress = "192.168.248.2";

        async Task RunIpAsync(params string[] arguments)
        {
            var (exitCode, output, error) = await ExecAsync("sudo", ["ip", .. arguments]).ConfigureAwait(false);
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"LAN fixture network command failed (sudo ip {string.Join(' ', arguments)}; rc={exitCode}; output={output}; error={error}).");
            }
        }

        bool namespaceCreated = false;
        bool vethCreated = false;
        try
        {
            await RunIpAsync("netns", "add", namespaceName).ConfigureAwait(false);
            namespaceCreated = true;
            await RunIpAsync("link", "add", hostDevice, "type", "veth", "peer", "name", lanDevice).ConfigureAwait(false);
            vethCreated = true;
            await RunIpAsync("link", "set", lanDevice, "netns", namespaceName).ConfigureAwait(false);
            await RunIpAsync("addr", "add", hostAddress, "dev", hostDevice).ConfigureAwait(false);
            await RunIpAsync("link", "set", hostDevice, "up").ConfigureAwait(false);
            await RunIpAsync("-n", namespaceName, "addr", "add", $"{fixtureAddress}/24", "dev", lanDevice).ConfigureAwait(false);
            await RunIpAsync("-n", namespaceName, "link", "set", "lo", "up").ConfigureAwait(false);
            await RunIpAsync("-n", namespaceName, "link", "set", lanDevice, "up").ConfigureAwait(false);
            await RunIpAsync("-n", namespaceName, "route", "add", "default", "via", "192.168.248.1").ConfigureAwait(false);
            return (namespaceName, hostDevice, fixtureAddress);
        }
        catch
        {
            if (vethCreated)
            {
                try
                {
                    _ = await ExecAsync("sudo", ["ip", "link", "del", hostDevice]).ConfigureAwait(false);
                }
                catch
                {
                    // Continue cleanup and preserve the setup failure that caused this path.
                }
            }
            if (namespaceCreated)
            {
                try
                {
                    _ = await ExecAsync("sudo", ["ip", "netns", "del", namespaceName]).ConfigureAwait(false);
                }
                catch
                {
                    // Cleanup is best effort; preserve the original setup failure.
                }
            }
            throw;
        }
    }

    private static async Task VerifyRoutedLanFixtureCleanupAsync()
    {
        List<string> commands = [];
        execOverride = (fileName, arguments) =>
        {
            if (!string.Equals(fileName, "sudo", StringComparison.Ordinal)
                || arguments.Count < 2
                || !string.Equals(arguments[0], "ip", StringComparison.Ordinal))
            {
                return Task.FromResult((1, string.Empty, "Unexpected command in routed LAN fixture fake runner."));
            }

            string command = string.Join(' ', arguments);
            commands.Add(command);
            if (arguments.Count >= 4
                && string.Equals(arguments[1], "link", StringComparison.Ordinal)
                && string.Equals(arguments[2], "add", StringComparison.Ordinal))
            {
                return Task.FromResult((2, string.Empty, "File exists"));
            }

            return Task.FromResult((0, string.Empty, string.Empty));
        };

        try
        {
            bool failedAtVethAdd = false;
            try
            {
                _ = await CreateRoutedLanFixtureAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("link add", StringComparison.Ordinal))
            {
                failedAtVethAdd = true;
            }

            string addCommand = commands.Single(c => c.Contains("link add", StringComparison.Ordinal));
            string attemptedHostDevice = addCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries)[3];
            bool interfaceDeleteIssued = commands.Any(c => c == $"ip link del {attemptedHostDevice}");
            bool namespaceAddIssued = commands.Any(c => c.StartsWith("ip netns add ", StringComparison.Ordinal));
            bool namespaceDeleteIssued = commands.Any(c => c.StartsWith("ip netns del ", StringComparison.Ordinal));

            if (!failedAtVethAdd || !namespaceAddIssued || !namespaceDeleteIssued || interfaceDeleteIssued)
            {
                throw new InvalidOperationException($"Routed LAN fixture cleanup ownership self-test failed (failedAtVethAdd={failedAtVethAdd}; namespaceAdd={namespaceAddIssued}; namespaceDelete={namespaceDeleteIssued}; interfaceDelete={interfaceDeleteIssued}; commands={string.Join("; ", commands)}).");
            }

            commands.Clear();
            execOverride = (fileName, arguments) =>
            {
                if (!string.Equals(fileName, "sudo", StringComparison.Ordinal)
                    || arguments.Count < 2
                    || !string.Equals(arguments[0], "ip", StringComparison.Ordinal))
                {
                    return Task.FromResult((1, string.Empty, "Unexpected command in routed LAN cleanup-success fake runner."));
                }

                string command = string.Join(' ', arguments);
                commands.Add(command);
                if (arguments.Count >= 5
                    && string.Equals(arguments[1], "link", StringComparison.Ordinal)
                    && string.Equals(arguments[2], "set", StringComparison.Ordinal)
                    && string.Equals(arguments[4], "netns", StringComparison.Ordinal))
                {
                    return Task.FromResult((5, string.Empty, "Injected failure after veth creation."));
                }

                return Task.FromResult((0, string.Empty, string.Empty));
            };

            string? laterFailure = null;
            try
            {
                _ = await CreateRoutedLanFixtureAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("link set", StringComparison.Ordinal))
            {
                laterFailure = ex.Message;
            }

            string createdNamespace = commands.Single(c => c.StartsWith("ip netns add ", StringComparison.Ordinal))["ip netns add ".Length..];
            string createdVeth = commands.Single(c => c.Contains("link add", StringComparison.Ordinal)).Split(' ', StringSplitOptions.RemoveEmptyEntries)[3];
            bool createdVethRemoved = commands.Count(c => c == $"ip link del {createdVeth}") == 1;
            bool createdNamespaceRemoved = commands.Count(c => c == $"ip netns del {createdNamespace}") == 1;
            if (laterFailure is null || !createdVethRemoved || !createdNamespaceRemoved)
            {
                throw new InvalidOperationException($"Routed LAN fixture owned-resource cleanup self-test failed (setupFailureObserved={laterFailure is not null}; vethRemoved={createdVethRemoved}; namespaceRemoved={createdNamespaceRemoved}; commands={string.Join("; ", commands)}).");
            }

            commands.Clear();
            execOverride = (fileName, arguments) =>
            {
                if (!string.Equals(fileName, "sudo", StringComparison.Ordinal)
                    || arguments.Count < 2
                    || !string.Equals(arguments[0], "ip", StringComparison.Ordinal))
                {
                    return Task.FromResult((1, string.Empty, "Unexpected command in routed LAN cleanup-failure fake runner."));
                }

                string command = string.Join(' ', arguments);
                commands.Add(command);
                if (arguments.Count >= 5
                    && string.Equals(arguments[1], "link", StringComparison.Ordinal)
                    && string.Equals(arguments[2], "set", StringComparison.Ordinal)
                    && string.Equals(arguments[4], "netns", StringComparison.Ordinal))
                {
                    throw new IOException("Injected original fixture setup failure.");
                }
                if (arguments.Count >= 4
                    && string.Equals(arguments[1], "link", StringComparison.Ordinal)
                    && string.Equals(arguments[2], "del", StringComparison.Ordinal))
                {
                    throw new IOException("Injected veth cleanup failure.");
                }

                return Task.FromResult((0, string.Empty, string.Empty));
            };

            string? preservedFailure = null;
            try
            {
                _ = await CreateRoutedLanFixtureAsync().ConfigureAwait(false);
            }
            catch (IOException ex) when (ex.Message == "Injected original fixture setup failure.")
            {
                preservedFailure = ex.Message;
            }

            string failingNamespace = commands.Single(c => c.StartsWith("ip netns add ", StringComparison.Ordinal))["ip netns add ".Length..];
            bool namespaceCleanupAttempted = commands.Count(c => c == $"ip netns del {failingNamespace}") == 1;
            if (preservedFailure is null || !namespaceCleanupAttempted)
            {
                throw new InvalidOperationException($"Routed LAN fixture cleanup-failure self-test failed (originalFailurePreserved={preservedFailure is not null}; namespaceCleanupAttempted={namespaceCleanupAttempted}; commands={string.Join("; ", commands)}).");
            }
        }
        finally
        {
            execOverride = null;
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "scripts", "nac")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

    private static async Task EnsureBoundaryImageAsync(string repoRoot)
    {
        var res = await ExecAsync("podman", ["image", "exists", EgressTopologyManager.BoundaryInitImage]).ConfigureAwait(false);
        if (res.ExitCode == 0)
        {
            return;
        }
        string df = Path.Combine(repoRoot, "src", "images", "kautuka-egress-boundary", "Dockerfile");
        string ctx = Path.GetDirectoryName(df)!;
        await ExecPodmanAsync(["build", "-t", EgressTopologyManager.BoundaryInitImage, "-f", df, ctx]).ConfigureAwait(false);
    }

    private static async Task ExecPodmanAsync(IReadOnlyList<string> args)
    {
        var (code, stdOut, stdErr) = await ExecAsync("podman", args).ConfigureAwait(false);
        if (code != 0)
        {
            throw new InvalidOperationException($"podman {string.Join(' ', args)} failed (rc {code}): {stdErr}\n{stdOut}");
        }
    }

    private static async Task<string> InspectContainerIdAsync(string name)
    {
        var (code, stdOut, _) = await ExecAsync("podman", ["inspect", "-f", "{{.Id}}", name]).ConfigureAwait(false);
        return code == 0 ? stdOut.Trim() : string.Empty;
    }

    private static async Task<string> InspectPodInfraIdAsync(string podName)
    {
        var (code, stdOut, _) = await ExecAsync("podman", ["pod", "inspect", "-f", "{{.InfraContainerID}}", podName]).ConfigureAwait(false);
        return code == 0 ? stdOut.Trim() : string.Empty;
    }

    private static async Task<int> InspectContainerPidAsync(string name)
    {
        var (code, stdOut, _) = await ExecAsync("podman", ["inspect", "-f", "{{.State.Pid}}", name]).ConfigureAwait(false);
        return code == 0 && int.TryParse(stdOut.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid) ? pid : 0;
    }

    private static async Task<bool> IsContainerRunningAsync(string name)
    {
        var (code, stdOut, _) = await ExecAsync("podman", ["inspect", "-f", "{{.State.Running}}", name]).ConfigureAwait(false);
        return code == 0 && bool.TryParse(stdOut.Trim(), out bool running) && running;
    }

    private static async Task<string> ExecInContainerAsync(string name, IReadOnlyList<string> cmd)
    {
        var (code, stdOut, stdErr) = await ExecAsync("podman", ["exec", name, .. cmd]).ConfigureAwait(false);
        if (code != 0)
        {
            throw new InvalidOperationException($"exec in {name} failed (rc {code}): {stdErr}");
        }
        return stdOut;
    }

    private static async Task<string> ExecInContainerExpectFailAsync(string name, IReadOnlyList<string> cmd)
    {
        var (code, stdOut, _) = await ExecAsync("podman", ["exec", name, .. cmd]).ConfigureAwait(false);
        return code == 0 ? $"ALLOWED:{stdOut.Trim()}" : "BLOCKED";
    }

    private static async Task<string> RunNsenterNftListAsync(int pid)
    {
        var (code, stdOut, stdErr) = await ExecAsync("podman", ["unshare", "nsenter", "-t", pid.ToString(CultureInfo.InvariantCulture), "-n", "nft", "-j", "list", "table", "inet", "kautuka_workload"]).ConfigureAwait(false);
        if (code != 0)
        {
            throw new InvalidOperationException($"nsenter nft failed (rc {code}): {stdErr}");
        }
        return stdOut;
    }

    private static async Task<string> RunPodNftListEgressAsync(string containerName)
    {
        int pid = await InspectContainerPidAsync(containerName).ConfigureAwait(false);
        var (code, stdOut, stdErr) = await ExecAsync("podman", ["unshare", "nsenter", "-t", pid.ToString(CultureInfo.InvariantCulture), "-n", "nft", "-j", "list", "table", "ip", "kautuka_egress"]).ConfigureAwait(false);
        if (code != 0)
        {
            throw new InvalidOperationException($"nsenter egress nft failed (rc {code}): {stdErr}");
        }
        return stdOut;
    }

    private static async Task RunProductionUserBrakeProofAsync(
        string workspaceRoot,
        ContainerContext sourceContext,
        string baseImage,
        PodmanContainerTool containerTool,
        NacController controller,
        EgressTopologyManager topology,
        WorkspaceDesiredStateStore desiredState,
        ContainerContext dnsContext,
        string? brakeNcReportPath,
        List<AcceptanceObservation> observations)
    {
        // Unique identities and agent IDs keep every Podman object created by this proof
        // unambiguously owned by this invocation and safe to clean up on every exit path.
        string suffix = Guid.NewGuid().ToString("N")[..12];
        string brakeUser = $"brake-a-{suffix}";
        string otherUser = $"brake-b-{suffix}";
        string existingAgent = $"brake-existing-{suffix}";
        string otherAgent = $"brake-other-{suffix}";
        string blockedAgent = $"brake-blocked-{suffix}";
        string restoredAgent = $"brake-restored-{suffix}";
        ContainerContext ContextFor(string userId, string agentId)
        {
            string workspace = Path.Combine(workspaceRoot, "users", userId, "agents", agentId);
            Directory.CreateDirectory(workspace);
            return sourceContext with
            {
                ScopeId = agentId,
                AgentId = agentId,
                UserId = userId,
                HostWorkspacePath = workspace,
                Image = baseImage,
            };
        }

        ContainerContext existingContext = ContextFor(brakeUser, existingAgent);
        ContainerContext otherContext = ContextFor(otherUser, otherAgent);
        ContainerContext blockedContext = ContextFor(brakeUser, blockedAgent);
        ContainerContext restoredContext = ContextFor(brakeUser, restoredAgent);
        _ = desiredState.ApplyDesiredState(
            desiredState.Snapshot.AcceptedVersion + 1,
            WorkspaceDesiredStateSyncReason.Bootstrap,
            new ManagerRuntimeSettings(
                new WorkspaceDefinition { BaseRoot = workspaceRoot },
                new SandboxRuntimeDefinition { Image = baseImage, DefaultTimeout = 30 },
                new WorkspaceTelemetryDefinition()),
            [dnsContext, existingContext, otherContext, blockedContext, restoredContext],
            [],
            []);
        string[] ownedContainerNames =
        [
            PodmanContainerTool.BuildContainerName(existingContext),
            PodmanContainerTool.BuildContainerName(otherContext),
            PodmanContainerTool.BuildContainerName(blockedContext),
            PodmanContainerTool.BuildContainerName(restoredContext),
        ];
        var grpcService = new WorkspaceGrpcService(
            null!, null!, null!, null!, null!, null!, null!, null!,
            NullLogger<WorkspaceGrpcService>.Instance,
            controller,
            egressTopologyManager: topology);
        var callContext = new AcceptanceServerCallContext();
        var admin = new NetworkAccessActorMessage { UserId = "admin-1", IsAdmin = true };
        bool brakeEngaged = false;
        try
        {
            OciSandboxSession existingSession = await containerTool.SpawnSandboxAsync(existingContext, CancellationToken.None).ConfigureAwait(false);
            OciSandboxSession otherSession = await containerTool.SpawnSandboxAsync(otherContext, CancellationToken.None).ConfigureAwait(false);
            string existingBefore = await InspectContainerStateAsync(existingSession.ContainerName).ConfigureAwait(false);
            string otherBefore = await InspectContainerStateAsync(otherSession.ContainerName).ConfigureAwait(false);
            if (!string.Equals(existingBefore, "running", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(otherBefore, "running", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"User-brake fixture workloads were not both running before the brake (target={existingBefore}, other={otherBefore}).");
            }

            // The handler can persist the brake before a later operation throws. Always
            // attempt the release in finally after beginning the SetBrake request.
            brakeEngaged = true;
            NetworkAccessBrakeMessage brakeOn = await grpcService.SetBrake(
                new NetworkAccessSetBrakeRequest
                {
                    Actor = admin,
                    Scope = "user",
                    UserId = brakeUser,
                    On = true,
                },
                callContext).ConfigureAwait(false);
            string existingAfter = await InspectContainerStateAsync(existingSession.ContainerName).ConfigureAwait(false);
            string otherAfter = await InspectContainerStateAsync(otherSession.ContainerName).ConfigureAwait(false);
            bool brakedExistingStopped = string.Equals(existingAfter, "exited", StringComparison.OrdinalIgnoreCase)
                || string.Equals(existingAfter, "stopped", StringComparison.OrdinalIgnoreCase);
            bool otherUserStillRunning = string.Equals(otherAfter, "running", StringComparison.OrdinalIgnoreCase);

            bool newLaunchRejectedByBrake = false;
            try
            {
                _ = await containerTool.SpawnSandboxAsync(blockedContext, CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("user network brake is active", StringComparison.OrdinalIgnoreCase))
            {
                newLaunchRejectedByBrake = true;
            }

            _ = await grpcService.SetBrake(
                new NetworkAccessSetBrakeRequest
                {
                    Actor = admin,
                    Scope = "user",
                    UserId = brakeUser,
                    On = false,
                },
                callContext).ConfigureAwait(false);
            brakeEngaged = false;
            OciSandboxSession restoredSession = await containerTool.SpawnSandboxAsync(restoredContext, CancellationToken.None).ConfigureAwait(false);
            string restoredState = await InspectContainerStateAsync(restoredSession.ContainerName).ConfigureAwait(false);
            bool restoredLaunchRunning = string.Equals(restoredState, "running", StringComparison.OrdinalIgnoreCase);
            bool responseContainsUser = brakeOn.Users.Any(user => string.Equals(user.UserId, brakeUser, StringComparison.Ordinal));
            string ncObservedValue = brakeNcReportPath is null
                ? "NC_NOT_RUN"
                : await ReadBrakeNcObservedValueAsync(brakeNcReportPath).ConfigureAwait(false);
            observations.Add(AcceptanceHarness.Record(
                "PROD_USER_BRAKE",
                $"existingStopped={brakedExistingStopped}:otherUserRunning={otherUserStillRunning}:newLaunchRejected={newLaunchRejectedByBrake}:releaseLaunchRunning={restoredLaunchRunning}:responseContainsUser={responseContainsUser}",
                "existingStopped=True:otherUserRunning=True:newLaunchRejected=True:releaseLaunchRunning=True:responseContainsUser=True",
                ncObservedValue,
                UserBrakeDisabledHookObservation));
            Console.WriteLine("[PROD_STAGE] production user-brake stop, scope, launch refusal, and release checks completed");
        }
        finally
        {
            if (brakeEngaged)
            {
                try
                {
                    _ = await grpcService.SetBrake(
                        new NetworkAccessSetBrakeRequest
                        {
                            Actor = admin,
                            Scope = "user",
                            UserId = brakeUser,
                            On = false,
                        },
                        callContext).ConfigureAwait(false);
                }
                catch
                {
                    // Cleanup continues even if the brake release call fails.
                }
            }

            _ = await ExecAsync("podman", ["rm", "-f", .. ownedContainerNames]).ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadBrakeNcObservedValueAsync(string reportPath)
    {
        string fullPath = Path.GetFullPath(reportPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The separately captured mutated-handler production report was not found.", fullPath);
        }

        string json = await File.ReadAllTextAsync(fullPath).ConfigureAwait(false);
        ProductionProofReport report = JsonSerializer.Deserialize(json, AcceptanceJsonContext.Default.ProductionProofReport)
            ?? throw new InvalidDataException("The mutated-handler NC report was empty or invalid.");
        AcceptanceObservation[] matches = report.Observations
            .Where(static observation => string.Equals(observation.TestId, "PROD_USER_BRAKE", StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException($"The mutated-handler NC report must contain exactly one PROD_USER_BRAKE observation; found {matches.Length}.");
        }

        AcceptanceObservation ncObservation = matches[0];
        if (!string.Equals(ncObservation.ObservedValue, UserBrakeDisabledHookObservation, StringComparison.Ordinal)
            || ncObservation.NcFlipped
            || !ncObservation.Outcome.StartsWith("FAIL", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The imported NC report does not show the expected failed user-brake scenario with EngageBrakeAsync disabled.");
        }

        return ncObservation.ObservedValue;
    }

    private static async Task<string> InspectContainerStateAsync(string containerName)
    {
        var (exitCode, output, error) = await ExecAsync(
            "podman",
            ["inspect", "--format", "{{.State.Status}}", containerName]).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Could not inspect acceptance workload '{containerName}' (rc={exitCode}; {error}).");
        }

        return output.Trim();
    }

    private static long ExtractNftCommentCounterPackets(string nftJson, string commentTag)
    {
        using JsonDocument doc = JsonDocument.Parse(nftJson);
        if (!doc.RootElement.TryGetProperty("nftables", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        long packets = 0;
        foreach (JsonElement item in arr.EnumerateArray())
        {
            if (!item.TryGetProperty("rule", out JsonElement rule))
            {
                continue;
            }

            if (!rule.TryGetProperty("comment", out JsonElement cElem)
                || !string.Equals(cElem.GetString(), commentTag, StringComparison.Ordinal))
            {
                continue;
            }

            if (rule.TryGetProperty("expr", out JsonElement expr) && expr.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement e in expr.EnumerateArray())
                {
                    if (e.TryGetProperty("counter", out JsonElement cnt) && cnt.TryGetProperty("packets", out JsonElement pkts))
                    {
                        packets += pkts.GetInt64();
                    }
                }
            }
        }

        return packets;
    }

    private static long ExtractNftCommentRuleHandle(string nftJson, string commentTag)
    {
        using JsonDocument doc = JsonDocument.Parse(nftJson);
        if (!doc.RootElement.TryGetProperty("nftables", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        foreach (JsonElement item in arr.EnumerateArray())
        {
            if (!item.TryGetProperty("rule", out JsonElement rule)
                || !rule.TryGetProperty("comment", out JsonElement comment)
                || !string.Equals(comment.GetString(), commentTag, StringComparison.Ordinal))
            {
                continue;
            }

            if (rule.TryGetProperty("handle", out JsonElement handle) && handle.TryGetInt64(out long value))
            {
                return value;
            }
        }

        return 0;
    }

    private static async Task RunBashAsync(string script)
    {
        var (code, _, stdErr) = await ExecAsync("bash", ["-lc", script]).ConfigureAwait(false);
        if (code != 0)
        {
            throw new InvalidOperationException($"bash failed (rc {code}): {stdErr}");
        }
    }

    private static int CountLines(string path)
        => File.Exists(path) ? File.ReadAllLines(path).Count(l => !string.IsNullOrWhiteSpace(l)) : 0;

    private static async Task<int> RunProductionTopologyProofAsync(string? outputReportPath, string? brakeNcReportPath)
    {
        string repoRoot = FindRepoRoot();
        string baseTmp = Directory.Exists("/var/tmp") ? "/var/tmp" : Path.GetTempPath();
        (string tempRoot, string tempRootOwnerToken) = CreateOwnedTempRoot(baseTmp);
        string xdgState = Path.Combine(tempRoot, "xdg-state");
        string stateDir = Path.Combine(tempRoot, "manager-nac");
        string socketDir = Path.Combine(tempRoot, "sock");
        string hooksDir = Path.Combine(tempRoot, "hooks.d");
        string podmanStorageDir = Path.Combine(tempRoot, "podman-storage");
        Directory.CreateDirectory(xdgState);
        Directory.CreateDirectory(stateDir);
        Directory.CreateDirectory(socketDir);
        Directory.CreateDirectory(hooksDir);
        Directory.CreateDirectory(podmanStorageDir);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(socketDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Environment.SetEnvironmentVariable("XDG_STATE_HOME", xdgState);
        string nacStateDir = Path.Combine(xdgState, "kautuka", "nac");
        Directory.CreateDirectory(nacStateDir);

        string hookSrc = Path.Combine(repoRoot, "scripts", "nac", "kautuka_hook.py");
        string hookTemplateSrc = Path.Combine(repoRoot, "scripts", "nac", "kautuka_workload.nft.json.tmpl");
        string hookPath = Path.Combine(hooksDir, "kautuka_hook.py");
        File.Copy(hookSrc, hookPath, overwrite: true);
        File.Copy(hookTemplateSrc, Path.Combine(hooksDir, "kautuka_workload.nft.json.tmpl"), overwrite: true);
        File.Copy(Path.Combine(repoRoot, "scripts", "nac", "kautuka_package_proxy.nft.json.tmpl"),
            Path.Combine(hooksDir, "kautuka_package_proxy.nft.json.tmpl"), overwrite: true);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(hookPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        string hookWrapper = Path.Combine(hooksDir, "kautuka_hook_wrapper.sh");
        await File.WriteAllTextAsync(hookWrapper, $"#!/bin/bash\nexport XDG_STATE_HOME='{xdgState}'\nexec python3 '{hookPath}' \"$@\"\n").ConfigureAwait(false);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(hookWrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        string hookJson = $$"""
        {
          "version": "1.0.0",
          "hook": { "path": "{{hookWrapper}}" },
          "when": { "always": true },
          "stages": ["createRuntime"]
        }
        """;
        await File.WriteAllTextAsync(Path.Combine(hooksDir, "kautuka_hook.json"), hookJson).ConfigureAwait(false);

        string podmanGraphRoot = Path.Combine(podmanStorageDir, "graphroot");
        string podmanRuntimeDirectory = Path.Combine(Path.GetTempPath(), $"nac-run-{Guid.NewGuid():N}"[..16]);
        Directory.CreateDirectory(podmanRuntimeDirectory);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(podmanRuntimeDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        string podmanRuntimeOwnerToken = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(Path.Combine(podmanRuntimeDirectory, ".acceptance-owner"), podmanRuntimeOwnerToken).ConfigureAwait(false);
        string podmanRunRoot = Path.Combine(podmanRuntimeDirectory, "run");
        Directory.CreateDirectory(podmanRunRoot);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(podmanRunRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        string storageConfigPath = Path.Combine(tempRoot, "storage.conf");
        await File.WriteAllTextAsync(
            storageConfigPath,
            $"[storage]\ndriver = \"overlay\"\ngraphroot = \"{podmanGraphRoot}\"\nrunroot = \"{podmanRunRoot}\"\n").ConfigureAwait(false);
        string containersConfigPath = Path.Combine(tempRoot, "containers.conf");
        await File.WriteAllTextAsync(containersConfigPath, $"[engine]\nhooks_dir = [\"{hooksDir}\"]\n").ConfigureAwait(false);
        string? previousStorageConfig = Environment.GetEnvironmentVariable("CONTAINERS_STORAGE_CONF");
        string? previousContainersConfig = Environment.GetEnvironmentVariable("CONTAINERS_CONF");
        string? previousPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("CONTAINERS_STORAGE_CONF", storageConfigPath);
        Environment.SetEnvironmentVariable("CONTAINERS_CONF", containersConfigPath);
        string podmanBinary = previousPath?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, "podman"))
            .FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException("Production proof could not locate the rootless Podman binary.");
        string podmanWrapperDirectory = Path.Combine(tempRoot, "podman-bin");
        Directory.CreateDirectory(podmanWrapperDirectory);
        string podmanWrapperPath = Path.Combine(podmanWrapperDirectory, "podman");
        await File.WriteAllTextAsync(
            podmanWrapperPath,
            $"#!/bin/sh\nexec '{podmanBinary.Replace("'", "'\\''", StringComparison.Ordinal)}' --root '{podmanGraphRoot.Replace("'", "'\\''", StringComparison.Ordinal)}' --runroot '{podmanRunRoot.Replace("'", "'\\''", StringComparison.Ordinal)}' --hooks-dir '{hooksDir.Replace("'", "'\\''", StringComparison.Ordinal)}' \"$@\"\n")
            .ConfigureAwait(false);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(podmanWrapperPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Environment.SetEnvironmentVariable("PATH", $"{podmanWrapperDirectory}{Path.PathSeparator}{previousPath}");

        string wlName = PodmanContainerTool.BuildContainerName(new ContainerContext(
            "agent", "a1", "u1", Path.Combine(tempRoot, "workspaces", "users", "u1", "agents", "a1"), "/workspace",
            "ghcr.io/devkrity/kautuka-base@sha256:675400832f2ced5cf802db66eb067ef5022f65f0551c665d87ae7edc58dc0839",
            1, "512m", 30, [], new Dictionary<string, string>()));
        Process? lanFixtureProcess = null;
        string? lanFixtureNamespaceName = null;
        string? lanFixtureHostDevice = null;
        string lanHostAddress = string.Empty;
        var isolationCleanupGate = new PodmanIsolationCleanupGate();
        var podman = new PodmanCommandRunner();
        var registry = new WorkloadRegistry();
        using var policyStore = new NetworkAccessPolicyStore(Path.Combine(stateDir, "policy-state.json"));
        using var auditRing = new NetworkAccessAuditRing(Path.Combine(stateDir, "audit.jsonl"));
        var allocator = new WorkloadAddressAllocator(Path.Combine(stateDir, "ip-cursor.json"));
        string workspaceRoot = Path.Combine(tempRoot, "workspaces");
        Directory.CreateDirectory(workspaceRoot);
        var desiredState = new WorkspaceDesiredStateStore();
        var verifier = new WorkloadPostStartVerifier(podman, desiredState);
        using ILoggerFactory loggerFactory = LoggerFactory.Create(static b => b.AddSimpleConsole());
        var topology = new EgressTopologyManager(
            podman,
            allocator,
            verifier,
            registry,
            stateDir,
            loggerFactory.CreateLogger<EgressTopologyManager>(),
            envoySocketHostDir: socketDir,
            hookStateDirectory: nacStateDir);
        int packetPathOnlyGateOverride = 0;
        var controller = new NacController(
            policyStore,
            registry,
            auditRing,
            isContainerAuthorizationGateOpen: () => Volatile.Read(ref packetPathOnlyGateOverride) == 1 || topology.IsStartupGateOpen,
            isPackageProxyReady: () => topology.IsPackageProxyReady);
        string sockPath = Path.Combine(socketDir, "envoy.sock");
        string lockPath = Path.Combine(socketDir, "networkaccess.lock");
        var authHost = EnvoyAuthorizationHost.Create(
            controller,
            new EnvoyAuthorizationHostOptions
            {
                TransportMode = EnvoyListenerTransportMode.UnixDomainSocket,
                SocketPath = sockPath,
                LockFilePath = lockPath,
            });
        await using var authHostScope = authHost.ConfigureAwait(false);

        try
        {
            var storageInfo = await ExecAsync("podman", ["info", "--format", "{{.Store.GraphRoot}}|{{.Store.RunRoot}}|{{.Host.Security.Rootless}}"]).ConfigureAwait(false);
            string[] storageValues = storageInfo.StdOut.Trim().Split('|');
            if (storageInfo.ExitCode != 0
                || storageValues.Length != 3
                || !string.Equals(Path.GetFullPath(storageValues[0]), Path.GetFullPath(podmanGraphRoot), StringComparison.Ordinal)
                || !string.Equals(Path.GetFullPath(storageValues[1]), Path.GetFullPath(podmanRunRoot), StringComparison.Ordinal)
                || !string.Equals(storageValues[2], "true", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Production proof requires rootless Podman with isolated storage. Observed: {storageInfo.StdOut} {storageInfo.StdErr}");
            }

            isolationCleanupGate.MarkVerified();

            // Clean any leftover production-named pod/networks before initializing fresh topology
            await ExecAsync("podman", ["rm", "-f", wlName]).ConfigureAwait(false);
            await ExecAsync("podman", ["pod", "rm", "-f", EgressTopologyManager.EgressPodName]).ConfigureAwait(false);
            await ExecAsync("podman", ["network", "rm", "-f", EgressTopologyManager.WorkloadNetworkName, EgressTopologyManager.OutsideNetworkName]).ConfigureAwait(false);

            await authHost.StartAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await topology.InitializeProductionAsync(registry, isWindowsHostNativeDev: false, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                var (podInspectRc, podInspectOut, podInspectErr) = await ExecAsync("podman", ["pod", "inspect", EgressTopologyManager.EgressPodName, "--format", "json"]).ConfigureAwait(false);
                var (boundaryInspectRc, boundaryInspectOut, boundaryInspectErr) = await ExecAsync("podman", ["inspect", "kautuka-egress-boundary", "--format", "json"]).ConfigureAwait(false);
                var (envoyInspectRc, envoyInspectOut, envoyInspectErr) = await ExecAsync("podman", ["inspect", "kautuka-egress-envoy", "--format", "{{json .State}}|{{json .Config.Cmd}}|{{json .Config.Entrypoint}}|{{json .HostConfig.Binds}}|{{json .Mounts}}"]).ConfigureAwait(false);
                var (envoyLogsRc, envoyLogsOut, envoyLogsErr) = await ExecAsync("podman", ["logs", "kautuka-egress-envoy"]).ConfigureAwait(false);
                await Console.Error.WriteLineAsync($"NAC topology startup diagnostics: pod rc={podInspectRc} {podInspectOut}; boundary rc={boundaryInspectRc}; envoy rc={envoyInspectRc} state/cmd/entrypoint/binds/mounts={envoyInspectOut}; inspectErr={envoyInspectErr}; logsRc={envoyLogsRc} logs={envoyLogsOut} logsErr={envoyLogsErr}").ConfigureAwait(false);
                throw;
            }

            if (!topology.IsStartupGateOpen)
            {
                throw new InvalidOperationException("EgressTopologyManager startup gate remained closed after InitializeAsync.");
            }
            Console.WriteLine("[PROD_STAGE] production topology initialized and canary passed");

            IReadOnlyList<AcceptanceObservation> packageProxyObservations = await VerifyManagedPackageProxyPathAsync(repoRoot, tempRoot, topology, controller, registry, podman, verifier, loggerFactory)
                .ConfigureAwait(false);

            int egressPid = await InspectContainerPidAsync("kautuka-egress-envoy").ConfigureAwait(false);
            var (routeExitCode, routeOutput, routeError) = await ExecAsync(
                "podman",
                ["unshare", "nsenter", "-t", egressPid.ToString(CultureInfo.InvariantCulture), "-n", "ip", "-4", "route"])
                .ConfigureAwait(false);
            string[] defaultRoutes = routeOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static line => line.StartsWith("default via ", StringComparison.Ordinal))
                .ToArray();
            string outsideDefaultRoute = defaultRoutes.FirstOrDefault(static line =>
                line.StartsWith("default via 10.248.0.1 ", StringComparison.Ordinal)
                && line.EndsWith("metric 100", StringComparison.Ordinal)) ?? string.Empty;
            string workloadDefaultRoute = defaultRoutes.FirstOrDefault(static line =>
                line.StartsWith("default via 10.247.0.2 ", StringComparison.Ordinal)
                && line.EndsWith("metric 1000", StringComparison.Ordinal)) ?? string.Empty;
            if (routeExitCode != 0 || outsideDefaultRoute.Length == 0 || workloadDefaultRoute.Length == 0)
            {
                throw new InvalidOperationException(
                    $"The live egress pod routes did not prefer the outside gateway (rc={routeExitCode}; routes={routeOutput}; err={routeError}).");
            }
            string[] outsideRouteTokens = outsideDefaultRoute.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            int outsideDeviceIndex = Array.IndexOf(outsideRouteTokens, "dev");
            if (outsideDeviceIndex < 0 || outsideDeviceIndex + 1 >= outsideRouteTokens.Length)
            {
                throw new InvalidOperationException($"The live outside default route has no device (route={outsideDefaultRoute}).");
            }
            string outsideNetworkDevice = outsideRouteTokens[outsideDeviceIndex + 1];
            string egressDefaultRoutes = string.Join('\n', outsideDefaultRoute, workloadDefaultRoute);
            var (outsideSourceRc, outsideSourceOut, outsideSourceErr) = await ExecAsync(
                "podman",
                ["unshare", "nsenter", "-t", egressPid.ToString(CultureInfo.InvariantCulture), "-n", "ip", "-4", "route", "get", "1.1.1.1"])
                .ConfigureAwait(false);
            if (outsideSourceRc != 0 || !Regex.IsMatch(outsideSourceOut, @"\bsrc 10\.248\.0\.2\b", RegexOptions.CultureInvariant))
            {
                throw new InvalidOperationException($"The egress default route did not select source 10.248.0.2 (rc={outsideSourceRc}; route={outsideSourceOut}; err={outsideSourceErr}).");
            }

            string boundaryPath = Path.Combine(stateDir, "nac", "boundary", "boundary.nft");
            string boundaryText = await File.ReadAllTextAsync(boundaryPath).ConfigureAwait(false);
            if (!boundaryText.Contains("comment \"lan_approved\"", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("LAN acceptance requires the production boundary to admit LAN packets to Envoy for policy enforcement.");
            }

            const string baseImage = "ghcr.io/devkrity/kautuka-base@sha256:675400832f2ced5cf802db66eb067ef5022f65f0551c665d87ae7edc58dc0839";
            (lanFixtureNamespaceName, lanFixtureHostDevice, lanHostAddress) = await CreateRoutedLanFixtureAsync().ConfigureAwait(false);
            Console.WriteLine($"[PROD_STAGE] routed LAN fixture address={lanHostAddress}; namespace={lanFixtureNamespaceName}");
            string rebindHostName = $"rebind.{lanHostAddress}.nip.io";
            string lanHostName = $"lan.{lanHostAddress}.nip.io";
            int envoyLanRoutePid = await InspectContainerPidAsync("kautuka-egress-envoy").ConfigureAwait(false);
            var (lanRouteGetRc, lanRouteGetOut, lanRouteGetErr) = await ExecAsync(
                "podman",
                ["unshare", "nsenter", "-t", envoyLanRoutePid.ToString(CultureInfo.InvariantCulture), "-n", "ip", "-4", "route", "get", lanHostAddress, "from", EgressTopologyManager.OutsideLanSourceIp]).ConfigureAwait(false);
            Console.WriteLine($"[PROD_STAGE] LAN fixture route: host={lanHostAddress}; outsideDevice={outsideNetworkDevice}; rc={lanRouteGetRc}; route={lanRouteGetOut.Trim()}; error={lanRouteGetErr.Trim()}");
            string lanCertificatePath = Path.Combine(tempRoot, "lan-fixture.crt");
            string lanPrivateKeyPath = Path.Combine(tempRoot, "lan-fixture.key");
            string lanAccessLogPath = Path.Combine(tempRoot, "lan-fixture-access.log");
            await File.WriteAllTextAsync(lanAccessLogPath, string.Empty).ConfigureAwait(false);
            using (RSA rsa = RSA.Create())
            {
                rsa.KeySize = 2048;
                var certificateRequest = new CertificateRequest(
                    "CN=kautuka-nac-fixture",
                    rsa,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);
                certificateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
                using X509Certificate2 certificate = certificateRequest.CreateSelfSigned(
                    DateTimeOffset.UtcNow.AddMinutes(-5),
                    DateTimeOffset.UtcNow.AddHours(1));
                await File.WriteAllTextAsync(lanCertificatePath, certificate.ExportCertificatePem()).ConfigureAwait(false);
                await File.WriteAllTextAsync(lanPrivateKeyPath, rsa.ExportPkcs8PrivateKeyPem()).ConfigureAwait(false);
            }

            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(lanCertificatePath, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                File.SetUnixFileMode(lanPrivateKeyPath, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                File.SetUnixFileMode(lanAccessLogPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
            }

            const string lanServer = """
                import http.server, os, ssl, sys
                _, cert_path, key_path, access_path, pid_path = sys.argv[1:6]
                with open(pid_path, 'w') as pid_file:
                    pid_file.write(str(os.getpid()))
                access=open(access_path,'a',buffering=1)
                def record(message): print(message, file=access, flush=True)
                class Handler(http.server.BaseHTTPRequestHandler):
                    def setup(self):
                        http.server.BaseHTTPRequestHandler.setup(self)
                        record('accepted TCP from '+str(self.client_address))
                    def do_GET(self):
                        record('GET '+self.path+' from '+str(self.client_address))
                        body=b'NAC LAN fixture ok'
                        self.send_response(200)
                        self.send_header('Content-Length', str(len(body)))
                        self.end_headers()
                        self.wfile.write(body)
                    def log_message(self, fmt, *args): record(fmt % args)
                server=http.server.ThreadingHTTPServer(('0.0.0.0',8443),Handler)
                context=ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
                context.load_cert_chain(cert_path,key_path)
                server.socket=context.wrap_socket(server.socket,server_side=True)
                server.serve_forever()
                """;
            string lanServerScriptPath = Path.Combine(tempRoot, "lan-fixture.py");
            await File.WriteAllTextAsync(lanServerScriptPath, lanServer).ConfigureAwait(false);
            var lanServerStartInfo = new ProcessStartInfo("sudo")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            lanServerStartInfo.ArgumentList.Add("ip");
            lanServerStartInfo.ArgumentList.Add("netns");
            lanServerStartInfo.ArgumentList.Add("exec");
            lanServerStartInfo.ArgumentList.Add(lanFixtureNamespaceName!);
            lanServerStartInfo.ArgumentList.Add("python3");
            lanServerStartInfo.ArgumentList.Add("-u");
            lanServerStartInfo.ArgumentList.Add(lanServerScriptPath);
            lanServerStartInfo.ArgumentList.Add(lanHostAddress);
            lanServerStartInfo.ArgumentList.Add(lanCertificatePath);
            lanServerStartInfo.ArgumentList.Add(lanPrivateKeyPath);
            lanServerStartInfo.ArgumentList.Add(lanAccessLogPath);
            string lanFixturePidPath = Path.Combine(tempRoot, "lan-fixture.pid");
            lanServerStartInfo.ArgumentList.Add(lanFixturePidPath);
            lanFixtureProcess = Process.Start(lanServerStartInfo)
                ?? throw new InvalidOperationException("Could not start the host LAN fixture process.");
            int lanFixtureHealthRc = 1;
            string lanFixtureHealthOut = string.Empty;
            string lanFixtureHealthErr = string.Empty;
            for (int attempt = 0; attempt < 10 && lanFixtureHealthRc != 0; attempt++)
            {
                (lanFixtureHealthRc, lanFixtureHealthOut, lanFixtureHealthErr) = await ExecAsync(
                    "curl",
                    ["--insecure", "--silent", "--show-error", "--max-time", "2", $"https://{lanHostAddress}:8443/"]).ConfigureAwait(false);
                if (lanFixtureHealthRc != 0 && lanFixtureProcess.HasExited)
                {
                    break;
                }
                if (lanFixtureHealthRc != 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
                }
            }
            if (lanFixtureHealthRc != 0 || !lanFixtureHealthOut.Contains("NAC LAN fixture ok", StringComparison.Ordinal))
            {
                string lanFixtureAccessLog = await File.ReadAllTextAsync(lanAccessLogPath).ConfigureAwait(false);
                string lanFixtureState = lanFixtureProcess.HasExited ? $"exited:{lanFixtureProcess.ExitCode}" : "running";
                throw new InvalidOperationException($"Controlled host LAN fixture did not become healthy (rc={lanFixtureHealthRc}; {lanFixtureHealthErr}; state={lanFixtureState}; access={lanFixtureAccessLog}).");
            }
            var (lanNetworkProbeRc, lanNetworkProbeOut, lanNetworkProbeErr) = await ExecAsync(
                "podman",
                ["run", "--rm", "--network", EgressTopologyManager.OutsideNetworkName, "--entrypoint", "curl", baseImage,
                    "--insecure", "--silent", "--show-error", "--max-time", "4", $"https://{lanHostAddress}:8443/"]).ConfigureAwait(false);
            if (lanNetworkProbeRc != 0 || !lanNetworkProbeOut.Contains("NAC LAN fixture ok", StringComparison.Ordinal))
            {
                string lanFixtureAccessLog = await File.ReadAllTextAsync(lanAccessLogPath).ConfigureAwait(false);
                throw new InvalidOperationException($"Routed LAN fixture could not be reached from the outside network before the NAC flow (rc={lanNetworkProbeRc}; body={lanNetworkProbeOut}; error={lanNetworkProbeErr}; access={lanFixtureAccessLog}).");
            }

            string corefilePath = Path.Combine(stateDir, "nac", "dns", "Corefile");
            string corefile = await File.ReadAllTextAsync(corefilePath).ConfigureAwait(false);
            string workloadDnsBind = "    bind 10.247.0.2";
            string envoyDnsBind = "    bind 127.0.0.1";
            if (!corefile.Contains(workloadDnsBind, StringComparison.Ordinal)
                || !corefile.Contains(envoyDnsBind, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The production CoreDNS configuration did not contain both expected DNS listeners.");
            }

            foreach (string resolverFixtureHost in new[] { rebindHostName, lanHostName })
            {
                var (resolveRc, resolveOut, resolveErr) = await ExecAsync("getent", ["ahostsv4", resolverFixtureHost]).ConfigureAwait(false);
                bool resolvesToLanFixture = resolveRc == 0 && resolveOut
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Any(line => string.Equals(line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), lanHostAddress, StringComparison.Ordinal));
                if (!resolvesToLanFixture)
                {
                    throw new InvalidOperationException($"Wildcard DNS fixture {resolverFixtureHost} did not resolve to the routed LAN fixture {lanHostAddress} using the acceptance runner's configured resolver (rc={resolveRc}; out={resolveOut}; err={resolveErr}). The live NAC proof requires external nip.io DNS resolution.");
                }
                Console.WriteLine($"[PROD_STAGE] wildcard DNS fixture resolved: {resolverFixtureHost} -> {lanHostAddress}");
            }
            string dnsUpstreamLine = boundaryText
                .Split('\n')
                .Select(static l => l.Trim())
                .FirstOrDefault(static l => l.Contains("comment \"dns_upstream\"", StringComparison.Ordinal))
                ?? string.Empty;

            string wlWorkspace = Path.Combine(workspaceRoot, "users", "u1", "agents", "a1");
            var wlContext = new ContainerContext(
                "agent", "a1", "u1", wlWorkspace, "/workspace", baseImage, 1, "512m", 30, [], new Dictionary<string, string>());
            string parserWorkspace = Path.Combine(workspaceRoot, "users", "u1", "agents", "parser-internal");
            string parserControlWorkspace = Path.Combine(workspaceRoot, "users", "u1", "agents", "parser-control");
            string rebindWorkspace = Path.Combine(workspaceRoot, "users", "u1", "agents", "rebind");
            string lanWorkspace = Path.Combine(workspaceRoot, "users", "u1", "agents", "lan-grant");
            Directory.CreateDirectory(wlWorkspace);
            Directory.CreateDirectory(parserWorkspace);
            Directory.CreateDirectory(parserControlWorkspace);
            Directory.CreateDirectory(rebindWorkspace);
            Directory.CreateDirectory(lanWorkspace);
            var parserContext = wlContext with { ScopeId = "parser-internal", HostWorkspacePath = parserWorkspace };
            var parserControlContext = wlContext with { ScopeId = "parser-control", HostWorkspacePath = parserControlWorkspace };
            var rebindContext = wlContext with { ScopeId = "rebind", HostWorkspacePath = rebindWorkspace };
            var lanContext = wlContext with { ScopeId = "lan-grant", HostWorkspacePath = lanWorkspace };
            string parserBundlePath = Path.Combine(tempRoot, "nac-parser-fixture.mcpb");
            using (var bundleStream = File.Create(parserBundlePath))
            using (var bundleArchive = new ZipArchive(bundleStream, ZipArchiveMode.Create))
            {
                ZipArchiveEntry manifestEntry = bundleArchive.CreateEntry("manifest.json");
                Stream manifestStream = await manifestEntry.OpenAsync().ConfigureAwait(false);
                await using (manifestStream.ConfigureAwait(false))
                {
                    using var manifestWriter = new StreamWriter(manifestStream);
                    await manifestWriter.WriteAsync("""
                        {"manifest_version":"0.4","name":"nac-parser-fixture","display_name":"NAC parser fixture","version":"1.0.0","description":"Production acceptance fixture","author":{"name":"Kautuka"},"server":{"type":"binary","entry_point":"server.py","mcp_config":{"command":"python3","args":["server.py"]}}}
                        """).ConfigureAwait(false);
                }
                ZipArchiveEntry serverEntry = bundleArchive.CreateEntry("server.py");
                Stream serverStream = await serverEntry.OpenAsync().ConfigureAwait(false);
                await using (serverStream.ConfigureAwait(false))
                {
                    using var serverWriter = new StreamWriter(serverStream);
                    await serverWriter.WriteAsync("print('fixture entry point')").ConfigureAwait(false);
                }
            }
            const string parserServer = "import json,subprocess,sys\nfor line in sys.stdin:\n r=json.loads(line); m=r.get('method'); i=r.get('id')\n if m=='initialize': print(json.dumps({'jsonrpc':'2.0','id':i,'result':{'protocolVersion':'2025-03-26','capabilities':{'tools':{}},'serverInfo':{'name':'nac-parser-fixture','version':'1'}}}),flush=True)\n elif m=='tools/list': print(json.dumps({'jsonrpc':'2.0','id':i,'result':{'tools':[{'name':'network_probe','description':'Attempt a network request','inputSchema':{'type':'object','properties':{}}}]}}),flush=True)\n elif m=='tools/call':\n  p=subprocess.run(['curl','--insecure','--silent','--show-error','--max-time','6','https://example.com/'],capture_output=True,text=True)\n  print(json.dumps({'jsonrpc':'2.0','id':i,'result':{'content':[{'type':'text','text':'exit='+str(p.returncode)+';body='+p.stdout[:80]}],'isError':False}}),flush=True)\n";
            var internalParserConnector = new WorkspaceMcpContainerConnectorDefinition(
                "nac-internal-parser", baseImage, "python3", ["-u", "-c", parserServer],
                Enabled: false, IsInternalSystemConnector: true, BundlePath: parserBundlePath, ServerType: "binary",
                Environment: [], ReadinessTimeoutSeconds: 30, JitCooldownSeconds: 600)
            {
                CatalogScope = "system",
            };
            var parserControlConnector = internalParserConnector with
            {
                ConnectorId = "nac-parser-control",
                Enabled = true,
                IsInternalSystemConnector = false,
                CatalogScope = "user",
            };
            _ = desiredState.ApplyDesiredState(
                1,
                WorkspaceDesiredStateSyncReason.Bootstrap,
                new ManagerRuntimeSettings(
                    new WorkspaceDefinition { BaseRoot = workspaceRoot },
                    new SandboxRuntimeDefinition { Image = baseImage, DefaultTimeout = 30 },
                    new WorkspaceTelemetryDefinition()),
                [wlContext, parserContext, parserControlContext, rebindContext, lanContext], [internalParserConnector, parserControlConnector], []);
            wlContext = ResolveDesiredWorkspaceContext(wlContext);
            parserContext = ResolveDesiredWorkspaceContext(parserContext);
            parserControlContext = ResolveDesiredWorkspaceContext(parserControlContext);
            rebindContext = ResolveDesiredWorkspaceContext(rebindContext);
            lanContext = ResolveDesiredWorkspaceContext(lanContext);

            ContainerContext ResolveDesiredWorkspaceContext(ContainerContext requested)
            {
                if (!desiredState.TryResolveWorkspace(requested.ScopeType, requested.ScopeId, requested.UserId, out ContainerContext? resolved)
                    || resolved is null)
                {
                    throw new InvalidOperationException($"v7-21 failed: production desired state did not resolve workspace '{requested.ScopeId}'.");
                }

                return resolved;
            }

            var containerTool = new PodmanContainerTool(
                podman,
                allocator,
                registry,
                verifier,
                new WorkloadLaunchGate(topology, policyStore));
            OciSandboxSession workloadSession = await containerTool.SpawnSandboxAsync(wlContext, CancellationToken.None).ConfigureAwait(false);
            wlName = workloadSession.ContainerName;
            RegistryEntry workloadEntry = registry.GetAll().Single(entry =>
                string.Equals(entry.UserId, "u1", StringComparison.Ordinal)
                && string.Equals(entry.AgentId, "a1", StringComparison.Ordinal)
                && string.Equals(entry.Kind, "agent-sandbox", StringComparison.Ordinal));
            string wlIp = workloadEntry.Ip;
            if (!string.Equals(workloadEntry.Kind, "agent-sandbox", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("v7-21 failed: the acceptance workload was not registered through PodmanContainerTool's production builder.");
            }
            WorkloadVerificationResult verResult = await verifier.VerifyWorkloadAsync(wlName, wlIp, null, registry, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            int workloadPid = await InspectContainerPidAsync(wlName).ConfigureAwait(false);
            var (workloadAddressRc, workloadAddresses, workloadAddressErr) = await ExecAsync(
                "podman",
                ["unshare", "nsenter", "-t", workloadPid.ToString(CultureInfo.InvariantCulture), "-n", "ip", "-o", "-4", "address"])
                .ConfigureAwait(false);
            if (workloadAddressRc != 0
                || Regex.IsMatch(workloadAddresses, @"\b10\.248\.0\.[23]/", RegexOptions.CultureInvariant))
            {
                throw new InvalidOperationException($"The workload NICs must not contain egress addresses 10.248.0.2 or 10.248.0.3 (rc={workloadAddressRc}; addresses={workloadAddresses}; err={workloadAddressErr}).");
            }

            var admin = new NetworkAccessActor("admin-1", IsAdmin: true);
            string dnsProbeHostName = $"dns-proof-{Guid.NewGuid():N}.{lanHostAddress}.nip.io";
            NetworkAccessRule dnsProbeRule = policyStore.SetRule(
                admin,
                new RuleWriteSpec("agent", "u1", "a1", null, dnsProbeHostName, "host", 8443, Allowed: true, AllowLan: true),
                expectedRevision: policyStore.Revision).Rule.Rule
                ?? throw new InvalidOperationException("The unique DNS probe policy rule was not created.");

            string[] dnsProbeUpstreamAddresses = corefile
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static line => line.StartsWith("forward . ", StringComparison.Ordinal))
                .SelectMany(static line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Skip(2))
                .Where(static token => System.Net.IPAddress.TryParse(token, out System.Net.IPAddress? address)
                    && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (dnsProbeUpstreamAddresses.Length == 0)
            {
                throw new InvalidOperationException("The production CoreDNS forwarder had no IPv4 upstream addresses for the unique DNS probe.");
            }

            var conntrackDnsClearResults = new List<string>(dnsProbeUpstreamAddresses.Length);
            foreach (string upstreamAddress in dnsProbeUpstreamAddresses)
            {
                var (conntrackClearRc, conntrackClearOut, conntrackClearErr) = await ExecAsync(
                    "podman",
                    ["unshare", "nsenter", "-t", egressPid.ToString(CultureInfo.InvariantCulture), "-n", "conntrack", "-D", "-p", "udp", "--dst", upstreamAddress, "--dport", "53"])
                    .ConfigureAwait(false);
                if (!TryParseConntrackDeleteResult(conntrackClearRc, conntrackClearOut, conntrackClearErr, out long removedFlowCount))
                {
                    throw new InvalidOperationException($"Could not clear DNS conntrack state for configured upstream {upstreamAddress} (rc={conntrackClearRc}; out={conntrackClearOut}; err={conntrackClearErr}).");
                }

                // Zero is valid: it means no established conntrack flow existed for this
                // resolver. The unique-name lookup below must still hit the nft rule.
                conntrackDnsClearResults.Add($"{upstreamAddress}:removed={removedFlowCount}");
            }

            string conntrackDnsClearSummary = string.Join(",", conntrackDnsClearResults);
            string dnsProbeCountersBefore = await RunPodNftListEgressAsync("kautuka-egress-envoy").ConfigureAwait(false);
            long dnsProbePacketsBefore = ExtractNftCommentCounterPackets(dnsProbeCountersBefore, "dns_upstream");
            var (dnsProbeRc, dnsProbeBody, dnsProbeErr) = await ExecAsync(
                "podman",
                ["exec", wlName, "curl", "--insecure", "--silent", "--show-error", "--max-time", "8", "https://" + dnsProbeHostName + ":8443/"])
                .ConfigureAwait(false);
            string dnsProbeCountersAfter = await RunPodNftListEgressAsync("kautuka-egress-envoy").ConfigureAwait(false);
            long dnsProbePacketsAfter = ExtractNftCommentCounterPackets(dnsProbeCountersAfter, "dns_upstream");
            long dnsProbeDelta = dnsProbePacketsAfter - dnsProbePacketsBefore;
            bool dnsProbeBodyReceived = dnsProbeRc == 0 && dnsProbeBody.Contains("NAC LAN fixture ok", StringComparison.Ordinal);
            if (!dnsProbeBodyReceived || dnsProbeDelta <= 0)
            {
                throw new InvalidOperationException($"The unique approved DNS probe did not exercise a counted upstream lookup (host={dnsProbeHostName}; upstreams={string.Join(",", dnsProbeUpstreamAddresses)}; conntrack={conntrackDnsClearSummary}; rc={dnsProbeRc}; body={dnsProbeBody}; err={dnsProbeErr}; dnsDelta={dnsProbeDelta}; countersBefore:\n{dnsProbeCountersBefore}\ncountersAfter:\n{dnsProbeCountersAfter}).");
            }
            Console.WriteLine($"[PROD_STAGE] unique approved DNS lookup reached LAN fixture; host={dnsProbeHostName}; delta={dnsProbeDelta}; conntrack={conntrackDnsClearSummary}");
            _ = policyStore.DeleteRule(admin, dnsProbeRule.Id, expectedRevision: policyStore.Revision);

            AcceptanceObservation parserAcceptance;
            var connectorService = new WorkspaceContainerConnectorExecutionService(
                desiredState,
                podman,
                new McpbImageMaterializer(podman, loggerFactory.CreateLogger<McpbImageMaterializer>()),
                containerTool,
                loggerFactory.CreateLogger<WorkspaceContainerConnectorExecutionService>(),
                addressAllocator: allocator,
                workloadRegistry: registry,
                postStartVerifier: verifier,
                workloadLaunchGate: new WorkloadLaunchGate(topology, policyStore));
            await using (connectorService.ConfigureAwait(false))
            {
                var internalParserCall = await connectorService.CallToolAsync(
                    internalParserConnector.ConnectorId,
                    null,
                    "network_probe",
                    "{}",
                    30,
                    allowInternalSystemConnector: true,
                    parserContext,
                    WorkspaceMcpExecutionContext.Isolated,
                    CancellationToken.None).ConfigureAwait(false);
                RegistryEntry internalParserEntry = registry.GetAll().Single(entry =>
                    string.Equals(entry.ConnectorId, internalParserConnector.ConnectorId, StringComparison.Ordinal));
                bool internalParserDenied = string.Equals(internalParserEntry.Egress, "deny-all", StringComparison.Ordinal)
                    && string.Equals(internalParserEntry.Kind, "system", StringComparison.Ordinal)
                    && string.Equals(internalParserEntry.Attribution, "none", StringComparison.Ordinal)
                    && string.IsNullOrEmpty(internalParserEntry.AgentId)
                    && string.Equals(internalParserEntry.UserId, "u1", StringComparison.Ordinal)
                    && internalParserCall.Ok
                    && !(internalParserCall.Result ?? string.Empty).Contains("Example Domain", StringComparison.OrdinalIgnoreCase)
                    && !policyStore.ListRequests(admin).Requests.Any(view => view.Request.Status == "pending"
                        && string.Equals(view.Request.ConnectorId, internalParserConnector.ConnectorId, StringComparison.Ordinal));

                var parserControlCall = await connectorService.CallToolAsync(
                    parserControlConnector.ConnectorId,
                    null,
                    "network_probe",
                    "{}",
                    30,
                    allowInternalSystemConnector: false,
                    parserControlContext,
                    WorkspaceMcpExecutionContext.Isolated,
                    CancellationToken.None).ConfigureAwait(false);
                RegistryEntry parserControlEntry = registry.GetAll().Single(entry =>
                    string.Equals(entry.ConnectorId, parserControlConnector.ConnectorId, StringComparison.Ordinal));
                bool parserControlApprovalCreated = parserControlCall.Ok
                    && string.Equals(parserControlEntry.Egress, "normal", StringComparison.Ordinal)
                    && string.Equals(parserControlEntry.Kind, "connector", StringComparison.Ordinal)
                    && string.Equals(parserControlEntry.Attribution, "user-agent", StringComparison.Ordinal)
                    && policyStore.ListRequests(admin).Requests.Any(view => view.Request.Status == "pending"
                        && string.Equals(view.Request.ConnectorId, parserControlConnector.ConnectorId, StringComparison.Ordinal)
                        && string.Equals(view.Request.Destination, "example.com", StringComparison.OrdinalIgnoreCase));
                NetworkAccessRequest? parserControlCard = policyStore.ListRequests(admin).Requests
                    .Select(static view => view.Request)
                    .FirstOrDefault(request => request.Status == "pending"
                        && string.Equals(request.ConnectorId, parserControlConnector.ConnectorId, StringComparison.Ordinal));
                if (parserControlCard is not null)
                {
                    _ = policyStore.DecideRequest(admin, parserControlCard.Id, allow: false, scope: "agent", expectedRevision: policyStore.Revision, allowLan: false);
                }
                string parserObservation = $"egress={internalParserEntry.Egress}:denyAll={internalParserDenied}:kindSystem={string.Equals(internalParserEntry.Kind, "system", StringComparison.Ordinal)}:noApproval={policyStore.ListRequests(admin).Requests.All(view => !string.Equals(view.Request.ConnectorId, internalParserConnector.ConnectorId, StringComparison.Ordinal))}";
                string parserNcObservation = $"egress={parserControlEntry.Egress}:normal={string.Equals(parserControlEntry.Egress, "normal", StringComparison.Ordinal)}:kindConnector={string.Equals(parserControlEntry.Kind, "connector", StringComparison.Ordinal)}:approvalCreated={parserControlApprovalCreated}";
                parserAcceptance = AcceptanceHarness.Record(
                    "PROD_E_PARSER",
                    parserObservation,
                    "egress=deny-all:denyAll=True:kindSystem=True:noApproval=True",
                    parserNcObservation,
                    "egress=normal:normal=True:kindConnector=True:approvalCreated=True");
            }
            Console.WriteLine("[PROD_STAGE] parser attribution and approval checks completed");

            _ = policyStore.SetRule(
                admin,
                new RuleWriteSpec("agent", "u1", "rebind", null, rebindHostName, "host", 8443, Allowed: true, AllowLan: false),
                expectedRevision: policyStore.Revision);
            OciSandboxSession rebindSession = await containerTool.SpawnSandboxAsync(rebindContext, CancellationToken.None).ConfigureAwait(false);
            string rebindName = rebindSession.ContainerName;
            RegistryEntry rebindEntry = registry.GetAll().Single(entry => string.Equals(entry.AgentId, "rebind", StringComparison.Ordinal));
            string rebindCountersBefore = await RunPodNftListEgressAsync("kautuka-egress-envoy").ConfigureAwait(false);
            long rebindRejectBefore = ExtractNftCommentCounterPackets(rebindCountersBefore, "private_reject");
            var (rebindRc, _, rebindErr) = await ExecAsync(
                "podman",
                ["exec", rebindName, "curl", "--resolve", rebindHostName + ":8443:93.184.216.34", "--insecure", "--silent", "--show-error", "--max-time", "8", "https://" + rebindHostName + ":8443/"]).ConfigureAwait(false);
            string rebindCountersAfter = await RunPodNftListEgressAsync("kautuka-egress-envoy").ConfigureAwait(false);
            long rebindRejectDelta = ExtractNftCommentCounterPackets(rebindCountersAfter, "private_reject") - rebindRejectBefore;

            AcceptanceObservation? rebindAcceptance = null;
            if (rebindErr.Length > 0 && rebindRc == 0)
            {
                await Console.Error.WriteLineAsync($"E-rebind curl details: curlErr={rebindErr}").ConfigureAwait(false);
            }
            OciSandboxSession lanSession = await containerTool.SpawnSandboxAsync(lanContext, CancellationToken.None).ConfigureAwait(false);
            string lanWorkloadName = lanSession.ContainerName;
            int lanWorkloadPid = await InspectContainerPidAsync(lanWorkloadName).ConfigureAwait(false);
            var (lanWorkloadAddressRc, lanWorkloadAddresses, lanWorkloadAddressErr) = await ExecAsync(
                "podman",
                ["unshare", "nsenter", "-t", lanWorkloadPid.ToString(CultureInfo.InvariantCulture), "-n", "ip", "-o", "-4", "address"])
                .ConfigureAwait(false);
            if (lanWorkloadAddressRc != 0
                || Regex.IsMatch(lanWorkloadAddresses, @"\b10\.248\.0\.[23]/", RegexOptions.CultureInvariant))
            {
                throw new InvalidOperationException($"The LAN acceptance workload NICs must not contain egress addresses 10.248.0.2 or 10.248.0.3 (rc={lanWorkloadAddressRc}; addresses={lanWorkloadAddresses}; err={lanWorkloadAddressErr}).");
            }
            _ = registry.GetAll().Single(entry => string.Equals(entry.AgentId, "lan-grant", StringComparison.Ordinal));
            string lanCountersBefore = await RunPodNftListEgressAsync("kautuka-egress-envoy").ConfigureAwait(false);
            long lanPrivateRejectBeforeNoGrant = ExtractNftCommentCounterPackets(lanCountersBefore, "private_reject");
            long lanApprovedBefore = ExtractNftCommentCounterPackets(lanCountersBefore, "lan_approved");
            long lanMasqueradeBefore = ExtractNftCommentCounterPackets(lanCountersBefore, "lan_source_masquerade");
            int lanFixtureHitsBeforeNoGrant = File.Exists(lanAccessLogPath) ? (await File.ReadAllLinesAsync(lanAccessLogPath).ConfigureAwait(false)).Length : 0;
            var (lanDeniedRc, _, _) = await ExecAsync(
                "podman",
                ["exec", lanWorkloadName, "curl", "--insecure", "--silent", "--show-error", "--max-time", "8", "https://" + lanHostName + ":8443/"]).ConfigureAwait(false);
            string noGrantEgressCounters = await RunPodNftListEgressAsync("kautuka-egress-envoy").ConfigureAwait(false);
            long noGrantPrivateRejectDelta = ExtractNftCommentCounterPackets(noGrantEgressCounters, "private_reject") - lanPrivateRejectBeforeNoGrant;
            long noGrantMasqueradeDelta = ExtractNftCommentCounterPackets(noGrantEgressCounters, "lan_source_masquerade") - lanMasqueradeBefore;
            int lanFixtureHitsAfterNoGrant = File.Exists(lanAccessLogPath) ? (await File.ReadAllLinesAsync(lanAccessLogPath).ConfigureAwait(false)).Length : 0;
            string noGrantLanObservation = $"blocked={lanDeniedRc != 0}:privateRejectPacketDeltaZero={noGrantPrivateRejectDelta == 0}:lanMasqueradeRulePacketDeltaZero={noGrantMasqueradeDelta == 0}:fixtureHitsZero={lanFixtureHitsAfterNoGrant == lanFixtureHitsBeforeNoGrant}";
            await policyStore.FlushPendingWritesAsync().ConfigureAwait(false);
            NetworkAccessRequest? lanCard = policyStore.ListRequests(admin).Requests
                .Select(static view => view.Request)
                .FirstOrDefault(request => request.Status == "pending"
                    && string.Equals(request.UserId, "u1", StringComparison.Ordinal)
                    && string.Equals(request.AgentId, "lan-grant", StringComparison.Ordinal)
                    && string.Equals(request.Destination, lanHostName, StringComparison.OrdinalIgnoreCase)
                    && request.Port == 8443);
            bool lanCardCreated = lanDeniedRc != 0 && lanCard is not null;
            NetworkAccessRule? lanRule = null;
            bool lanBodyReceived = false;
            int lanAllowedRc = 1;
            int lanFixtureHitsAfterApproval = 0;
            long lanApprovedDelta = 0;
            long lanMasqueradeDelta = 0;
            if (lanCard is not null)
            {
                DecideRequestResult lanDecision = policyStore.DecideRequest(
                    admin,
                    lanCard.Id,
                    allow: true,
                    scope: "agent",
                    allowLan: true,
                    expectedRevision: policyStore.Revision);
                lanRule = lanDecision.Rule;
                var (allowedRc, lanAllowedOut, lanAllowedErr) = await ExecAsync(
                    "podman",
                    ["exec", lanWorkloadName, "curl", "--verbose", "--insecure", "--silent", "--show-error", "--max-time", "8", "https://" + lanHostName + ":8443/"]).ConfigureAwait(false);
                lanAllowedRc = allowedRc;
                lanBodyReceived = lanAllowedRc == 0 && lanAllowedOut.Contains("NAC LAN fixture ok", StringComparison.Ordinal);
                lanFixtureHitsAfterApproval = (File.Exists(lanAccessLogPath) ? (await File.ReadAllLinesAsync(lanAccessLogPath).ConfigureAwait(false)).Length : 0) - lanFixtureHitsBeforeNoGrant;
                string lanCountersAfter = await RunPodNftListEgressAsync("kautuka-egress-envoy").ConfigureAwait(false);
                lanApprovedDelta = ExtractNftCommentCounterPackets(lanCountersAfter, "lan_approved") - lanApprovedBefore;
                lanMasqueradeDelta = ExtractNftCommentCounterPackets(lanCountersAfter, "lan_source_masquerade") - lanMasqueradeBefore;
                if (lanAllowedRc != 0)
                {
                    string envoyLogs = (await ExecAsync("podman", ["logs", "kautuka-egress-envoy"]).ConfigureAwait(false)).StdOut;
                    string lanFixtureAccessLog = await File.ReadAllTextAsync(lanAccessLogPath).ConfigureAwait(false);
                    int envoyFailurePid = await InspectContainerPidAsync("kautuka-egress-envoy").ConfigureAwait(false);
                    var (envoyRouteRc, envoyRoutes, envoyRouteErr) = await ExecAsync("podman", ["unshare", "nsenter", "-t", envoyFailurePid.ToString(CultureInfo.InvariantCulture), "-n", "ip", "-4", "route"]).ConfigureAwait(false);
                    var (envoyAddressRc, envoyAddresses, envoyAddressErr) = await ExecAsync("podman", ["unshare", "nsenter", "-t", envoyFailurePid.ToString(CultureInfo.InvariantCulture), "-n", "ip", "-4", "address"]).ConfigureAwait(false);
                    var (envoyNeighborRc, envoyNeighbors, envoyNeighborErr) = await ExecAsync("podman", ["unshare", "nsenter", "-t", envoyFailurePid.ToString(CultureInfo.InvariantCulture), "-n", "ip", "-4", "neighbor"]).ConfigureAwait(false);
                    string lanFixtureState = lanFixtureProcess.HasExited ? $"exited:{lanFixtureProcess.ExitCode}" : "running";
                    await Console.Error.WriteLineAsync($"LAN-grant diagnostics: serverState={lanFixtureState}; serverAccess={lanFixtureAccessLog}; envoy={envoyLogs}; envoyPid={envoyFailurePid}; envoyRouteRc={envoyRouteRc}; envoyRoutes={envoyRoutes}; envoyRouteErr={envoyRouteErr}; envoyAddressRc={envoyAddressRc}; envoyAddresses={envoyAddresses}; envoyAddressErr={envoyAddressErr}; envoyNeighborRc={envoyNeighborRc}; envoyNeighbors={envoyNeighbors}; envoyNeighborErr={envoyNeighborErr}; fixture={lanHostAddress}; namespace={lanFixtureNamespaceName}; outsideDevice={outsideNetworkDevice}; curlOut={lanAllowedOut}; curlErr={lanAllowedErr}; lanApprovedDelta={lanApprovedDelta}; lanMasqueradeDelta={lanMasqueradeDelta}; nft={lanCountersAfter}").ConfigureAwait(false);
                    await Console.Error.WriteLineAsync($"LAN-grant retry failed: {lanAllowedErr}").ConfigureAwait(false);
                }
            }
            string lanObservation = $"blocked={lanAllowedRc != 0}:beforeApprovalBlocked={lanDeniedRc != 0}:cardCreated={lanCardCreated}:allowLanRule={lanRule?.AllowLan == true}:lanApprovedPacketDeltaGt0={lanApprovedDelta > 0}:lanMasqueradePacketDeltaGt0={lanMasqueradeDelta > 0}:body={lanBodyReceived}";
            string lanNcObservation = $"blocked={lanDeniedRc != 0}:beforeApprovalBlocked={lanDeniedRc != 0}:cardCreated={lanCardCreated}";
            AcceptanceObservation lanAcceptance = AcceptanceHarness.Record(
                "PROD_LAN_GRANT",
                lanObservation,
                "blocked=False:beforeApprovalBlocked=True:cardCreated=True:allowLanRule=True:lanApprovedPacketDeltaGt0=True:lanMasqueradePacketDeltaGt0=True:body=True",
                lanNcObservation,
                "blocked=True:beforeApprovalBlocked=True:cardCreated=True");
            AcceptanceObservation noGrantLanAcceptance = AcceptanceHarness.Record(
                "PROD_LAN_NO_GRANT",
                noGrantLanObservation,
                "blocked=True:privateRejectPacketDeltaZero=True:lanMasqueradeRulePacketDeltaZero=True:fixtureHitsZero=True",
                $"blocked={lanAllowedRc != 0}:lanMasqueradeRulePacketDeltaZero={lanMasqueradeDelta == 0}:fixtureHitsZero={lanFixtureHitsAfterApproval == 0}",
                "blocked=False:lanMasqueradeRulePacketDeltaZero=False:fixtureHitsZero=False");
            Console.WriteLine($"[PROD_LAN_NO_GRANT] {noGrantLanAcceptance.Outcome} ({noGrantLanAcceptance.ObservedValue}; nc={noGrantLanAcceptance.NcObservedValue})");
            Console.WriteLine("[PROD_STAGE] approved LAN traffic check completed");

            // v7-28: ReconcileOnceAsync preserves running container (even if previously registered by name, now promoted to hex ID)
            await topology.ReconcileOnceAsync(registry, CancellationToken.None).ConfigureAwait(false);
            bool stillRegisteredWhileRunning = registry.TryGetByIp(wlIp, out RegistryEntry? afterReconcileEntry);
            if (!topology.IsStartupGateOpen)
            {
                var (podInspectRc, podInspectOut, podInspectErr) = await ExecAsync("podman", ["pod", "inspect", EgressTopologyManager.EgressPodName, "--format", "json"]).ConfigureAwait(false);
                var (boundaryInspectRc, boundaryInspectOut, boundaryInspectErr) = await ExecAsync("podman", ["inspect", "kautuka-egress-boundary", "--format", "json"]).ConfigureAwait(false);
                await Console.Error.WriteLineAsync($"NAC topology health diagnostics: pod rc={podInspectRc} out={podInspectOut} err={podInspectErr}; boundary rc={boundaryInspectRc} out={boundaryInspectOut} err={boundaryInspectErr}").ConfigureAwait(false);
            }

            // Production flow Step A: Denied request -> Envoy asks Manager -> pending card created!
            var (denyRc, denyOut, denyErr) = await ExecAsync("podman", ["exec", wlName, "curl", "-v", "--max-time", "8", "https://example.com/"]).ConfigureAwait(false);
            string deniedOutput = denyRc == 0 ? $"ALLOWED:{denyOut.Trim()}" : "BLOCKED";
            await policyStore.FlushPendingWritesAsync().ConfigureAwait(false);

            NetworkAccessRequest? card = policyStore.ListRequests(admin).Requests
                .Select(static v => v.Request)
                .FirstOrDefault(r => r.Status == "pending"
                    && string.Equals(r.AgentId, "a1", StringComparison.Ordinal)
                    && string.Equals(r.Destination, "example.com", StringComparison.OrdinalIgnoreCase));
            if (card is null)
            {
                var (_, psOut, _) = await ExecAsync("podman", ["ps", "-a", "--format", "{{.Names}} {{.Status}}"]).ConfigureAwait(false);
                var (_, envState, _) = await ExecAsync("podman", ["inspect", "kautuka-egress-envoy", "--format", "State={{json .State}} Cmd={{json .Config.Cmd}}"]).ConfigureAwait(false);
                var (_, envLogs, envErr) = await ExecAsync("podman", ["logs", "kautuka-egress-envoy"]).ConfigureAwait(false);
                var (_, dnsLogs, dnsErr) = await ExecAsync("podman", ["logs", "kautuka-egress-dns"]).ConfigureAwait(false);
                string hookLogTail = File.Exists(Path.Combine(nacStateDir, "hook.log")) ? await File.ReadAllTextAsync(Path.Combine(nacStateDir, "hook.log")).ConfigureAwait(false) : "<no hook.log>";
                throw new InvalidOperationException($"Expected pending approval card for 'example.com' was not recorded by Manager. denyRc={denyRc} denyOut={denyOut} denyErr={denyErr}\nps:\n{psOut}\nenvoyInspect:{envState}\nenvoy:{envLogs}\nenvoyErr:{envErr}\ndns:{dnsLogs}\ndnsErr:{dnsErr}\nhook.log:\n{hookLogTail}");
            }

            // Production flow Step B: Decide card -> rule created -> retry through Envoy + CoreDNS upstream succeeds!
            DecideRequestResult decision = policyStore.DecideRequest(admin, card.Id, allow: true, scope: "agent", allowLan: false, expectedRevision: policyStore.Revision);
            NetworkAccessRule allowedRule = decision.Rule
                ?? throw new InvalidOperationException("Allow decision did not return the created network rule.");
            NetworkAccessDecisionResult directAllowed = controller.EvaluateContainerConnection(
                wlIp,
                "198.18.250.250",
                443,
                isTls: true,
                sni: "example.com");
            if (!directAllowed.Allowed)
            {
                throw new InvalidOperationException($"The production registry and saved allow rule do not agree: {directAllowed.Reason}.");
            }
            var (allowRc, allowOut, allowErr) = await ExecAsync("podman", ["exec", wlName, "curl", "-v", "--max-time", "12", "https://example.com/"]).ConfigureAwait(false);
            int allowAttempts = 1;
            while (allowRc != 0 && allowAttempts < 3)
            {
                allowAttempts++;
                (allowRc, allowOut, allowErr) = await ExecAsync(
                    "podman",
                    ["exec", wlName, "curl", "-v", "--max-time", "12", "https://example.com/"])
                    .ConfigureAwait(false);
            }
            if (allowRc != 0)
            {
                var (_, envoyInspect, envoyInspectErr) = await ExecAsync("podman", ["inspect", "kautuka-egress-envoy", "--format", "{{json .State}}|{{json .HostConfig.Binds}}|{{json .Mounts}}"]).ConfigureAwait(false);
                var (_, podInspect, podInspectErr) = await ExecAsync("podman", ["pod", "inspect", EgressTopologyManager.EgressPodName, "--format", "{{json .State}}|{{json .Containers}}"]).ConfigureAwait(false);
                var (_, envLogs, envLogErr) = await ExecAsync("podman", ["logs", "kautuka-egress-envoy"]).ConfigureAwait(false);
                var (_, dnsLogs, dnsLogErr) = await ExecAsync("podman", ["logs", "kautuka-egress-dns"]).ConfigureAwait(false);
                var (_, dnsResolv, _) = await ExecAsync("podman", ["exec", "kautuka-egress-dns", "cat", "/etc/resolv.conf"]).ConfigureAwait(false);
                var (dnsResolvPathRc, dnsResolvPath, dnsResolvPathErr) = await ExecAsync("podman", ["inspect", "kautuka-egress-dns", "--format", "{{.ResolvConfPath}}"]).ConfigureAwait(false);
                string dnsResolvHostContents = dnsResolvPathRc == 0 && File.Exists(dnsResolvPath.Trim())
                    ? await File.ReadAllTextAsync(dnsResolvPath.Trim()).ConfigureAwait(false)
                    : $"<unavailable: rc={dnsResolvPathRc} {dnsResolvPathErr}>";
                var (adminStatsRc, adminStats, adminStatsErr) = await ExecAsync(
                    "podman",
                    ["unshare", "curl", "--silent", "--show-error", "--max-time", "3", "--unix-socket", Path.Combine(stateDir, "admin-socket", "admin.sock"), "http://localhost/stats?format=json"])
                    .ConfigureAwait(false);
                int failedEnvoyPid = await InspectContainerPidAsync("kautuka-egress-envoy").ConfigureAwait(false);
                var (routeRc, routeOut, routeErr) = await ExecAsync(
                    "podman",
                    ["unshare", "nsenter", "-t", failedEnvoyPid.ToString(CultureInfo.InvariantCulture), "-n", "ip", "route"])
                    .ConfigureAwait(false);
                string nftAfter = await RunPodNftListEgressAsync("kautuka-egress-envoy").ConfigureAwait(false);
                throw new InvalidOperationException($"Retry after Allow failed after {allowAttempts} requests (rc {allowRc}): {allowErr}\nunique DNS probe {dnsProbeHostName} conntrack={conntrackDnsClearSummary} countersBefore:\n{dnsProbeCountersBefore}\ncountersAfter:\n{dnsProbeCountersAfter}\nadmin stats rc={adminStatsRc}: {adminStats}{adminStatsErr}\negress pod route rc={routeRc}: {routeOut}{routeErr}\npod state: {podInspect}{podInspectErr}\nenvoy state/mounts: {envoyInspect}{envoyInspectErr}\nenvoyLogs:\n{envLogs}{envLogErr}\ndnsLogs:\n{dnsLogs}{dnsLogErr}\nnft:\n{nftAfter}");
            }
            string allowedBody = allowOut;
            bool hasExampleDomain = allowedBody.Contains("Example Domain", StringComparison.OrdinalIgnoreCase);

            string allowedObs = $"ALLOWED:card={card.Destination}:{card.Port}:rule={allowedRule.Destination}:body={hasExampleDomain}";

            // Run real tampering negative control for v7-29 verifier (C6)
            string wl2Workspace = Path.Combine(workspaceRoot, "users", "u1", "agents", "a2");
            var wl2Context = wlContext with { ScopeId = "a2", HostWorkspacePath = wl2Workspace };
            _ = desiredState.ApplyDesiredState(
                2,
                WorkspaceDesiredStateSyncReason.Bootstrap,
                new ManagerRuntimeSettings(
                    new WorkspaceDefinition { BaseRoot = workspaceRoot },
                    new SandboxRuntimeDefinition { Image = baseImage, DefaultTimeout = 30 },
                    new WorkspaceTelemetryDefinition()),
                [wlContext, wl2Context], [], []);
            OciSandboxSession workloadNcSession = await containerTool.SpawnSandboxAsync(wl2Context, CancellationToken.None).ConfigureAwait(false);
            string wl2Name = workloadNcSession.ContainerName;
            string wl2Ip = registry.GetAll().Single(entry =>
                string.Equals(entry.UserId, "u1", StringComparison.Ordinal)
                && string.Equals(entry.AgentId, "a2", StringComparison.Ordinal)
                && string.Equals(entry.Kind, "agent-sandbox", StringComparison.Ordinal)).Ip;
            int pid2 = await InspectContainerPidAsync(wl2Name).ConfigureAwait(false);
            await RunBashAsync($"podman unshare nsenter -t {pid2} -n nft add rule inet kautuka_workload output accept").ConfigureAwait(false);
            bool verifierCaughtTamper = false;
            try
            {
                await verifier.VerifyWorkloadAsync(wl2Name, wl2Ip, null, registry, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                verifierCaughtTamper = true;
            }

            // v7-28 NC: Stop & remove the original workload and reconcile it before the
            // final DNS mutation closes the startup gate.
            await ExecPodmanAsync(["rm", "-f", wlName]).ConfigureAwait(false);
            await topology.ReconcileOnceAsync(registry, CancellationToken.None).ConfigureAwait(false);
            bool deregisteredAfterRm = !registry.TryGetByIp(wlIp, out _);

            var observations = AcceptanceHarness.ProductionObservations(
                packageProxyObservations,
                parserAcceptance,
                lanAcceptance,
                noGrantLanAcceptance,
                AcceptanceHarness.Record("PROD_CARD_AND_RULE", allowedObs, "ALLOWED:card=example.com:443:rule=example.com:body=True", deniedOutput, "BLOCKED"),
                AcceptanceHarness.Record("PROD_V7_28_RECONCILE", $"{stillRegisteredWhileRunning}:{afterReconcileEntry?.ContainerId?.Length == 64}", "True:True", deregisteredAfterRm ? "DEREGISTERED_ON_RM" : "STILL_PRESENT", "DEREGISTERED_ON_RM"),
                AcceptanceHarness.Record("PROD_V7_29_VERIFIER", verResult.Passed.ToString(), "True", verifierCaughtTamper ? "TAMPER_CAUGHT_AND_KILLED" : "TAMPER_MISSED", "TAMPER_CAUGHT_AND_KILLED")).ToList();

            // Build a fresh production workload for the final DNS negative control. This
            // keeps the rule deletion last while still exercising DNS from a live workload.
            string wl3Workspace = Path.Combine(workspaceRoot, "users", "u1", "agents", "a3");
            var wl3Context = wlContext with { ScopeId = "a3", HostWorkspacePath = wl3Workspace };
            string dnsNcHost = $"nac-{Guid.NewGuid():N}.iana.org";
            _ = desiredState.ApplyDesiredState(
                3,
                WorkspaceDesiredStateSyncReason.Bootstrap,
                new ManagerRuntimeSettings(
                    new WorkspaceDefinition { BaseRoot = workspaceRoot },
                    new SandboxRuntimeDefinition { Image = baseImage, DefaultTimeout = 30 },
                    new WorkspaceTelemetryDefinition()),
                [wl3Context], [], []);
            OciSandboxSession dnsWorkloadSession = await containerTool.SpawnSandboxAsync(wl3Context, CancellationToken.None).ConfigureAwait(false);
            string dnsWorkloadName = dnsWorkloadSession.ContainerName;
            _ = policyStore.SetRule(
                admin,
                new RuleWriteSpec("agent", "u1", "a3", null, dnsNcHost, "host", 443, Allowed: true, AllowLan: false),
                expectedRevision: policyStore.Revision);
            bool startupGateOpenBeforeFinalNegativeControl = topology.IsStartupGateOpen;

            await RunProductionUserBrakeProofAsync(
                workspaceRoot,
                wlContext,
                baseImage,
                containerTool,
                controller,
                topology,
                desiredState,
                wl3Context,
                brakeNcReportPath,
                observations).ConfigureAwait(false);

            string currentCorefile = await File.ReadAllTextAsync(corefilePath).ConfigureAwait(false);
            if (!string.Equals(currentCorefile, corefile, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The acceptance harness changed the production CoreDNS configuration after topology startup.");
            }

            if (!await IsContainerRunningAsync(rebindName).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The existing rebind workload exited before the isolated Envoy filter negative control.");
            }

            if (!startupGateOpenBeforeFinalNegativeControl)
            {
                throw new InvalidOperationException("The NAC startup gate closed before the isolated Envoy filter negative control.");
            }

            // The Envoy filter negative control deliberately edits the rendered file. Stop
            // the Manager reconcile lifecycle only after all launches and checks have
            // completed. The acceptance auth host retains the controller, but its gate
            // is explicitly bypassed for this isolated packet-path control only. Gate-denial behavior is
            // covered separately by NetworkAccessTopologyGateTests.
            await topology.DisposeAsync().ConfigureAwait(false);
            Volatile.Write(ref packetPathOnlyGateOverride, 1);
            string envoyConfigPath = Path.Combine(stateDir, "nac", "envoy", "envoy.yaml");
            string originalEnvoyConfig = await File.ReadAllTextAsync(envoyConfigPath).ConfigureAwait(false);
            const string removePrivateLanFilterRangePattern = @"(?m)^(?<i>[ \t]*)- address_prefix: 192\.168\.0\.0\r?\n\k<i>  prefix_len: 16\r?\n";
            int privateLanRangeOccurrences = Regex.Count(originalEnvoyConfig, removePrivateLanFilterRangePattern, RegexOptions.CultureInvariant);
            if (privateLanRangeOccurrences != 2)
            {
                throw new InvalidOperationException($"The production Envoy config contained {privateLanRangeOccurrences} 192.168.0.0/16 private-address filter ranges; expected 2 for the negative control.");
            }

            string mutatedEnvoyConfig = Regex.Replace(originalEnvoyConfig, removePrivateLanFilterRangePattern, string.Empty, RegexOptions.CultureInvariant);
            await File.WriteAllTextAsync(envoyConfigPath, mutatedEnvoyConfig).ConfigureAwait(false);
            try
            {
                var (envoyRestartRc, _, envoyRestartErr) = await ExecAsync("podman", ["restart", "kautuka-egress-envoy"]).ConfigureAwait(false);
                if (envoyRestartRc != 0)
                {
                    throw new InvalidOperationException($"Could not restart Envoy for the resolved-address-filter negative control (rc={envoyRestartRc}; {envoyRestartErr}).");
                }
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                string rebindNcCountersBefore = await RunPodNftListEgressAsync("kautuka-egress-envoy").ConfigureAwait(false);
                long rebindNcRejectBefore = ExtractNftCommentCounterPackets(rebindNcCountersBefore, "private_reject");
                long rebindNcMasqueradeBefore = ExtractNftCommentCounterPackets(rebindNcCountersBefore, "lan_source_masquerade");
                int rebindNcFixtureHitsBefore = File.Exists(lanAccessLogPath)
                    ? (await File.ReadAllLinesAsync(lanAccessLogPath).ConfigureAwait(false)).Length
                    : 0;
                var (rebindNcRc, _, _) = await ExecAsync(
                    "podman",
                    ["exec", rebindName, "curl", "--resolve", rebindHostName + ":8443:93.184.216.34", "--insecure", "--silent", "--show-error", "--max-time", "8", "https://" + rebindHostName + ":8443/"]).ConfigureAwait(false);
                string rebindNcCountersAfter = await RunPodNftListEgressAsync("kautuka-egress-envoy").ConfigureAwait(false);
                long rebindNcRejectDelta = ExtractNftCommentCounterPackets(rebindNcCountersAfter, "private_reject") - rebindNcRejectBefore;
                long rebindNcMasqueradeDelta = ExtractNftCommentCounterPackets(rebindNcCountersAfter, "lan_source_masquerade") - rebindNcMasqueradeBefore;
                int rebindNcFixtureHitsAfter = File.Exists(lanAccessLogPath)
                    ? (await File.ReadAllLinesAsync(lanAccessLogPath).ConfigureAwait(false)).Length
                    : 0;
                string rebindObservation = $"workloadTargetPublic=True:blocked={rebindRc != 0}:privateRejectDeltaZero={rebindRejectDelta == 0}";
                string rebindNcObservation = $"envoyPrivateFilterRemoved=True:allowLanGrant=False:blocked={rebindNcRc != 0}:privateRejectDeltaZero={rebindNcRejectDelta == 0}:privateRejectDeltaGt0={rebindNcRejectDelta > 0}:lanMasqueradeRulePacketDeltaZero={rebindNcMasqueradeDelta == 0}:fixtureHitsZero={rebindNcFixtureHitsAfter == rebindNcFixtureHitsBefore}:authorizationGateBypassed={Volatile.Read(ref packetPathOnlyGateOverride) == 1}:packetPathOnly=True";
                rebindAcceptance = AcceptanceHarness.Record(
                    "PROD_E_REBIND",
                    rebindObservation,
                    "workloadTargetPublic=True:blocked=True:privateRejectDeltaZero=True",
                    rebindNcObservation,
                    "envoyPrivateFilterRemoved=True:allowLanGrant=False:blocked=True:privateRejectDeltaZero=False:privateRejectDeltaGt0=True:lanMasqueradeRulePacketDeltaZero=True:fixtureHitsZero=True:authorizationGateBypassed=True:packetPathOnly=True");
            }
            finally
            {
                await File.WriteAllTextAsync(envoyConfigPath, originalEnvoyConfig).ConfigureAwait(false);
                _ = await ExecAsync("podman", ["restart", "kautuka-egress-envoy"]).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            observations.Add(rebindAcceptance ?? throw new InvalidOperationException("The E-rebind production acceptance observation was not recorded."));
            Console.WriteLine("[PROD_STAGE] resolved-address rebinding check completed");

            // v7-33 negative control runs after the packet-path control above, so refresh
            // both its rule handle and counter baseline from the live ruleset now.
            string dnsNcCountersBefore = await RunPodNftListEgressAsync("kautuka-egress-envoy").ConfigureAwait(false);
            long dnsUpstreamHandle = ExtractNftCommentRuleHandle(dnsNcCountersBefore, "dns_upstream");
            if (dnsUpstreamHandle == 0)
            {
                throw new InvalidOperationException("The live egress namespace has no dns_upstream rule handle to remove for the negative control.");
            }
            long privateRejectBefore = ExtractNftCommentCounterPackets(dnsNcCountersBefore, "private_reject");
            int envoyPid = await InspectContainerPidAsync("kautuka-egress-envoy").ConfigureAwait(false);
            await RunBashAsync(
                $"podman unshare nsenter -t {envoyPid} -n nft delete rule ip kautuka_egress output handle {dnsUpstreamHandle.ToString(CultureInfo.InvariantCulture)}")
                .ConfigureAwait(false);
            var (conntrackRc, conntrackOut, conntrackErr) = await ExecAsync(
                "podman",
                ["unshare", "nsenter", "-t", envoyPid.ToString(CultureInfo.InvariantCulture), "-n", "conntrack", "-D", "-p", "udp", "--dst", "10.248.0.1", "--dport", "53"])
                .ConfigureAwait(false);
            Match conntrackDeletedMatch = Regex.Match(
                conntrackOut + "\n" + conntrackErr,
                @"(?<count>\d+)\s+flow entries? have been deleted",
                RegexOptions.CultureInvariant);
            bool conntrackEntryRemoved = conntrackRc == 0
                && long.TryParse(conntrackDeletedMatch.Groups["count"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long conntrackDeletedCount)
                && conntrackDeletedCount > 0;
            if (conntrackRc != 0 || !conntrackEntryRemoved)
            {
                throw new InvalidOperationException($"DNS negative control could not clear an established resolver connection (rc={conntrackRc}, out={conntrackOut}, err={conntrackErr}).");
            }
            var (dnsNcRc, _, _) = await ExecAsync(
                "podman",
                ["exec", dnsWorkloadName, "curl", "--silent", "--show-error", "--max-time", "12", $"https://{dnsNcHost}/"])
                .ConfigureAwait(false);
            string dnsCountersAfterNc = await RunPodNftListEgressAsync("kautuka-egress-envoy").ConfigureAwait(false);
            bool dnsRuleRemoved = ExtractNftCommentRuleHandle(dnsCountersAfterNc, "dns_upstream") == 0;
            bool dnsNcBlocked = dnsNcRc != 0;
            long privateRejectDelta = ExtractNftCommentCounterPackets(dnsCountersAfterNc, "private_reject") - privateRejectBefore;
            string dnsNcObservation = $"rule={!dnsRuleRemoved}:ruleRemoved={dnsRuleRemoved}:conntrackCleared={conntrackEntryRemoved}:lookupBlocked={dnsNcBlocked}:privateRejectDelta_gt0={privateRejectDelta > 0}";
            observations.Add(AcceptanceHarness.Record(
                "PROD_V7_33_DNS_UPSTREAM",
                $"rule={!string.IsNullOrEmpty(dnsUpstreamLine)}:uniqueLookup=True:delta_gt0={dnsProbeDelta > 0}:body={hasExampleDomain}",
                "rule=True:uniqueLookup=True:delta_gt0=True:body=True",
                dnsNcObservation,
                "rule=False:ruleRemoved=True:conntrackCleared=True:lookupBlocked=True:privateRejectDelta_gt0=True"));

            var (_, podmanVer, _) = await ExecAsync("podman", ["--version"]).ConfigureAwait(false);
            var (_, kernelVer, _) = await ExecAsync("uname", ["-r"]).ConfigureAwait(false);
            var (_, envoyVer, _) = await ExecAsync("podman", ["run", "--rm", EgressTopologyManager.EnvoyPinnedImage, "--version"]).ConfigureAwait(false);
            var (_, corednsVer, _) = await ExecAsync("podman", ["run", "--rm", EgressTopologyManager.CoreDnsPinnedImage, "-version"]).ConfigureAwait(false);
            var (gitExitCode, gitSha, gitErr) = await ExecAsync(
                "git",
                ["-C", repoRoot, "-c", $"safe.directory={repoRoot}", "rev-parse", "--short", "HEAD"]).ConfigureAwait(false);
            if (gitExitCode != 0 || string.IsNullOrWhiteSpace(gitSha))
            {
                throw new InvalidOperationException($"Production proof could not record the source revision: {gitErr}");
            }

            int failedCount = observations.Count(o => o.Outcome != "PASS");
            var report = new ProductionProofReport(
                DateTimeOffset.UtcNow,
                gitSha.Trim(),
                Environment.OSVersion.VersionString,
                podmanVer.Trim(),
                envoyVer.Trim().Split('\n')[0].Trim(),
                corednsVer.Trim().Replace('\n', ' '),
                kernelVer.Trim(),
                startupGateOpenBeforeFinalNegativeControl,
                verResult.Passed,
                dnsUpstreamLine,
                card.Id,
                $"{card.Destination}:{card.Port}",
                decision.Rule.Id,
                $"{decision.Rule.Destination}:{decision.Rule.Port}",
                egressDefaultRoutes,
                observations,
                $"{observations.Count - failedCount}/{observations.Count} PASS, {failedCount} FAIL");

            foreach (AcceptanceObservation observation in observations)
            {
                Console.WriteLine(
                    $"[ACCEPTANCE] {observation.TestId} outcome={observation.Outcome}; observed={observation.ObservedValue}; expected={observation.ExpectedValue}; ncObserved={observation.NcObservedValue}; ncExpected={observation.NcExpectedValue}; ncFlipped={observation.NcFlipped}");
            }

            string targetReport = string.IsNullOrWhiteSpace(outputReportPath)
                ? Path.Combine(tempRoot, "nac-prod-report.json")
                : Path.GetFullPath(outputReportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetReport)!);
            await File.WriteAllTextAsync(targetReport, JsonSerializer.Serialize(report, AcceptanceJsonContext.Default.ProductionProofReport)).ConfigureAwait(false);
            Console.WriteLine($"Production rootless Podman proof report written to {targetReport}");
            Console.WriteLine($"SUMMARY: {report.Summary}");
            return failedCount == 0 ? 0 : 1;
        }
        finally
        {
            await topology.DisposeAsync().ConfigureAwait(false);
            if (isolationCleanupGate.IsVerified)
            {
                foreach (RegistryEntry entry in registry.GetAll())
                {
                    _ = await ExecAsync("podman", ["rm", "-f", entry.ContainerId]).ConfigureAwait(false);
                }
                await ExecAsync("podman", ["rm", "-f", wlName, PodmanContainerTool.BuildContainerName(new ContainerContext(
                    "agent", "a2", "u1", Path.Combine(workspaceRoot, "users", "u1", "agents", "a2"), "/workspace",
                    "ghcr.io/devkrity/kautuka-base@sha256:675400832f2ced5cf802db66eb067ef5022f65f0551c665d87ae7edc58dc0839",
                    1, "512m", 30, [], new Dictionary<string, string>())), PodmanContainerTool.BuildContainerName(new ContainerContext(
                    "agent", "a3", "u1", Path.Combine(workspaceRoot, "users", "u1", "agents", "a3"), "/workspace",
                    "ghcr.io/devkrity/kautuka-base@sha256:675400832f2ced5cf802db66eb067ef5022f65f0551c665d87ae7edc58dc0839",
                    1, "512m", 30, [], new Dictionary<string, string>()))]).ConfigureAwait(false);
                await ExecAsync("podman", ["pod", "rm", "-f", EgressTopologyManager.EgressPodName]).ConfigureAwait(false);
                await ExecAsync("podman", ["network", "rm", "-f", EgressTopologyManager.WorkloadNetworkName, EgressTopologyManager.OutsideNetworkName]).ConfigureAwait(false);
            }

            if (lanFixtureProcess is not null)
            {
                if (!lanFixtureProcess.HasExited)
                {
                    int? fixturePid = null;
                    if (File.Exists(Path.Combine(tempRoot, "lan-fixture.pid"))
                        && int.TryParse(await File.ReadAllTextAsync(Path.Combine(tempRoot, "lan-fixture.pid")).ConfigureAwait(false), NumberStyles.None, CultureInfo.InvariantCulture, out int parsedFixturePid)
                        && parsedFixturePid > 0)
                    {
                        fixturePid = parsedFixturePid;
                    }

                    _ = await ExecAsync(
                        "sudo",
                        ["kill", "-TERM", (fixturePid ?? lanFixtureProcess.Id).ToString(CultureInfo.InvariantCulture)]).ConfigureAwait(false);
                    using var fixtureShutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try
                    {
                        await lanFixtureProcess.WaitForExitAsync(fixtureShutdownCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        _ = await ExecAsync(
                            "sudo",
                            ["kill", "-KILL", (fixturePid ?? lanFixtureProcess.Id).ToString(CultureInfo.InvariantCulture)]).ConfigureAwait(false);
                        await lanFixtureProcess.WaitForExitAsync().ConfigureAwait(false);
                    }
                }
                lanFixtureProcess.Dispose();
            }
            if (!string.IsNullOrWhiteSpace(lanFixtureNamespaceName))
            {
                if (!string.IsNullOrWhiteSpace(lanFixtureHostDevice))
                {
                    _ = await ExecAsync("sudo", ["ip", "link", "del", lanFixtureHostDevice]).ConfigureAwait(false);
                }
                _ = await ExecAsync("sudo", ["ip", "netns", "del", lanFixtureNamespaceName]).ConfigureAwait(false);
            }
            Environment.SetEnvironmentVariable("CONTAINERS_STORAGE_CONF", previousStorageConfig);
            Environment.SetEnvironmentVariable("CONTAINERS_CONF", previousContainersConfig);
            Environment.SetEnvironmentVariable("PATH", previousPath);
            try
            {
                string runtimeOwnerMarker = Path.Combine(podmanRuntimeDirectory, ".acceptance-owner");
                if (Directory.Exists(podmanRuntimeDirectory)
                    && string.Equals(await File.ReadAllTextAsync(runtimeOwnerMarker).ConfigureAwait(false), podmanRuntimeOwnerToken, StringComparison.Ordinal)
                    && Path.GetDirectoryName(Path.GetFullPath(podmanRuntimeDirectory)) == Path.GetFullPath(Path.GetTempPath())
                    && Path.GetFileName(podmanRuntimeDirectory).StartsWith("nac-run-", StringComparison.Ordinal))
                {
                    Directory.Delete(podmanRuntimeDirectory, recursive: true);
                }
            }
            catch
            {
                // Best effort cleanup of this test's uniquely marked, short Podman runroot.
            }
            try
            {
                string ownerMarkerPath = Path.Combine(tempRoot, ".acceptance-owner");
                if (File.Exists(ownerMarkerPath)
                    && string.Equals(await File.ReadAllTextAsync(ownerMarkerPath).ConfigureAwait(false), tempRootOwnerToken, StringComparison.Ordinal))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }

    private static (string Path, string OwnerToken) CreateOwnedTempRoot(string baseDirectory)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            string ownerToken = Guid.NewGuid().ToString("N");
            string path = Path.Combine(baseDirectory, $"kautuka-nac-prod-{ownerToken}");
            string reservationPath = path + ".reserve";
            try
            {
                using (new FileStream(reservationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                }
            }
            catch (IOException) when (File.Exists(reservationPath))
            {
                continue;
            }

            try
            {
                if (Directory.Exists(path))
                {
                    continue;
                }

                Directory.CreateDirectory(path);
                string markerPath = Path.Combine(path, ".acceptance-owner");
                using (var marker = new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(marker))
                {
                    writer.Write(ownerToken);
                }

                return (path, ownerToken);
            }
            finally
            {
                File.Delete(reservationPath);
            }
        }

        throw new IOException($"Could not reserve an unused acceptance directory beneath '{baseDirectory}'.");
    }

    private static async Task BestEffortCleanupAsync(string podName, string[] containers, string[] networks)
    {
        foreach (string c in containers)
        {
            await ExecAsync("podman", ["rm", "-f", c]).ConfigureAwait(false);
        }
        await ExecAsync("podman", ["pod", "rm", "-f", podName]).ConfigureAwait(false);
        foreach (string n in networks)
        {
            await ExecAsync("podman", ["network", "rm", "-f", n]).ConfigureAwait(false);
        }
    }

    private static async Task VerifyProductionIsolationCleanupAsync()
    {
        List<string> podmanCommands = [];
        execOverride = (fileName, arguments) =>
        {
            if (string.Equals(fileName, "podman", StringComparison.Ordinal))
            {
                podmanCommands.Add(string.Join(' ', arguments));
                if (arguments.Count > 0 && string.Equals(arguments[0], "info", StringComparison.Ordinal))
                {
                    return Task.FromResult((0, "C:\\unsafe\\graph|C:\\unsafe\\run|false", string.Empty));
                }
            }

            return Task.FromResult((1, string.Empty, "Unexpected command in fake Podman test."));
        };
        bool rejected = false;
        try
        {
            _ = await RunProductionTopologyProofAsync(null, null).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("requires rootless Podman with isolated storage", StringComparison.Ordinal))
        {
            rejected = true;
        }
        finally
        {
            execOverride = null;
        }

        if (!rejected
            || podmanCommands.Count != 1
            || !podmanCommands[0].StartsWith("info ", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The real production runner must reject mismatched isolation and run no Podman cleanup/mutation commands. Observed fake Podman calls: {string.Join("; ", podmanCommands)}");
        }

        int cleanupCalls = 0;
        var gate = new PodmanIsolationCleanupGate();
        await gate.RunIfVerifiedAsync(() =>
        {
            cleanupCalls++;
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        if (gate.IsVerified || cleanupCalls != 0)
        {
            throw new InvalidOperationException("Unverified Podman isolation must suppress every cleanup action.");
        }

        gate.MarkVerified();
        await gate.RunIfVerifiedAsync(() =>
        {
            cleanupCalls++;
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        if (!gate.IsVerified || cleanupCalls != 1)
        {
            throw new InvalidOperationException("Verified Podman isolation must allow cleanup actions.");
        }
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> ExecAsync(
        string fileName,
        IReadOnlyList<string> args,
        int timeoutSeconds = 60)
    {
        if (execOverride is not null)
        {
            return await execOverride(fileName, args).ConfigureAwait(false);
        }

        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo { FileName = fileName, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string a in args)
        {
            proc.StartInfo.ArgumentList.Add(a);
        }
        proc.Start();
        Task<string> stdOutTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> stdErrTask = proc.StandardError.ReadToEndAsync();
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            await proc.WaitForExitAsync().ConfigureAwait(false);
            string timeoutError = await stdErrTask.ConfigureAwait(false);
            timeoutError = string.IsNullOrWhiteSpace(timeoutError) ? string.Empty : timeoutError + Environment.NewLine;
            timeoutError += $"Process '{fileName}' exceeded the {timeoutSeconds}s acceptance-command timeout.";
            return (124, await stdOutTask.ConfigureAwait(false), timeoutError);
        }

        string stdOut = await stdOutTask.ConfigureAwait(false);
        string stdErr = await stdErrTask.ConfigureAwait(false);
        return (proc.ExitCode, stdOut, stdErr);
    }

    private sealed class AcceptanceServerCallContext : ServerCallContext
    {
        private readonly Metadata requestHeaders = [];
        private readonly Metadata responseTrailers = [];
        private readonly Dictionary<object, object> userState = [];

        protected override string MethodCore => "/kautuka.manager.v1.ManagerService/SetBrake";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "unix:acceptance";
        protected override DateTime DeadlineCore => DateTime.MaxValue;
        protected override Metadata RequestHeadersCore => requestHeaders;
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;
        protected override Metadata ResponseTrailersCore => responseTrailers;
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => new(string.Empty, []);
        protected override IDictionary<object, object> UserStateCore => userState;

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options)
            => throw new NotSupportedException();

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders)
            => Task.CompletedTask;
    }
}
