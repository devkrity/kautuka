using System.Text;

using Kautuka.Gateway.Domain.Stores;

using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Domain.Sessions;

internal interface ISessionCompactionService
{
    ValueTask<GatewaySession?> CompactAsync(string sessionId, string reason, CancellationToken cancellationToken);
}

internal sealed class SessionCompactionService(
    IGatewaySessionStore sessionStore,
    IPlatformConfigurationProvider configProvider,
    KautukaAiClientFactory aiClientFactory,
    MafSessionStore mafSessionStore,
    ILogger<SessionCompactionService> logger) : ISessionCompactionService
{
    private const int recentTailMessageCount = 8;
    private const int fallbackOlderExcerptCount = 6;
    private const int excerptLength = 240;
    private const string summaryHeading = "Session summary";
    private const string durableFactsHeading = "Durable facts";
    private const string openThreadsHeading = "Open threads";

    public async ValueTask<GatewaySession?> CompactAsync(string sessionId, string reason, CancellationToken cancellationToken)
    {
        if (!sessionStore.TryGetSession(sessionId, out GatewaySession? session) || session is null)
        {
            return null;
        }

        string compactSummary = await BuildCompactSummaryAsync(session, cancellationToken).ConfigureAwait(false);

        if (string.Equals(session.TargetType, "agent", StringComparison.OrdinalIgnoreCase))
        {
            await mafSessionStore.DeleteAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }

        return sessionStore.CompactSession(sessionId, reason, compactSummary);
    }

    private async ValueTask<string> BuildCompactSummaryAsync(GatewaySession session, CancellationToken cancellationToken)
    {
        List<GatewayMessage> transcriptMessages = session.Messages
            .Where(static message => !message.Internal && !string.IsNullOrWhiteSpace(message.Content))
            .OrderBy(static message => message.Sequence)
            .ThenBy(message => message.Timestamp, StringComparer.Ordinal)
            .ToList();

        List<GatewayMessage> recentTail = transcriptMessages.TakeLast(recentTailMessageCount).ToList();
        List<GatewayMessage> olderMessages = transcriptMessages.Take(Math.Max(0, transcriptMessages.Count - recentTail.Count)).ToList();

        string? generatedSummary = await TryGenerateModelSummaryAsync(session, olderMessages, recentTail, cancellationToken).ConfigureAwait(false);
        string summaryBody = string.IsNullOrWhiteSpace(generatedSummary)
            ? BuildFallbackSummary(olderMessages)
            : generatedSummary.Trim();

        var builder = new StringBuilder();
        builder.AppendLine("Conversation history was compacted before this run.");

        if (!string.IsNullOrWhiteSpace(summaryBody))
        {
            builder.AppendLine();
            builder.AppendLine("Earlier conversation summary:");
            builder.AppendLine(summaryBody.Trim());
        }

        if (recentTail.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Most recent transcript excerpts:");
            foreach (GatewayMessage message in recentTail)
            {
                builder.Append("- ");
                builder.Append(GetRoleLabel(message.Role));
                builder.Append(": ");
                builder.AppendLine(ToExcerpt(message.Content));
            }
        }

        return builder.ToString().Trim();
    }

    private async ValueTask<string?> TryGenerateModelSummaryAsync(
        GatewaySession session,
        List<GatewayMessage> olderMessages,
        List<GatewayMessage> recentTail,
        CancellationToken cancellationToken)
    {
        if (olderMessages.Count == 0)
        {
            return null;
        }

        PlatformConfiguration config = configProvider.GetCurrent();
        string? compactionModelId = config.Memory.SessionCompactionModelId;
        if (string.IsNullOrWhiteSpace(compactionModelId))
        {
            return null;
        }

        ModelDefinition? model = config.Models.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, compactionModelId, StringComparison.OrdinalIgnoreCase));
        if (model is null)
        {
            SessionCompactionServiceLog.CompactionModelMissing(logger, compactionModelId);
            return null;
        }

        ProviderDefinition? provider = config.Providers.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, model.ProviderId, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            SessionCompactionServiceLog.CompactionProviderMissing(logger, model.ProviderId, compactionModelId);
            return null;
        }

        IChatClient? client = await aiClientFactory.TryCreateAsync(provider, model.ModelName, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            return null;
        }

        string prompt = BuildSummaryPrompt(session, olderMessages, recentTail);

        try
        {
            ChatResponse response = await client.GetResponseAsync(
                prompt,
                new ChatOptions
                {
                    MaxOutputTokens = 512,
                    ResponseFormat = ChatResponseFormat.Text,
                },
                cancellationToken).ConfigureAwait(false);

            return response.Text?.Trim();
        }
        catch (HttpRequestException ex)
        {
            SessionCompactionServiceLog.CompactionSummaryFailed(logger, ex, session.Id);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            SessionCompactionServiceLog.CompactionSummaryFailed(logger, ex, session.Id);
            return null;
        }
        catch (IOException ex)
        {
            SessionCompactionServiceLog.CompactionSummaryFailed(logger, ex, session.Id);
            return null;
        }
    }

    private static string BuildSummaryPrompt(
        GatewaySession session,
        List<GatewayMessage> olderMessages,
        List<GatewayMessage> recentTail)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are compacting a chat transcript for future continuation.");
        builder.AppendLine();
        builder.AppendLine("Summarize only the \"Earlier messages to summarize\" block. The \"Recent messages already preserved separately\" block is already preserved in full and must not be summarized.");
        builder.AppendLine();
        builder.AppendLine("Keep:");
        builder.AppendLine("- Concrete facts, decisions, and constraints");
        builder.AppendLine("- User preferences and explicit instructions");
        builder.AppendLine("- File paths, symbols, commands, and tool outputs");
        builder.AppendLine("- Unresolved work, open questions, and pending tasks that matter later");
        builder.AppendLine();
        builder.AppendLine("Omit filler, repetition, and speculative details. Do not invent facts. Do not restate the transcript verbatim unless exact wording is needed for correctness.");
        builder.AppendLine();
        builder.AppendLine("Prefer preserving concrete outcomes and commitments over conversational phrasing. If assistant messages contain useful conclusions, decisions, or confirmed results, retain those briefly without reproducing step-by-step reasoning.");
        builder.AppendLine();
        builder.AppendLine("If the earlier messages contain no durable facts or open threads, state that explicitly under the relevant headings.");
        builder.AppendLine();
        builder.AppendLine("Return plain text using exactly these headings:");
        builder.AppendLine(summaryHeading);
        builder.AppendLine(durableFactsHeading);
        builder.AppendLine(openThreadsHeading);
        builder.AppendLine();
        builder.Append("Session target: ");
        builder.AppendLine(session.TargetId);
        builder.AppendLine();
        builder.AppendLine("Earlier messages to summarize:");
        foreach (GatewayMessage message in olderMessages)
        {
            builder.Append('[');
            builder.Append(GetRoleLabel(message.Role));
            builder.Append("] ");
            builder.AppendLine(message.Content.Trim());
        }

        if (recentTail.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Recent messages already preserved separately:");
            foreach (GatewayMessage message in recentTail)
            {
                builder.Append('[');
                builder.Append(GetRoleLabel(message.Role));
                builder.Append("] ");
                builder.AppendLine(message.Content.Trim());
            }
        }

        return builder.ToString();
    }

    private static string BuildFallbackSummary(List<GatewayMessage> olderMessages)
    {
        if (olderMessages.Count == 0)
        {
            return $"{summaryHeading}\nNo earlier transcript details were retained beyond the recent preserved tail.\n\n{durableFactsHeading}\n- None extracted without a compaction model.\n\n{openThreadsHeading}\n- Review the recent preserved transcript excerpts for the latest active thread.";
        }

        var builder = new StringBuilder();
        builder.AppendLine(summaryHeading);
        builder.AppendLine("Deterministic fallback summary generated without a compaction model.");
        builder.AppendLine();
        builder.AppendLine(durableFactsHeading);
        foreach (GatewayMessage message in olderMessages.TakeLast(fallbackOlderExcerptCount))
        {
            builder.Append("- ");
            builder.Append(GetRoleLabel(message.Role));
            builder.Append(": ");
            builder.AppendLine(ToExcerpt(message.Content));
        }

        builder.AppendLine();
        builder.AppendLine(openThreadsHeading);
        builder.AppendLine("- Continue from the recent preserved transcript excerpts and verify any unresolved assumptions against the raw session history if needed.");

        return builder.ToString().TrimEnd();
    }

    private static string GetRoleLabel(string role)
        => string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)
            ? "Assistant"
            : string.Equals(role, "system", StringComparison.OrdinalIgnoreCase)
                ? "System"
                : "User";

    private static string ToExcerpt(string content)
    {
        string normalized = content.Replace("\n", " ", StringComparison.Ordinal).Trim();
        if (normalized.Length <= excerptLength)
        {
            return normalized;
        }

        return string.Concat(normalized.AsSpan(0, excerptLength), "...");
    }
}

internal static partial class SessionCompactionServiceLog
{
    [LoggerMessage(EventId = 7011, Level = LogLevel.Warning, Message = "Session compaction model '{ModelId}' was configured but not found.")]
    public static partial void CompactionModelMissing(ILogger logger, string modelId);

    [LoggerMessage(EventId = 7012, Level = LogLevel.Warning, Message = "Session compaction provider '{ProviderId}' for model '{ModelId}' was not found.")]
    public static partial void CompactionProviderMissing(ILogger logger, string providerId, string modelId);

    [LoggerMessage(EventId = 7013, Level = LogLevel.Warning, Message = "Session compaction summary generation failed for session {SessionId}; using fallback summary.")]
    public static partial void CompactionSummaryFailed(ILogger logger, Exception exception, string sessionId);
}
