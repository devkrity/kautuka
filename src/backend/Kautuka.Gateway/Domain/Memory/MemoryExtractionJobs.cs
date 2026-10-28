using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Dapper;

using Kautuka.Gateway.Domain.Execution;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Domain.Memory;

internal sealed record MemoryExtractionJob(
    string Id,
    string TargetType,
    string TargetId,
    string SessionId,
    string UserMessage,
    string AssistantMessage,
    int AttemptCount);

internal interface IMemoryExtractionProcessor
{
    Task<bool> ProcessMemoryExtractionAsync(MemoryExtractionJob job, CancellationToken cancellationToken);
}

internal sealed class MemoryExtractionJobStore(KautukaDatabase database, TimeProvider timeProvider)
{
    public bool Enqueue(string targetType, string targetId, string sessionId, string userMessage, string assistantMessage)
    {
        using var connection = OpenConnection();
        string now = Timestamp(timeProvider.GetUtcNow());
        string id = BuildJobId(targetType, targetId, sessionId, userMessage, assistantMessage);
        _ = connection.Execute(
            """
            INSERT INTO memory_extraction_jobs
                (id,target_type,target_id,session_id,user_message,assistant_message,status,attempt_count,next_attempt_at,created_at,updated_at)
            VALUES (@id,@targetType,@targetId,@sessionId,@userMessage,@assistantMessage,'pending',0,@now,@now,@now)
            ON CONFLICT(id) DO NOTHING
            """,
            new { id, targetType, targetId, sessionId, userMessage, assistantMessage, now });
        return true;
    }

    public MemoryExtractionJob? ClaimNext(string owner, TimeSpan lease)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        string now = Timestamp(timeProvider.GetUtcNow());
        string leaseExpires = Timestamp(timeProvider.GetUtcNow().Add(lease));
        string? id = connection.QueryFirstOrDefault<string>(
            """
            SELECT id FROM memory_extraction_jobs
            WHERE (status='pending' AND next_attempt_at<=@now)
               OR (status='leased' AND lease_expires_at<=@now)
            ORDER BY created_at,id LIMIT 1
            """, new { now }, transaction);
        if (id is null)
        {
            transaction.Rollback();
            return null;
        }

        if (connection.Execute(
            """
            UPDATE memory_extraction_jobs
            SET status='leased',lease_owner=@owner,lease_expires_at=@leaseExpires,attempt_count=attempt_count+1,updated_at=@now
            WHERE id=@id AND ((status='pending' AND next_attempt_at<=@now) OR (status='leased' AND lease_expires_at<=@now))
            """, new { id, owner, leaseExpires, now }, transaction) != 1)
        {
            transaction.Rollback();
            return null;
        }

        MemoryExtractionJob job = connection.QuerySingle<MemoryExtractionJob>(
            """
            SELECT id AS Id,target_type AS TargetType,target_id AS TargetId,session_id AS SessionId,
                   user_message AS UserMessage,assistant_message AS AssistantMessage,attempt_count AS AttemptCount
            FROM memory_extraction_jobs WHERE id=@id
            """, new { id }, transaction);
        transaction.Commit();
        return job;
    }

    public bool RenewLease(string id, string owner, TimeSpan lease)
    {
        using var connection = OpenConnection();
        string now = Timestamp(timeProvider.GetUtcNow());
        string leaseExpires = Timestamp(timeProvider.GetUtcNow().Add(lease));
        return connection.Execute(
            "UPDATE memory_extraction_jobs SET lease_expires_at=@leaseExpires,updated_at=@now WHERE id=@id AND status='leased' AND lease_owner=@owner",
            new { id, owner, leaseExpires, now }) == 1;
    }

    public void Complete(string id)
    {
        using var connection = OpenConnection();
        string now = Timestamp(timeProvider.GetUtcNow());
        connection.Execute(
            """
            UPDATE memory_extraction_jobs
            SET status='completed',user_message='',assistant_message='',lease_owner=NULL,lease_expires_at=NULL,last_error=NULL,updated_at=@now
            WHERE id=@id
            """, new { id, now });
    }

    public void Retry(MemoryExtractionJob job, string safeError)
    {
        using var connection = OpenConnection();
        string now = Timestamp(timeProvider.GetUtcNow());
        int seconds = Math.Min(300, 1 << Math.Min(job.AttemptCount, 8));
        string next = Timestamp(timeProvider.GetUtcNow().AddSeconds(seconds));
        string status = job.AttemptCount >= 8 ? "failed" : "pending";
        connection.Execute(
            """
            UPDATE memory_extraction_jobs
            SET status=@status,next_attempt_at=@next,lease_owner=NULL,lease_expires_at=NULL,last_error=@safeError,updated_at=@now
            WHERE id=@id
            """, new { id = job.Id, status, next, safeError, now });
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        connection.Execute("PRAGMA busy_timeout=5000;");
        return connection;
    }

    private static string Timestamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static string BuildJobId(string targetType, string targetId, string sessionId, string userMessage, string assistantMessage)
    {
        string input = string.Join('\n', targetType, targetId, sessionId, userMessage, assistantMessage);
        return "memory-job-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }
}

internal sealed partial class MemoryExtractionDispatcherService(
    MemoryExtractionJobStore jobs,
    IChatGatewayService chatGateway,
    ILogger<MemoryExtractionDispatcherService> logger) : BackgroundService
{
    private readonly string owner = $"gateway-{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IMemoryExtractionProcessor processor = (IMemoryExtractionProcessor)chatGateway;
        while (!stoppingToken.IsCancellationRequested)
        {
            MemoryExtractionJob? job = jobs.ClaimNext(owner, TimeSpan.FromMinutes(5));
            if (job is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                using CancellationTokenSource renewalStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                Task renewal = RenewLeaseAsync(job.Id, renewalStop.Token);
                try
                {
                    if (await processor.ProcessMemoryExtractionAsync(job, stoppingToken).ConfigureAwait(false))
                    {
                        jobs.Complete(job.Id);
                    }
                    else
                    {
                        jobs.Retry(job, "processing_failed");
                    }
                }
                finally
                {
                    await renewalStop.CancelAsync().ConfigureAwait(false);
                    try { await renewal.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (renewalStop.IsCancellationRequested) { }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                // HttpClient.Timeout surfaces as TaskCanceledException (an OCE). Catch those when the
                // host is not shutting down so StopHost cannot tear down Gateway on slow Ollama.
                try
                {
                    jobs.Retry(job, ex.GetType().Name);
                }
                catch (Exception retryEx) when (retryEx is not OperationCanceledException)
                {
                    LogJobFailed(logger, retryEx, job.Id, job.AttemptCount);
                }

                LogJobFailed(logger, ex, job.Id, job.AttemptCount);
            }
        }
    }

    private async Task RenewLeaseAsync(string jobId, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!jobs.RenewLease(jobId, owner, TimeSpan.FromMinutes(5)))
            {
                return;
            }
        }
    }

    [LoggerMessage(EventId = 180, Level = LogLevel.Warning, Message = "Memory extraction job {JobId} failed on attempt {AttemptCount}")]
    private static partial void LogJobFailed(ILogger logger, Exception exception, string jobId, int attemptCount);
}
