using System.Text.RegularExpressions;

using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.NetworkAccessController;

using NacController = Kautuka.NetworkAccessController.NetworkAccessController;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class U8_NonPublicV4AndOpenRulesTests : IDisposable
{
    private static readonly string[] goldenNonPublicV4 =
    [
        "10.0.0.0/8",
        "172.16.0.0/12",
        "192.168.0.0/16",
        "100.64.0.0/10",
        "127.0.0.0/8",
        "169.254.0.0/16",
        "0.0.0.0/8",
        "192.0.0.0/24",
        "198.18.0.0/15",
        "224.0.0.0/3",
        "168.63.129.16/32",
    ];

    private readonly string tempDir;

    public U8_NonPublicV4AndOpenRulesTests()
    {
        tempDir = Path.Combine(Path.GetTempPath(), "kautuka-nac-u8-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void U8_NonPublicV4_MatchesAcrossAllFourSurfaces_WithNegativeControl()
    {
        IReadOnlyList<string> ruleValidationRanges = NetworkAddressClassifier.GetRuleValidationNonPublicRanges();
        IReadOnlyList<string> notPublicRanges = NetworkAddressClassifier.GetNotPublicDecisionRanges();
        string envoyYaml = NetworkAddressClassifier.RenderEnvoyResolvedAddressFilterYaml();
        IReadOnlyList<string> envoyRanges = NetworkAddressClassifier.ExtractRangesFromEnvoyFilterYaml(envoyYaml);
        string nftRule = NetworkAddressClassifier.RenderNftPrivateRejectRule();
        IReadOnlyList<string> nftRanges = NetworkAddressClassifier.ExtractRangesFromNftPrivateReject(nftRule);

        Assert.Equal(goldenNonPublicV4, ruleValidationRanges);
        Assert.Equal(goldenNonPublicV4, notPublicRanges);
        Assert.Equal(goldenNonPublicV4, envoyRanges);
        Assert.Equal(goldenNonPublicV4, nftRanges);

        // Also verify rendered BoundaryRulesetRenderer templates
        string renderedBoundaryNft = BoundaryRulesetRenderer.RenderBoundaryNft(includeLanGrants: false);
        IReadOnlyList<string> boundaryNftRanges = NetworkAddressClassifier.ExtractRangesFromNftPrivateReject(renderedBoundaryNft);
        Assert.Equal(goldenNonPublicV4, boundaryNftRanges);

        string renderedEnvoyConfig = BoundaryRulesetRenderer.RenderEnvoyConfig();
        IReadOnlyList<string> renderedEnvoyRanges = NetworkAddressClassifier.ExtractRangesFromEnvoyFilterYaml(renderedEnvoyConfig);
        Assert.Equal(goldenNonPublicV4, renderedEnvoyRanges);

        IReadOnlyList<IReadOnlyList<string>> renderedEnvoyFilters = ExtractAllResolvedAddressFilters(renderedEnvoyConfig);
        Assert.Equal(4, renderedEnvoyFilters.Count);
        Assert.Equal(goldenNonPublicV4, renderedEnvoyFilters[0]);
        // Independent literal golden: do not derive this from the public list or renderer filter.
        string[] expectedLanRanges =
        [
            "100.64.0.0/10",
            "127.0.0.0/8",
            "169.254.0.0/16",
            "0.0.0.0/8",
            "192.0.0.0/24",
            "198.18.0.0/15",
            "224.0.0.0/3",
            "168.63.129.16/32",
            "10.247.0.0/16",
        ];
        Assert.Equal(expectedLanRanges, renderedEnvoyFilters[1]);
        Assert.Equal(goldenNonPublicV4, renderedEnvoyFilters[2]);
        Assert.Equal(expectedLanRanges, renderedEnvoyFilters[3]);

        // Negative control (.NC): remove one range from the Envoy rendering only -> golden comparison fails
        string incompleteEnvoyYaml = NetworkAddressClassifier.RenderEnvoyResolvedAddressFilterYaml(
            NetworkAddressClassifier.NonPublicV4.Take(NetworkAddressClassifier.NonPublicV4.Count - 1));
        IReadOnlyList<string> incompleteEnvoyRanges = NetworkAddressClassifier.ExtractRangesFromEnvoyFilterYaml(incompleteEnvoyYaml);
        Assert.NotEqual(goldenNonPublicV4, incompleteEnvoyRanges);
    }

    private static IReadOnlyList<IReadOnlyList<string>> ExtractAllResolvedAddressFilters(string yaml)
    {
        string[] lines = yaml.Split(['\r', '\n'], StringSplitOptions.None);
        var filters = new List<IReadOnlyList<string>>();
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            if (!line.Trim().Equals("resolved_address_filter:", StringComparison.Ordinal))
            {
                continue;
            }

            int filterIndent = line.Length - line.TrimStart().Length;
            var ranges = new List<string>();
            string? addressPrefix = null;
            for (int next = index + 1; next < lines.Length; next++)
            {
                string candidate = lines[next];
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                int indent = candidate.Length - candidate.TrimStart().Length;
                if (indent <= filterIndent)
                {
                    break;
                }

                Match address = Regex.Match(candidate.Trim(), "^- address_prefix: (?<address>[^\\s]+)$", RegexOptions.CultureInvariant);
                if (address.Success)
                {
                    addressPrefix = address.Groups["address"].Value;
                    continue;
                }

                Match prefixLength = Regex.Match(candidate.Trim(), "^prefix_len: (?<length>\\d+)$", RegexOptions.CultureInvariant);
                if (prefixLength.Success && addressPrefix is not null)
                {
                    ranges.Add(addressPrefix + "/" + prefixLength.Groups["length"].Value);
                    addressPrefix = null;
                }
            }

            filters.Add(ranges);
        }

        return filters;
    }

    [Fact]
    public void U8_AnyPublicRule_EnforcesConstraintsAndBlocksNonPublicV4_WithNegativeControl()
    {
        string statePath = Path.Combine(tempDir, "state.json");
        var store = new NetworkAccessPolicyStore(statePath);
        var registry = new WorkloadRegistry();
        var controller = new NacController(store, registry);

        var admin = new NetworkAccessActor("admin-1", IsAdmin: true);
        var userX = new NetworkAccessActor("user-x", IsAdmin: false);
        store.SetUserPrivileges(admin, "user-x", canApproveOwn: true, canAllowAllAgents: true);

        // Reject writing any-public by non-admin, at agent scope, without connectorId, or for agent-sandbox
        Assert.Throws<UnauthorizedAccessException>(() =>
            store.SetRule(userX, new RuleWriteSpec("user", "user-x", null, "conn-web", "", "any-public", 0, true), store.Revision));
        Assert.Throws<ArgumentException>(() =>
            store.SetRule(admin, new RuleWriteSpec("agent", "user-x", "a1", null, "", "any-public", 0, true), store.Revision));
        Assert.Throws<ArgumentException>(() =>
            store.SetRule(admin, new RuleWriteSpec("user", "user-x", null, null, "", "any-public", 0, true), store.Revision));
        Assert.Throws<ArgumentException>(() =>
            store.SetRule(admin, new RuleWriteSpec("user", "user-x", null, "conn-web", "", "any-public", 0, true, ExecContext: "agent-sandbox"), store.Revision));

        // Admin writes valid any-public rule for (user-x, conn-web)
        (_, NetworkAccessRuleView openRule) = store.SetRule(
            admin,
            new RuleWriteSpec("user", "user-x", null, "conn-web", "", "any-public", 0, Allowed: true, ExecContext: "isolated"),
            store.Revision);
        Assert.True(openRule.OpenInternetBadge);

        // Register dedicated Isolated connector container for (user-x, a1, conn-web)
        registry.Register(new RegistryEntry(
            "10.247.1.50",
            "user-x",
            "a1",
            "ctr-conn-web",
            "connector",
            "conn-web",
            "isolated",
            Systemwide: false,
            Egress: "normal",
            Attribution: "user-agent",
            CompatProfile: null,
            DateTimeOffset.UtcNow));

        // Public IP literal and public hostname are allowed with reason any_public and OpenInternetBadge = true
        NetworkAccessDecisionResult pubIpDecision = controller.EvaluateContainerConnection(
            "10.247.1.50",
            "93.184.216.34",
            8080,
            isTls: false,
            sni: null);
        Assert.True(pubIpDecision.Allowed);
        Assert.Equal("any_public", pubIpDecision.Reason);
        Assert.True(pubIpDecision.OpenInternetBadge);

        // Every NonPublicV4 range representative is denied with reason not_public (or sinkhole_plain for 198.18.250.250)
        string[] nonPublicSampleIps =
        [
            "10.1.2.3",
            "172.16.5.4",
            "192.168.1.1",
            "100.64.1.1",
            "127.0.0.1",
            "169.254.169.254",
            "0.0.0.1",
            "192.0.0.8",
            "198.18.0.1",
            "224.0.0.1",
            "168.63.129.16",
        ];

        foreach (string sampleIp in nonPublicSampleIps)
        {
            NetworkAccessDecisionResult nonPubDecision = controller.EvaluateContainerConnection(
                "10.247.1.50",
                sampleIp,
                443,
                isTls: false,
                sni: null);
            Assert.False(nonPubDecision.Allowed);
            Assert.Equal("not_public", nonPubDecision.Reason);
        }

        // An agent block beats any-public
        (_, NetworkAccessRuleView blockRule) = store.SetRule(
            admin,
            new RuleWriteSpec("agent", "user-x", "a1", null, "93.184.216.34", "ip", 8080, Allowed: false),
            store.Revision);
        NetworkAccessDecisionResult blocked = controller.EvaluateContainerConnection("10.247.1.50", "93.184.216.34", 8080, isTls: false, sni: null);
        Assert.False(blocked.Allowed);
        Assert.Equal("blocked", blocked.Reason);

        // Negative control (.NC): delete the agent block -> allowed again; delete the any-public rule -> public IP is denied (no_rule)
        store.DeleteRule(admin, blockRule.Rule.Id, store.Revision);
        Assert.True(controller.EvaluateContainerConnection("10.247.1.50", "93.184.216.34", 8080, isTls: false, sni: null).Allowed);

        store.DeleteRule(admin, openRule.Rule.Id, store.Revision);
        NetworkAccessDecisionResult afterDelete = controller.EvaluateContainerConnection("10.247.1.50", "93.184.216.34", 8080, isTls: false, sni: null);
        Assert.False(afterDelete.Allowed);
        Assert.Equal("no_rule", afterDelete.Reason);
    }
}
