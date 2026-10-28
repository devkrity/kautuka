using System.Threading.Channels;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Execution;

/// <summary>
/// Live derived delivery for Narad authority events. Durable replay always comes from the Narad journal.
/// </summary>
internal interface IRuntimeEventStreamHub
{
    void Publish(NaradPersistedEvent persistedEvent);

    RuntimeEventStreamSubscription SubscribeForRoot(string rootRunId);

    RuntimeEventStreamSubscription SubscribeForSession(string sessionId);

    ApprovalInvalidationSubscription SubscribeForUserApprovals(string userId);

    ApprovalInvalidationSubscription SubscribeForAdminApprovals();

    void PublishApprovalInvalidation(
        string userId,
        string? agentId,
        string requestId,
        string eventId,
        string eventType,
        string? sessionId = null);

    IAsyncEnumerable<NaradPersistedEvent> Subscribe(string rootRunId, CancellationToken cancellationToken);
}

/// <summary>
/// Watch doorbell for the Approvals list. Not an Narad authority record.
/// </summary>
internal sealed record ApprovalInvalidation(
    string EventId,
    string? SessionId,
    string? RootRunId,
    string? EventType);

internal sealed class ApprovalInvalidationSubscription : IDisposable
{
    private readonly Action dispose;

    public ApprovalInvalidationSubscription(ChannelReader<ApprovalInvalidation> reader, Action dispose)
    {
        Reader = reader;
        this.dispose = dispose;
    }

    public ChannelReader<ApprovalInvalidation> Reader { get; }

    public void Dispose() => dispose();
}

internal sealed class RuntimeEventStreamSubscription : IDisposable
{
    private readonly Action dispose;

    public RuntimeEventStreamSubscription(ChannelReader<NaradPersistedEvent> reader, Action dispose)
    {
        Reader = reader;
        this.dispose = dispose;
    }

    public ChannelReader<NaradPersistedEvent> Reader { get; }

    public void Dispose() => dispose();
}
