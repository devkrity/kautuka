using Kautuka.Gateway.Domain.Channels;

namespace Kautuka.IntegrationTests;

public sealed class ChannelTargetSelectionStateTests
{
    private static readonly GatewayChannelTargetBinding target = new("binding", "reg", "agent", "agent-1", true, "");

    [Fact]
    public void PickerRequiresOriginalScopeExpiresAndCanOnlyBeUsedOnce()
    {
        var clock = new ManualClock();
        var state = new ChannelTargetSelectionState(clock);
        string id = state.Create("reg", "user", "chat", [target]);
        Assert.False(state.TrySelect(id, 0, "other", "user", "chat", out _));
        Assert.False(state.TrySelect(id, 0, "reg", "other", "chat", out _));
        Assert.False(state.TrySelect(id, 0, "reg", "user", "other", out _));
        Assert.False(state.TrySelect(id, 99, "reg", "user", "chat", out _));
        Assert.True(state.TrySelect(id, 0, "reg", "user", "chat", out GatewayChannelTargetBinding? selected));
        Assert.Equal(target, selected);
        Assert.False(state.TrySelect(id, 0, "reg", "user", "chat", out _));
        id = state.Create("reg", "user", "chat", [target]);
        clock.Now = clock.Now.AddMinutes(11);
        Assert.False(state.TrySelect(id, 0, "reg", "user", "chat", out _));
    }

    [Fact]
    public void PickerCanBeInvalidatedForItsLinkedChannelScope()
    {
        var state = new ChannelTargetSelectionState(new ManualClock());
        string id = state.Create("slack-reg", "slack-user", "D-owner", [target]);

        state.Invalidate("slack-reg", "slack-user", "D-owner");

        Assert.False(state.TrySelect(id, 0, "slack-reg", "slack-user", "D-owner", out _));
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
