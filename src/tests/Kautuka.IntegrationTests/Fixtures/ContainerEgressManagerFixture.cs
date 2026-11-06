using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Manager.Protos;
using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Sandbox;
using Kautuka.Manager.RuntimeContainer.Sandbox.Models;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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
        ContainerEgressRuntime? recoveryRuntime = null;
        bool recoveredBeforeCommit = false;
        string? forceAddress = null;
        var runner = new PodmanCommandRunner(async (arguments, timeout, token, environment) =>
        {
            var actual = arguments.ToList();
            if (actual[0] == "create" && forceAddress is not null && actual.Contains("kautuka.egress.connector=owner::b"))
            {
                actual.InsertRange(1, ["--ip", forceAddress]); forceAddress = null;
            }
            trace.Add(actual.ToArray());
            if (actual[0] == "commit" && !recoveredBeforeCommit && recoveryRuntime is not null)
            {
                recoveredBeforeCommit = true;
                await native.RunAsync(["stop", "--time", "1", "kautuka-egress-envoy"], 15, token);
                await recoveryRuntime.EnsureReadyAsync(token);
            }
            if (failAdmin && actual.Contains("http://127.0.0.1:9901/config_dump")) return new PodmanCommandResult(1, "", "fixture control-plane outage");
            if (actual[0] == "stop" && actual.Contains("kautuka-egress-envoy")) failAdmin = false;
            return await native.RunAsync(actual, timeout, token, environment);
        });
        var allowlist = new RuntimeImageAllowlist(1, [
         new("egress-setup",data.GetProperty("helper").GetString()!,null),
         new("egress-envoy",data.GetProperty("envoy").GetString()!,null),
         new("egress-dns",data.GetProperty("dns").GetString()!,null),
         new("git-pkgs-proxy",data.GetProperty("pkgs").GetString()!,null),
         new("kautuka-base",data.GetProperty("sandbox").GetString()!,null)]);
        var options = new ContainerEgressOptions
        {
            Enabled = true,
            DnsUpstreams = ["10.247.90.20"],
            Grants = [
         new(){Role="agent",UserId="owner",AgentId="alpha",ComponentId="alpha",Destination="rebind.kautuka.test",Port=20101},
         new(){UserId="owner",AgentId="alpha",ComponentId="owner::a",Destination="allowed.kautuka.test",Port=8443},
         new(){UserId="owner",AgentId="beta",ComponentId="owner::c",Destination="allowed.kautuka.test",Port=8443}]
        };
        using var runtime = new ContainerEgressRuntime(runner, options, allowlist, stateRoot);
        recoveryRuntime = runtime;
        var store = new WorkspaceDesiredStateStore();
        var alpha = Context("alpha"); var beta = Context("beta");
        List<WorkspaceMcpContainerConnectorDefinition> definitions = [Definition("a"), Definition("b"), Definition("c"), Definition("d")];
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
        var packageDefinition = new WorkspaceSidecarDefinition("kautuka-pkgs-proxy", data.GetProperty("pkgs").GetString()!, [],
            [new WorkspaceSidecarVolumeBinding(Path.Combine(stateRoot, "pkg-cache"), "/data", false)],
            new Dictionary<string, string> { ["PROXY_LISTEN"] = ":20101", ["PROXY_BASE_URL"] = "http://kautuka-pkgs-proxy:20101", ["PROXY_CACHE_METADATA"] = "true" }, [], null, null, null);
        var packageSupervisor = new WorkspaceSidecarSupervisor(new WorkspaceSidecarCatalog([packageDefinition]), new UnusedHttpClientFactory(), runner,
            Options.Create(new ManagerContainerStartupOptions()), NullLogger<WorkspaceSidecarSupervisor>.Instance, allowlist, null, runtime);
        var settings = new ManagerRuntimeSettings(new WorkspaceDefinition { BaseRoot = stateRoot }, new SandboxRuntimeDefinition(), new WorkspaceTelemetryDefinition());
        if (!await packageSupervisor.EnsureReadyAsync(settings, ct)) throw new InvalidOperationException("Actual package service did not become healthy");
        var firstPackage = await runtime.FindSourceAsync("kautuka-pkgs-proxy", ct) ?? throw new InvalidOperationException("Package source missing after startup");
        if (!await packageSupervisor.EnsureReadyAsync(settings, ct)) throw new InvalidOperationException("Actual package service could not be adopted");
        var package = await runtime.FindSourceAsync("kautuka-pkgs-proxy", ct) ?? throw new InvalidOperationException("Package source not registered");
        if (package.Id != firstPackage.Id) throw new InvalidOperationException("Unchanged package service was replaced during adoption");
        await File.AppendAllTextAsync(data.GetProperty("dnsHosts").GetString()!, package.Address + " rebind.kautuka.test\n", ct);
        await Task.Delay(1200, ct);
        var sandbox = new PodmanContainerTool(runner, runtime);
        ContainerContext packageWorkspace = alpha with { Image = data.GetProperty("sandbox").GetString()!, DesiredStateVersion = 1, SpecFingerprint = "native-fixture-shipped-base-v1" };
        var firstSandbox = await sandbox.SpawnSandboxAsync(packageWorkspace, ct);
        var workspaceWrite = await sandbox.RunShellAsync(packageWorkspace, "printf fixture-write > /workspace/native-fixture.txt && cat /workspace/native-fixture.txt", cancellationToken: ct);
        if (workspaceWrite.ExitCode != 0 || workspaceWrite.StdOut != "fixture-write") throw new InvalidOperationException("Shipped sandbox workspace write failed: " + workspaceWrite.StdErr);
        var pkgHealth = await sandbox.RunShellAsync(packageWorkspace, "curl --silent --fail --show-error --max-time 8 http://kautuka-pkgs-proxy:20101/health", cancellationToken: ct);
        if (pkgHealth.ExitCode != 0) throw new InvalidOperationException("Sandbox package route failed: " + pkgHealth.StdErr);
        var metadata = await sandbox.RunShellAsync(packageWorkspace, "curl --silent --fail --show-error --max-time 12 --output /tmp/npm-metadata.json --write-out '%{http_code}' http://kautuka-pkgs-proxy:20101/npm/is-number", cancellationToken: ct);
        if (metadata.ExitCode != 0 || metadata.StdOut != "200") throw new InvalidOperationException("Real package metadata fetch failed: " + metadata.StdOut + " " + metadata.StdErr);
        var coreDenied = await sandbox.RunShellAsync(packageWorkspace, "curl --silent --fail --max-time 3 http://" + package.Address + ":20101/health", cancellationToken: ct);
        if (coreDenied.ExitCode == 0) throw new InvalidOperationException("Sandbox could bypass the designated package endpoint");
        async Task<long> RejectedPackets()
        {
            var result = await native.RunAsync(["run", "--rm", "--pull=never", "--pod", "kautuka-egress", "--cap-drop=ALL", "--cap-add=NET_ADMIN", "--security-opt=no-new-privileges",
                "--entrypoint", "/usr/sbin/nft", data.GetProperty("helper").GetString()!, "-j", "list", "chain", "ip", "kautuka_egress", "output"], 15, ct);
            using JsonDocument counters = JsonDocument.Parse(result.StdOut);
            return counters.RootElement.GetProperty("nftables").EnumerateArray().Where(item => item.TryGetProperty("rule", out _))
                .Select(item => item.GetProperty("rule").GetProperty("expr")).Where(expr => expr.EnumerateArray().Any(item => item.TryGetProperty("reject", out _)))
                .Sum(expr => expr.EnumerateArray().Where(item => item.TryGetProperty("counter", out _)).Sum(item => item.GetProperty("counter").GetProperty("packets").GetInt64()));
        }
        if ((await sandbox.SpawnSandboxAsync(packageWorkspace, ct)).ContainerId != firstSandbox.ContainerId)
            throw new InvalidOperationException("Unchanged sandbox was replaced across shell calls: " + string.Join("; ", sandbox.TakeDriftNotes()));
        long beforeRebind = await RejectedPackets();
        var rebind = await sandbox.RunShellAsync(packageWorkspace, "curl --silent --insecure --fail --max-time 4 https://rebind.kautuka.test:20101/", cancellationToken: ct);
        long afterRebind = await RejectedPackets();
        if (rebind.ExitCode == 0 || afterRebind <= beforeRebind) throw new InvalidOperationException($"DNS rebinding boundary check failed: exit={rebind.ExitCode}, reject counters={beforeRebind}->{afterRebind}, stdout={rebind.StdOut}, stderr={rebind.StdErr}");
        await sandbox.CloseSandboxAsync(packageWorkspace, ct);
        report["actualPackageServiceHealthyAndAdopted"] = true; report["sandboxPackageMetadataFetched"] = true;
        report["sandboxAdoptedAcrossShellCalls"] = true;
        report["directCorePackageDenied"] = true; report["externalGrantRebindingDenied"] = true;
        await Console.Error.WriteLineAsync("fixture: actual package path and sandbox passed");
        var service = new WorkspaceContainerConnectorExecutionService(store, runner, new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance, allowlist, null, runtime),
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
            var stoppedProxy = await native.RunAsync(["stop", "--time", "1", "kautuka-egress-envoy"], 15, ct);
            if (stoppedProxy.ExitCode != 0) throw new InvalidOperationException("Fixture could not simulate an unexpected proxy exit");
            await Check("a", alpha, "exit:0");
            report["stoppedProxyRecoveredThroughReadiness"] = true;
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
        ContainerContext bundleWorkspace = alpha with { Image = data.GetProperty("sandbox").GetString()!, DesiredStateVersion = 1, SpecFingerprint = "native-fixture-shipped-base-v1" };
        foreach (string language in new[] { "node", "python", "uv", "binary" })
        {
            string bundle = WriteDependencyBundle(stateRoot, language);
            var environment = language == "node"
                ? new List<KeyValuePair<string, string>> { new("NPM_CONFIG_REGISTRY", "http://kautuka-pkgs-proxy:20101/npm/") }
                : [new("PIP_INDEX_URL", "http://kautuka-pkgs-proxy:20101/pypi/simple/"), new("PIP_TRUSTED_HOST", "kautuka-pkgs-proxy")];
            if (language == "binary") environment.Clear();
            if (language == "uv") environment.Add(new("UV_INDEX_URL", "http://kautuka-pkgs-proxy:20101/pypi/simple/"));
            var definition = new WorkspaceMcpContainerConnectorDefinition("owner::bundle-" + language, bundleWorkspace.Image,
                language == "node" ? "node" : language == "python" ? "python3" : language == "binary" ? "${__dirname}/server.py" : "uv",
                language == "uv" ? ["run", "python", "${__dirname}/server.py"] : language == "binary" ? [] : [language == "node" ? "${__dirname}/index.js" : "${__dirname}/server.py"],
                true, false, bundle, language, environment, 30, 600);
            definitions.Add(definition);
            string foreignId = "foreign::bundle-" + language;
            definitions.Add(definition with { ConnectorId = foreignId });
            store.ApplyDesiredState(3 + definitions.Count, WorkspaceDesiredStateSyncReason.Bootstrap, settings, [bundleWorkspace], definitions,
                [new WorkspaceSandboxConnectorBindingsDefinition("agent", "alpha", [definition.ConnectorId, foreignId])]);
            await using var bundleService = new WorkspaceContainerConnectorExecutionService(store, runner,
                new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance, allowlist, null, runtime), sandbox,
                NullLogger<WorkspaceContainerConnectorExecutionService>.Instance, runtime);
            if (!bundleService.ResolveDesiredSandboxConnectorIds(bundleWorkspace).SequenceEqual(new[] { definition.ConnectorId }))
                throw new InvalidOperationException("Foreign connector leaked into a user's sandbox projection");
            foreach (WorkspaceMcpExecutionContext mode in new[] { WorkspaceMcpExecutionContext.Isolated, WorkspaceMcpExecutionContext.AgentSandbox })
            {
                var result = await bundleService.CallToolAsync(definition.ConnectorId, null, "dependency", "{}", 30, false, bundleWorkspace, mode, ct);
                if (!result.Ok || result.Result?.Contains("dependency:true", StringComparison.Ordinal) != true)
                    throw new InvalidOperationException("Materialized " + language + "/" + mode + " failed: " + result.FailureMessage + " " + result.Result);
                report[language + mode + "DependencyExecuted"] = true;
            }
            await sandbox.CloseSandboxAsync(bundleWorkspace, ct);
            await Console.Error.WriteLineAsync("fixture: materialized " + language + " isolated and sandbox passed");
        }
        if (!recoveredBeforeCommit) throw new InvalidOperationException("The live exited installer recovery test did not run");
        report["exitedInstallerSurvivedRecoveryForCommit"] = true;
        if (trace.Any(command => command[0] == "pull" || command.Contains("kautuka-sandbox")
            || command[0] == "build" && (!command.Contains("--pull=never") || !command.Contains("--network=none"))))
            throw new InvalidOperationException("Materialization used a legacy or networked build path");
        // Simulate a previous Manager's abandoned installer, then hand off to a fresh runtime owner.
        string orphanName = "kautuka-orphan-installer-fixture";
        var orphan = await runtime.PrepareAndAuthorizeAsync(new ContainerEgressRuntime.SourceIdentity(new ContainerEgressRuntime.Scope("owner", "alpha"), "materializer", "owner::orphan", true),
            orphanName, (network, dns) => ["create", "--pull=never", "--restart=no", "--name", orphanName, "--label", "kautuka.managed=manager", "--label", "kautuka.role=materializer",
                "--network", network, "--dns", dns, "--cap-drop=ALL", "--security-opt=no-new-privileges", "--read-only", "--entrypoint", "/usr/bin/sleep", data.GetProperty("helper").GetString()!, "300"], ct);
        await runtime.StartWithAdmissionAsync(orphan, token => native.RunAsync(orphan.StartArguments(false), 15, token), ct);
        using (var replacement = new ContainerEgressRuntime(runner, options, allowlist, stateRoot)) await replacement.EnsureReadyAsync(ct);
        var absent = await native.RunAsync(["container", "exists", orphan.Id], 10, ct);
        if (absent.ExitCode != 1) throw new InvalidOperationException("Fresh Manager did not retire the unowned materializer");
        report["abandonedMaterializerRevokedAndRemoved"] = true;
        var remaining = await native.RunAsync(["network", "ls", "--filter", "label=kautuka.egress=true", "--format", "{{.Name}}"], 15, ct);
        string[] names = remaining.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length != 2 || !names.Contains("kautuka-core") || !names.Contains("kautuka-outside")) throw new InvalidOperationException("Private scope capacity was not recovered: " + remaining.StdOut);
        report["emptyScopesRetired"] = true; report["passed"] = true;
        return report;
    }
    private static string WriteDependencyBundle(string root, string language)
    {
        string path = Path.Combine(root, "bundle-" + language + ".mcpb");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string value)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open()); writer.Write(value);
        }
        Add("manifest.json", "{\"server\":{\"type\":\"" + language + "\",\"entry_point\":\"" + (language == "node" ? "index.js" : "server.py") + "\"}}");
        if (language == "node")
        {
            Add("package.json", "{\"name\":\"native-egress-fixture\",\"version\":\"1.0.0\",\"dependencies\":{\"is-number\":\"7.0.0\"}}");
            Add("index.js", """
                const readline = require('node:readline');
                const isNumber = require('is-number');
                readline.createInterface({input:process.stdin}).on('line', line => {
                  const q=JSON.parse(line); if(q.id===undefined) return;
                  let result;
                  if(q.method==='initialize') result={protocolVersion:q.params.protocolVersion,capabilities:{tools:{}},serverInfo:{name:'fixture',version:'1'}};
                  else if(q.method==='tools/list') result={tools:[{name:'dependency',inputSchema:{type:'object'}}]};
                  else if(q.method==='tools/call') result={content:[{type:'text',text:'dependency:'+isNumber(42)}]};
                  else {console.log(JSON.stringify({jsonrpc:'2.0',id:q.id,error:{code:-32601,message:'Method not found'}}));return;}
                  console.log(JSON.stringify({jsonrpc:'2.0',id:q.id,result}));
                });
                """);
        }
        else
        {
            if (language != "binary") Add("requirements.txt", "requests==2.32.5\n");
            string python = """
                import sys,json,requests
                for line in sys.stdin:
                    q=json.loads(line)
                    if 'id' not in q: continue
                    method=q['method']
                    if method=='initialize': result={'protocolVersion':q['params']['protocolVersion'],'capabilities':{'tools':{}},'serverInfo':{'name':'fixture','version':'1'}}
                    elif method=='tools/list': result={'tools':[{'name':'dependency','inputSchema':{'type':'object'}}]}
                    elif method=='tools/call': result={'content':[{'type':'text','text':'dependency:'+str(requests.__version__=='2.32.5').lower()}]}
                    else:
                        print(json.dumps({'jsonrpc':'2.0','id':q['id'],'error':{'code':-32601,'message':'Method not found'}}),flush=True)
                        continue
                    print(json.dumps({'jsonrpc':'2.0','id':q['id'],'result':result}),flush=True)
                """;
            if (language == "binary")
            {
                python = "#!/usr/bin/python3\n" + python.Replace(",requests", "", StringComparison.Ordinal).Replace("requests.__version__=='2.32.5'", "True", StringComparison.Ordinal);
                Add("pathlib.py", "raise RuntimeError('untrusted bundle module ran during offline setup')\n");
            }
            Add("server.py", python);
        }
        return path;
    }

    private sealed class UnusedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("The package service must use its native health check.");
    }

}
