namespace Kautuka.Gateway.Domain.Channels;

internal interface IChannelOutboxStore
{
    ChannelOutboxEntry Enqueue(
        string channelType,
        string registrationId,
        string actionType,
        string payloadJson,
        DateTimeOffset? availableAtUtc = null,
        string? runId = null,
        string? traceParent = null,
        string? traceState = null,
        string? ownerUserId = null);

    IReadOnlyList<ChannelOutboxEntry> LeasePending(
        string leaseOwner,
        int maxItems,
        TimeSpan leaseDuration,
        DateTimeOffset nowUtc);

    void Acknowledge(
        string actionId,
        string leaseOwner,
        string leaseId,
        bool success,
        string? error,
        DateTimeOffset nowUtc,
        DateTimeOffset? retryAtUtc = null,
        int maxAttempts = 10);
}

internal sealed record ChannelOutboxEntry(
    string Id,
    string ChannelType,
    string RegistrationId,
    string ActionType,
    string PayloadJson,
    string Status,
    int AttemptCount,
    string CreatedAt,
    string UpdatedAt,
    string? AvailableAt,
    string? LeaseId,
    string? LeaseOwner,
    string? LeasedUntil,
    string? LastError,
    string? RunId,
    string? TraceParent,
    string? TraceState,
    string? OwnerUserId = null);
