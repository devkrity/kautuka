using Kautuka.Gateway.Infrastructure.Connectors;

namespace Kautuka.IntegrationTests;

public sealed class BoundMailSessionStoreTests
{
    [Fact]
    public void BrokerSwitchFenceExcludesNewStartsAndRotations()
    {
        var store = new BoundMailSessionStore();
        var intent = store.BeginStart("user", "mail")!;
        Assert.False(store.TryFenceBrokerSwitch(DateTimeOffset.UtcNow));
        store.EndStart(intent);
        Assert.True(store.TryBeginBrokerRotation());
        Assert.False(store.TryFenceBrokerSwitch(DateTimeOffset.UtcNow));
        store.EndBrokerRotation();
        Assert.True(store.TryFenceBrokerSwitch(DateTimeOffset.UtcNow));
        Assert.Null(store.BeginStart("user", "mail"));
        Assert.False(store.TryBeginBrokerRotation());
        store.ClearBrokerSwitchFence();
        Assert.NotNull(store.BeginStart("user", "mail"));
    }

    [Fact]
    public void ExpiredButInFlightCompletionStillBlocksBrokerSwitch()
    {
        string id = new('A', 43), cookie = new('B', 43), tab = new('C', 43);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var session = new BoundMailSession
        {
            UserId = "user",
            ConnectorId = "mail",
            CatalogConnectorId = "mail",
            Provider = "google",
            FlowId = id,
            ConnectionId = id,
            Verifier = id,
            DpopPrivateJwk = "secret",
            Origin = "http://localhost:20229",
            BrokerOrigin = "https://auth.example",
            ExpectedGeneration = 0,
            CookieHash = BoundMailSessionStore.Hash(cookie),
            TabHash = BoundMailSessionStore.Hash(tab),
            ExpiresAt = now.AddSeconds(1)
        };
        var store = new BoundMailSessionStore();
        Assert.True(store.TrySave(id, session, now));
        Assert.True(store.Confirm(id, "user", session.Origin, cookie, tab, now));
        Assert.True(store.TryBegin(id, "user", session.Origin, cookie, tab, now, out _));
        Assert.False(store.TryFenceBrokerSwitch(now.AddSeconds(2)));
        store.End(id, session, completed: false);
        Assert.True(store.TryFenceBrokerSwitch(now.AddSeconds(2)));
    }
    [Fact]
    public void MailCompletionRequiresOriginalUserOriginCookieAndTabAndIsSerialized()
    {
        string state = new('A', 43), cookie = new('B', 43), tab = new('C', 43), wrong = new('D', 43);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var session = new BoundMailSession
        {
            UserId = "user",
            ConnectorId = "mail",
            CatalogConnectorId = "mail",
            Provider = "google",
            FlowId = state,
            ConnectionId = state,
            Verifier = state,
            DpopPrivateJwk = "secret",
            Origin = "http://localhost:20229",
            BrokerOrigin = "https://auth.example",
            ExpectedGeneration = 0,
            CookieHash = BoundMailSessionStore.Hash(cookie),
            TabHash = BoundMailSessionStore.Hash(tab),
            ExpiresAt = now.AddMinutes(5)
        };
        var store = new BoundMailSessionStore();
        BoundMailSessionStore.StartIntent intent = store.BeginStart("user", "mail")!;
        Assert.Empty(store.CancelForConnector("user", "mail", now));
        Assert.False(store.SaveStarted(intent, state, session, now));
        store.EndStart(intent);
        Assert.True(store.TrySave(state, session, now));
        Assert.False(store.TrySave(state, session, now));
        Assert.False(store.TryBegin(state, "user", session.Origin, cookie, tab, now, out _));
        Assert.False(store.Confirm(state, "other", session.Origin, cookie, tab, now));
        Assert.False(store.Confirm(state, "user", "https://other", cookie, tab, now));
        Assert.False(store.Confirm(state, "user", session.Origin, wrong, tab, now));
        Assert.False(store.Confirm(state, "user", session.Origin, cookie, wrong, now));
        Assert.True(store.Confirm(state, "user", session.Origin, cookie, tab, now));
        Assert.True(store.TryBegin(state, "user", session.Origin, cookie, tab, now, out BoundMailSession? claimed));
        Assert.Same(session, claimed);
        Assert.False(store.TryBegin(state, "user", session.Origin, cookie, tab, now, out _));
        store.End(state, session, completed: false);
        Assert.True(store.TryBegin(state, "user", session.Origin, cookie, tab, now, out _));
        store.End(state, session, completed: true);
        Assert.True(store.TryGet(state, "user", session.Origin, cookie, tab, now, out BoundMailSession? completed));
        Assert.True(completed!.Completed);
        Assert.False(store.TryBegin(state, "user", session.Origin, cookie, tab, now, out _));
        Assert.False(store.TryGet(state, "other", session.Origin, cookie, tab, now, out _));
        Assert.Empty(store.CancelForConnector("user", "mail", now));
        Assert.False(store.TryGet(state, "user", session.Origin, cookie, tab, now, out _));
        Assert.DoesNotContain("secret", session.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CancellationInvalidatesAnInFlightSession()
    {
        string state = new('A', 43), cookie = new('B', 43), tab = new('C', 43);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var session = new BoundMailSession
        {
            UserId = "user",
            ConnectorId = "mail",
            CatalogConnectorId = "mail",
            Provider = "google",
            FlowId = state,
            ConnectionId = state,
            Verifier = state,
            DpopPrivateJwk = "secret",
            Origin = "http://localhost:20229",
            BrokerOrigin = "https://auth.example",
            ExpectedGeneration = 0,
            CookieHash = BoundMailSessionStore.Hash(cookie),
            TabHash = BoundMailSessionStore.Hash(tab),
            ExpiresAt = now.AddMinutes(5)
        };
        var store = new BoundMailSessionStore();
        Assert.True(store.TrySave(state, session, now));
        Assert.True(store.Confirm(state, "user", session.Origin, cookie, tab, now));
        Assert.True(store.TryBegin(state, "user", session.Origin, cookie, tab, now, out _));
        Assert.True(store.Cancel(state, "user", session.Origin, cookie, tab, now));
        Assert.False(store.TryGet(state, "user", session.Origin, cookie, tab, now, out _));
        Assert.True(store.TryGetForCancellation(state, "user", session.Origin, cookie, tab, now, out _));
        Assert.True(store.Cancel(state, "user", session.Origin, cookie, tab, now));
        Assert.False(store.TryGetForCancellation(state, "other", session.Origin, cookie, tab, now, out _));
        store.End(state, session, completed: true);
        Assert.False(session.Completed);
        Assert.False(store.TryGet(state, "user", session.Origin, cookie, tab, now, out _));
    }
}
