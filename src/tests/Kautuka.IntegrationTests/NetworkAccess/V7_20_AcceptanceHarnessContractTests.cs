namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class V7_20_AcceptanceHarnessContractTests
{
    [Fact]
    public void ProductionAcceptanceKeepsRenderedCorefileStableAndIsolatesEnvoyNegativeControl()
    {
        string sourcePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "NetworkAccessAcceptanceProgram.cs");
        string source = File.ReadAllText(sourcePath);
        const string methodStart = "private static async Task<int> RunProductionTopologyProofAsync(";
        const string methodEnd = "private static async Task BestEffortCleanupAsync(";
        int start = source.IndexOf(methodStart, StringComparison.Ordinal);
        Assert.True(start >= 0, "The production acceptance method source must be available for the contract check.");
        int end = source.IndexOf(methodEnd, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "The production acceptance method source must be available for the contract check.");
        string productionProof = source[start..end];

        Assert.Contains(".nip.io", productionProof, StringComparison.Ordinal);
        Assert.Contains("The live NAC proof requires external nip.io DNS resolution.", productionProof, StringComparison.Ordinal);
        Assert.Contains("The acceptance harness changed the production CoreDNS configuration after topology startup.", productionProof, StringComparison.Ordinal);
        Assert.Contains("authorizationGateBypassed=True:packetPathOnly=True", productionProof, StringComparison.Ordinal);
        Assert.Contains("NetworkAccessTopologyGateTests", productionProof, StringComparison.Ordinal);
        Assert.DoesNotContain("File.WriteAllTextAsync(corefilePath", productionProof, StringComparison.Ordinal);

        int dnsProbeHost = productionProof.IndexOf("string dnsProbeHostName = $\"dns-proof-{Guid.NewGuid():N}.{lanHostAddress}.nip.io\"", StringComparison.Ordinal);
        int dnsProbeRule = productionProof.IndexOf("dnsProbeHostName, \"host\", 8443, Allowed: true, AllowLan: true", StringComparison.Ordinal);
        int upstreamConntrackClear = productionProof.IndexOf("foreach (string upstreamAddress in dnsProbeUpstreamAddresses)", StringComparison.Ordinal);
        int liveCounterBefore = productionProof.IndexOf("string dnsProbeCountersBefore = await RunPodNftListEgressAsync", StringComparison.Ordinal);
        int uniqueApprovedLookup = productionProof.IndexOf("[\"exec\", wlName, \"curl\", \"--insecure\", \"--silent\", \"--show-error\", \"--max-time\", \"8\", \"https://\" + dnsProbeHostName", StringComparison.Ordinal);
        int liveCounterAfter = productionProof.IndexOf("string dnsProbeCountersAfter = await RunPodNftListEgressAsync", StringComparison.Ordinal);
        int dnsPositiveDelta = productionProof.IndexOf("long dnsProbeDelta = dnsProbePacketsAfter - dnsProbePacketsBefore", StringComparison.Ordinal);
        Assert.True(dnsProbeHost >= 0 && dnsProbeRule > dnsProbeHost && upstreamConntrackClear > dnsProbeRule,
            "A unique per-run host must have an approved LAN rule and its resolver conntrack flows must be cleared first.");
        Assert.True(liveCounterBefore > upstreamConntrackClear && uniqueApprovedLookup > liveCounterBefore
            && liveCounterAfter > uniqueApprovedLookup && dnsPositiveDelta > liveCounterAfter,
            "The probe must sample the live rule, issue the approved workload lookup, then verify a counted packet delta.");
        Assert.Contains("Zero is valid: it means no established conntrack flow existed", productionProof, StringComparison.Ordinal);
        Assert.Contains("TryParseConntrackDeleteResult(conntrackClearRc, conntrackClearOut, conntrackClearErr", productionProof, StringComparison.Ordinal);
        Assert.Contains("--self-test-conntrack-delete-result", source, StringComparison.Ordinal);
        Assert.Contains("exitCode != 0 && removedFlowCount == 0", source, StringComparison.Ordinal);
        Assert.Contains("Operation failed: Permission denied", source, StringComparison.Ordinal);
        Assert.Contains("command not found", source, StringComparison.Ordinal);
        Assert.Contains("if (!dnsProbeBodyReceived || dnsProbeDelta <= 0)", productionProof, StringComparison.Ordinal);
        Assert.Contains("dnsProbeBodyReceived = dnsProbeRc == 0 && dnsProbeBody.Contains(\"NAC LAN fixture ok\"", productionProof, StringComparison.Ordinal);
        Assert.Contains("policyStore.DeleteRule(admin, dnsProbeRule.Id", productionProof, StringComparison.Ordinal);
        Assert.Contains("delta_gt0={dnsProbeDelta > 0}:body={hasExampleDomain}", productionProof, StringComparison.Ordinal);
        Assert.Contains("body={hasExampleDomain}", productionProof, StringComparison.Ordinal);

        int lastWorkloadLaunch = productionProof.LastIndexOf("SpawnSandboxAsync(", StringComparison.Ordinal);
        int managerDispose = productionProof.IndexOf("await topology.DisposeAsync()", StringComparison.Ordinal);
        int envoyMutation = productionProof.IndexOf("await File.WriteAllTextAsync(envoyConfigPath, mutatedEnvoyConfig)", StringComparison.Ordinal);
        Assert.True(managerDispose > lastWorkloadLaunch, "The manager must remain active through the last normal workload launch.");
        Assert.True(envoyMutation > managerDispose, "The deliberate Envoy config mutation must happen after the manager's reconcile lifecycle stops.");
        Assert.DoesNotContain("SpawnSandboxAsync(", productionProof[managerDispose..], StringComparison.Ordinal);

        const string dnsNegativeControl = "// v7-33 negative control";
        int dnsControlStart = productionProof.IndexOf(dnsNegativeControl, StringComparison.Ordinal);
        Assert.True(dnsControlStart >= 0, "The final DNS-upstream negative control must remain identifiable.");
        string dnsControlSource = productionProof[dnsControlStart..];
        int freshCounters = dnsControlSource.IndexOf("string dnsNcCountersBefore = await RunPodNftListEgressAsync", StringComparison.Ordinal);
        int freshHandle = dnsControlSource.IndexOf("ExtractNftCommentRuleHandle(dnsNcCountersBefore, \"dns_upstream\")", StringComparison.Ordinal);
        int freshRejectBaseline = dnsControlSource.IndexOf("ExtractNftCommentCounterPackets(dnsNcCountersBefore, \"private_reject\")", StringComparison.Ordinal);
        int ruleDeletion = dnsControlSource.IndexOf("nft delete rule ip kautuka_egress output handle", StringComparison.Ordinal);
        Assert.True(freshCounters >= 0 && freshCounters < freshHandle && freshHandle < ruleDeletion,
            "The DNS negative control must refresh and use the live rule handle immediately before deleting it.");
        Assert.True(freshRejectBaseline >= 0 && freshRejectBaseline < ruleDeletion,
            "The DNS negative control must sample its private_reject baseline after earlier negative controls.");
    }
}
