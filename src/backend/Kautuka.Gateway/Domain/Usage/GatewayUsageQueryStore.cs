using System.Globalization;
using System.Text;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Domain.Usage;

internal interface IGatewayUsageQueryStore
{
    IReadOnlyList<GatewayUsageRunListItem> ListRuns(GatewayUsageQueryFilter filter);
    long CountRuns(GatewayUsageQueryFilter filter);
    GatewayUsageTotalsPayload GetTotals(GatewayUsageQueryFilter filter);
    IReadOnlyList<GatewayUsageSummaryBucket> GetBuckets(GatewayUsageQueryFilter filter, string groupBy, int limit);
}

internal sealed class GatewayUsageQueryStore(KautukaUsageDatabase database) : IGatewayUsageQueryStore
{
    public IReadOnlyList<GatewayUsageRunListItem> ListRuns(GatewayUsageQueryFilter filter)
    {
        if (!database.IsAvailable)
        {
            return [];
        }

        using var connection = OpenConnection();
        var sql = new StringBuilder(
            """
            SELECT run_id AS RunId,
                   session_id AS SessionId,
                   user_id AS UserId,
                   username_snapshot AS UsernameSnapshot,
                   user_display_name_snapshot AS UserDisplayNameSnapshot,
                   agent_id AS AgentId,
                   agent_name_snapshot AS AgentNameSnapshot,
                   model_id AS ModelId,
                   model_name_snapshot AS ModelNameSnapshot,
                   provider_id AS ProviderId,
                   provider_name_snapshot AS ProviderNameSnapshot,
                   run_status AS RunStatus,
                   source_channel AS SourceChannel,
                   run_started_at AS RunStartedAt,
                   first_model_output_at AS FirstModelOutputAt,
                   run_ended_at AS RunEndedAt,
                   total_duration_ms AS TotalDurationMs,
                   first_token_latency_ms AS FirstTokenLatencyMs,
                   token_in AS TokenIn,
                   token_out AS TokenOut,
                   total_tokens AS TotalTokens,
                   provider_request_count AS ProviderRequestCount,
                   model_turn_count AS ModelTurnCount,
                   assistant_turn_count AS AssistantTurnCount,
                   tool_call_count AS ToolCallCount,
                   cost_basis AS CostBasis,
                   input_cost AS InputCost,
                   output_cost AS OutputCost,
                   total_cost AS TotalCost,
                   cost_currency AS CostCurrency,
                   pricing_source AS PricingSource,
                   usage_date AS UsageDate
            FROM runtime_run_usage
            """);

        AppendWhereClause(sql, filter);
        sql.AppendLine("ORDER BY run_started_at DESC");
        sql.AppendLine("LIMIT @Limit OFFSET @Offset");

        using SqliteCommand command = CreateCommand(connection, sql.ToString(), filter);
        using SqliteDataReader reader = command.ExecuteReader();

        List<GatewayUsageRunListItem> items = [];
        while (reader.Read())
        {
            items.Add(new GatewayUsageRunListItem(
                reader.GetString(0),
                ReadNullableString(reader, 1),
                ReadNullableString(reader, 2),
                ReadNullableString(reader, 3),
                ReadNullableString(reader, 4),
                ReadNullableString(reader, 5),
                ReadNullableString(reader, 6),
                ReadNullableString(reader, 7),
                ReadNullableString(reader, 8),
                ReadNullableString(reader, 9),
                ReadNullableString(reader, 10),
                reader.GetString(11),
                ReadNullableString(reader, 12),
                reader.GetString(13),
                ReadNullableString(reader, 14),
                reader.GetString(15),
                reader.GetInt32(16),
                ReadNullableInt32(reader, 17),
                ReadNullableInt32(reader, 18),
                ReadNullableInt32(reader, 19),
                ReadNullableInt32(reader, 20),
                ReadNullableInt32(reader, 21),
                ReadNullableInt32(reader, 22),
                ReadNullableInt32(reader, 23),
                ReadNullableInt32(reader, 24),
                ReadNullableString(reader, 25),
                ReadNullableDecimal(reader, 26),
                ReadNullableDecimal(reader, 27),
                ReadNullableDecimal(reader, 28),
                ReadNullableString(reader, 29),
                ReadNullableString(reader, 30),
                reader.GetString(31)));
        }

        return items;
    }

    public long CountRuns(GatewayUsageQueryFilter filter)
    {
        if (!database.IsAvailable)
        {
            return 0;
        }

        using var connection = OpenConnection();
        var sql = new StringBuilder("SELECT COUNT(*) FROM runtime_run_usage");
        AppendWhereClause(sql, filter);

        using SqliteCommand command = CreateCommand(connection, sql.ToString(), filter);
        object? value = command.ExecuteScalar();
        return value is null || value is DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public GatewayUsageTotalsPayload GetTotals(GatewayUsageQueryFilter filter)
    {
        if (!database.IsAvailable)
        {
            return EmptyTotals();
        }

        using var connection = OpenConnection();
        var sql = new StringBuilder(
            """
            SELECT COUNT(*) AS RunCount,
                   COALESCE(SUM(provider_request_count), 0) AS ProviderRequestCount,
                   COALESCE(SUM(assistant_turn_count), 0) AS AssistantTurnCount,
                   COALESCE(SUM(model_turn_count), 0) AS ModelTurnCount,
                   COALESCE(SUM(tool_call_count), 0) AS ToolCallCount,
                   COALESCE(SUM(token_in), 0) AS TokenIn,
                   COALESCE(SUM(token_out), 0) AS TokenOut,
                   COALESCE(SUM(total_tokens), 0) AS TotalTokens,
                   COALESCE(SUM(total_duration_ms), 0) AS TotalDurationMs,
                   CAST(AVG(first_token_latency_ms) AS INTEGER) AS AvgFirstTokenLatencyMs,
                   SUM(input_cost) AS InputCost,
                   SUM(output_cost) AS OutputCost,
                   SUM(total_cost) AS TotalCost
            FROM runtime_run_usage
            """);
        AppendWhereClause(sql, filter);

        using SqliteCommand command = CreateCommand(connection, sql.ToString(), filter);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return EmptyTotals();
        }

        return new GatewayUsageTotalsPayload(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            ReadNullableInt64(reader, 9),
            ReadNullableDecimal(reader, 10),
            ReadNullableDecimal(reader, 11),
            ReadNullableDecimal(reader, 12));
    }

    public IReadOnlyList<GatewayUsageSummaryBucket> GetBuckets(GatewayUsageQueryFilter filter, string groupBy, int limit)
    {
        if (!database.IsAvailable)
        {
            return [];
        }

        (string keyExpr, string labelExpr, string orderExpr) = groupBy switch
        {
            "date" => ("usage_date", "usage_date", "usage_date DESC"),
            "session" => ("COALESCE(session_id, 'unknown')", "COALESCE(session_id, 'Unknown session')", "RunCount DESC, Label ASC"),
            "model" => ("COALESCE(model_id, 'unknown')", "COALESCE(model_name_snapshot, model_id, 'Unknown model')", "RunCount DESC, Label ASC"),
            "provider" => ("COALESCE(provider_id, 'unknown')", "COALESCE(provider_name_snapshot, provider_id, 'Unknown provider')", "RunCount DESC, Label ASC"),
            "agent" => ("COALESCE(agent_id, 'unknown')", "COALESCE(agent_name_snapshot, agent_id, 'Unknown agent')", "RunCount DESC, Label ASC"),
            "source" => ("COALESCE(source_channel, 'unknown')", "COALESCE(source_channel, 'unknown')", "RunCount DESC, Label ASC"),
            "user" => ("COALESCE(user_id, 'unknown')", "COALESCE(user_display_name_snapshot, username_snapshot, user_id, 'Unknown user')", "RunCount DESC, Label ASC"),
            _ => throw new ArgumentOutOfRangeException(nameof(groupBy), groupBy, "Unsupported usage summary group.")
        };

        using var connection = OpenConnection();
        var sql = new StringBuilder(
            $"""
            SELECT {keyExpr} AS [Key],
                   {labelExpr} AS Label,
                   COUNT(*) AS RunCount,
                   COALESCE(SUM(provider_request_count), 0) AS ProviderRequestCount,
                   COALESCE(SUM(assistant_turn_count), 0) AS AssistantTurnCount,
                   COALESCE(SUM(model_turn_count), 0) AS ModelTurnCount,
                   COALESCE(SUM(tool_call_count), 0) AS ToolCallCount,
                   COALESCE(SUM(token_in), 0) AS TokenIn,
                   COALESCE(SUM(token_out), 0) AS TokenOut,
                   COALESCE(SUM(total_tokens), 0) AS TotalTokens,
                   COALESCE(SUM(total_duration_ms), 0) AS TotalDurationMs,
                   CAST(AVG(first_token_latency_ms) AS INTEGER) AS AvgFirstTokenLatencyMs,
                   SUM(input_cost) AS InputCost,
                   SUM(output_cost) AS OutputCost,
                   SUM(total_cost) AS TotalCost
            FROM runtime_run_usage
            """);

        AppendWhereClause(sql, filter);
        sql.Append("GROUP BY ");
        sql.Append(keyExpr);
        sql.Append(", ");
        sql.Append(labelExpr);
        sql.AppendLine();
        sql.Append("ORDER BY ");
        sql.Append(orderExpr);
        sql.AppendLine();
        sql.AppendLine("LIMIT @BucketLimit");

        using SqliteCommand command = CreateCommand(connection, sql.ToString(), filter, limit);
        using SqliteDataReader reader = command.ExecuteReader();

        List<BucketRow> rows = [];
        while (reader.Read())
        {
            rows.Add(new BucketRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                reader.GetInt64(9),
                reader.GetInt64(10),
                ReadNullableInt64(reader, 11),
                ReadNullableDecimal(reader, 12),
                ReadNullableDecimal(reader, 13),
                ReadNullableDecimal(reader, 14)));
        }

        return [.. rows.Select(static row => new GatewayUsageSummaryBucket(
            row.Key,
            row.Label,
            new GatewayUsageTotalsPayload(
                row.RunCount,
                row.ProviderRequestCount,
                row.AssistantTurnCount,
                row.ModelTurnCount,
                row.ToolCallCount,
                row.TokenIn,
                row.TokenOut,
                row.TotalTokens,
                row.TotalDurationMs,
                row.AvgFirstTokenLatencyMs,
                row.InputCost,
                row.OutputCost,
                row.TotalCost)))];
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=1000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static SqliteCommand CreateCommand(SqliteConnection connection, string sql, GatewayUsageQueryFilter filter, int? bucketLimit = null)
    {
        SqliteCommand command = connection.CreateCommand();
#pragma warning disable CA2100 // SQL is composed only from static templates and whitelisted group expressions.
        command.CommandText = sql;
#pragma warning restore CA2100
        AddParameter(command, "@UserId", filter.UserId);
        AddParameter(command, "@SessionId", filter.SessionId);
        AddParameter(command, "@AgentId", filter.AgentId);
        AddParameter(command, "@ModelId", filter.ModelId);
        AddParameter(command, "@ProviderId", filter.ProviderId);
        AddParameter(command, "@Source", filter.Source);
        AddParameter(command, "@FromTimestamp", filter.FromTimestamp);
        AddParameter(command, "@ToTimestamp", filter.ToTimestamp);
        command.Parameters.AddWithValue("@Limit", filter.Limit);
        command.Parameters.AddWithValue("@Offset", filter.Offset);

        if (bucketLimit.HasValue)
        {
            command.Parameters.AddWithValue("@BucketLimit", bucketLimit.Value);
        }

        return command;
    }

    private static void AddParameter(SqliteCommand command, string name, string? value)
        => command.Parameters.AddWithValue(name, string.IsNullOrWhiteSpace(value) ? DBNull.Value : value);

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? ReadNullableInt32(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static long? ReadNullableInt64(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static decimal? ReadNullableDecimal(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);

    private static void AppendWhereClause(StringBuilder sql, GatewayUsageQueryFilter filter)
    {
        List<string> clauses = [];

        if (!string.IsNullOrWhiteSpace(filter.UserId))
        {
            clauses.Add("user_id = @UserId");
        }

        if (!string.IsNullOrWhiteSpace(filter.SessionId))
        {
            clauses.Add("session_id = @SessionId");
        }

        if (!string.IsNullOrWhiteSpace(filter.AgentId))
        {
            clauses.Add("agent_id = @AgentId");
        }

        if (!string.IsNullOrWhiteSpace(filter.ModelId))
        {
            clauses.Add("model_id = @ModelId");
        }

        if (!string.IsNullOrWhiteSpace(filter.ProviderId))
        {
            clauses.Add("provider_id = @ProviderId");
        }

        if (!string.IsNullOrWhiteSpace(filter.Source))
        {
            clauses.Add("source_channel = @Source");
        }

        if (!string.IsNullOrWhiteSpace(filter.FromTimestamp))
        {
            clauses.Add("run_started_at >= @FromTimestamp");
        }

        if (!string.IsNullOrWhiteSpace(filter.ToTimestamp))
        {
            clauses.Add("run_started_at <= @ToTimestamp");
        }

        if (clauses.Count == 0)
        {
            return;
        }

        sql.AppendLine();
        sql.Append("WHERE ");
        sql.Append(string.Join(" AND ", clauses));
        sql.AppendLine();
    }

    private static GatewayUsageTotalsPayload EmptyTotals()
        => new(0, 0, 0, 0, 0, 0, 0, 0, 0, null, null, null, null);

    private sealed record BucketRow(
        string Key,
        string Label,
        long RunCount,
        long ProviderRequestCount,
        long AssistantTurnCount,
        long ModelTurnCount,
        long ToolCallCount,
        long TokenIn,
        long TokenOut,
        long TotalTokens,
        long TotalDurationMs,
        long? AvgFirstTokenLatencyMs,
        decimal? InputCost,
        decimal? OutputCost,
        decimal? TotalCost);
}

internal sealed record GatewayUsageQueryFilter(
    string? UserId,
    string? SessionId,
    string? AgentId,
    string? ModelId,
    string? ProviderId,
    string? Source,
    string? FromTimestamp,
    string? ToTimestamp,
    int Limit,
    int Offset);
