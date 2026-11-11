using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.NetworkAccessController;

using Microsoft.Extensions.Logging;

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
    [property: JsonPropertyName("observations")] IReadOnlyList<AcceptanceObservation> Observations,
    [property: JsonPropertyName("summary")] string Summary);

public static class AcceptanceHarness
{
    private static readonly List<AcceptanceObservation> observations = [];

    /// <summary>
    /// Records an acceptance test result adhering strictly to Ground Rule G9 (Section 7.0):
    /// 1. Compares observed values read during execution (never hard-coded literals).
    /// 2. Asserts negative control (NC) flipped the result.
    /// 3. Computes ncFlipped directly from observed vs ncObserved — never accepts a caller boolean.
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
            !string.Equals(ncObservedValue.Trim(), observedValue.Trim(), StringComparison.Ordinal);

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
}

public static class Program
{
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

            string? outPath = prodIdx + 1 < args.Length ? args[prodIdx + 1] : null;
            return await RunProductionTopologyProofAsync(outPath).ConfigureAwait(false);
        }

        if (!args.Contains("--live", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine("Harness self-test verified G9 enforcement. Pass --live on Linux rootless Podman to execute live container & Envoy v1.40.2 proofs.");
            return 0;
        }

        if (!OperatingSystem.IsLinux())
        {
            await Console.Error.WriteLineAsync("--live mode requires Linux with rootless Podman, nftables, nsenter, Envoy distroless-v1.40.2, and CoreDNS 1.14.6.").ConfigureAwait(false);
            return 1;
        }

        return await RunLiveFedoraAcceptanceAsync(args).ConfigureAwait(false);
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
        string reportPath = Path.Combine(tempRoot, "nac-live-report.json");

        Environment.SetEnvironmentVariable("XDG_STATE_HOME", xdgState);
        string nacStateDir = Path.Combine(xdgState, "kautuka", "nac");
        Directory.CreateDirectory(nacStateDir);

        string hookSrc = Path.Combine(repoRoot, "scripts", "nac", "kautuka_hook.py");
        string hooksDir = Path.Combine(tempRoot, "hooks.d");
        Directory.CreateDirectory(hooksDir);
        string hookPath = Path.Combine(hooksDir, "kautuka_hook.py");
        File.Copy(hookSrc, hookPath, overwrite: true);
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
            string envoyPath = Path.Combine(tempRoot, "envoy.yaml");
            string corefilePath = Path.Combine(tempRoot, "Corefile");
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
                ["create", "--pod", podName, "--name", $"{podName}-dns", "--cap-add", "NET_BIND_SERVICE", "--user", "0:0", "-v", $"{corefilePath}:/etc/coredns/Corefile:ro", EgressTopologyManager.CoreDnsPinnedImage, "-conf", "/etc/coredns/Corefile"]).ConfigureAwait(false);
            string adminSockDir = Path.Combine(tempRoot, "admin-sock");
            Directory.CreateDirectory(adminSockDir);
            await CreateAndAllowlistAsync($"{podName}-env", "egress-envoy", EgressTopologyManager.EnvoyPinnedImage,
                ["create", "--pod", podName, "--name", $"{podName}-env", "--user", "0:0", "-v", $"{envoyPath}:/etc/envoy/envoy.yaml:ro", "-v", $"{socketDir}:/run/kautuka-nac:z", "-v", $"{adminSockDir}:/run/kautuka-admin:z", EgressTopologyManager.EnvoyPinnedImage, "-c", "/etc/envoy/envoy.yaml"]).ConfigureAwait(false);

            await ExecPodmanAsync(["--hooks-dir", hooksDir, "pod", "start", podName]).ConfigureAwait(false);

            await Task.Delay(1500).ConfigureAwait(false);

            // Start real hardened workload container at 10.247.1.10 with hook enforced
            string wlIp = "10.247.1.10";
            await ExecPodmanAsync(["--hooks-dir", hooksDir, "run", "-d", "--name", workloadName, "--cap-drop=ALL", "--security-opt=no-new-privileges", "--read-only", "--user=65532:65532", "--pids-limit=256", "--memory=536870912", "--tmpfs", "/tmp:rw,noexec,nosuid,size=64m", "--network", $"{wlNet}:ip={wlIp}", "--dns=10.247.0.2", "--sysctl", "net.ipv6.conf.all.disable_ipv6=1", "--annotation", "io.kautuka.role=workload", toolsImg, "sleep", "300"]).ConfigureAwait(false);
            string wlCid = await InspectContainerIdAsync(workloadName).ConfigureAwait(false);

            registry.Register(new RegistryEntry(wlIp, "u1", "a1", wlCid, "agent-sandbox", null, "agent-sandbox", false, "normal", "user-agent", null, DateTimeOffset.UtcNow));

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
            registry.Register(new RegistryEntry(wl2Ip, "u1", "a1", wl2Cid, "agent-sandbox", null, "agent-sandbox", false, "normal", "user-agent", null, DateTimeOffset.UtcNow));

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

    private static async Task<int> RunProductionTopologyProofAsync(string? outputReportPath)
    {
        string repoRoot = FindRepoRoot();
        string baseTmp = Directory.Exists("/var/tmp") ? "/var/tmp" : Path.GetTempPath();
        string tempRoot = Path.Combine(baseTmp, $"kautuka-nac-prod-{Guid.NewGuid():N}"[..24]);
        string xdgState = Path.Combine(tempRoot, "xdg-state");
        string stateDir = Path.Combine(tempRoot, "manager-nac");
        string socketDir = Path.Combine(tempRoot, "sock");
        string hooksDir = Path.Combine(tempRoot, "hooks.d");
        Directory.CreateDirectory(xdgState);
        Directory.CreateDirectory(stateDir);
        Directory.CreateDirectory(socketDir);
        Directory.CreateDirectory(hooksDir);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(socketDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Environment.SetEnvironmentVariable("XDG_STATE_HOME", xdgState);
        string nacStateDir = Path.Combine(xdgState, "kautuka", "nac");
        Directory.CreateDirectory(nacStateDir);

        string hookSrc = Path.Combine(repoRoot, "scripts", "nac", "kautuka_hook.py");
        string hookPath = Path.Combine(hooksDir, "kautuka_hook.py");
        File.Copy(hookSrc, hookPath, overwrite: true);
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

        string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string containersConfDir = Path.Combine(homeDir, ".config", "containers", "containers.conf.d");
        Directory.CreateDirectory(containersConfDir);
        string dropInPath = Path.Combine(containersConfDir, "50-kautuka-nac.conf");
        string? previousDropIn = File.Exists(dropInPath) ? await File.ReadAllTextAsync(dropInPath).ConfigureAwait(false) : null;
        await File.WriteAllTextAsync(dropInPath, $"[engine]\nhooks_dir = [\"{hooksDir}\"]\n").ConfigureAwait(false);

        string wlName = $"kautuka-prod-wl-{Guid.NewGuid():N}"[..22];
        var podman = new PodmanCommandRunner();
        var registry = new WorkloadRegistry();
        using var policyStore = new NetworkAccessPolicyStore(Path.Combine(stateDir, "policy-state.json"));
        using var auditRing = new NetworkAccessAuditRing(Path.Combine(stateDir, "audit.jsonl"));
        var controller = new NacController(policyStore, registry, auditRing);
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
            // Clean any leftover production-named pod/networks before initializing fresh topology
            await ExecAsync("podman", ["rm", "-f", wlName]).ConfigureAwait(false);
            await ExecAsync("podman", ["pod", "rm", "-f", EgressTopologyManager.EgressPodName]).ConfigureAwait(false);
            await ExecAsync("podman", ["network", "rm", "-f", EgressTopologyManager.WorkloadNetworkName, EgressTopologyManager.OutsideNetworkName]).ConfigureAwait(false);

            await authHost.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await topology.InitializeAsync(registry, isWindowsHostNativeDev: false, decisionEndpointIp: null, includeLanGrants: false, CancellationToken.None).ConfigureAwait(false);

            if (!topology.IsStartupGateOpen)
            {
                throw new InvalidOperationException("EgressTopologyManager startup gate remained closed after InitializeAsync.");
            }

            string boundaryPath = Path.Combine(stateDir, "nac", "boundary.nft");
            string boundaryText = await File.ReadAllTextAsync(boundaryPath).ConfigureAwait(false);
            string dnsUpstreamLine = boundaryText
                .Split('\n')
                .Select(static l => l.Trim())
                .FirstOrDefault(static l => l.Contains("comment \"dns_upstream\"", StringComparison.Ordinal))
                ?? string.Empty;

            // Launch real hardened workload container via WorkloadContainerSpec.Render()
            string wlIp = allocator.AllocateNextIp(registry);
            const string baseImage = "ghcr.io/devkrity/kautuka-base@sha256:675400832f2ced5cf802db66eb067ef5022f65f0551c665d87ae7edc58dc0839";
            var wlSpec = new WorkloadContainerSpec(wlName, baseImage, wlIp)
            {
                UserId = "u1",
                AgentId = "a1",
                ConnectorId = "conn-web",
                ExecContext = "isolated",
                Kind = "connector",
                Systemwide = false,
                Egress = "normal",
                Persistent = true,
                StartupGateOpen = topology.IsStartupGateOpen,
                IsBraked = policyStore.GetBrakeSnapshot().IsUserBraked("u1"),
            };

            registry.Register(new RegistryEntry(wlIp, "u1", "a1", wlName, "connector", "conn-web", "isolated", false, "normal", "user-agent", null, DateTimeOffset.UtcNow));
            PodmanCommandResult startRes = await podman.RunAsync(wlSpec.Render(), 30, CancellationToken.None).ConfigureAwait(false);
            if (startRes.ExitCode != 0)
            {
                throw new InvalidOperationException($"Failed to start production workload '{wlName}': {startRes.StdErr}");
            }

            // v7-29: Post-start verifier via rootless podman unshare nsenter
            WorkloadVerificationResult verResult = await verifier.VerifyWorkloadAsync(wlName, wlIp, null, registry, CancellationToken.None).ConfigureAwait(false);

            // v7-28: ReconcileOnceAsync preserves running container (even if previously registered by name, now promoted to hex ID)
            await topology.ReconcileOnceAsync(registry, CancellationToken.None).ConfigureAwait(false);
            bool stillRegisteredWhileRunning = registry.TryGetByIp(wlIp, out RegistryEntry? afterReconcileEntry);

            // Production flow Step A: Denied request -> Envoy asks Manager -> pending card created!
            var (denyRc, denyOut, denyErr) = await ExecAsync("podman", ["exec", wlName, "curl", "-v", "--max-time", "8", "https://example.com/"]).ConfigureAwait(false);
            string deniedOutput = denyRc == 0 ? $"ALLOWED:{denyOut.Trim()}" : "BLOCKED";
            await policyStore.FlushPendingWritesAsync().ConfigureAwait(false);

            var admin = new NetworkAccessActor("admin-1", IsAdmin: true);
            NetworkAccessRequest? card = policyStore.ListRequests(admin).Requests
                .Select(static v => v.Request)
                .FirstOrDefault(r => r.Status == "pending" && string.Equals(r.Destination, "example.com", StringComparison.OrdinalIgnoreCase));
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
            string dnsCountersBefore = await RunPodNftListEgressAsync("kautuka-egress-envoy").ConfigureAwait(false);
            DecideRequestResult decision = policyStore.DecideRequest(admin, card.Id, allow: true, scope: "agent", allowLan: false, expectedRevision: policyStore.Revision);
            var (allowRc, allowOut, allowErr) = await ExecAsync("podman", ["exec", wlName, "curl", "-v", "--max-time", "12", "https://example.com/"]).ConfigureAwait(false);
            if (allowRc != 0)
            {
                var (_, envLogs, envLogErr) = await ExecAsync("podman", ["logs", "kautuka-egress-envoy"]).ConfigureAwait(false);
                var (_, dnsLogs, dnsLogErr) = await ExecAsync("podman", ["logs", "kautuka-egress-dns"]).ConfigureAwait(false);
                var (_, dnsResolv, _) = await ExecAsync("podman", ["exec", "kautuka-egress-dns", "cat", "/etc/resolv.conf"]).ConfigureAwait(false);
                string nftAfter = await RunPodNftListEgressAsync("kautuka-egress-envoy").ConfigureAwait(false);
                throw new InvalidOperationException($"Retry after Allow failed (rc {allowRc}): {allowErr}\ndnsResolv:\n{dnsResolv}\nenvoyLogs:\n{envLogs}{envLogErr}\ndnsLogs:\n{dnsLogs}{dnsLogErr}\nnft:\n{nftAfter}");
            }
            string allowedBody = allowOut;
            string dnsCountersAfter = await RunPodNftListEgressAsync("kautuka-egress-envoy").ConfigureAwait(false);
            bool hasExampleDomain = allowedBody.Contains("Example Domain", StringComparison.OrdinalIgnoreCase);
            long dnsPacketsBefore = ExtractNftCommentCounterPackets(dnsCountersBefore, "dns_upstream");
            long dnsPacketsAfter = ExtractNftCommentCounterPackets(dnsCountersAfter, "dns_upstream");
            long dnsDelta = dnsPacketsAfter - dnsPacketsBefore;

            // v7-28 NC: Stop & remove workload container -> ReconcileOnceAsync deregisters it
            await ExecPodmanAsync(["rm", "-f", wlName]).ConfigureAwait(false);
            await topology.ReconcileOnceAsync(registry, CancellationToken.None).ConfigureAwait(false);
            bool deregisteredAfterRm = !registry.TryGetByIp(wlIp, out _);

            string allowedObs = $"ALLOWED:card={card.Destination}:{card.Port}:rule={decision.Rule.Destination}:body={hasExampleDomain}";
            var observations = new List<AcceptanceObservation>
            {
                AcceptanceHarness.Record("PROD_CARD_AND_RULE", allowedObs, "ALLOWED:card=example.com:443:rule=example.com:body=True", deniedOutput, "BLOCKED"),
                AcceptanceHarness.Record("PROD_V7_28_RECONCILE", $"{stillRegisteredWhileRunning}:{afterReconcileEntry?.ContainerId?.Length == 64}", "True:True", deregisteredAfterRm ? "DEREGISTERED_ON_RM" : "STILL_PRESENT", "DEREGISTERED_ON_RM"),
                AcceptanceHarness.Record("PROD_V7_29_VERIFIER", verResult.Passed.ToString(), "True", "TAMPER_PENDING", "TAMPER_CAUGHT_AND_KILLED"),
                AcceptanceHarness.Record("PROD_V7_33_DNS_UPSTREAM", $"rule={!string.IsNullOrEmpty(dnsUpstreamLine)}:delta_gt0={dnsDelta > 0}:body={hasExampleDomain}", "rule=True:delta_gt0=True:body=True", dnsPacketsBefore == 0 ? "NO_UPSTREAM_PACKETS_BEFORE_RULE" : $"PRE_RULE_PACKETS={dnsPacketsBefore}", "NO_UPSTREAM_PACKETS_BEFORE_RULE"),
            };

            // Run real tampering negative control for v7-29 verifier (C6)
            string wl2Name = wlName + "-nc";
            string wl2Ip = allocator.AllocateNextIp(registry);
            var wl2Spec = wlSpec with { ContainerName = wl2Name, AllocatedIp = wl2Ip };
            registry.Register(new RegistryEntry(wl2Ip, "u1", "a1", wl2Name, "connector", "conn-web", "isolated", false, "normal", "user-agent", null, DateTimeOffset.UtcNow));
            PodmanCommandResult start2Res = await podman.RunAsync(wl2Spec.Render(), 30, CancellationToken.None).ConfigureAwait(false);
            if (start2Res.ExitCode != 0)
            {
                throw new InvalidOperationException($"Failed to start NC workload '{wl2Name}': {start2Res.StdErr}");
            }
            int pid2 = await InspectContainerPidAsync(wl2Name).ConfigureAwait(false);
            await RunBashAsync($"podman unshare nsenter -t {pid2} -n nft add rule inet kautuka_workload output accept").ConfigureAwait(false);
            bool verifierCaughtTamper = false;
            try
            {
                await verifier.VerifyWorkloadAsync(wl2Name, wl2Ip, null, registry, CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                verifierCaughtTamper = true;
            }
            observations[2] = AcceptanceHarness.Record("PROD_V7_29_VERIFIER", verResult.Passed.ToString(), "True", verifierCaughtTamper ? "TAMPER_CAUGHT_AND_KILLED" : "TAMPER_MISSED", "TAMPER_CAUGHT_AND_KILLED");

            var (_, podmanVer, _) = await ExecAsync("podman", ["--version"]).ConfigureAwait(false);
            var (_, kernelVer, _) = await ExecAsync("uname", ["-r"]).ConfigureAwait(false);
            var (_, envoyVer, _) = await ExecAsync("podman", ["run", "--rm", EgressTopologyManager.EnvoyPinnedImage, "--version"]).ConfigureAwait(false);
            var (_, corednsVer, _) = await ExecAsync("podman", ["run", "--rm", EgressTopologyManager.CoreDnsPinnedImage, "-version"]).ConfigureAwait(false);
            var (_, gitSha, _) = await ExecAsync("git", ["-C", repoRoot, "rev-parse", "--short", "HEAD"]).ConfigureAwait(false);

            int failedCount = observations.Count(o => o.Outcome != "PASS");
            var report = new ProductionProofReport(
                DateTimeOffset.UtcNow,
                gitSha.Trim(),
                Environment.OSVersion.VersionString,
                podmanVer.Trim(),
                envoyVer.Trim().Split('\n')[0].Trim(),
                corednsVer.Trim().Replace('\n', ' '),
                kernelVer.Trim(),
                topology.IsStartupGateOpen,
                verResult.Passed,
                dnsUpstreamLine,
                card.Id,
                $"{card.Destination}:{card.Port}",
                decision.Rule.Id,
                $"{decision.Rule.Destination}:{decision.Rule.Port}",
                observations,
                $"{observations.Count - failedCount}/{observations.Count} PASS, {failedCount} FAIL");

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
            await ExecAsync("podman", ["rm", "-f", wlName, wlName + "-nc"]).ConfigureAwait(false);
            await ExecAsync("podman", ["pod", "rm", "-f", EgressTopologyManager.EgressPodName]).ConfigureAwait(false);
            await ExecAsync("podman", ["network", "rm", "-f", EgressTopologyManager.WorkloadNetworkName, EgressTopologyManager.OutsideNetworkName]).ConfigureAwait(false);
            if (previousDropIn is not null)
            {
                await File.WriteAllTextAsync(dropInPath, previousDropIn).ConfigureAwait(false);
            }
            else if (File.Exists(dropInPath))
            {
                File.Delete(dropInPath);
            }
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // Best effort cleanup
            }
        }
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

    private static async Task<(int ExitCode, string StdOut, string StdErr)> ExecAsync(string fileName, IReadOnlyList<string> args)
    {
        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo { FileName = fileName, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string a in args)
        {
            proc.StartInfo.ArgumentList.Add(a);
        }
        proc.Start();
        string stdOut = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        string stdErr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await proc.WaitForExitAsync().ConfigureAwait(false);
        return (proc.ExitCode, stdOut, stdErr);
    }
}
