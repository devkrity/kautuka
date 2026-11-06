using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Manager.Protos;
using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Sandbox;
using Kautuka.Manager.RuntimeContainer.Sandbox.Models;

using Microsoft.Extensions.Logging.Abstractions;

using SandboxRuntimeDefinition = Kautuka.Manager.RuntimeContainer.Configuration.SandboxRuntimeDefinition;
using WorkspaceDefinition = Kautuka.Manager.RuntimeContainer.Configuration.WorkspaceDefinition;

namespace Kautuka.IntegrationTests.Fixtures;

internal static class ContainerEgressManagerFixture
{
    internal static async Task<JsonObject> RunAsync(string inputPath, CancellationToken ct)
    {
        using var input = JsonDocument.Parse(await File.ReadAllTextAsync(inputPath, ct));
        var data = input.RootElement;
        string stateRoot = data.GetProperty("stateRoot").GetString()!;
        string image = data.GetProperty("connector").GetString()!;
        string expectedUser = data.GetProperty("disposableUser").GetString()!;
        if (!expectedUser.StartsWith("kev6-", StringComparison.Ordinal) || Environment.UserName != expectedUser)
            throw new InvalidOperationException("The Manager fixture must run as its disposable rootless test account.");
        var report = new JsonObject();
        var trace = new List<string[]>();
        var native = new PodmanCommandRunner();
        bool failAdmin = false;
        string? forceAddress = null;
        var runner = new PodmanCommandRunner(async (arguments, token) =>
        {
            var actual = arguments.ToList();
            if (actual[0] == "create" && forceAddress is not null && actual.Contains("kautuka.egress.connector=owner::b"))
            {
                actual.InsertRange(1, ["--ip", forceAddress]); forceAddress = null;
            }
            trace.Add(actual.ToArray());
            if (failAdmin && actual.Contains("http://127.0.0.1:9901/config_dump")) return new PodmanCommandResult(1, "", "fixture control-plane outage");
            if (actual[0] == "stop" && actual.Contains("kautuka-egress-envoy")) failAdmin = false;
            return await native.RunAsync(actual, 30, token);
        });
        var allowlist = new RuntimeImageAllowlist(1, [
         new("egress-setup",data.GetProperty("helper").GetString()!,null),
         new("egress-envoy",data.GetProperty("envoy").GetString()!,null),
         new("egress-dns",data.GetProperty("dns").GetString()!,null)]);
        var options = new ContainerEgressOptions
        {
            Enabled = true,
            DnsUpstreams = ["10.247.90.20"],
            Grants = [
         new(){UserId="owner",AgentId="alpha",ConnectorId="owner::a",Destination="allowed.kautuka.test",Port=8443},
         new(){UserId="owner",AgentId="beta",ConnectorId="owner::c",Destination="allowed.kautuka.test",Port=8443}]
        };
        using var runtime = new ContainerEgressRuntime(runner, options, allowlist, stateRoot);
        var store = new WorkspaceDesiredStateStore();
        var alpha = Context("alpha"); var beta = Context("beta");
        WorkspaceMcpContainerConnectorDefinition[] definitions = [Definition("a"), Definition("b"), Definition("c"), Definition("d")];
        void Apply(long version, ContainerContext workspace) => store.ApplyDesiredState(version, WorkspaceDesiredStateSyncReason.Bootstrap,
         new ManagerRuntimeSettings(new WorkspaceDefinition { BaseRoot = stateRoot }, new SandboxRuntimeDefinition(), new WorkspaceTelemetryDefinition()), [workspace], definitions, []);
        ContainerContext Context(string agent) => new("agent", agent, "owner", Path.Combine(stateRoot, agent), "/workspace", image, 1, "512m", 20, [], new Dictionary<string, string> { { "AGENT_PRIVATE", "fixture-no-forward" } });
        WorkspaceMcpContainerConnectorDefinition Definition(string suffix) => new("owner::" + suffix, image, suffix == "d" ? "" : "/usr/bin/bash", suffix == "d" ? [] : ["/mcp-fixture.sh"], true, false, null, null, [], 20, 600);
        Apply(1, alpha);
        await runtime.EnsureReadyAsync(ct);
        report["bootstrapReady"] = true;
        await Console.Error.WriteLineAsync("fixture: bootstrap ready");
        var adminDenied = await native.RunAsync(["run", "--rm", "--pull=never", "--pod", "kautuka-egress", "--cap-drop=ALL",
            "--security-opt=no-new-privileges", "--read-only", "--user", "65532:65532", "--entrypoint", "/usr/bin/curl",
            data.GetProperty("helper").GetString()!, "--silent", "--max-time", "2", "http://127.0.0.1:9901/config_dump"], 15, ct);
        if (adminDenied.ExitCode != 7) throw new InvalidOperationException("The proxy identity can reach its admin listener: " + adminDenied.ExitCode);
        report["proxyIdentityCannotReachAdmin"] = true;
        using (JsonDocument core = JsonDocument.Parse((await native.RunAsync(["network", "inspect", "kautuka-core"], 15, ct)).StdOut))
        {
            string coreHost = core.RootElement[0].GetProperty("subnets")[0].GetProperty("subnet").GetString()!.Split('/')[0];
            string coreProxy = coreHost[..(coreHost.LastIndexOf('.') + 1)] + "2";
            var privateDenied = await native.RunAsync(["run", "--rm", "--pull=never", "--pod", "kautuka-egress", "--cap-drop=ALL",
                "--security-opt=no-new-privileges", "--read-only", "--user", "65532:65532", "--entrypoint", "/usr/bin/curl",
                data.GetProperty("helper").GetString()!, "--silent", "--max-time", "2", "http://" + coreProxy + ":53/"], 15, ct);
            if (privateDenied.ExitCode != 7) throw new InvalidOperationException("The proxy identity can initiate a private-network connection: " + privateDenied.ExitCode);
        }
        report["proxyIdentityCannotInitiatePrivateConnection"] = true;
        var service = new WorkspaceContainerConnectorExecutionService(store, runner, new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance),
         new PodmanContainerTool(runner, runtime), NullLogger<WorkspaceContainerConnectorExecutionService>.Instance, runtime);
        try
        {
            var tools = await service.ListToolsAsync("owner::a", null, 20, false, alpha, WorkspaceMcpExecutionContext.Isolated, ct);
            if (!tools.Ok || tools.Tools.Count != 3) throw new InvalidOperationException("Real stdio tool discovery failed: " + tools.FailureMessage);
            report["tools"] = tools.Tools.Count;
            await Console.Error.WriteLineAsync("fixture: tool discovery passed");
            async Task Check(string suffix, ContainerContext context, string expected)
            {
                var result = await service.CallToolAsync("owner::" + suffix, null, "allowed", "{}", 20, false, context, WorkspaceMcpExecutionContext.Isolated, ct);
                if (!result.Ok || result.Result?.Contains(expected, StringComparison.Ordinal) != true) throw new InvalidOperationException("Real tool " + suffix + "/" + context.ScopeId + " failed: " + result.FailureMessage + " result=" + result.Result);
            }
            var firstCall = service.CallToolAsync("owner::d", null, "hold", "{}", 20, false, alpha, WorkspaceMcpExecutionContext.Isolated, ct);
            string? oneShotName = null;
            for (int attempt = 0; attempt < 100 && oneShotName is null; attempt++)
            {
                oneShotName = service.TrackedContainerNames.SingleOrDefault(name => name.Contains("-d-", StringComparison.Ordinal));
                if (oneShotName is null) await Task.Delay(50, ct);
            }
            if (oneShotName is null) throw new InvalidOperationException("One-shot was not prepared");
            var concurrentCall = service.CallToolAsync("owner::d", null, "allowed", "{}", 20, false, alpha, WorkspaceMcpExecutionContext.Isolated, ct);
            var held = await firstCall; var denied = await concurrentCall;
            if (!held.Ok || held.Result?.Contains("held", StringComparison.Ordinal) != true || !denied.Ok || denied.Result?.Contains("exit:35", StringComparison.Ordinal) != true)
                throw new InvalidOperationException("Overlapping one-shot calls interrupted each other: " + held.FailureMessage + "; " + denied.FailureMessage);
            if (service.TrackedContainerNames.Contains(oneShotName)) throw new InvalidOperationException("Completed one-shot was not retired");
            report["overlappingOneShotsCompleted"] = true;
            await Console.Error.WriteLineAsync("fixture: overlapping one-shots passed");
            await Check("a", alpha, "exit:0");
            string nameA = service.TrackedContainerNames.Single(name => name.Contains("-a-", StringComparison.Ordinal));
            var first = await runtime.FindSourceAsync(nameA, ct) ?? throw new InvalidOperationException("Source A not tracked");
            var beforeAdoption = await native.RunAsync(["inspect", "--format", "{{.State.Pid}}", "kautuka-egress-envoy"], 15, ct);
            using (var restartedManager = new ContainerEgressRuntime(runner, options, allowlist, stateRoot))
            {
                await restartedManager.EnsureReadyAsync(ct);
                if (await restartedManager.FindSourceAsync(nameA, ct) != first)
                    throw new InvalidOperationException("Manager restart did not restore the exact live source allocation");
            }
            var afterAdoption = await native.RunAsync(["inspect", "--format", "{{.State.Pid}}", "kautuka-egress-envoy"], 15, ct);
            if (beforeAdoption.ExitCode != 0 || afterAdoption.ExitCode != 0 || beforeAdoption.StdOut != afterAdoption.StdOut)
                throw new InvalidOperationException("Healthy Manager adoption restarted the live proxy");
            await Check("a", alpha, "exit:0");
            report["healthyRestartAdoptedLiveProxy"] = true;
            await Console.Error.WriteLineAsync("fixture: live adoption passed");
            // Force native address reuse after an unexpected allocation loss, leaving the old cached permission present.
            var removed = await native.RunAsync(["rm", "--force", first.Id], 15, ct);
            if (removed.ExitCode != 0) throw new InvalidOperationException("Fixture could not simulate allocation loss");
            forceAddress = first.Address;
            await Check("b", alpha, "exit:35");
            string nameB = service.TrackedContainerNames.Single(name => name.Contains("-b-", StringComparison.Ordinal));
            var second = await runtime.FindSourceAsync(nameB, ct) ?? throw new InvalidOperationException("Source B not tracked");
            if (second.Address != first.Address || await runtime.FindSourceAsync(nameA, ct) is not null) throw new InvalidOperationException("Address reuse did not replace former source ownership");
            report["reusedAddress"] = second.Address; report["reusedSourceDenied"] = true;
            await Console.Error.WriteLineAsync("fixture: address reuse passed");
            await Check("a", alpha, "exit:0");
            var replacement = await runtime.FindSourceAsync(nameA, ct) ?? throw new InvalidOperationException("Replacement A not tracked");
            int traceStart = trace.Count;
            failAdmin = true;
            bool failed = false;
            try { await runtime.RetireAsync(replacement.Id, ct); }
            catch (AggregateException) { failed = true; }
            catch (InvalidOperationException) { failed = true; }
            if (!failed) throw new InvalidOperationException("Injected policy outage was not observed");
            int proxyStop = trace.FindIndex(traceStart, command => command[0] == "stop" && command.Contains("kautuka-egress-envoy"));
            int sourceStop = trace.FindIndex(traceStart, command => command[0] == "stop" && command.Contains(replacement.Id));
            if (proxyStop < 0 || sourceStop <= proxyStop) throw new InvalidOperationException("Uncertain retirement released source before blackholing proxy");
            report["proxyStoppedBeforeSource"] = true;
            await Check("b", alpha, "exit:35");
            report["recoveredWithoutOldGrant"] = true;
            await Console.Error.WriteLineAsync("fixture: uncertain-policy recovery passed");
            Apply(2, beta);
            var retired = await service.RemoveOrphanedConnectorContainersAsync(ct);
            if (!retired.Succeeded) throw new InvalidOperationException("Agent scope retirement failed");
            await Check("c", beta, "exit:0");
            await Check("a", beta, "exit:35");
            report["agentScopeReusedCapacity"] = true; report["agentGrantDidNotLeak"] = true;
        }
        finally { await service.DisposeAsync(); }
        var remaining = await native.RunAsync(["network", "ls", "--filter", "label=kautuka.egress=true", "--format", "{{.Name}}"], 15, ct);
        string[] names = remaining.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length != 2 || !names.Contains("kautuka-core") || !names.Contains("kautuka-outside")) throw new InvalidOperationException("Private scope capacity was not recovered: " + remaining.StdOut);
        report["emptyScopesRetired"] = true; report["passed"] = true;
        return report;
    }
}
