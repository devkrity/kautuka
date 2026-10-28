using System.Text.Json;

using Kautuka.Gateway.Domain.Memory;

using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Domain.Execution;

internal sealed partial class ChatGatewayService
{
    private void PersistUserMessage(string sessionId, string userText, string? source = null, IReadOnlyList<string>? userAssetIds = null, string? userId = null)
    {
        try
        {
            var userMsg = new GatewayMessage(
                $"msg-{Guid.NewGuid():N}",
                "user",
                userText,
                DateTimeOffset.UtcNow.ToString("O"),
                false,
                null,
                null,
                null,
                Source: source,
                Internal: string.Equals(source, "hitl", StringComparison.OrdinalIgnoreCase));

            sessions.AppendUserMessage(sessionId, userMsg, userAssetIds, userId);
        }
        catch (InvalidOperationException ex)
        {
            ChatGatewayServiceLog.PersistMessagesFailed(logger, ex, sessionId);
        }
        catch (JsonException ex)
        {
            ChatGatewayServiceLog.PersistMessagesFailed(logger, ex, sessionId);
        }
        catch (IOException ex)
        {
            ChatGatewayServiceLog.PersistMessagesFailed(logger, ex, sessionId);
        }
    }

    private bool UserMessageAlreadyRecorded(string sessionId, string userText)
    {
        if (string.IsNullOrWhiteSpace(userText)
            || !sessions.TryGetSession(sessionId, out GatewaySession? session)
            || session is null)
        {
            return false;
        }

        GatewayMessage? lastUserMessage = session.Messages.LastOrDefault(static message =>
            string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)
            && !message.Internal);
        return lastUserMessage is not null
            && string.Equals(lastUserMessage.Content, userText, StringComparison.Ordinal);
    }

    private void PersistMessages(string sessionId, string userText, GatewayChatResult result, string? source = null, IReadOnlyList<string>? userAssetIds = null, string? userId = null)
    {
        try
        {
            if (UserMessageAlreadyRecorded(sessionId, userText))
            {
                PersistAssistantStateOnly(sessionId, result, userId);
                return;
            }

            var userMsg = new GatewayMessage(
                $"msg-{Guid.NewGuid():N}",
                "user",
                userText,
                DateTimeOffset.UtcNow.ToString("O"),
                false,
                null,
                null,
                null,
                Source: source,
                Internal: string.Equals(source, "hitl", StringComparison.OrdinalIgnoreCase));

            var assistantMsg = new GatewayMessage(
                $"msg-{Guid.NewGuid():N}",
                "assistant",
                result.Response,
                DateTimeOffset.UtcNow.ToString("O"),
                result.Compacted,
                string.IsNullOrWhiteSpace(result.Thinking) ? null : result.Thinking,
                result.Metrics,
                result.ToolCalls?.Count > 0 ? result.ToolCalls : null,
                Assets: result.Assets?.Count > 0 ? result.Assets : null,
                TraceId: result.TraceId,
                Run: result.Run,
                ExecutedActions: result.ExecutedActions is { Count: > 0 } ? result.ExecutedActions : null,
                ActivityEntries: result.ActivityEntries is { Count: > 0 } ? result.ActivityEntries : null);

            sessions.AppendMessages(sessionId, userMsg, assistantMsg, userAssetIds, userId);
        }
        catch (InvalidOperationException ex)
        {
            ChatGatewayServiceLog.PersistMessagesFailed(logger, ex, sessionId);
        }
        catch (JsonException ex)
        {
            ChatGatewayServiceLog.PersistMessagesFailed(logger, ex, sessionId);
        }
        catch (IOException ex)
        {
            ChatGatewayServiceLog.PersistMessagesFailed(logger, ex, sessionId);
        }
    }

    private void PersistAssistantStateOnly(string sessionId, GatewayChatResult result, string? userId = null)
    {
        bool hasAssistantState =
            !string.IsNullOrWhiteSpace(result.Response)
            || !string.IsNullOrWhiteSpace(result.Thinking)
            || (result.ToolCalls?.Count ?? 0) > 0
            || (result.ExecutedActions?.Count ?? 0) > 0
            || (result.ActivityEntries?.Count ?? 0) > 0
            || result.Run is not null
            || (result.Assets?.Count ?? 0) > 0;

        if (!hasAssistantState)
        {
            return;
        }

        try
        {
            IReadOnlyList<GatewayToolCall>? toolCalls = result.ToolCalls is { Count: > 0 }
                ? result.ToolCalls
                : null;
            IReadOnlyList<GatewayConversationAsset>? assets = result.Assets is { Count: > 0 }
                ? result.Assets
                : null;

            var assistantMsg = new GatewayMessage(
                $"msg-{Guid.NewGuid():N}",
                "assistant",
                result.Response,
                DateTimeOffset.UtcNow.ToString("O"),
                result.Compacted,
                string.IsNullOrWhiteSpace(result.Thinking) ? null : result.Thinking,
                result.Metrics,
                toolCalls,
                Assets: assets,
                TraceId: result.TraceId,
                Run: result.Run,
                ExecutedActions: result.ExecutedActions is { Count: > 0 } ? result.ExecutedActions : null,
                ActivityEntries: result.ActivityEntries is { Count: > 0 } ? result.ActivityEntries : null);

            sessions.AppendAssistantMessage(sessionId, assistantMsg, userId);
        }
        catch (InvalidOperationException ex)
        {
            ChatGatewayServiceLog.PersistMessagesFailed(logger, ex, sessionId);
        }
        catch (JsonException ex)
        {
            ChatGatewayServiceLog.PersistMessagesFailed(logger, ex, sessionId);
        }
        catch (IOException ex)
        {
            ChatGatewayServiceLog.PersistMessagesFailed(logger, ex, sessionId);
        }
    }

    private void PersistApprovalPausedRun(
        string sessionId,
        string userMessage,
        GatewayChatResult result,
        GatewayChatApprovalRequest? _,
        IReadOnlyList<string>? userAssetIds,
        string? userId,
        string? runId,
        string? source,
        bool enqueueUsage = true)
    {
        bool hasAssistantState =
            !string.IsNullOrWhiteSpace(result.Response)
            || !string.IsNullOrWhiteSpace(result.Thinking)
            || (result.ToolCalls?.Count ?? 0) > 0
            || (result.ExecutedActions?.Count ?? 0) > 0
            || (result.ActivityEntries?.Count ?? 0) > 0
            || result.Run is not null
            || (result.Assets?.Count ?? 0) > 0;

        if (hasAssistantState)
        {
            try
            {
                IReadOnlyList<GatewayToolCall>? toolCalls = result.ToolCalls is { Count: > 0 }
                    ? result.ToolCalls
                    : null;
                IReadOnlyList<GatewayConversationAsset>? assets = result.Assets is { Count: > 0 }
                    ? result.Assets
                    : null;

                var userMsg = new GatewayMessage(
                    $"msg-{Guid.NewGuid():N}",
                    "user",
                    userMessage,
                    DateTimeOffset.UtcNow.ToString("O"),
                    false,
                    null,
                    null,
                    null,
                    Source: source,
                    Internal: string.Equals(source, "hitl", StringComparison.OrdinalIgnoreCase));

                // Pause is a delivery boundary, not a root final. Do not persist pre-tool assistant
                // prose as a completed answer — continuation will persist the real final, and keeping
                // both surfaces as "final → stop → resume → final again" on Chat / channel peers.
                bool awaitingApproval = result.Run is not null
                    && string.Equals(
                        result.Run.Phase,
                        GatewayRunPhases.AwaitingToolApproval,
                        StringComparison.Ordinal);
                string assistantContent = awaitingApproval
                    ? string.Empty
                    : result.Response;

                var assistantMsg = new GatewayMessage(
                    $"msg-{Guid.NewGuid():N}",
                    "assistant",
                    assistantContent,
                    DateTimeOffset.UtcNow.ToString("O"),
                    result.Compacted,
                    string.IsNullOrWhiteSpace(result.Thinking) ? null : result.Thinking,
                    result.Metrics,
                    toolCalls,
                    Assets: assets,
                    TraceId: result.TraceId,
                    Run: result.Run,
                    ExecutedActions: result.ExecutedActions is { Count: > 0 } ? result.ExecutedActions : null,
                    ActivityEntries: result.ActivityEntries is { Count: > 0 } ? result.ActivityEntries : null);

                sessions.AppendMessages(sessionId, userMsg, assistantMsg, userAssetIds, userId);
            }
            catch (InvalidOperationException ex)
            {
                ChatGatewayServiceLog.PersistMessagesFailed(logger, ex, sessionId);
            }
            catch (JsonException ex)
            {
                ChatGatewayServiceLog.PersistMessagesFailed(logger, ex, sessionId);
            }
            catch (IOException ex)
            {
                ChatGatewayServiceLog.PersistMessagesFailed(logger, ex, sessionId);
            }
        }
        else
        {
            PersistUserMessage(sessionId, userMessage, source, userAssetIds, userId);
        }

        GatewayRun run = sessions.AppendRun(result.TargetType, result.TargetId, userId ?? string.Empty, sessionId, userMessage, string.Empty, result.Metrics, result.EffectiveBehavior, runId, traceId: result.TraceId);
        if (enqueueUsage)
        {
            EnqueueUsageFact(run, result, userId, source);
        }
    }

    private void QueueFactExtraction(string targetType, string targetId, string sessionId, string userMessage, string assistantMessage)
    {
        if (!IsFactExtractionEnabledForTarget(targetType, targetId))
        {
            ChatGatewayServiceLog.FactExtractionSkipped(logger, targetType, targetId);
            return;
        }

        GatewayMemoryScope writeScope = memoryScopePolicy.GetWriteScope(targetType, targetId);
        if (!memoryExtractionJobs.Enqueue(writeScope.TargetType, writeScope.TargetId, sessionId, userMessage, assistantMessage))
        {
            ChatGatewayServiceLog.FactExtractionEnqueueFailed(logger, writeScope.TargetType, writeScope.TargetId, sessionId);
        }
    }

    public Task<bool> ProcessMemoryExtractionAsync(MemoryExtractionJob job, CancellationToken cancellationToken)
        => ExtractAndStoreFactsAsync(job, cancellationToken);

    private async Task<bool> ExtractAndStoreFactsAsync(
        MemoryExtractionJob job,
        CancellationToken ct)
    {
        string targetType = job.TargetType;
        string targetId = job.TargetId;
        string sessionId = job.SessionId;
        try
        {
            IChatClient? client = await ResolveExtractionClientAsync(targetType, targetId, ct).ConfigureAwait(false);
            ChatGatewayServiceLog.FactExtractionClientResolved(logger, client is null ? "null" : "resolved", targetType, targetId);
            string ownerUserId = state.Sessions.TryGetValue(sessionId, out GatewaySession? session) ? session.OwnerUserId : string.Empty;

            DateTimeOffset timestamp = DateTimeOffset.UtcNow;
            IReadOnlyList<GatewayMemoryFact> facts = await factExtractor.ExtractAsync(
                client, targetType, targetId, ownerUserId, job.UserMessage, job.AssistantMessage, timestamp, ct).ConfigureAwait(false);

            ChatGatewayServiceLog.FactExtractionCompleted(logger, facts.Count, targetType, targetId);

            if (facts.Count > 0)
            {
                double persistThreshold = configProvider.GetCurrent().Memory.FactPersistThreshold;
                List<GatewayMemoryFact> toStore = facts
                    .Where(f => f.Confidence >= persistThreshold)
                    .ToList();

                ChatGatewayServiceLog.FactExtractionThresholdApplied(logger, toStore.Count, facts.Count, persistThreshold);

                if (toStore.Count > 0)
                {
                    List<GatewayMemoryFact> stamped = toStore
                        .Select((f, index) => f with { Id = $"{job.Id}-{index}", OwnerUserId = state.Sessions.TryGetValue(sessionId, out GatewaySession? session) ? session.OwnerUserId : string.Empty, SessionId = sessionId })
                        .Where(f => !state.MemoryStore.TryGet(f.Id, out _))
                        .ToList();
                    state.UpsertAgentMemoryFacts(targetType, targetId, stamped);
                    ChatGatewayServiceLog.FactExtractionPersisted(logger, stamped.Count);
                    _ = memoryEmbeddingService.EnqueuePersistFacts(targetType, targetId, stamped);
                }
            }
            return true;
        }
        catch (InvalidOperationException ex)
        {
            ChatGatewayServiceLog.FactExtractionFailed(logger, ex, targetType, targetId);
            return false;
        }
        catch (JsonException ex)
        {
            ChatGatewayServiceLog.FactExtractionFailed(logger, ex, targetType, targetId);
            return false;
        }
        catch (IOException ex)
        {
            ChatGatewayServiceLog.FactExtractionFailed(logger, ex, targetType, targetId);
            return false;
        }
    }

    private async Task<IChatClient?> ResolveExtractionClientAsync(string targetType, string targetId, CancellationToken ct)
    {
        string? agentId = targetType == "agent" ? targetId : null;
        if (agentId is null) return null;
        if (!state.Agents.TryGetValue(agentId, out GatewayAgent? agent)) return null;
        if (!state.Models.TryGetValue(agent.ModelId, out GatewayModel? model)) return null;
        if (!state.Providers.TryGetValue(model.ProviderId, out GatewayProvider? provider)) return null;
        return await aiClientFactory.TryCreateAsync(provider.Id, provider.Type, provider.Settings, model.ModelName, ct)
            .ConfigureAwait(false);
    }

    private bool IsFactExtractionEnabledForTarget(string targetType, string targetId)
    {
        if (!string.Equals(targetType, "agent", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!state.Agents.TryGetValue(targetId, out GatewayAgent? agent) || agent is null)
        {
            return true;
        }

        return agent.Session.GetBoolNodeValue("memoryFactExtractionEnabled", true);
    }

    private static IEnumerable<string> ChunkText(string text, int chunkSize)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        for (int idx = 0; idx < text.Length; idx += chunkSize)
        {
            yield return text.Substring(idx, Math.Min(chunkSize, text.Length - idx));
        }
    }
}

internal static partial class ChatGatewayServiceLog
{
    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Executing in-process stream chat with {FactCount} memory facts for {TargetType}:{TargetId}")]
    public static partial void ExecutingStreamChat(ILogger logger, int factCount, string targetType, string targetId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Failed to persist messages for session {SessionId}")]
    public static partial void PersistMessagesFailed(ILogger logger, Exception exception, string sessionId);

    [LoggerMessage(EventId = 10, Level = LogLevel.Information, Message = "Fact extraction skipped (disabled) for {TargetType}:{TargetId}")]
    public static partial void FactExtractionSkipped(ILogger logger, string targetType, string targetId);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "FactExtract: client={ClientNull} targetType={TargetType} targetId={TargetId}")]
    public static partial void FactExtractionClientResolved(ILogger logger, string clientNull, string targetType, string targetId);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "FactExtract: extracted {Count} facts for {TargetType}:{TargetId}")]
    public static partial void FactExtractionCompleted(ILogger logger, int count, string targetType, string targetId);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "FactExtract: {Stored}/{Total} facts met persist threshold ({Threshold:P0})")]
    public static partial void FactExtractionThresholdApplied(ILogger logger, int stored, int total, double threshold);

    [LoggerMessage(EventId = 8, Level = LogLevel.Information, Message = "FactExtract: persisted {Count} facts")]
    public static partial void FactExtractionPersisted(ILogger logger, int count);

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning, Message = "Fact extraction failed for {TargetType}:{TargetId}")]
    public static partial void FactExtractionFailed(ILogger logger, Exception exception, string targetType, string targetId);

    [LoggerMessage(EventId = 11, Level = LogLevel.Warning, Message = "Usage queue full. Dropping usage fact for run {RunId}")]
    public static partial void UsageQueueDropped(ILogger logger, string runId);

    [LoggerMessage(EventId = 12, Level = LogLevel.Warning, Message = "Failed to durably enqueue memory extraction for {TargetType}:{TargetId} session {SessionId}")]
    public static partial void FactExtractionEnqueueFailed(ILogger logger, string targetType, string targetId, string sessionId);

}
