using Kautuka.NetworkAccessController;

using NacController = Kautuka.NetworkAccessController.NetworkAccessController;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class U1_DecisionFunctionTests : IDisposable
{
    private readonly string tempDir;

    public U1_DecisionFunctionTests()
    {
        tempDir = Path.Combine(Path.GetTempPath(), "kautuka-nac-u1-" + Guid.NewGuid().ToString("N"));
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
            // Best effort on Windows
        }
    }

    public static TheoryData<string, bool, string> SniNormalizationVectors() => new()
    {
        // Valid SNI hostnames (normalized to lowercase A-label without trailing dot)
        { "api.example.com", true, "api.example.com" },
        { "API.EXAMPLE.COM", true, "api.example.com" },
        { "Api.Example.Com.", true, "api.example.com" },
        { "sub-1.deep.example.co.uk", true, "sub-1.deep.example.co.uk" },
        { "bücher.example.com", true, "xn--bcher-kva.example.com" },
        { "MÜNCHEN.EXAMPLE.DE.", true, "xn--mnchen-3ya.example.de" },
        { "a.b.c.d.e.example.org", true, "a.b.c.d.e.example.org" },
        { "99bottles.example.com", true, "99bottles.example.com" },

        // Hostile / malformed SNI inputs (all must be rejected as bad_sni)
        { "", false, "" },
        { "   ", false, "" },
        { ".", false, "" },
        { "..", false, "" },
        { ".example.com", false, "" },
        { "example.com..", false, "" },
        { "example..com", false, "" },
        { "allowed.test@evil.com", false, "" },
        { "allowed.test:443", false, "" },
        { "allowed.test/path", false, "" },
        { "allowed.test?query=1", false, "" },
        { "allowed.test#frag", false, "" },
        { "allowed%20.test", false, "" },
        { "allowed .test", false, "" },
        { "\tallowed.test", false, "" },
        { "allowed.test\n", false, "" },
        { "*.example.com", false, "" },
        { "-badlabel.example.com", false, "" },
        { "badlabel-.example.com", false, "" },
        { "192.168.1.1", false, "" },
        { "127.0.0.1", false, "" },
        { "example.123", false, "" },
        { new string('a', 64) + ".example.com", false, "" },
        { new string('a', 60) + "." + new string('b', 60) + "." + new string('c', 60) + "." + new string('d', 60) + ".example.com", false, "" },
    };

    [Theory]
    [MemberData(nameof(SniNormalizationVectors))]
    public void U1_SniNormalization_HandlesValidAndHostileVectors_WithNegativeControl(
        string rawSni,
        bool expectedValid,
        string expectedNormalized)
    {
        NacController controller = CreateController(out _);
        RegisterOrdinaryAgent(controller, "10.247.1.10", "user-x", "agent-1");

        string targetHost = expectedValid ? expectedNormalized : "api.example.com";
        var admin = new NetworkAccessActor("admin-1", IsAdmin: true);
        (_, NetworkAccessRuleView createdRule) = controller.PolicyStore.SetRule(
            admin,
            new RuleWriteSpec("agent", "user-x", "agent-1", null, targetHost, "host", 443, Allowed: true),
            controller.PolicyStore.Revision);

        NetworkAccessDecisionResult result = controller.EvaluateContainerConnection(
            "10.247.1.10",
            "198.18.250.250",
            443,
            isTls: true,
            sni: rawSni);

        if (expectedValid)
        {
            Assert.True(result.Allowed);
            Assert.Equal(NacController.ClusterDfpPublic, result.Cluster);

            // Negative control (.NC): remove the rule -> must flip to denied (no_rule)
            controller.PolicyStore.DeleteRule(admin, createdRule.Rule.Id, controller.PolicyStore.Revision);
            NetworkAccessDecisionResult ncResult = controller.EvaluateContainerConnection(
                "10.247.1.10",
                "198.18.250.250",
                443,
                isTls: true,
                sni: rawSni);
            Assert.False(ncResult.Allowed);
            Assert.Equal("no_rule", ncResult.Reason);
        }
        else
        {
            Assert.False(result.Allowed);
            Assert.Equal(string.IsNullOrEmpty(rawSni) ? "tls_no_sni" : "bad_sni", result.Reason);
            Assert.Null(result.RequestId);

            // Negative control (.NC): valid twin SNI against the same rule succeeds
            NetworkAccessDecisionResult ncValidTwin = controller.EvaluateContainerConnection(
                "10.247.1.10",
                "198.18.250.250",
                443,
                isTls: true,
                sni: targetHost);
            Assert.True(ncValidTwin.Allowed);
        }
    }

    [Fact]
    public void U1_ExactHostAndPortMatching_RejectsSubstringsAffixesAndPortMismatch_WithNegativeControl()
    {
        NacController controller = CreateController(out _);
        RegisterOrdinaryAgent(controller, "10.247.1.11", "user-x", "agent-1");
        var admin = new NetworkAccessActor("admin-1", IsAdmin: true);

        controller.PolicyStore.SetRule(
            admin,
            new RuleWriteSpec("agent", "user-x", "agent-1", null, "allowed.test", "host", 443, Allowed: true),
            controller.PolicyStore.Revision);

        // Hostile lookalikes and port mismatch must all be denied as no_rule
        string[] hostileHosts = ["evil-allowed.test", "allowed.test.evil", "sub.allowed.test", "allowed.tes"];
        foreach (string host in hostileHosts)
        {
            NetworkAccessDecisionResult denied = controller.EvaluateContainerConnection(
                "10.247.1.11",
                "198.18.250.250",
                443,
                isTls: true,
                sni: host);
            Assert.False(denied.Allowed);
            Assert.Equal("no_rule", denied.Reason);
        }

        NetworkAccessDecisionResult wrongPort = controller.EvaluateContainerConnection(
            "10.247.1.11",
            "198.18.250.250",
            8443,
            isTls: true,
            sni: "allowed.test");
        Assert.False(wrongPort.Allowed);
        Assert.Equal("no_rule", wrongPort.Reason);

        // Negative control (.NC): exact host and port 443 succeeds
        NetworkAccessDecisionResult exactMatch = controller.EvaluateContainerConnection(
            "10.247.1.11",
            "198.18.250.250",
            443,
            isTls: true,
            sni: "allowed.test");
        Assert.True(exactMatch.Allowed);
        Assert.Equal("rule_agent", exactMatch.Reason);
    }

    [Fact]
    public void U1_DecisionOrder_CoversUnknownSource_DenyAll_ProxyOnly_TlsNoSni_SinkholePlain_AndLanClusters_WithNegativeControl()
    {
        NacController controller = CreateController(out _);
        var admin = new NetworkAccessActor("admin-1", IsAdmin: true);

        // 1. Unknown source IP -> unknown_source, no request recorded
        NetworkAccessDecisionResult unknown = controller.EvaluateContainerConnection(
            "10.247.9.99",
            "93.184.216.34",
            443,
            isTls: true,
            sni: "allowed.test");
        Assert.False(unknown.Allowed);
        Assert.Equal("unknown_source", unknown.Reason);
        Assert.Empty(controller.PolicyStore.ListRequests(admin).Requests);

        // 1a. Egress = deny-all (system:: parser) and proxy-only (builder) -> deny_all, even if rules exist
        controller.Registry.Register(new RegistryEntry(
            "10.247.1.20",
            UserId: null,
            AgentId: null,
            ContainerId: "ctr-parser",
            Kind: "system",
            ConnectorId: "markitdown",
            ExecContext: "system",
            Systemwide: false,
            Egress: "deny-all",
            Attribution: "none",
            CompatProfile: null,
            DateTimeOffset.UtcNow));
        controller.Registry.Register(new RegistryEntry(
            "10.247.1.21",
            UserId: null,
            AgentId: null,
            ContainerId: "ctr-builder",
            Kind: "builder",
            ConnectorId: null,
            ExecContext: "builder",
            Systemwide: false,
            Egress: "proxy-only",
            Attribution: "none",
            CompatProfile: null,
            DateTimeOffset.UtcNow));

        controller.PolicyStore.SetRule(
            admin,
            new RuleWriteSpec("connector-system", null, null, "markitdown", "allowed.test", "host", 443, Allowed: true),
            controller.PolicyStore.Revision);

        NetworkAccessDecisionResult parserRes = controller.EvaluateContainerConnection("10.247.1.20", "198.18.250.250", 443, isTls: true, sni: "allowed.test");
        Assert.False(parserRes.Allowed);
        Assert.Equal("deny_all", parserRes.Reason);

        NetworkAccessDecisionResult builderRes = controller.EvaluateContainerConnection("10.247.1.21", "198.18.250.250", 443, isTls: true, sni: "allowed.test");
        Assert.False(builderRes.Allowed);
        Assert.Equal("deny_all", builderRes.Reason);
        Assert.Empty(controller.PolicyStore.ListRequests(admin).Requests);

        // Negative control (.NC): register an ordinary agent on 10.247.1.22 with an allow rule -> allowed
        RegisterOrdinaryAgent(controller, "10.247.1.22", "user-x", "agent-1");
        controller.PolicyStore.SetRule(
            admin,
            new RuleWriteSpec("agent", "user-x", "agent-1", null, "allowed.test", "host", 443, Allowed: true),
            controller.PolicyStore.Revision);
        Assert.True(controller.EvaluateContainerConnection("10.247.1.22", "198.18.250.250", 443, isTls: true, sni: "allowed.test").Allowed);

        // 3. TLS with no SNI -> tls_no_sni
        NetworkAccessDecisionResult noSni = controller.EvaluateContainerConnection("10.247.1.22", "93.184.216.34", 443, isTls: true, sni: null);
        Assert.False(noSni.Allowed);
        Assert.Equal("tls_no_sni", noSni.Reason);

        // 4. Plain TCP to sinkhole 198.18.250.250 -> sinkhole_plain, no request recorded
        NetworkAccessDecisionResult sinkholePlain = controller.EvaluateContainerConnection("10.247.1.22", "198.18.250.250", 80, isTls: false, sni: null);
        Assert.False(sinkholePlain.Allowed);
        Assert.Equal("sinkhole_plain", sinkholePlain.Reason);
        Assert.Empty(controller.PolicyStore.ListRequests(admin).Requests);

        // Plain TCP to approved public IP -> orig_dst_public; LAN rule -> orig_dst_lan / dfp_lan
        controller.PolicyStore.SetRule(
            admin,
            new RuleWriteSpec("agent", "user-x", "agent-1", null, "93.184.216.34", "ip", 8080, Allowed: true, AllowLan: false),
            controller.PolicyStore.Revision);
        NetworkAccessDecisionResult plainPublic = controller.EvaluateContainerConnection("10.247.1.22", "93.184.216.34", 8080, isTls: false, sni: null);
        Assert.True(plainPublic.Allowed);
        Assert.Equal(NacController.ClusterOrigDstPublic, plainPublic.Cluster);

        controller.PolicyStore.SetRule(
            admin,
            new RuleWriteSpec("agent", "user-x", "agent-1", null, "192.168.1.50", "ip", 8080, Allowed: true, AllowLan: true),
            controller.PolicyStore.Revision);
        NetworkAccessDecisionResult plainLan = controller.EvaluateContainerConnection("10.247.1.22", "192.168.1.50", 8080, isTls: false, sni: null);
        Assert.True(plainLan.Allowed);
        Assert.Equal(NacController.ClusterOrigDstLan, plainLan.Cluster);

        controller.PolicyStore.SetRule(
            admin,
            new RuleWriteSpec("agent", "user-x", "agent-1", null, "nas.home.arpa", "host", 443, Allowed: true, AllowLan: true),
            controller.PolicyStore.Revision);
        NetworkAccessDecisionResult tlsLan = controller.EvaluateContainerConnection("10.247.1.22", "198.18.250.250", 443, isTls: true, sni: "nas.home.arpa");
        Assert.True(tlsLan.Allowed);
        Assert.Equal(NacController.ClusterDfpLan, tlsLan.Cluster);
    }

    [Fact]
    public void U1_BlockedBeatsSeed_SystemActorCheckOutbound_WithNegativeControl()
    {
        NacController controller = CreateController(out _);
        var admin = new NetworkAccessActor("admin-1", IsAdmin: true);

        // Positive baseline: seeded host api.openai.com:443 is allowed for system/gateway
        NetworkAccessDecisionResult seedAllowed = controller.EvaluateOutboundCheck(
            new OutboundCheckInput("system", "gateway", null, false, "api.openai.com", 443, "model-provider", "corr-1"));
        Assert.True(seedAllowed.Allowed);
        Assert.Equal("seed", seedAllowed.Reason);

        // Explicit block rule beats seed
        (_, NetworkAccessRuleView blockRule) = controller.PolicyStore.SetRule(
            admin,
            new RuleWriteSpec("agent", "system", "gateway", null, "api.openai.com", "host", 443, Allowed: false),
            controller.PolicyStore.Revision);

        NetworkAccessDecisionResult blocked = controller.EvaluateOutboundCheck(
            new OutboundCheckInput("system", "gateway", null, false, "api.openai.com", 443, "model-provider", "corr-2"));
        Assert.False(blocked.Allowed);
        Assert.Equal("blocked", blocked.Reason);

        // Negative control (.NC): delete the block rule -> seed allows again
        controller.PolicyStore.DeleteRule(admin, blockRule.Rule.Id, controller.PolicyStore.Revision);
        NetworkAccessDecisionResult restored = controller.EvaluateOutboundCheck(
            new OutboundCheckInput("system", "gateway", null, false, "api.openai.com", 443, "model-provider", "corr-3"));
        Assert.True(restored.Allowed);
        Assert.Equal("seed", restored.Reason);
    }

    [Fact]
    public void U1_ScopeResolutionMatrix_OrdinaryAndSystemwideConnectors_WithNegativeControl()
    {
        NacController controller = CreateController(out _);
        var admin = new NetworkAccessActor("admin-1", IsAdmin: true);

        // Grant user-x both privileges so user-scope rules are active
        controller.PolicyStore.SetUserPrivileges(admin, "user-x", canApproveOwn: true, canAllowAllAgents: true);

        RegisterOrdinaryAgent(controller, "10.247.1.31", "user-x", "a1");
        RegisterOrdinaryAgent(controller, "10.247.1.32", "user-x", "a2");
        RegisterOrdinaryAgent(controller, "10.247.1.33", "user-y", "b1");

        // Systemwide connector with attribution = user (for user-x and user-y)
        controller.Registry.Register(new RegistryEntry(
            "10.247.1.41",
            UserId: "user-x",
            AgentId: null,
            ContainerId: "ctr-sys-x",
            Kind: "connector",
            ConnectorId: "conn-sys",
            ExecContext: "isolated",
            Systemwide: true,
            Egress: "normal",
            Attribution: "user",
            CompatProfile: null,
            DateTimeOffset.UtcNow));
        controller.Registry.Register(new RegistryEntry(
            "10.247.1.42",
            UserId: "user-y",
            AgentId: null,
            ContainerId: "ctr-sys-y",
            Kind: "connector",
            ConnectorId: "conn-sys",
            ExecContext: "isolated",
            Systemwide: true,
            Egress: "normal",
            Attribution: "user",
            CompatProfile: null,
            DateTimeOffset.UtcNow));

        // Shared systemwide connector with attribution = none
        controller.Registry.Register(new RegistryEntry(
            "10.247.1.43",
            UserId: null,
            AgentId: null,
            ContainerId: "ctr-sys-shared",
            Kind: "connector",
            ConnectorId: "conn-sys",
            ExecContext: "isolated",
            Systemwide: true,
            Egress: "normal",
            Attribution: "none",
            CompatProfile: null,
            DateTimeOffset.UtcNow));

        // 1. Agent rule for (user-x, a1) allows a1, NOT a2, and NEVER applies to systemwide connector
        (_, NetworkAccessRuleView a1Rule) = controller.PolicyStore.SetRule(
            admin,
            new RuleWriteSpec("agent", "user-x", "a1", null, "host1.test", "host", 443, Allowed: true),
            controller.PolicyStore.Revision);
        Assert.True(controller.EvaluateContainerConnection("10.247.1.31", "198.18.250.250", 443, true, "host1.test").Allowed);
        Assert.False(controller.EvaluateContainerConnection("10.247.1.32", "198.18.250.250", 443, true, "host1.test").Allowed);
        Assert.False(controller.EvaluateContainerConnection("10.247.1.41", "198.18.250.250", 443, true, "host1.test").Allowed);

        // 2. User rule for user-x (no connector) allows a1, a2, and a3 registered later; never user-y's b1; never systemwide connector
        (_, NetworkAccessRuleView xUserRule) = controller.PolicyStore.SetRule(
            admin,
            new RuleWriteSpec("user", "user-x", null, null, "host2.test", "host", 443, Allowed: true),
            controller.PolicyStore.Revision);
        RegisterOrdinaryAgent(controller, "10.247.1.34", "user-x", "a3");
        Assert.True(controller.EvaluateContainerConnection("10.247.1.31", "198.18.250.250", 443, true, "host2.test").Allowed);
        Assert.True(controller.EvaluateContainerConnection("10.247.1.32", "198.18.250.250", 443, true, "host2.test").Allowed);
        Assert.True(controller.EvaluateContainerConnection("10.247.1.34", "198.18.250.250", 443, true, "host2.test").Allowed);
        Assert.False(controller.EvaluateContainerConnection("10.247.1.33", "198.18.250.250", 443, true, "host2.test").Allowed);
        Assert.False(controller.EvaluateContainerConnection("10.247.1.41", "198.18.250.250", 443, true, "host2.test").Allowed);

        // 3. Agent block for (user-x, a1) beats user-x's user allow
        (_, NetworkAccessRuleView a1Block) = controller.PolicyStore.SetRule(
            admin,
            new RuleWriteSpec("agent", "user-x", "a1", null, "host2.test", "host", 443, Allowed: false),
            controller.PolicyStore.Revision);
        NetworkAccessDecisionResult a1BlockedRes = controller.EvaluateContainerConnection("10.247.1.31", "198.18.250.250", 443, true, "host2.test");
        Assert.False(a1BlockedRes.Allowed);
        Assert.Equal("blocked", a1BlockedRes.Reason);
        Assert.True(controller.EvaluateContainerConnection("10.247.1.32", "198.18.250.250", 443, true, "host2.test").Allowed);

        // Negative control (.NC): delete a1 block -> a1 allowed again; delete user rule -> a2 and a3 denied
        controller.PolicyStore.DeleteRule(admin, a1Block.Rule.Id, controller.PolicyStore.Revision);
        Assert.True(controller.EvaluateContainerConnection("10.247.1.31", "198.18.250.250", 443, true, "host2.test").Allowed);
        controller.PolicyStore.DeleteRule(admin, xUserRule.Rule.Id, controller.PolicyStore.Revision);
        Assert.False(controller.EvaluateContainerConnection("10.247.1.32", "198.18.250.250", 443, true, "host2.test").Allowed);
        Assert.False(controller.EvaluateContainerConnection("10.247.1.34", "198.18.250.250", 443, true, "host2.test").Allowed);

        // 4. Systemwide connector precedence: user (userId, connectorId) beats connector-system in BOTH directions,
        //    and attribution = none ignores user (userId, connectorId) rules!
        controller.PolicyStore.SetRule(
            admin,
            new RuleWriteSpec("connector-system", null, null, "conn-sys", "sys.test", "host", 443, Allowed: false),
            controller.PolicyStore.Revision);
        (_, NetworkAccessRuleView userSysAllow) = controller.PolicyStore.SetRule(
            admin,
            new RuleWriteSpec("user", "user-x", null, "conn-sys", "sys.test", "host", 443, Allowed: true),
            controller.PolicyStore.Revision);

        // user-x's attributed systemwide instance is allowed (user allow beats connector-system block)
        Assert.True(controller.EvaluateContainerConnection("10.247.1.41", "198.18.250.250", 443, true, "sys.test").Allowed);
        // user-y's attributed instance is blocked by connector-system
        Assert.False(controller.EvaluateContainerConnection("10.247.1.42", "198.18.250.250", 443, true, "sys.test").Allowed);
        // shared unattributed instance (10.247.1.43) ignores user-x's rule and sees connector-system block
        Assert.False(controller.EvaluateContainerConnection("10.247.1.43", "198.18.250.250", 443, true, "sys.test").Allowed);

        // Flip: connector-system allow + user-x block -> user-x blocked, user-y and shared allowed!
        controller.PolicyStore.SetRule(
            admin,
            new RuleWriteSpec("connector-system", null, null, "conn-sys", "sys.test", "host", 443, Allowed: true),
            controller.PolicyStore.Revision);
        controller.PolicyStore.SetRule(
            admin,
            new RuleWriteSpec("user", "user-x", null, "conn-sys", "sys.test", "host", 443, Allowed: false),
            controller.PolicyStore.Revision);

        Assert.False(controller.EvaluateContainerConnection("10.247.1.41", "198.18.250.250", 443, true, "sys.test").Allowed);
        Assert.True(controller.EvaluateContainerConnection("10.247.1.42", "198.18.250.250", 443, true, "sys.test").Allowed);
        Assert.True(controller.EvaluateContainerConnection("10.247.1.43", "198.18.250.250", 443, true, "sys.test").Allowed);

        // Negative control (.NC): remove user-x block -> user-x inherits connector-system allow
        controller.PolicyStore.DeleteRule(admin, userSysAllow.Rule.Id, controller.PolicyStore.Revision);
        Assert.True(controller.EvaluateContainerConnection("10.247.1.41", "198.18.250.250", 443, true, "sys.test").Allowed);
        _ = a1Rule;
    }

    [Fact]
    public void V7_37_SystemwideProviderRule_AllowsOnlyAdminGrantedEndpoint_WithNegativeControls()
    {
        NacController controller = CreateController(out _);
        var admin = new NetworkAccessActor("admin-1", IsAdmin: true);
        var member = new NetworkAccessActor("user-1", IsAdmin: false);

        static OutboundCheckInput Call(string user, string host, int port)
            => new(user, "gateway", null, false, host, port, "provider-test", null);

        // No built-in exception: before any grant, localhost-style provider endpoints are denied.
        Assert.False(controller.EvaluateOutboundCheck(Call("admin-1", "192.168.1.50", 11434)).Allowed);
        Assert.False(controller.EvaluateOutboundCheck(Call("admin-1", "api.provider.test", 443)).Allowed);

        // A member cannot write a systemwide rule.
        Assert.Throws<UnauthorizedAccessException>(() => controller.PolicyStore.SetRule(
            member,
            new RuleWriteSpec("system", null, null, null, "api.provider.test", "host", 443, Allowed: true),
            controller.PolicyStore.Revision));

        // Admin grants the provider's public host systemwide.
        controller.PolicyStore.SetRule(
            admin,
            new RuleWriteSpec("system", null, null, null, "api.provider.test", "host", 443, Allowed: true),
            controller.PolicyStore.Revision);
        Assert.True(controller.EvaluateOutboundCheck(Call("admin-1", "api.provider.test", 443)).Allowed);
        Assert.True(controller.EvaluateOutboundCheck(Call("user-1", "api.provider.test", 443)).Allowed);

        // NC: a different port and a different host remain denied.
        Assert.False(controller.EvaluateOutboundCheck(Call("admin-1", "api.provider.test", 8443)).Allowed);
        Assert.False(controller.EvaluateOutboundCheck(Call("admin-1", "other.provider.test", 443)).Allowed);

        // A LAN address needs allowLan; without it the rule is rejected.
        Assert.Throws<ArgumentException>(() => controller.PolicyStore.SetRule(
            admin,
            new RuleWriteSpec("system", null, null, null, "192.168.1.50", "ip", 11434, Allowed: true),
            controller.PolicyStore.Revision));
        controller.PolicyStore.SetRule(
            admin,
            new RuleWriteSpec("system", null, null, null, "192.168.1.50", "ip", 11434, Allowed: true, AllowLan: true),
            controller.PolicyStore.Revision);
        Assert.True(controller.EvaluateOutboundCheck(Call("user-1", "192.168.1.50", 11434)).Allowed);

        // Loopback is allowed only through an admin allowLan systemwide rule; plain allow is rejected.
        Assert.Throws<ArgumentException>(() => controller.PolicyStore.SetRule(
            admin,
            new RuleWriteSpec("system", null, null, null, "127.0.0.1", "ip", 11434, Allowed: true),
            controller.PolicyStore.Revision));
        Assert.Throws<ArgumentException>(() => controller.PolicyStore.SetRule(
            admin,
            new RuleWriteSpec("agent", "user-1", "agent-1", null, "127.0.0.1", "ip", 11434, Allowed: true, AllowLan: true),
            controller.PolicyStore.Revision));
        controller.PolicyStore.SetRule(
            admin,
            new RuleWriteSpec("system", null, null, null, "127.0.0.1", "ip", 11434, Allowed: true, AllowLan: true),
            controller.PolicyStore.Revision);
        Assert.True(controller.EvaluateOutboundCheck(Call("admin-1", "127.0.0.1", 11434)).Allowed);

        // An admin block rule at system scope beats nothing-allowed and stays denied.
        controller.PolicyStore.SetRule(
            admin,
            new RuleWriteSpec("system", null, null, null, "api.provider.test", "host", 443, Allowed: false),
            controller.PolicyStore.Revision);
        Assert.False(controller.EvaluateOutboundCheck(Call("user-1", "api.provider.test", 443)).Allowed);
    }

    private NacController CreateController(out string statePath)
    {
        statePath = Path.Combine(tempDir, Guid.NewGuid().ToString("N"), "state.json");
        var store = new NetworkAccessPolicyStore(statePath);
        var registry = new WorkloadRegistry();
        return new NacController(store, registry);
    }

    private static void RegisterOrdinaryAgent(NacController controller, string ip, string userId, string agentId)
    {
        controller.Registry.Register(new RegistryEntry(
            ip,
            userId,
            agentId,
            ContainerId: $"ctr-{userId}-{agentId}",
            Kind: "agent-sandbox",
            ConnectorId: null,
            ExecContext: "agent-sandbox",
            Systemwide: false,
            Egress: "normal",
            Attribution: "user-agent",
            CompatProfile: null,
            DateTimeOffset.UtcNow));
    }
}
