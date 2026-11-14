namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class V7_20_AcceptanceHarnessContractTests
{
    [Fact]
    public void ProductionAcceptanceKeepsRenderedCorefileStableAndIsolatesEnvoyNegativeControl()
    {
        string sourcePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "NetworkAccessAcceptanceProgram.cs");
        string source = File.ReadAllText(sourcePath);
        const string methodStart = "private static async Task<int> RunProductionTopologyProofAsync(string? outputReportPath)";
        const string methodEnd = "private static async Task BestEffortCleanupAsync(";
        int start = source.IndexOf(methodStart, StringComparison.Ordinal);
        int end = source.IndexOf(methodEnd, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "The production acceptance method source must be available for the contract check.");
        string productionProof = source[start..end];

        Assert.Contains(".nip.io", productionProof, StringComparison.Ordinal);
        Assert.Contains("The live NAC proof requires external nip.io DNS resolution.", productionProof, StringComparison.Ordinal);
        Assert.Contains("The acceptance harness changed the production CoreDNS configuration after topology startup.", productionProof, StringComparison.Ordinal);
        Assert.Contains("authorizationGate=notAttached:packetPathOnly=True", productionProof, StringComparison.Ordinal);
        Assert.Contains("NetworkAccessTopologyGateTests", productionProof, StringComparison.Ordinal);
        Assert.DoesNotContain("File.WriteAllTextAsync(corefilePath", productionProof, StringComparison.Ordinal);

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
