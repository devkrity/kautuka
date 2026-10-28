using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Sessions;

namespace Kautuka.Gateway.Domain.Execution;

internal sealed partial class ChatGatewayService
{
    private const int localDefaultContextWindowTokens = 32_000;
    private const int cloudDefaultContextWindowTokens = 256_000;
    private const int defaultAutoCompactThresholdPercent = 80;

    private async ValueTask<GatewaySession> TryAutoCompactSessionAsync(
        GatewaySession session,
        string targetType,
        string targetId,
        string currentMessage,
        CancellationToken cancellationToken)
    {
        PlatformConfiguration config = configProvider.GetCurrent();
        if (!TryResolveAutoCompactionThreshold(config, targetType, targetId, out int thresholdTokens))
        {
            return session;
        }

        if (session.Compacted
            && DateTimeOffset.TryParse(session.LastCompactedAt, out DateTimeOffset lastCompactedAt)
            && DateTimeOffset.TryParse(session.UpdatedAt, out DateTimeOffset updatedAt)
            && lastCompactedAt >= updatedAt)
        {
            return session;
        }

        int estimatedContextTokens = session.EstimatedContextTokens
            ?? SessionContextEstimator.EstimateTokens(session.Messages, config.Memory.InjectionTokenBudget);

        int upcomingMessageTokens = SessionContextEstimator.EstimateUserMessageTokens(currentMessage);
        if (estimatedContextTokens + upcomingMessageTokens < thresholdTokens)
        {
            return session;
        }

        return await sessionCompactionService.CompactAsync(session.Id, "auto-threshold", cancellationToken).ConfigureAwait(false) ?? session;
    }

    private static IReadOnlyList<InjectedMemoryFact> AppendCompactionContext(
        IReadOnlyList<InjectedMemoryFact> memoryFacts,
        GatewaySession session)
    {
        if (!session.Compacted || string.IsNullOrWhiteSpace(session.CompactSummary))
        {
            return memoryFacts;
        }

        return
        [
            new InjectedMemoryFact(
                SourceScope: "agent",
                SourceId: session.TargetId,
                Title: "Compacted session context",
                Content: session.CompactSummary,
                Category: "session_compaction",
                UpdatedAt: session.LastCompactedAt ?? session.UpdatedAt),
            .. memoryFacts,
        ];
    }

    private static bool TryResolveAutoCompactionThreshold(
        PlatformConfiguration config,
        string targetType,
        string targetId,
        out int thresholdTokens)
    {
        thresholdTokens = 0;

        if (!string.Equals(targetType, "agent", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        AgentDefinition? agentDef = config.Agents.FirstOrDefault(agent =>
            string.Equals(agent.Id, targetId, StringComparison.Ordinal));
        if (agentDef is null)
        {
            return false;
        }

        ModelDefinition? modelDef = config.Models.FirstOrDefault(model =>
            string.Equals(model.Id, agentDef.ModelId, StringComparison.OrdinalIgnoreCase));
        if (modelDef is null)
        {
            return false;
        }

        ProviderDefinition? providerDef = config.Providers.FirstOrDefault(provider =>
            string.Equals(provider.Id, modelDef.ProviderId, StringComparison.OrdinalIgnoreCase));
        if (providerDef is null)
        {
            return false;
        }

        int thresholdPercent = ReadPositiveInt(modelDef.Settings, "autoCompactThresholdPercent")
            ?? ReadPositiveInt(modelDef.Settings, "auto_compact_threshold_percent")
            ?? defaultAutoCompactThresholdPercent;

        int contextWindowTokens = ReadPositiveInt(modelDef.Settings, "contextWindowTokens")
            ?? ReadPositiveInt(modelDef.Settings, "context_window_tokens")
            ?? GetDefaultContextWindowTokens(providerDef.Type);

        int boundedThresholdPercent = Math.Clamp(thresholdPercent, 1, 100);
        thresholdTokens = Math.Max(1, contextWindowTokens * boundedThresholdPercent / 100);
        return true;
    }

    private static int GetDefaultContextWindowTokens(string providerType)
        => string.Equals(providerType, "ollama", StringComparison.OrdinalIgnoreCase)
            || string.Equals(providerType, "test", StringComparison.OrdinalIgnoreCase)
            ? localDefaultContextWindowTokens
            : cloudDefaultContextWindowTokens;

    private static int? ReadPositiveInt(Dictionary<string, object?> settings, string key)
    {
        if (!settings.TryGetValue(key, out object? value) || value is null)
        {
            return null;
        }

        return value switch
        {
            int intValue when intValue > 0 => intValue,
            long longValue when longValue > 0 && longValue <= int.MaxValue => (int)longValue,
            double doubleValue when doubleValue > 0 && doubleValue <= int.MaxValue => (int)Math.Round(doubleValue),
            decimal decimalValue when decimalValue > 0 && decimalValue <= int.MaxValue => (int)Math.Round(decimalValue),
            JsonElement jsonElement => ReadPositiveInt(jsonElement),
            JsonNode jsonNode => ReadPositiveInt(jsonNode.ToJsonString()),
            string text => ReadPositiveInt(text),
            _ => ReadPositiveInt(Convert.ToString(value, CultureInfo.InvariantCulture)),
        };
    }

    private static int? ReadPositiveInt(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int direct) && direct > 0)
        {
            return direct;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return ReadPositiveInt(value.GetString());
        }

        return null;
    }

    private static int? ReadPositiveInt(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0
            ? parsed
            : null;
}
