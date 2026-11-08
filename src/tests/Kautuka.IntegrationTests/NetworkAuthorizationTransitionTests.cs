using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class NetworkAuthorizationTransitionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "nac-transition-" + Guid.NewGuid().ToString("N"));
    private const string source = "10.247.91.2";
    private const string target = "93.184.216.34";
    private static readonly NetworkAccessIdentity identity = new("agent", "owner", "agent", "connector", "owner::connector");
    private static readonly EgressTcpRule allowed = new(source, "allowed.example.com", 443);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private ContainerEgressRuntime Runtime() => new(new PodmanCommandRunner(), new ContainerEgressOptions { Enabled = true }, RuntimeImageAllowlist.Empty, root);
    private static EgressAuthorizationSnapshot Snapshot(string generation, EgressTcpRule[] rules,
        string allocation = "container-1", NetworkAccessIdentity? owner = null)
        => new(generation, new Dictionary<string, NetworkAccessIdentity> { [source] = owner ?? identity }, rules,
            new Dictionary<string, string> { [source] = allocation });
    private static bool Check(ContainerEgressRuntime runtime, string generation, string host = "allowed.example.com")
        => runtime.AuthorizeNetworkConnection(generation, source, target, 443, host);
    private static void Retry(ContainerEgressRuntime runtime, string generation, string host = "allowed.example.com")
        => Assert.Throws<InvalidOperationException>(() => Check(runtime, generation, host));
    private static void NoRequests(ContainerEgressRuntime runtime)
        => Assert.Empty(runtime.GetNetworkAccessPolicy(new("owner", true)).Policy.Requests);

    [Fact]
    public void UnchangedGrantSurvivesBothActivationBoundariesWhileNewGrantWaits()
    {
        using var runtime = Runtime();
        EgressAuthorizationSnapshot old = Snapshot("old", [allowed]);
        EgressAuthorizationSnapshot next = Snapshot("new", [allowed, new(source, "new.example.com", 443)]);
        runtime.CompleteAuthorizationTransition(old);
        runtime.BeginAuthorizationTransition(next);
        Assert.True(Check(runtime, "old"));
        Assert.True(Check(runtime, "new"));
        Retry(runtime, "old", "new.example.com");
        Retry(runtime, "new", "new.example.com");
        NoRequests(runtime);

        runtime.CompleteAuthorizationTransition(next);
        Assert.True(Check(runtime, "old"));
        Assert.True(Check(runtime, "new"));
        Retry(runtime, "old", "new.example.com");
        Assert.True(Check(runtime, "new", "new.example.com"));
        NoRequests(runtime);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OldPrivateRouteCannotUseNewPublicOnlyGrantOrRevokedPermission(bool revoked)
    {
        using var runtime = Runtime();
        runtime.CompleteAuthorizationTransition(Snapshot("private", [allowed with { AllowPrivateNetwork = true }]));
        EgressAuthorizationSnapshot next = Snapshot("public", revoked ? [] : [allowed]);
        if (revoked)
            new NetworkAccessPolicyStore(Path.Combine(root, "egress", "policy", "state.json"))
                .SetRule(new("owner", false), identity, allowed.Destination, 443, false, 0);

        runtime.BeginAuthorizationTransition(next);
        Retry(runtime, "private");
        Retry(runtime, "public");
        runtime.CompleteAuthorizationTransition(next);
        Retry(runtime, "private");
        Assert.Equal(!revoked, Check(runtime, "public"));
        NoRequests(runtime);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReusedIpCannotBorrowOldAllocationEvenWhenLogicalOwnerIsUnchanged(bool differentOwner)
    {
        using var runtime = Runtime();
        runtime.CompleteAuthorizationTransition(Snapshot("old", [allowed]));
        EgressAuthorizationSnapshot replacement = Snapshot("replacement", [allowed], "container-2",
            differentOwner ? identity with { UserId = "other" } : identity);
        runtime.BeginAuthorizationTransition(replacement);
        Retry(runtime, "old");
        Retry(runtime, "replacement");
        runtime.CompleteAuthorizationTransition(replacement);
        Retry(runtime, "old");
        Assert.True(Check(runtime, "replacement"));
        NoRequests(runtime);
    }

    [Fact]
    public void RemovedHostnameGrantCannotBorrowALiteralFallbackFromAnotherGeneration()
    {
        using var runtime = Runtime();
        var literal = new EgressTcpRule(source, target, 443);
        runtime.CompleteAuthorizationTransition(Snapshot("hostname", [allowed, literal]));
        EgressAuthorizationSnapshot next = Snapshot("literal", [literal]);
        runtime.BeginAuthorizationTransition(next);
        Retry(runtime, "hostname");
        Retry(runtime, "literal");
        runtime.CompleteAuthorizationTransition(next);
        Retry(runtime, "hostname");
        Assert.True(Check(runtime, "literal"));
        NoRequests(runtime);
    }

    [Fact]
    public void FailedActivationRestoresLastAcceptedPermissionsWithoutTrustingCandidateRoutes()
    {
        using var runtime = Runtime();
        runtime.CompleteAuthorizationTransition(Snapshot("old", [allowed]));
        runtime.BeginAuthorizationTransition(Snapshot("candidate", [allowed with { AllowPrivateNetwork = true }]));
        Retry(runtime, "old");
        Retry(runtime, "candidate");
        // Called only after the existing publication path confirms rollback.
        runtime.RestoreAuthorizationTransition();
        Assert.True(Check(runtime, "old"));
        Retry(runtime, "candidate");
        NoRequests(runtime);
    }

    [Fact]
    public void RapidChangesRetainBoundedCompatibleRoutesAndUnknownGenerationsDoNotCreateRequests()
    {
        using var runtime = Runtime();
        runtime.CompleteAuthorizationTransition(Snapshot("v0", [allowed]));
        for (int index = 1; index <= 10; index++)
        {
            EgressAuthorizationSnapshot next = Snapshot("v" + index, [allowed]);
            runtime.BeginAuthorizationTransition(next);
            Assert.True(Check(runtime, "v" + (index - 1)));
            runtime.CompleteAuthorizationTransition(next);
        }
        Retry(runtime, "v0");
        Retry(runtime, "unknown");
        Assert.True(Check(runtime, "v2"));
        Assert.True(Check(runtime, "v9"));
        NoRequests(runtime);
    }

    [Fact]
    public async Task RetiredRouteDescriptorsExpireWithoutRemovingTheCurrentGrant()
    {
        using var runtime = Runtime();
        runtime.CompleteAuthorizationTransition(Snapshot("old", [allowed]));
        EgressAuthorizationSnapshot next = Snapshot("current", [allowed]);
        runtime.BeginAuthorizationTransition(next);
        runtime.CompleteAuthorizationTransition(next);
        Assert.True(Check(runtime, "old"));
        await Task.Delay(TimeSpan.FromSeconds(11), TestContext.Current.CancellationToken);
        Retry(runtime, "old");
        Assert.True(Check(runtime, "current"));
        NoRequests(runtime);
    }
}
