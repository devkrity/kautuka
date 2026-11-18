using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class ApplianceHostExposureAssertTests
{
    [Fact]
    public void ExpectedSocketPathsFollowConfiguredRuntimeRoot()
    {
        string devSocketRoot = Path.Combine(Path.GetTempPath(), "kautuka-dev", "runtime");

        IReadOnlyList<string> paths = ApplianceHostExposureAssert.GetExpectedSocketPaths(devSocketRoot);

        Assert.Equal(5, paths.Count);
        Assert.All(paths, path => Assert.StartsWith(devSocketRoot + Path.DirectorySeparatorChar, path, StringComparison.Ordinal));
        Assert.Contains(Path.Combine(devSocketRoot, "gateway", "gateway.sock"), paths);
    }

    [Fact]
    public void ProductionSocketRootRemainsTheProductionDefault()
    {
        IReadOnlyList<string> paths = ApplianceHostExposureAssert.GetExpectedSocketPaths("/run/kautuka");

        Assert.Contains(Path.Combine("/run/kautuka", "gateway", "gateway.sock"), paths);
    }

    [Fact]
    public void MissingSocketWarningsWaitForDevelopmentServicesButRemainImmediateInProduction()
    {
        Assert.False(ApplianceHostExposureAssert.ShouldWarnForMissingSockets(requireSidecars: false, TimeSpan.Zero));
        Assert.False(ApplianceHostExposureAssert.ShouldWarnForMissingSockets(requireSidecars: false, TimeSpan.FromSeconds(59)));
        Assert.True(ApplianceHostExposureAssert.ShouldWarnForMissingSockets(requireSidecars: false, TimeSpan.FromMinutes(1)));
        Assert.True(ApplianceHostExposureAssert.ShouldWarnForMissingSockets(requireSidecars: true, TimeSpan.Zero));
    }

    [Fact]
    public void OverdueDevelopmentSocketCheckRetriesUntilCompleteThenReturnsToNormalDelay()
    {
        TimeSpan normalDelay = TimeSpan.FromMinutes(30);

        Assert.Equal(
            TimeSpan.FromSeconds(10),
            ApplianceHostExposureAssert.BoundNextReconcileDelay(
                requireSidecars: false,
                developmentSocketCheckCompleted: false,
                elapsedSinceManagerStart: TimeSpan.FromMinutes(2),
                normalDelay));
        Assert.Equal(
            normalDelay,
            ApplianceHostExposureAssert.BoundNextReconcileDelay(
                requireSidecars: false,
                developmentSocketCheckCompleted: true,
                elapsedSinceManagerStart: TimeSpan.FromMinutes(2),
                normalDelay));
    }
}
