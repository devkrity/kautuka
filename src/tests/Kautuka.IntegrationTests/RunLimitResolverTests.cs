using Kautuka.Gateway.Domain.Runtime;

namespace Kautuka.IntegrationTests;

public sealed class RunLimitResolverTests
{
    [Fact]
    public void UnsetAgentAndPlatformZeroIsInfinite()
    {
        Assert.Null(RunLimitResolver.ResolveClock(null, 0));
    }

    [Fact]
    public void UnsetAgentInheritsPlatformCap()
    {
        Assert.Equal(TimeSpan.FromSeconds(600), RunLimitResolver.ResolveClock(null, 600));
    }

    [Fact]
    public void AgentZeroOverridesPlatformCapAsInfinite()
    {
        Assert.Null(RunLimitResolver.ResolveClock(0, 600));
    }

    [Fact]
    public void AgentPositiveSecondsWins()
    {
        Assert.Equal(TimeSpan.FromSeconds(120), RunLimitResolver.ResolveClock(120, 0));
    }

    [Fact]
    public void UnsetActionInheritsPlatformAbortDefault()
    {
        Assert.Equal(HitlExpiryAction.Abort, RunLimitResolver.ResolveAction(null, HitlExpiryActions.Default));
    }

    [Fact]
    public void AgentActionOverridesPlatform()
    {
        Assert.Equal(HitlExpiryAction.Inform, RunLimitResolver.ResolveAction("inform", "abort"));
        Assert.Equal(HitlExpiryAction.Retry, RunLimitResolver.ResolveAction("retry", "abort"));
        Assert.Equal(HitlExpiryAction.Abort, RunLimitResolver.ResolveAction("abort", "inform"));
    }
}
