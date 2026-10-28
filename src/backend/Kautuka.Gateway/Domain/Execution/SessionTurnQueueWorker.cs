using System.Diagnostics.CodeAnalysis;


using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Domain.Execution;

internal sealed partial class SessionTurnQueueWorker(SessionTurnQueueStore queue, SessionTurnExecution execution, ILogger<SessionTurnQueueWorker> logger) : BackgroundService
{
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A queue failure must be observed without terminating unrelated channel sessions.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var running = new Dictionary<string, (QueuedSessionTurn Job, Task Completion)>(StringComparer.Ordinal);
        DateTimeOffset nextRenewal = DateTimeOffset.UtcNow;
        int headOffset = 0;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                foreach ((string id, (QueuedSessionTurn _, Task completion)) in running.ToArray())
                    if (completion.IsCompleted)
                    {
                        await completion.ConfigureAwait(false);
                        running.Remove(id);
                    }
                try
                {
                    if (nextRenewal <= DateTimeOffset.UtcNow)
                    {
                        foreach (var item in running.Values) queue.Renew(item.Job);
                        nextRenewal = DateTimeOffset.UtcNow.AddSeconds(15);
                    }
                    IReadOnlyList<QueuedSessionTurn> heads = queue.ListHeads(headOffset);
                    headOffset = heads.Count < 64 ? 0 : headOffset + 64;
                    foreach (QueuedSessionTurn candidate in heads)
                    {
                        if (running.Count >= 8) break;
                        if (running.ContainsKey(candidate.Id) || execution.IsBusy(candidate.TargetId, candidate.SessionId, candidate.Id)) continue;
                        QueuedSessionTurn? leased = queue.TryLease(candidate);
                        if (leased is null) continue;
                        running.Add(leased.Id, (leased, ProcessAsync(leased, stoppingToken)));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogQueueFailure(logger, ex.GetType().Name);
                }
                await Task.Delay(500, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            await Task.WhenAll(running.Values.Select(static item => item.Completion)).ConfigureAwait(false);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Each durable turn is isolated; failures are retained without replaying admitted tool work.")]
    private async Task ProcessAsync(QueuedSessionTurn job, CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            // Refresh projections after lease acquisition. Presentation failure must not reject executable work.
            try { await execution.QueueChangedAsync(job, cancellationToken).ConfigureAwait(false); }
            catch (Exception projectionFailure) when (projectionFailure is not OperationCanceledException)
            { LogQueueFailure(logger, projectionFailure.GetType().Name); }
            try
            {
                await execution.ExecuteAsync(job, cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelSessionBusyException)
            {
                queue.Retry(job);
                return;
            }
            catch (SqliteException) { throw; } // Recover the lease rather than discard a turn on a database outage.
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception executionFailure)
            {
                await execution.FailedAsync(job, cancellationToken).ConfigureAwait(false);
                queue.Fail(job, executionFailure.GetType().Name);
                await execution.QueueChangedAsync(job, cancellationToken).ConfigureAwait(false);
                LogQueueFailure(logger, executionFailure.GetType().Name);
                return;
            }
            // A failed completion checkpoint must not report a successful run as failed.
            // Recovery inspects the stable run identity before attempting any execution.
            queue.Complete(job);
            await execution.QueueChangedAsync(job, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception persistenceFailure) { LogQueueFailure(logger, persistenceFailure.GetType().Name); }
    }

    [LoggerMessage(1, LogLevel.Warning, "Queued session turn could not progress. failureType={FailureType}")]
    private static partial void LogQueueFailure(ILogger logger, string failureType);
}
