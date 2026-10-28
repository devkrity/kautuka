using Kautuka.Gateway.Domain.Memory;
using Kautuka.Gateway.Infrastructure.Channels;

using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class ChannelOwnerRosterTests
{
    [Fact]
    public void OwnerSyncUsesTheSameAccessibleAgentRosterAsMessageRouting()
    {
        GatewayState state = CreateState();
        state.Users["owner"] = new GatewayUser("owner", "Owner", "password", "user", true, null, null, true, true);
        state.Agents["own"] = Agent("own", "owner");
        state.Agents["other"] = Agent("other", "another");
        state.Agents["shared"] = Agent("shared", "another") with { Scope = "system" };
        using var dispatcher = new ChannelHostOwnerTargetSyncDispatcherService(null!, null!, state, NullLogger<ChannelHostOwnerTargetSyncDispatcherService>.Instance);
        var request = dispatcher.BuildRequest(new ChannelHostOwnerTargetSyncWork("owner", "test", "operation"));
        Assert.Equal(new[] { "agent:own", "agent:shared" }, request.Targets.Select(item => item.TargetKey));
    }

    [Fact]
    public void PeriodicReconciliationStillIncludesUserAfterTheirLastAgentWasDeleted()
    {
        GatewayState state = CreateState();
        state.Users["owner"] = new GatewayUser("owner", "Owner", "password", "user", true, null, null, true, true);
        var notifier = new Notifier();
        using var service = new ChannelHostOwnerTargetReconciliationService(state, notifier);
        Assert.Equal(1, service.Reconcile());
        Assert.Equal("owner", Assert.Single(notifier.Owners));
    }

    private static GatewayState CreateState()
        => GatewayState.Create(null!, NullLogger<GatewayState>.Instance, null!, null!, null!, null!, new GatewayMemoryStore());

    private static GatewayAgent Agent(string id, string owner)
        => new(id, id, "model", "system", "disabled", [], [], [], [], owner, "");

    private sealed class Notifier : IChannelHostOwnerTargetSyncNotifier
    {
        public List<string> Owners { get; } = [];
        public bool TryNotify(string ownerUserId, string reason) { Owners.Add(ownerUserId); return true; }
    }
}
