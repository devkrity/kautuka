using Kautuka.Gateway.Domain.Runtime;

namespace Kautuka.IntegrationTests;

public sealed class ToolInvocationSettingsTests
{
    [Fact]
    public void DefaultsMatchCurrentHardCodedBehavior()
    {
        ResolvedToolInvocationSettings resolved = ToolInvocationSettings.Normalize(
            RuntimeSettingKeys.DefaultAllowMultipleToolCalls,
            RuntimeSettingKeys.DefaultAllowConcurrentInvocation,
            RuntimeSettingKeys.DefaultMaxParallelTools);

        Assert.True(resolved.AllowMultipleToolCalls);
        Assert.False(resolved.AllowConcurrentInvocation);
        Assert.Equal(0, resolved.MaxParallelTools);
        Assert.Null(resolved.ConcurrencyLimit);
    }

    [Fact]
    public void ConcurrentWithoutMultipleIsForcedSerial()
    {
        ResolvedToolInvocationSettings resolved = ToolInvocationSettings.Normalize(false, true, 8);

        Assert.False(resolved.AllowMultipleToolCalls);
        Assert.False(resolved.AllowConcurrentInvocation);
        Assert.Equal(8, resolved.MaxParallelTools);
        Assert.Null(resolved.ConcurrencyLimit);
    }

    [Fact]
    public void MaxParallelOfOneForcesSerialEvenWhenConcurrentRequested()
    {
        ResolvedToolInvocationSettings resolved = ToolInvocationSettings.Normalize(true, true, 1);

        Assert.True(resolved.AllowMultipleToolCalls);
        Assert.False(resolved.AllowConcurrentInvocation);
        Assert.Equal(1, resolved.MaxParallelTools);
        Assert.Null(resolved.ConcurrencyLimit);
    }

    [Fact]
    public void UnlimitedParallelLeavesConcurrencyOn()
    {
        ResolvedToolInvocationSettings resolved = ToolInvocationSettings.Normalize(true, true, 0);

        Assert.True(resolved.AllowConcurrentInvocation);
        Assert.Equal(0, resolved.MaxParallelTools);
        Assert.Null(resolved.ConcurrencyLimit);
    }

    [Fact]
    public void FiniteCapExposesConcurrencyLimit()
    {
        ResolvedToolInvocationSettings resolved = ToolInvocationSettings.Normalize(true, true, 4);

        Assert.True(resolved.AllowConcurrentInvocation);
        Assert.Equal(4, resolved.ConcurrencyLimit);
    }

    [Fact]
    public void NegativeCapBecomesUnlimited()
    {
        ResolvedToolInvocationSettings resolved = ToolInvocationSettings.Normalize(true, true, -3);

        Assert.Equal(0, resolved.MaxParallelTools);
        Assert.True(resolved.AllowConcurrentInvocation);
        Assert.Null(resolved.ConcurrencyLimit);
    }

    [Fact]
    public void CapAboveMaximumIsClamped()
    {
        ResolvedToolInvocationSettings resolved = ToolInvocationSettings.Normalize(
            true,
            true,
            RuntimeSettingKeys.MaxParallelToolsCap + 25);

        Assert.Equal(RuntimeSettingKeys.MaxParallelToolsCap, resolved.MaxParallelTools);
        Assert.Equal(RuntimeSettingKeys.MaxParallelToolsCap, resolved.ConcurrencyLimit);
    }

    [Fact]
    public void UnsetAgentInheritsPlatform()
    {
        ResolvedToolInvocationSettings resolved = ToolInvocationSettings.Combine(
            systemAllowMultipleToolCalls: true,
            systemAllowConcurrentInvocation: true,
            systemMaxParallelTools: 3,
            agentAllowMultipleToolCalls: null,
            agentAllowConcurrentInvocation: null,
            agentMaxParallelTools: null);

        Assert.True(resolved.AllowMultipleToolCalls);
        Assert.True(resolved.AllowConcurrentInvocation);
        Assert.Equal(3, resolved.ConcurrencyLimit);
    }

    [Fact]
    public void AgentFalseOverridesPlatformTrue()
    {
        ResolvedToolInvocationSettings resolved = ToolInvocationSettings.Combine(
            true,
            true,
            8,
            false,
            false,
            2);

        Assert.False(resolved.AllowMultipleToolCalls);
        Assert.False(resolved.AllowConcurrentInvocation);
        Assert.Equal(2, resolved.MaxParallelTools);
        Assert.Null(resolved.ConcurrencyLimit);
    }

    [Fact]
    public void AgentZeroMaxOverridesPlatformCapAsUnlimited()
    {
        ResolvedToolInvocationSettings resolved = ToolInvocationSettings.Combine(
            true,
            true,
            4,
            null,
            null,
            0);

        Assert.True(resolved.AllowConcurrentInvocation);
        Assert.Equal(0, resolved.MaxParallelTools);
        Assert.Null(resolved.ConcurrencyLimit);
    }

    [Fact]
    public void FromSnapshotReNormalizesInconsistentValues()
    {
        ResolvedToolInvocationSettings resolved = ToolInvocationSettings.FromSnapshot(false, true, 1);

        Assert.False(resolved.AllowMultipleToolCalls);
        Assert.False(resolved.AllowConcurrentInvocation);
        Assert.Equal(1, resolved.MaxParallelTools);
    }
}
