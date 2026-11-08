using System.Reflection;

using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class EgressReadinessTimeoutTests
{
    [Fact]
    public async Task UnknownInspectionRefusesAdmissionAndNextSuccessDoesNotRestartServices()
    {
        var commands = new List<string[]>();
        int inspection = 0;
        var runner = new PodmanCommandRunner((arguments, _) =>
        {
            commands.Add([.. arguments]);
            Assert.Equal("inspect", arguments[0]);
            inspection++;
            return Task.FromResult(inspection == 2
                ? new PodmanCommandResult(124, "", "inspection timed out", ProcessTerminationReason.TimedOut)
                : new PodmanCommandResult(0, "true\ntrue\n", "", ProcessTerminationReason.Completed));
        });
        using var runtime = Runtime(runner);
        await runtime.EnsureReadyAsync(TestContext.Current.CancellationToken);
        Assert.True(runtime.AuthorizeNetworkConnection("accepted", "10.0.0.1", "8.8.8.8", 443, null));
        bool createRequested = false;
        var identity = new ContainerEgressRuntime.SourceIdentity(new ContainerEgressRuntime.Scope("owner", "new-agent"), "connector", "owner::tool");
        await Assert.ThrowsAsync<ContainerInspectionUnavailableException>(() => runtime.PrepareAndAuthorizeAsync(identity, "new-container",
            (_, _) => { createRequested = true; return ["create"]; }, TestContext.Current.CancellationToken));
        Assert.False(createRequested);
        Assert.True(runtime.AuthorizeNetworkConnection("accepted", "10.0.0.1", "8.8.8.8", 443, null));
        await runtime.EnsureReadyAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, commands.Count);
        Assert.All(commands, command => Assert.Equal("inspect", command[0]));
    }

    [Fact]
    public async Task CallerCancellationDuringInspectionDoesNotInvalidateAcceptedReadiness()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        int inspection = 0;
        var runner = new PodmanCommandRunner(async (arguments, _) =>
        {
            Assert.Equal("inspect", arguments[0]);
            if (++inspection == 1)
            {
                await cancellation.CancelAsync();
                return new PodmanCommandResult(130, "", "cancelled", ProcessTerminationReason.CallerCancelled);
            }
            return new PodmanCommandResult(0, "true\ntrue\n", "", ProcessTerminationReason.Completed);
        });
        using var runtime = Runtime(runner);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.EnsureReadyAsync(cancellation.Token));
        await runtime.EnsureReadyAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, inspection);
    }

    private static ContainerEgressRuntime Runtime(PodmanCommandRunner runner)
    {
        var runtime = new ContainerEgressRuntime(runner, new ContainerEgressOptions { Enabled = true }, RuntimeImageAllowlist.Empty, Path.GetTempPath());
        typeof(ContainerEgressRuntime).GetField("ready", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, true);
        var identity = new NetworkAccessIdentity("agent", "owner", "known-agent", "connector", "owner::known-tool");
        runtime.CompleteAuthorizationTransition(new EgressAuthorizationSnapshot("accepted",
            new Dictionary<string, NetworkAccessIdentity> { ["10.0.0.1"] = identity },
            [new EgressTcpRule("10.0.0.1", "8.8.8.8", 443)],
            new Dictionary<string, string> { ["10.0.0.1"] = "known-allocation" }));
        return runtime;
    }
}
