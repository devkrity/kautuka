using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Domain.Usage;

internal interface IGatewayUsageStore
{
    void AppendBatch(IReadOnlyList<GatewayUsageFact> facts);
}

internal sealed class GatewayUsageStore(KautukaUsageDatabase database) : IGatewayUsageStore
{
    public void AppendBatch(IReadOnlyList<GatewayUsageFact> facts)
    {
        if (!database.IsAvailable || facts.Count == 0)
        {
            if (facts.Count > 0)
            {
                throw new InvalidOperationException("Usage database is unavailable.");
            }
            return;
        }

        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();

        using var pragmaCmd = connection.CreateCommand();
        pragmaCmd.CommandText = "PRAGMA busy_timeout=1000;";
        pragmaCmd.ExecuteNonQuery();

        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO runtime_run_usage (
                id, usage_fact_version, run_id, session_id, target_type, target_id,
                user_id, username_snapshot, user_display_name_snapshot,
                agent_id, agent_name_snapshot, model_id, model_name_snapshot,
                provider_id, provider_name_snapshot, run_status, source_channel,
                run_started_at, first_model_output_at, run_ended_at,
                total_duration_ms, first_token_latency_ms, token_in, token_out, total_tokens,
                provider_request_count, model_turn_count, assistant_turn_count, tool_call_count,
                cost_basis, input_cost, output_cost, total_cost, cost_currency, pricing_source,
                usage_date, created_at)
            VALUES (
                $id, $usageFactVersion, $runId, $sessionId, $targetType, $targetId,
                $userId, $usernameSnapshot, $userDisplayNameSnapshot,
                $agentId, $agentNameSnapshot, $modelId, $modelNameSnapshot,
                $providerId, $providerNameSnapshot, $runStatus, $sourceChannel,
                $runStartedAt, $firstModelOutputAt, $runEndedAt,
                $totalDurationMs, $firstTokenLatencyMs, $tokenIn, $tokenOut, $totalTokens,
                $providerRequestCount, $modelTurnCount, $assistantTurnCount, $toolCallCount,
                $costBasis, $inputCost, $outputCost, $totalCost, $costCurrency, $pricingSource,
                $usageDate, $createdAt)
            ON CONFLICT(run_id) DO NOTHING
            """;

        AddParameter(command, "$id");
        AddParameter(command, "$usageFactVersion");
        AddParameter(command, "$runId");
        AddParameter(command, "$sessionId");
        AddParameter(command, "$targetType");
        AddParameter(command, "$targetId");
        AddParameter(command, "$userId");
        AddParameter(command, "$usernameSnapshot");
        AddParameter(command, "$userDisplayNameSnapshot");
        AddParameter(command, "$agentId");
        AddParameter(command, "$agentNameSnapshot");
        AddParameter(command, "$modelId");
        AddParameter(command, "$modelNameSnapshot");
        AddParameter(command, "$providerId");
        AddParameter(command, "$providerNameSnapshot");
        AddParameter(command, "$runStatus");
        AddParameter(command, "$sourceChannel");
        AddParameter(command, "$runStartedAt");
        AddParameter(command, "$firstModelOutputAt");
        AddParameter(command, "$runEndedAt");
        AddParameter(command, "$totalDurationMs");
        AddParameter(command, "$firstTokenLatencyMs");
        AddParameter(command, "$tokenIn");
        AddParameter(command, "$tokenOut");
        AddParameter(command, "$totalTokens");
        AddParameter(command, "$providerRequestCount");
        AddParameter(command, "$modelTurnCount");
        AddParameter(command, "$assistantTurnCount");
        AddParameter(command, "$toolCallCount");
        AddParameter(command, "$costBasis");
        AddParameter(command, "$inputCost");
        AddParameter(command, "$outputCost");
        AddParameter(command, "$totalCost");
        AddParameter(command, "$costCurrency");
        AddParameter(command, "$pricingSource");
        AddParameter(command, "$usageDate");
        AddParameter(command, "$createdAt");

        foreach (GatewayUsageFact fact in facts)
        {
            command.Parameters["$id"].Value = fact.Id;
            command.Parameters["$usageFactVersion"].Value = fact.UsageFactVersion;
            command.Parameters["$runId"].Value = fact.RunId;
            command.Parameters["$sessionId"].Value = DbValue(fact.SessionId);
            command.Parameters["$targetType"].Value = fact.TargetType;
            command.Parameters["$targetId"].Value = fact.TargetId;
            command.Parameters["$userId"].Value = DbValue(fact.UserId);
            command.Parameters["$usernameSnapshot"].Value = DbValue(fact.UsernameSnapshot);
            command.Parameters["$userDisplayNameSnapshot"].Value = DbValue(fact.UserDisplayNameSnapshot);
            command.Parameters["$agentId"].Value = DbValue(fact.AgentId);
            command.Parameters["$agentNameSnapshot"].Value = DbValue(fact.AgentNameSnapshot);
            command.Parameters["$modelId"].Value = DbValue(fact.ModelId);
            command.Parameters["$modelNameSnapshot"].Value = DbValue(fact.ModelNameSnapshot);
            command.Parameters["$providerId"].Value = DbValue(fact.ProviderId);
            command.Parameters["$providerNameSnapshot"].Value = DbValue(fact.ProviderNameSnapshot);
            command.Parameters["$runStatus"].Value = fact.RunStatus;
            command.Parameters["$sourceChannel"].Value = DbValue(fact.SourceChannel);
            command.Parameters["$runStartedAt"].Value = fact.RunStartedAt;
            command.Parameters["$firstModelOutputAt"].Value = DbValue(fact.FirstModelOutputAt);
            command.Parameters["$runEndedAt"].Value = fact.RunEndedAt;
            command.Parameters["$totalDurationMs"].Value = fact.TotalDurationMs;
            command.Parameters["$firstTokenLatencyMs"].Value = DbValue(fact.FirstTokenLatencyMs);
            command.Parameters["$tokenIn"].Value = DbValue(fact.TokenIn);
            command.Parameters["$tokenOut"].Value = DbValue(fact.TokenOut);
            command.Parameters["$totalTokens"].Value = DbValue(fact.TotalTokens);
            command.Parameters["$providerRequestCount"].Value = DbValue(fact.ProviderRequestCount);
            command.Parameters["$modelTurnCount"].Value = DbValue(fact.ModelTurnCount);
            command.Parameters["$assistantTurnCount"].Value = DbValue(fact.AssistantTurnCount);
            command.Parameters["$toolCallCount"].Value = DbValue(fact.ToolCallCount);
            command.Parameters["$costBasis"].Value = DbValue(fact.CostBasis);
            command.Parameters["$inputCost"].Value = DbValue(fact.InputCost);
            command.Parameters["$outputCost"].Value = DbValue(fact.OutputCost);
            command.Parameters["$totalCost"].Value = DbValue(fact.TotalCost);
            command.Parameters["$costCurrency"].Value = DbValue(fact.CostCurrency);
            command.Parameters["$pricingSource"].Value = DbValue(fact.PricingSource);
            command.Parameters["$usageDate"].Value = fact.UsageDate;
            command.Parameters["$createdAt"].Value = fact.CreatedAt;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static void AddParameter(SqliteCommand command, string name)
        => command.Parameters.Add(new SqliteParameter(name, DBNull.Value));

    private static object DbValue<T>(T? value) where T : struct
        => value.HasValue ? value.Value : DBNull.Value;

    private static object DbValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;

    private static object DbValue(decimal? value)
        => value.HasValue ? value.Value : DBNull.Value;
}
