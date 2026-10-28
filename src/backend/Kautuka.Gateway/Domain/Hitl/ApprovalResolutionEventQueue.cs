using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

using Kautuka.Gateway.Domain.Execution;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Hitl;

internal sealed record ApprovalResolutionEventWork(
    string EventId,
    string RequestId,
    JsonElement Payload,
    NaradEventContext Context,
    string? ToolCallId,
    string? CheckpointId,
    bool StopRun = false,
    string? StopThreadId = null,
    string? StopChannel = null,
    string? Actor = null,
    IReadOnlyList<string>? PackedRequestIds = null);

internal sealed record ApprovalResolutionEventLease(
    ApprovalResolutionEventWork Work,
    string LeaseOwner,
    int AttemptCount);

internal sealed record ApprovalResolutionEventDeadLetter(
    string EventId,
    string RequestId,
    int AttemptCount,
    string? LastError,
    string UpdatedAt);

internal enum ApprovalResolutionEventFailureDisposition
{
    RetryScheduled,
    DeadLettered,
}

internal interface IApprovalResolutionEventStore
{
    ApprovalResolutionEventLease? TryLeaseResolutionEvent(
        string leaseOwner,
        DateTimeOffset nowUtc,
        DateTimeOffset leaseExpiresAtUtc,
        string? requestId = null);

    void MarkResolutionEventPublished(ApprovalResolutionEventLease lease, DateTimeOffset nowUtc);

    ApprovalResolutionEventFailureDisposition FailResolutionEvent(
        ApprovalResolutionEventLease lease,
        string safeError,
        DateTimeOffset nowUtc);

    IReadOnlyList<ApprovalResolutionEventDeadLetter> ListResolutionEventDeadLetters(int limit);
}

internal interface IApprovalResolutionEventDispatcher
{
    ValueTask DispatchAsync(string? requestId, CancellationToken cancellationToken);
}

internal sealed class ApprovalResolutionEventDispatcher(
    IApprovalResolutionEventStore store,
    IRuntimeEventPublisher publisher,
    IApprovalResolutionSideEffectApplier sideEffectApplier,
    TimeProvider timeProvider,
    ILogger<ApprovalResolutionEventDispatcher> logger) : BackgroundService, IApprovalResolutionEventDispatcher
{
    private static readonly TimeSpan retryInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan leaseDuration = TimeSpan.FromMinutes(2);
    private readonly string leaseOwner = $"approval-events-{Environment.ProcessId}-{Guid.NewGuid():N}";

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Durable dispatcher process boundary records and retries each failed delivery; one poison event must not terminate recovery.")]
    public async ValueTask DispatchAsync(string? requestId, CancellationToken cancellationToken)
    {
        for (int dispatched = 0; dispatched < 64; dispatched++)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            ApprovalResolutionEventLease? lease;
            try
            {
                lease = store.TryLeaseResolutionEvent(
                    leaseOwner,
                    now,
                    now.Add(leaseDuration),
                    requestId);
            }
            catch (ObjectDisposedException ex)
            {
                ApprovalResolutionEventDispatcherLog.LeaseAttemptFailed(logger, ex);
                return;
            }

            if (lease is null)
            {
                return;
            }

            try
            {
                ApprovalResolutionEventWork work = lease.Work;
                if (work.StopRun)
                {
                    await sideEffectApplier.ApplyAsync(work, cancellationToken).ConfigureAwait(false);
                }

                await publisher.PublishWithIdAsync(
                    work.EventId,
                    NaradEventTypes.InterruptResolved,
                    work.Payload,
                    work.Context,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                store.MarkResolutionEventPublished(lease, timeProvider.GetUtcNow());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                ApprovalResolutionEventFailureDisposition disposition = store.FailResolutionEvent(
                    lease,
                    ex.Message,
                    timeProvider.GetUtcNow());
                ApprovalResolutionEventDispatcherLog.DeliveryFailed(logger, ex, lease.Work.EventId, disposition);
            }
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "BackgroundServiceExceptionBehavior=StopHost; a SQLite disposal or lease failure must not take down Gateway.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(retryInterval, timeProvider);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchAsync(requestId: null, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                ApprovalResolutionEventDispatcherLog.DispatchCycleFailed(logger, ex);
            }

            await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
        }
    }
}

internal static partial class ApprovalResolutionEventDispatcherLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Canonical approval resolution event {EventId} delivery failed with disposition {Disposition}")]
    public static partial void DeliveryFailed(
        ILogger logger,
        Exception exception,
        string eventId,
        ApprovalResolutionEventFailureDisposition disposition);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Approval resolution event lease attempt failed; retrying on the next dispatcher tick")]
    public static partial void LeaseAttemptFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Approval resolution dispatcher cycle failed; host stays up and retries")]
    public static partial void DispatchCycleFailed(ILogger logger, Exception exception);
}
