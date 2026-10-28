using Kautuka.Gateway.Domain.Hitl;

namespace Kautuka.IntegrationTests;

/// <summary>
/// Unit tests for ApprovalBypassRegistry.
/// The bypass registry tracks which runs have been granted "approve all for this run"
/// auto-approval, keyed by run ID.
/// </summary>
public sealed class ApprovalBypassRegistryTests
{
    [Fact]
    public void IsActiveWithNeverRegisteredRunReturnsFalse()
    {
        var registry = new ApprovalBypassRegistry();

        bool active = registry.IsActive("run-unknown", out string userId);

        Assert.False(active);
        Assert.Null(userId);
    }

    [Fact]
    public void RegisterBypassThenIsActiveReturnsTrueWithCorrectUser()
    {
        var registry = new ApprovalBypassRegistry();

        registry.RegisterBypass("run-1", "user-alice");
        bool active = registry.IsActive("run-1", out string userId);

        Assert.True(active);
        Assert.Equal("user-alice", userId);
    }

    [Fact]
    public void ClearAfterRegisterIsActiveReturnsFalse()
    {
        var registry = new ApprovalBypassRegistry();
        registry.RegisterBypass("run-1", "user-alice");

        registry.Clear("run-1");
        bool active = registry.IsActive("run-1", out _);

        Assert.False(active);
    }

    [Fact]
    public void ClearUnknownRunDoesNotThrow()
    {
        var registry = new ApprovalBypassRegistry();
        registry.Clear("run-does-not-exist"); // must not throw
    }

    [Fact]
    public void MultipleRunsIndependentlyTracked()
    {
        var registry = new ApprovalBypassRegistry();
        registry.RegisterBypass("run-a", "alice");
        registry.RegisterBypass("run-b", "bob");

        Assert.True(registry.IsActive("run-a", out string userA));
        Assert.Equal("alice", userA);

        Assert.True(registry.IsActive("run-b", out string userB));
        Assert.Equal("bob", userB);

        registry.Clear("run-a");

        Assert.False(registry.IsActive("run-a", out _));
        Assert.True(registry.IsActive("run-b", out _));
    }

    [Fact]
    public void RegisterBypassOverwritesExistingBypassUser()
    {
        var registry = new ApprovalBypassRegistry();
        registry.RegisterBypass("run-1", "alice");
        registry.RegisterBypass("run-1", "bob"); // re-register as different user

        bool active = registry.IsActive("run-1", out string userId);

        Assert.True(active);
        Assert.Equal("bob", userId);
    }

    [Fact]
    public void IsActiveIsConcurrentlySafe()
    {
        var registry = new ApprovalBypassRegistry();
        Exception? exception = Record.Exception(() => Parallel.For(0, 100, i =>
        {
            string runId = $"run-{i % 10}";
            registry.RegisterBypass(runId, $"user-{i}");
            registry.IsActive(runId, out _);
            if (i % 3 == 0)
            {
                registry.Clear(runId);
            }
        }));

        Assert.Null(exception);
    }
}
