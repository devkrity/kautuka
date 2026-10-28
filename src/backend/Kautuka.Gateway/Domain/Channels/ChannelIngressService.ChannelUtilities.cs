using System.Text;
using System.Text.Json;

namespace Kautuka.Gateway.Domain.Channels;

internal sealed partial class ChannelIngressService
{
    private bool IsDuplicateInboundUpdate(string registrationId, string? updateId)
    {
        if (string.IsNullOrWhiteSpace(updateId))
        {
            return false;
        }

        CleanupProcessedInboundUpdates();

        string key = $"{registrationId}:{updateId}";
        return !processedInboundUpdates.TryAdd(key, DateTimeOffset.UtcNow);
    }

    private string BuildChannelApprovalPrompt(
        string toolName,
        string? argumentsJson,
        string? outerAgentId = null,
        string? outerSessionId = null,
        string? userMessage = null,
        bool fullDetails = false)
    {
        const int maxChannelMessageChars = 3900;
        const int maxFormattedChars = 2200;

        string ask = string.IsNullOrWhiteSpace(toolName)
            ? "Allow the agent to run this tool?"
            : $"Allow the agent to run `{toolName}`?";

        var sections = new List<string>();
        if (!string.IsNullOrWhiteSpace(userMessage))
        {
            sections.Add(userMessage.Trim());
        }

        sections.Add(ask);

        if (!string.IsNullOrWhiteSpace(outerAgentId) || !string.IsNullOrWhiteSpace(outerSessionId))
        {
            string outerAgentLabel = ResolveOuterAgentLabel(outerAgentId);
            string? outerSessionLabel = ResolveOuterSessionLabel(outerSessionId);
            sections.Add(string.IsNullOrWhiteSpace(outerSessionLabel)
                ? $"Part of {outerAgentLabel} execution."
                : $"Part of {outerAgentLabel} execution in chat \"{outerSessionLabel}\".");
        }

        if (!string.IsNullOrWhiteSpace(argumentsJson) && !string.Equals(argumentsJson, "{}", StringComparison.Ordinal))
        {
            string formatted = fullDetails ? argumentsJson : FormatArgsForDisplay(argumentsJson);
            if (!fullDetails && formatted.Length > maxFormattedChars)
            {
                formatted = $"{formatted[..maxFormattedChars]}\n... (truncated)";
            }

            sections.Add(formatted);
        }

        string prompt = string.Join("\n\n", sections);
        return fullDetails || prompt.Length <= maxChannelMessageChars
            ? prompt
            : $"{prompt[..(maxChannelMessageChars - 20)]}\n... (truncated)";
    }

    private string ResolveOuterAgentLabel(string? outerAgentId)
    {
        if (string.IsNullOrWhiteSpace(outerAgentId))
        {
            return "outer agent";
        }

        return state.Agents.TryGetValue(outerAgentId, out GatewayAgent? agent) && !string.IsNullOrWhiteSpace(agent?.Name)
            ? agent.Name.Trim()
            : "outer agent";
    }

    private string? ResolveOuterSessionLabel(string? outerSessionId)
    {
        if (string.IsNullOrWhiteSpace(outerSessionId)
            || !sessionStore.TryGetSession(outerSessionId, out GatewaySession? session)
            || session is null)
        {
            return null;
        }

        string title = StripGeneratedOriginPrefix(session.Title.Trim());
        if (string.IsNullOrWhiteSpace(title)
            || string.Equals(title, "New Chat", StringComparison.OrdinalIgnoreCase)
            || string.Equals(title, $"{session.TargetType}:{session.TargetId}", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return title;
    }

    private static string StripGeneratedOriginPrefix(string title)
    {
        if (title.Length == 0 || title[0] != '[')
        {
            return title;
        }

        int closingBracketIndex = title.IndexOf("] ", StringComparison.Ordinal);
        if (closingBracketIndex <= 1)
        {
            return title;
        }

        string prefix = title[1..closingBracketIndex];
        return prefix switch
        {
            "Chat" or "Autopilot" or "Telegram" or "Slack" or "Tool" => title[(closingBracketIndex + 2)..].TrimStart(),
            _ => title,
        };
    }

    private static string FormatArgsForDisplay(string argumentsJson)
    {
#pragma warning disable CA1031 // Broad catch intentional — malformed JSON should not crash display formatting
        try
        {
            using JsonDocument doc = JsonDocument.Parse(argumentsJson);
            var sb = new StringBuilder();
            AppendJsonElement(doc.RootElement, sb, 0);
            return sb.ToString().TrimEnd();
        }
        catch
        {
            return argumentsJson;
        }
#pragma warning restore CA1031
    }

    private static void AppendJsonElement(JsonElement element, StringBuilder sb, int depth)
    {
        string indent = depth > 0 ? new string(' ', depth * 2) : string.Empty;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty prop in element.EnumerateObject())
                {
                    if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{indent}{prop.Name}:");
                        AppendJsonElement(prop.Value, sb, depth + 1);
                    }
                    else
                    {
                        string val = prop.Value.ValueKind == JsonValueKind.String
                            ? prop.Value.GetString() ?? string.Empty
                            : prop.Value.GetRawText();
                        sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{indent}{prop.Name}: {val}");
                    }
                }
                break;
            case JsonValueKind.Array:
                int idx = 0;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{indent}[{idx}]:");
                        AppendJsonElement(item, sb, depth + 1);
                    }
                    else
                    {
                        string val = item.ValueKind == JsonValueKind.String
                            ? item.GetString() ?? string.Empty
                            : item.GetRawText();
                        sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{indent}[{idx}]: {val}");
                    }
                    idx++;
                }
                break;
            default:
                string scalar = element.ValueKind == JsonValueKind.String
                    ? element.GetString() ?? string.Empty
                    : element.GetRawText();
                sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{indent}{scalar}");
                break;
        }
    }

    private void CleanupProcessedInboundUpdates()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now - lastInboundCleanupAt < processedInboundCleanupInterval)
        {
            return;
        }

        lastInboundCleanupAt = now;
        DateTimeOffset threshold = now - processedInboundRetention;
        foreach ((string key, DateTimeOffset processedAt) in processedInboundUpdates)
        {
            if (processedAt < threshold)
            {
                processedInboundUpdates.TryRemove(key, out _);
            }
        }
    }

    private void TryAppendInteractionSummaryToSession(GatewayChannelInteraction interaction, string status, string? selectedLabel, string? resolvedByExternalUserId)
    {
        if (string.IsNullOrWhiteSpace(interaction.SessionId)
            || string.IsNullOrWhiteSpace(interaction.TargetType)
            || string.IsNullOrWhiteSpace(interaction.TargetId))
        {
            return;
        }

        if (!sessionStore.TryGetSession(interaction.SessionId, out GatewaySession? existing) || existing is null)
        {
            return;
        }

        string decision = status switch
        {
            "resolved" => $"Decision: {selectedLabel ?? interaction.Resolution ?? "approved"}",
            "expired" => "Decision window expired.",
            "delivery_failed" => "Interaction delivery failed.",
            _ => $"Interaction status: {status}.",
        };

        string resolver = string.IsNullOrWhiteSpace(resolvedByExternalUserId)
            ? string.Empty
            : $" Resolved by external identity {resolvedByExternalUserId}.";
        string options = string.Join(", ", interaction.Options.Select(static option => option.Label));

        var userMessage = new GatewayMessage(
            $"msg-{Guid.NewGuid():N}",
            "user",
            $"[HITL] {interaction.Prompt}",
            Timestamp(),
            false,
            null,
            null,
            null,
            Internal: true);

        var assistantMessage = new GatewayMessage(
            $"msg-{Guid.NewGuid():N}",
            "assistant",
            $"[HITL] {decision} Options: {options}.{resolver}",
            Timestamp(),
            false,
            null,
            null,
            null,
            Internal: true);

        sessionStore.AppendMessages(existing.Id, userMessage, assistantMessage);
    }

    private bool TryResolveInteractionContext(string? sessionId, string ownerUserId, ref string? targetType, ref string? targetId, out string? detail)
    {
        detail = null;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return true;
        }

        if (!sessionStore.TryGetSession(sessionId, out GatewaySession? session)
            || session is null
            || !string.Equals(session.OwnerUserId, ownerUserId, StringComparison.Ordinal))
        {
            detail = "session_id was provided but no matching session exists.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(targetType) && string.IsNullOrWhiteSpace(targetId))
        {
            targetType = session.TargetType;
            targetId = session.TargetId;
            return true;
        }

        bool sameTargetType = string.Equals(targetType, session.TargetType, StringComparison.OrdinalIgnoreCase);
        bool sameTargetId = string.Equals(targetId, session.TargetId, StringComparison.Ordinal);
        if (!sameTargetType || !sameTargetId)
        {
            detail = "session_id does not belong to the supplied target_type/target_id.";
            return false;
        }

        targetType = session.TargetType;
        targetId = session.TargetId;
        return true;
    }

    private static Dictionary<string, object?> BuildInteractionOutput(GatewayChannelInteraction interaction, string? resolution, string? resolvedByExternalUserId)
    {
        return new Dictionary<string, object?>
        {
            ["prompt"] = interaction.Prompt,
            ["registration_id"] = interaction.RegistrationId,
            ["chat_id"] = interaction.ChatId,
            ["message_thread_id"] = interaction.MessageThreadId,
            ["options"] = interaction.Options.Select(static option => option.Value).ToArray(),
            ["resolution"] = resolution,
            ["resolved_by_external_user_id"] = resolvedByExternalUserId,
        };
    }

    private static List<GatewayChannelInteractionOption> ParseOptions(JsonElement payload)
    {
        if (!payload.TryGetProperty("options", out JsonElement optionsElement) || optionsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var options = new List<GatewayChannelInteractionOption>();
        foreach (JsonElement optionElement in optionsElement.EnumerateArray())
        {
            string value = optionElement.GetStringProperty("value", "").Trim();
            string label = optionElement.GetStringProperty("label", value).Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            options.Add(new GatewayChannelInteractionOption(
                $"opt-{Guid.NewGuid():N}",
                string.IsNullOrWhiteSpace(label) ? value : label,
                value.ToUpperInvariant()));
        }

        return options;
    }

    private static string Timestamp() => DateTimeOffset.UtcNow.ToString("O");

}
