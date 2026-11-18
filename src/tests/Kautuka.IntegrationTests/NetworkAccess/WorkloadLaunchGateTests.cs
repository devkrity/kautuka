using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.NetworkAccessController;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class WorkloadLaunchGateTests
{
    [Fact]
    public void ClosedStartupGateRejectsWorkloadLaunch()
    {
        using var temp = new TemporaryDirectory();
        using var store = new NetworkAccessPolicyStore(Path.Combine(temp.Path, "state.json"));
        var gate = new WorkloadLaunchGate(new FakeStartupState(false), store);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => gate.EnsureLaunchAllowed("user-a"));

        Assert.Contains("NAC startup gate is closed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemBrakeRejectsEveryWorkloadLaunch()
    {
        using var temp = new TemporaryDirectory();
        using var store = new NetworkAccessPolicyStore(Path.Combine(temp.Path, "state.json"));
        store.SetBrake(new NetworkAccessActor("admin", true), "system", null, true);
        var gate = new WorkloadLaunchGate(new FakeStartupState(true), store);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => gate.EnsureLaunchAllowed("user-a"));

        Assert.Contains("system network brake is active", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UserBrakeRejectsOnlyThatUsersWorkloads()
    {
        using var temp = new TemporaryDirectory();
        using var store = new NetworkAccessPolicyStore(Path.Combine(temp.Path, "state.json"));
        store.SetBrake(new NetworkAccessActor("admin", true), "user", "user-a", true);
        var gate = new WorkloadLaunchGate(new FakeStartupState(true), store);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => gate.EnsureLaunchAllowed("user-a"));
        Assert.Contains("user network brake is active", ex.Message, StringComparison.Ordinal);
        gate.EnsureLaunchAllowed("user-b");
        gate.EnsureLaunchAllowed(null);
    }

    [Fact]
    public void SpecBrakeStateComesFromPersistedSystemAndUserBrakes()
    {
        using var temp = new TemporaryDirectory();
        using var store = new NetworkAccessPolicyStore(Path.Combine(temp.Path, "state.json"));
        var gate = new WorkloadLaunchGate(new FakeStartupState(true), store);

        Assert.False(gate.IsBraked("user-a"));

        store.SetBrake(new NetworkAccessActor("admin", IsAdmin: true), "user", "user-a", true);
        Assert.True(gate.IsBraked("user-a"));
        Assert.False(gate.IsBraked("user-b"));

        store.SetBrake(new NetworkAccessActor("admin", IsAdmin: true), "system", null, true);
        Assert.True(gate.IsBraked("user-b"));
        Assert.True(gate.IsBraked(null));
    }

    private sealed class FakeStartupState(bool isStartupGateOpen) : IWorkloadStartupState
    {
        public bool IsStartupGateOpen { get; } = isStartupGateOpen;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nac-launch-gate-" + Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
