namespace Kautuka.Gateway.Domain.Hitl;

internal enum ApprovalContinuationEnqueueResult
{
    Inserted,
    Rescheduled,
    AlreadyPending,
    AlreadyLeased,
}

internal sealed record ApprovalContinuationWork(
    string RequestId,
    string SessionId,
    string TargetType,
    string TargetId,
    string? UserId,
    bool Approved,
    HitlResolutionSource Source,
    string? Reason,
    string? DecidedByUserId,
    bool StopRun = false);

internal sealed record ApprovalContinuationLease(
    ApprovalContinuationWork Work,
    string LeaseOwner,
    int AttemptCount);

internal enum ApprovalContinuationFailureDisposition
{
    RetryScheduled,
    DeadLettered,
}

internal interface IApprovalContinuationQueue
{
    ValueTask<ApprovalContinuationEnqueueResult> EnqueueAsync(ApprovalContinuationWork work, CancellationToken cancellationToken);

    IAsyncEnumerable<ApprovalContinuationLease> LeaseAllAsync(string leaseOwner, CancellationToken cancellationToken);

    ValueTask CompleteAsync(ApprovalContinuationLease lease, CancellationToken cancellationToken);

    ValueTask RenewAsync(ApprovalContinuationLease lease, CancellationToken cancellationToken);

    ValueTask<ApprovalContinuationFailureDisposition> FailAsync(
        ApprovalContinuationLease lease,
        string safeError,
        CancellationToken cancellationToken);
}
