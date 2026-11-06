using Kautuka.Gateway.Domain.Connectors;

namespace Kautuka.IntegrationTests;

public sealed class RemoteMcpCredentialRevisionTests
{
    [Fact]
    public void FaultyCancellationCallbackCannotStrandCredentialMutation()
    {
        var revisions = new RemoteMcpCredentialRevisionTracker();
        using RemoteMcpOperationLease lease = revisions.Acquire("one", "alice", "agent", true);
        using CancellationTokenRegistration callback = lease.Token.Register(static () => throw new InvalidOperationException("Synthetic callback failure."));
        revisions.BeginChange("one", "user:alice");
        Assert.True(lease.Token.IsCancellationRequested);
        revisions.EndChange("one", "user:alice");
        using RemoteMcpOperationLease replacement = revisions.Acquire("one", "alice", "agent", true);
        Assert.False(replacement.Token.IsCancellationRequested);
    }

    [Fact]
    public void DisposingOperationCancelsReadersAndUnregistersLease()
    {
        var revisions = new RemoteMcpCredentialRevisionTracker();
        RemoteMcpOperationLease lease = revisions.Acquire("one", "alice", "agent", true);
        CancellationToken token = lease.Token;
        lease.Dispose();
        Assert.True(token.IsCancellationRequested);
        Assert.Equal(0, revisions.ActiveLeaseCount);
        lease.Dispose();
    }

    [Fact]
    public void RevisionsAreScopedOpaqueAndInvalidateAcrossRestart()
    {
        var revisions = new RemoteMcpCredentialRevisionTracker();
        string first = revisions.Get("one", "alice", true);
        string other = revisions.Get("two", "alice", true);
        string bob = revisions.Get("one", "bob", false);
        revisions.BeginChange("one", "user:alice");
        Assert.Throws<InvalidOperationException>(() => revisions.Get("one", "alice", true));
        Assert.Equal(bob, revisions.Get("one", "bob", false));
        revisions.BeginChange("one", "user:alice");
        revisions.EndChange("one", "user:alice");
        Assert.Throws<InvalidOperationException>(() => revisions.Get("one", "alice", true));
        revisions.EndChange("one", "user:alice");
        Assert.NotEqual(first, revisions.Get("one", "alice", true));
        Assert.Equal(other, revisions.Get("two", "alice", true));
        string personal = revisions.Get("one", "alice", false);
        revisions.BeginChange("one", "system");
        Assert.Throws<InvalidOperationException>(() => revisions.Get("one", "alice", true));
        Assert.Equal(personal, revisions.Get("one", "alice", false));
        revisions.EndChange("one", "system");
        Assert.Equal(personal, revisions.Get("one", "alice", false));
        Assert.NotEqual(revisions.Get("one", "alice", true), new RemoteMcpCredentialRevisionTracker().Get("one", "alice", true));
    }
    [Fact]
    public void LeasesCancelOnlyAffectedScopesAndUnregister()
    {
        var tracker = new RemoteMcpCredentialRevisionTracker();
        using var own = tracker.Acquire("one", "alice", "agent-a", true);
        using var personal = tracker.Acquire("one", "bob", "agent-b", false);
        using var other = tracker.Acquire("two", "alice", "agent-a", true);
        tracker.BeginChange("one", "system");
        Assert.True(own.Token.IsCancellationRequested);
        Assert.False(personal.Token.IsCancellationRequested);
        Assert.False(other.Token.IsCancellationRequested);
        tracker.EndChange("one", "system");
        using (tracker.BeginPolicyChange("agent", "agent-b"))
        {
            Assert.True(personal.Token.IsCancellationRequested);
            Assert.False(other.Token.IsCancellationRequested);
            Assert.Throws<InvalidOperationException>(() => tracker.Acquire("one", "bob", "agent-b", false));
        }
        using (tracker.BeginPolicyChange("caller", "alice")) Assert.True(other.Token.IsCancellationRequested);
        own.Dispose();
        personal.Dispose();
        other.Dispose();
        Assert.Equal(0, tracker.ActiveLeaseCount);
    }

    [Fact]
    public async Task DraftCancelAndExpiryCancelOnlyDraftLease()
    {
        var tracker = new RemoteMcpCredentialRevisionTracker();
        using var draft = tracker.Acquire("one", "alice", null, false, "draft-a");
        using var runtime = tracker.Acquire("one", "alice", "agent", false);
        using (tracker.BeginPolicyChange("draft", "draft-a"))
        {
            Assert.True(draft.Token.IsCancellationRequested);
            Assert.False(runtime.Token.IsCancellationRequested);
            Assert.Throws<InvalidOperationException>(() => tracker.Acquire("one", "alice", null, false, "draft-a"));
        }
        using var expiring = tracker.Acquire("one", "alice", null, false, "draft-b");
        expiring.ScheduleExpiry(DateTimeOffset.UtcNow.AddMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(TimeSpan.FromSeconds(5), expiring.Token));
        Assert.False(runtime.Token.IsCancellationRequested);
    }

}
