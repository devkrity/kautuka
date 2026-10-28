using System.Text.Json;

using Dapper;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Domain.Usage;

internal interface IGatewayUsageQueue
{
    bool TryEnqueue(GatewayUsageFact fact);
    bool TryDequeue(out GatewayUsageFact? fact);
    IAsyncEnumerable<GatewayUsageFact> DequeueAllAsync(CancellationToken cancellationToken);
    void Complete(GatewayUsageFact fact) { }
    void RecordFailure(GatewayUsageFact fact, string safeError) { }
}

internal sealed class GatewayUsageQueue(KautukaDatabase database) : IGatewayUsageQueue, IDisposable
{
    private readonly SemaphoreSlim signal = new(0, 1);

    public bool TryEnqueue(GatewayUsageFact fact)
    {
        using var connection = OpenConnection();
        string payload = JsonSerializer.Serialize(fact, KautukaJsonContext.Default.GatewayUsageFact);
        int changed = connection.Execute(
            "INSERT INTO usage_staging (id,run_id,payload_json,created_at) VALUES (@id,@runId,@payload,@createdAt) ON CONFLICT(run_id) DO NOTHING",
            new
            {
                id = fact.Id,
                runId = fact.RunId,
                payload,
                createdAt = fact.CreatedAt,
            });
        if (changed > 0 && signal.CurrentCount == 0)
        {
            signal.Release();
        }
        return true;
    }

    public bool TryDequeue(out GatewayUsageFact? fact)
    {
        fact = ReadNext();
        return fact is not null;
    }

    public async IAsyncEnumerable<GatewayUsageFact> DequeueAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            GatewayUsageFact? fact = ReadNext();
            if (fact is not null)
            {
                yield return fact;
                continue;
            }

            await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Complete(GatewayUsageFact fact)
    {
        using var connection = OpenConnection();
        connection.Execute("DELETE FROM usage_staging WHERE id=@id", new { id = fact.Id });
    }

    public void RecordFailure(GatewayUsageFact fact, string safeError)
    {
        using var connection = OpenConnection();
        connection.Execute(
            "UPDATE usage_staging SET attempt_count=attempt_count+1,last_error=@safeError WHERE id=@id",
            new { id = fact.Id, safeError });
    }

    private GatewayUsageFact? ReadNext()
    {
        using var connection = OpenConnection();
        string? payload = connection.QueryFirstOrDefault<string>("SELECT payload_json FROM usage_staging ORDER BY created_at,id LIMIT 1");
        return payload is null ? null : JsonSerializer.Deserialize(payload, KautukaJsonContext.Default.GatewayUsageFact);
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        connection.Execute("PRAGMA busy_timeout=1000;");
        return connection;
    }

    public void Dispose() => signal.Dispose();
}
