namespace Narad.Contracts;

/// <summary>Pending or leased Narad authority event outbox entry joined with journal ordering fields.</summary>
public sealed record NaradRuntimeEventOutboxEntry(
    NaradPersistedEvent Event,
    string SourceStorageLocatorKey,
    bool IsPublished,
    bool IsMirrored,
    int AttemptCount,
    string? LastError,
    string LeaseOwner,
    string LeaseExpiresAt);

/// <summary>Dead-lettered Narad authority event outbox entry.</summary>
public sealed record NaradRuntimeEventOutboxDeadLetter(
    NaradPersistedEvent Event,
    string SourceStorageLocatorKey,
    int AttemptCount,
    string LastError,
    string DeadLetteredAt);
