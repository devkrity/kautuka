using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Domain.Execution;

internal static class ChatExecutionHelpers
{
    private static readonly string[] openingThoughtTags = ["<thought>", "<thinking>", "<think>"];
    private static readonly string[] closingThoughtTags = ["</thought>", "</thinking>", "</think>"];

    internal static void RecordToolCallStart(
        List<GatewayToolCall> toolCalls,
        string toolCallId,
        string toolName,
        string? agentId,
        string? argumentsJson)
        => ToolCallOutcomeReducer.RecordStart(toolCalls, toolCallId, toolName, agentId, argumentsJson);

    internal static bool RecordToolCallResult(
        List<GatewayToolCall> toolCalls,
        string toolCallId,
        string? resultJson)
        => ToolCallOutcomeReducer.RecordResult(toolCalls, toolCallId, resultJson);

    internal static bool RecordToolCallFailure(
        List<GatewayToolCall> toolCalls,
        string toolCallId,
        string failureJson)
        => ToolCallOutcomeReducer.RecordFailure(toolCalls, toolCallId, failureJson);

    internal static bool TryGetFunctionFailure(FunctionResultContent functionResult, out Exception? exception)
        => ToolCallOutcomeReducer.TryGetFailure(functionResult, out exception);

    private static bool IsTerminalToolCallStatus(string? status)
        => ToolCallOutcomeReducer.IsTerminal(status);

    internal static bool HasUnrecoveredToolFailure(IEnumerable<GatewayToolCall> toolCalls)
        => ToolCallOutcomeReducer.HasUnrecoveredFailure(toolCalls);

    internal static bool CanEmitAssistantOutput(IEnumerable<GatewayToolCall> toolCalls)
        => !ToolCallOutcomeReducer.HasUnrecoveredFailure(toolCalls);

    internal static IReadOnlyList<GatewayToolCall> ExtractToolCalls(IEnumerable<ChatMessage> messages, string? agentId = null)
    {
        var toolCalls = new List<GatewayToolCall>();

        foreach (AIContent content in messages.SelectMany(static message => message.Contents))
        {
            if (content is FunctionCallContent functionCall)
            {
                string callId = functionCall.CallId ?? Guid.NewGuid().ToString("N");
                string argumentsJson = functionCall.Arguments is not null
                    ? JsonSerializer.Serialize(functionCall.Arguments, KautukaJsonContext.Default.Object)
                    : "{}";
                RecordToolCallStart(toolCalls, callId, functionCall.Name, agentId, argumentsJson);
            }
            else if (content is FunctionResultContent functionResult)
            {
                string callId = functionResult.CallId ?? Guid.NewGuid().ToString("N");
                if (TryGetFunctionFailure(functionResult, out _))
                {
                    GatewayChatToolFailure failure = GatewayChatToolFailure.ExecutionFailed(callId, toolName: null);
                    RecordToolCallFailure(
                        toolCalls,
                        callId,
                        JsonSerializer.Serialize(failure, KautukaJsonContext.Default.GatewayChatToolFailure));
                }
                else
                {
                    string? resultJson = ChatToolResultProcessor.SerializeToolResult(functionResult.Result);
                    RecordToolCallResult(toolCalls, callId, resultJson);
                }
            }
        }

        return toolCalls;
    }

    internal static IReadOnlyList<GatewayExecutedAction> BuildExecutedActionsFromToolCalls(
        IEnumerable<GatewayToolCall> toolCalls)
    {
        List<MutableExecutedAction> actionTimeline = ReduceToolCallsToActionTimeline(toolCalls);
        var executedActions = new List<GatewayExecutedAction>();

        foreach (MutableExecutedAction action in actionTimeline)
        {
            executedActions.Add(new GatewayExecutedAction(
                Id: action.Id,
                Kind: action.Kind,
                Name: action.Name,
                Status: action.Status,
                ToolCallIds: action.ToolCallIds,
                ArgumentsJson: action.ArgumentsJson,
                ResultJson: action.ResultJson,
                StartedAt: action.StartedAt,
                CompletedAt: action.CompletedAt));
        }

        return executedActions;
    }

    internal static IReadOnlyList<GatewayActivityEntry> BuildActivityEntries(
        IReadOnlyList<GatewayExecutedAction>? executedActions,
        IReadOnlyList<GatewayToolApprovalRequest>? approvals = null,
        bool includeUnmatchedApprovals = true)
    {
        var entries = new List<GatewayActivityEntry>();
        if ((executedActions?.Count ?? 0) == 0 && (approvals?.Count ?? 0) == 0)
        {
            return entries;
        }

        var approvalsByToolCallId = new Dictionary<string, List<GatewayToolApprovalRequest>>(StringComparer.Ordinal);
        var unmatchedApprovals = new List<GatewayToolApprovalRequest>();
        var consumedApprovalIds = new HashSet<string>(StringComparer.Ordinal);
        var knownToolCallIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (GatewayExecutedAction action in executedActions ?? [])
        {
            foreach (string toolCallId in action.ToolCallIds ?? [])
            {
                if (!string.IsNullOrWhiteSpace(toolCallId))
                {
                    knownToolCallIds.Add(toolCallId);
                }
            }
        }

        foreach (GatewayToolApprovalRequest approval in approvals ?? [])
        {
            if (!string.IsNullOrWhiteSpace(approval.ToolCallId) && knownToolCallIds.Contains(approval.ToolCallId))
            {
                if (!approvalsByToolCallId.TryGetValue(approval.ToolCallId, out List<GatewayToolApprovalRequest>? toolApprovals))
                {
                    toolApprovals = [];
                    approvalsByToolCallId[approval.ToolCallId] = toolApprovals;
                }

                toolApprovals.Add(approval);
                continue;
            }

            unmatchedApprovals.Add(approval);
        }

        int sequence = 0;
        foreach (GatewayExecutedAction action in executedActions ?? [])
        {
            foreach (string toolCallId in action.ToolCallIds ?? [])
            {
                if (!approvalsByToolCallId.TryGetValue(toolCallId, out List<GatewayToolApprovalRequest>? matchedApprovals))
                {
                    continue;
                }

                foreach (GatewayToolApprovalRequest approval in matchedApprovals.OrderBy(static approval => approval.RequestedAt, StringComparer.Ordinal))
                {
                    if (!consumedApprovalIds.Add(approval.Id))
                    {
                        continue;
                    }

                    sequence += 10;
                    AddApprovalEntry(entries, approval, sequence);
                }
            }

            sequence += 10;
            entries.Add(new GatewayActivityEntry(
                Id: $"activity-{action.Id}",
                Sequence: sequence,
                Kind: "action",
                Status: action.Status,
                ActionKind: action.Kind,
                Name: action.Name,
                ToolCallIds: action.ToolCallIds,
                ApprovalRequestId: action.ApprovalRequestId,
                ArgumentsJson: action.ArgumentsJson,
                ResultJson: action.ResultJson,
                StartedAt: action.StartedAt,
                CompletedAt: action.CompletedAt));
        }

        if (includeUnmatchedApprovals)
        {
            foreach (GatewayToolApprovalRequest approval in unmatchedApprovals
                .Where(approval => !consumedApprovalIds.Contains(approval.Id))
                .OrderBy(static approval => approval.RequestedAt, StringComparer.Ordinal))
            {
                sequence += 10;
                AddApprovalEntry(entries, approval, sequence);
            }
        }

        return entries;
    }

    internal static GatewayChatResult SummarizePersistedChatResult(GatewayChatResult result)
    {
        if (result.ActivityEntries is not { Count: > 0 } activityEntries)
        {
            return result;
        }

        IReadOnlyList<GatewayActivityEntry> summarizedEntries = SummarizeActivityEntriesForPersistence(activityEntries);
        return summarizedEntries == activityEntries
            ? result
            : result with { ActivityEntries = summarizedEntries };
    }

    internal static IReadOnlyList<GatewayActivityEntry> SummarizeActivityEntriesForPersistence(
        IReadOnlyList<GatewayActivityEntry> entries)
    {
        var changed = false;
        var summarized = new GatewayActivityEntry[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            GatewayActivityEntry entry = entries[i];
            GatewayActivityEntry next = SummarizeActivityEntryForPersistence(entry);
            summarized[i] = next;
            changed |= !ReferenceEquals(entry, next);
        }

        return changed ? summarized : entries;
    }

    private static GatewayActivityEntry SummarizeActivityEntryForPersistence(GatewayActivityEntry entry)
    {
        if (!ShouldSummarizeSkillLoadResult(entry.Name, entry.ResultJson ?? string.Empty))
        {
            return entry;
        }

        return entry with
        {
            ResultJson = SummarizeToolResultForWire(entry.Name, entry.ResultJson, entry.ArgumentsJson),
        };
    }

    internal static string SummarizeToolResultForWire(string? toolName, string? resultJson, string? argumentsJson = null)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
        {
            return string.Empty;
        }

        if (!ShouldSummarizeSkillLoadResult(toolName, resultJson))
        {
            return resultJson;
        }

        string? skillId = TryReadSkillId(argumentsJson) ?? TryReadSkillId(resultJson);
        return skillId is null
            ? """{"status":"loaded"}"""
            : $$"""{"skill_id":"{{skillId}}","status":"loaded"}""";
    }

    private static bool ShouldSummarizeSkillLoadResult(string? toolName, string resultJson)
    {
        if (resultJson.Length <= 512)
        {
            return false;
        }

        if (string.Equals(toolName, "load_skill", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return LooksLikeSkillLoadPayload(resultJson);
    }

    private static bool LooksLikeSkillLoadPayload(string resultJson)
        => resultJson.Contains("instructions_markdown", StringComparison.Ordinal)
            || (resultJson.Contains("\"skill_id\"", StringComparison.Ordinal)
                && resultJson.Contains("\"package_hash\"", StringComparison.Ordinal));

    private static string? TryReadSkillId(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("skillId", out JsonElement skillIdElement)
                && skillIdElement.GetString() is { Length: > 0 } skillId)
            {
                return skillId;
            }

            if (root.TryGetProperty("skill_id", out JsonElement skillIdSnakeElement)
                && skillIdSnakeElement.GetString() is { Length: > 0 } skillIdSnake)
            {
                return skillIdSnake;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    internal static IReadOnlyList<GatewayActivityEntry> MergeActivityEntries(
        params IReadOnlyList<GatewayActivityEntry>?[] sources)
    {
        var byId = new Dictionary<string, GatewayActivityEntry>(StringComparer.Ordinal);
        var orderedIds = new List<string>();
        foreach (IReadOnlyList<GatewayActivityEntry>? source in sources)
        {
            if (source is not { Count: > 0 })
            {
                continue;
            }

            foreach (GatewayActivityEntry entry in source)
            {
                if (byId.TryGetValue(entry.Id, out GatewayActivityEntry? existing))
                {
                    byId[entry.Id] = ActivityEntryPriority(entry) >= ActivityEntryPriority(existing) ? entry : existing;
                }
                else
                {
                    byId[entry.Id] = entry;
                    orderedIds.Add(entry.Id);
                }
            }
        }

        return byId.Count > 0
            ? OrderActivityEntriesByCanonicalSequence([.. orderedIds.Select(id => byId[id])])
            : [];
    }

    private static int ActivityEntryPriority(GatewayActivityEntry entry)
        => entry.Status switch
        {
            "completed" or "approved" or "done" => 3,
            "failed" => 2,
            "started" or "running" or "in_progress" => 1,
            _ => 0,
        };

    private static IReadOnlyList<GatewayActivityEntry> OrderActivityEntriesByCanonicalSequence(
        IReadOnlyList<GatewayActivityEntry> entries)
        => [.. entries
            .Select(static (entry, index) => new
            {
                Entry = entry,
                Index = index,
                Sequence = ResolveActivitySequence(entry),
            })
            .OrderBy(static item => item.Sequence)
            .ThenBy(static item => item.Index)
            .Select(static item => item.Entry)];

    private static int ResolveActivitySequence(GatewayActivityEntry entry)
    {
        if (entry.Sequence > 0)
        {
            return entry.Sequence;
        }

        return int.MaxValue;
    }

    private static void AddApprovalEntry(List<GatewayActivityEntry> entries, GatewayToolApprovalRequest approval, int sequence)
    {
        string status = approval.Decision switch
        {
            "approved" or "auto_approved" => "approved",
            "denied" => "denied",
            "expired" => "expired",
            _ => "pending",
        };
        entries.Add(new GatewayActivityEntry(
            Id: $"activity-approval-{approval.Id}",
            Sequence: sequence,
            Kind: "approval",
            Status: status,
            ActionKind: approval.ToolType,
            Name: approval.ToolName,
            ToolCallId: approval.ToolCallId,
            ApprovalRequestId: approval.Id,
            ArgumentsJson: approval.ArgumentsJson,
            RequestedAt: approval.RequestedAt,
            DecidedAt: approval.DecidedAt,
            ResolvedVia: approval.ResolutionSource,
            CheckpointId: approval.CheckpointId));

        if (!string.IsNullOrWhiteSpace(approval.CheckpointId))
        {
            entries.Add(new GatewayActivityEntry(
                Id: $"activity-checkpoint-{approval.CheckpointId}",
                Sequence: sequence + 1,
                Kind: "checkpoint",
                Status: "completed",
                Name: $"Checkpoint for {approval.ToolName}",
                ToolCallId: approval.ToolCallId,
                ApprovalRequestId: approval.Id,
                RequestedAt: approval.RequestedAt,
                DecidedAt: approval.DecidedAt,
                CheckpointId: approval.CheckpointId,
                CheckpointResumable: true));
        }
    }

    private static List<MutableExecutedAction> ReduceToolCallsToActionTimeline(IEnumerable<GatewayToolCall> toolCalls)
    {
        var actionTimeline = new List<MutableExecutedAction>();
        foreach (GatewayToolCall toolCall in toolCalls.Where(static toolCall => !string.IsNullOrWhiteSpace(toolCall.ToolCallId) && !string.IsNullOrWhiteSpace(toolCall.ToolName)))
        {
            MutableExecutedAction? action = FindExistingActionForToolCall(actionTimeline, toolCall);
            if (action is null)
            {
                action = new MutableExecutedAction($"action-{toolCall.ToolCallId}", toolCall.ToolName, toolCall.ActionKind);
                actionTimeline.Add(action);
            }

            action.Apply(toolCall);
        }

        return actionTimeline;
    }

    private static MutableExecutedAction? FindExistingActionForToolCall(
        List<MutableExecutedAction> actionTimeline,
        GatewayToolCall toolCall)
    {
        MutableExecutedAction? exactToolCallMatch = actionTimeline.FirstOrDefault(action => action.ToolCallIds.Contains(toolCall.ToolCallId, StringComparer.Ordinal));
        if (exactToolCallMatch is not null)
        {
            return exactToolCallMatch;
        }

        if (!IsTerminalToolCallStatus(toolCall.Status))
        {
            return null;
        }

        return actionTimeline.FirstOrDefault(action =>
            !string.Equals(action.Status, "completed", StringComparison.OrdinalIgnoreCase)
            && string.Equals(action.Name, toolCall.ToolName, StringComparison.OrdinalIgnoreCase)
            && ActionKindsCompatible(action.Kind, toolCall.ActionKind));
    }

    private static bool ActionKindsCompatible(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
            || ActionCategoryCompatible("tool", left) && ActionCategoryCompatible("tool", right);

    private static bool ActionCategoryCompatible(string expectedCategory, string? actualActionKind)
    {
        if (string.IsNullOrWhiteSpace(expectedCategory))
        {
            return false;
        }

        if (string.Equals(expectedCategory, actualActionKind, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(expectedCategory, "tool", StringComparison.OrdinalIgnoreCase)
            && actualActionKind is "connector" or "agent" or "skill" or "superexec";
    }

    private sealed class MutableExecutedAction(string id, string name, string kind)
    {
        private readonly List<string> toolCallIds = [];

        public string Id { get; } = id;

        public string Name { get; } = name;

        public string Kind { get; private set; } = kind;

        public string Status { get; private set; } = "started";

        public List<string> ToolCallIds => toolCallIds;

        public string? ArgumentsJson { get; private set; }

        public string? ResultJson { get; private set; }

        public string? StartedAt { get; private set; }

        public string? CompletedAt { get; private set; }

        public void Apply(GatewayToolCall toolCall)
        {
            if (!toolCallIds.Contains(toolCall.ToolCallId, StringComparer.Ordinal))
            {
                toolCallIds.Add(toolCall.ToolCallId);
            }

            if (!string.IsNullOrWhiteSpace(toolCall.ActionKind))
            {
                Kind = toolCall.ActionKind;
            }

            if (!string.IsNullOrWhiteSpace(toolCall.ArgumentsJson))
            {
                ArgumentsJson = toolCall.ArgumentsJson;
            }

            if (!string.IsNullOrWhiteSpace(toolCall.ResultJson))
            {
                ResultJson = toolCall.ResultJson;
            }

            StartedAt = EarliestTimestamp(StartedAt, toolCall.StartedAt);
            CompletedAt = LatestTimestamp(CompletedAt, toolCall.CompletedAt);
            Status = toolCall.Status switch
            {
                "done" => "completed",
                "failed" => "failed",
                _ when Status is "completed" or "failed" => Status,
                _ => "started",
            };
        }
    }

    private static string? EarliestTimestamp(string? left, string? right)
        => CompareTimestamps(left, right) <= 0 ? left ?? right : right ?? left;

    private static string? LatestTimestamp(string? left, string? right)
        => CompareTimestamps(left, right) >= 0 ? left ?? right : right ?? left;

    private static int CompareTimestamps(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left))
        {
            return string.IsNullOrWhiteSpace(right) ? 0 : 1;
        }

        if (string.IsNullOrWhiteSpace(right))
        {
            return -1;
        }

        bool leftParsed = DateTimeOffset.TryParse(left, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset leftTime);
        bool rightParsed = DateTimeOffset.TryParse(right, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset rightTime);
        if (leftParsed && rightParsed)
        {
            return leftTime.CompareTo(rightTime);
        }

        return string.CompareOrdinal(left, right);
    }

    internal static string ExtractNativeThinking(IEnumerable<ChatMessage> messages)
        => string.Join(
            '\n',
            messages
                .SelectMany(static message => message.Contents)
                .OfType<TextReasoningContent>()
                .Select(static reasoning => reasoning.Text)
                .Where(static text => !string.IsNullOrWhiteSpace(text))!);

    internal static (string ResponseText, string ThinkingText) ExtractTaggedThinking(string text)
    {
        if (string.IsNullOrEmpty(text)) return (text, string.Empty);
        var thinking = new StringBuilder();
        string result = Regex.Replace(
            text,
            @"<(think|thinking|thought)>(.*?)<\/\1>",
            m =>
            {
                if (thinking.Length > 0) thinking.Append('\n');
                thinking.Append(m.Groups[2].Value.Trim());
                return string.Empty;
            },
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        // Strip any orphan closing tags that lacked a matching opener.
        result = Regex.Replace(result, @"<\/(think|thinking|thought)>", string.Empty, RegexOptions.IgnoreCase);
        return (result.Trim(), thinking.ToString());
    }

    internal static (string TextOut, string ThinkOut, string Pending, bool InThought)
        RouteTaggedChunk(string pending, string chunk, bool inThought)
    {
        string text = pending + chunk;
        var textOut = new StringBuilder();
        var thinkOut = new StringBuilder();

        while (text.Length > 0)
        {
            string[] searchTags = inThought ? closingThoughtTags : openingThoughtTags;
            int tagStart = FindTag(text, searchTags, out int tagLength);
            if (tagStart < 0)
            {
                return FlushRemainingChunk(text, inThought, searchTags, textOut, thinkOut);
            }

            AppendChunk(inThought, textOut, thinkOut, text[..tagStart]);
            text = text[(tagStart + tagLength)..];
            inThought = !inThought;
        }

        return (textOut.ToString(), thinkOut.ToString(), string.Empty, inThought);
    }

    internal static string BuildSkillAdvertisingContext(IReadOnlyList<EffectiveSkillManifestEntry>? manifest)
    {
        if (manifest is null || manifest.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine("Skills (NOT directly callable — use load_skill to access them):");

        foreach (EffectiveSkillManifestEntry entry in manifest.OrderBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append("- ");
            builder.Append(string.IsNullOrWhiteSpace(entry.Name) ? entry.SkillId : entry.Name);
            builder.Append(" (skillId: \"");
            builder.Append(entry.SkillId);
            builder.Append("\"):");

            if (!string.IsNullOrWhiteSpace(entry.Description))
            {
                builder.Append(' ');
                builder.Append(entry.Description.Trim());
            }

            builder.AppendLine();
        }

        builder.AppendLine("IMPORTANT: Skill names are NOT function names. Do not call them directly as tools.");
        builder.Append("To use a skill: call load_skill(skillId) first to get its instructions, ");
        builder.Append("then follow those instructions — which may involve calling run_skill_script. ");
        builder.Append("Use read_skill_resource only when you need supplementary files from a skill.");
        return builder.ToString();
    }

    internal static string BuildScopeContractContext(string personaCategory, string agentName)
    {
        string normalizedCategory = string.IsNullOrWhiteSpace(personaCategory) ? "general" : personaCategory.Trim();
        string normalizedAgentName = string.IsNullOrWhiteSpace(agentName) ? "this agent" : agentName.Trim();

        return $"Scope contract for {normalizedAgentName}: Operate only within the responsibilities implied by persona category {normalizedCategory}, the system instructions, the loaded skills, and the currently available tools. If the user asks for work outside that scope, unsupported capabilities, or an action you cannot actually perform or verify, say that directly and decline instead of improvising or pretending completion.";
    }

    internal static EffectiveRuntimeGrounding BuildRuntimeGrounding(string locale, string timeZone)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        CultureInfo culture = ResolveCulture(locale);
        TimeZoneInfo zone = ResolveTimeZone(timeZone);
        DateTimeOffset localNow = TimeZoneInfo.ConvertTime(nowUtc, zone);

        return new EffectiveRuntimeGrounding(
            nowUtc.ToString("O", CultureInfo.InvariantCulture),
            localNow.ToString("F", culture),
            culture.Name,
            zone.Id);
    }

    internal static string SerializeModelVisibleContext(EffectiveModelVisibleContext context)
    {
        JsonObject payload = new()
        {
            ["persona"] = new JsonObject
            {
                ["style"] = context.Persona.Style,
                ["tone"] = context.Persona.Tone,
                ["verbosity"] = context.Persona.Verbosity,
            },
            ["runtimeGrounding"] = new JsonObject
            {
                ["currentUtc"] = context.RuntimeGrounding.CurrentUtc,
                ["currentLocal"] = context.RuntimeGrounding.CurrentLocal,
                ["locale"] = context.RuntimeGrounding.Locale,
                ["timeZone"] = context.RuntimeGrounding.TimeZone,
            },
        };

        return payload.ToJsonString();
    }

    internal static string CombineSystemContext(
        string? systemPrompt,
        string memoryContext,
        string? modelVisibleContext = null,
        string? skillAdvertisingContext = null,
        string? scopeContractContext = null)
    {
        var parts = new List<string>(capacity: 6);
        if (!string.IsNullOrWhiteSpace(systemPrompt)) parts.Add(systemPrompt!);
        if (!string.IsNullOrWhiteSpace(skillAdvertisingContext)) parts.Add(skillAdvertisingContext!);
        if (!string.IsNullOrWhiteSpace(modelVisibleContext)) parts.Add(modelVisibleContext!);
        if (!string.IsNullOrWhiteSpace(scopeContractContext)) parts.Add(scopeContractContext!);
        if (!string.IsNullOrWhiteSpace(memoryContext)) parts.Add(memoryContext);
        return parts.Count == 0 ? string.Empty : string.Join("\n\n", parts);
    }

    private static CultureInfo ResolveCulture(string locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
        {
            return CultureInfo.GetCultureInfo("en-US");
        }

        try
        {
            return CultureInfo.GetCultureInfo(locale.Trim());
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.GetCultureInfo("en-US");
        }
    }

    private static TimeZoneInfo ResolveTimeZone(string timeZone)
    {
        if (string.IsNullOrWhiteSpace(timeZone))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZone.Trim());
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    private static (string TextOut, string ThinkOut, string Pending, bool InThought) FlushRemainingChunk(
        string text,
        bool inThought,
        string[] searchTags,
        StringBuilder textOut,
        StringBuilder thinkOut)
    {
        int partialLength = FindTrailingPartial(text, searchTags);
        string safeText = partialLength > 0 ? text[..^partialLength] : text;
        AppendChunk(inThought, textOut, thinkOut, safeText);
        return (
            textOut.ToString(),
            thinkOut.ToString(),
            partialLength > 0 ? text[^partialLength..] : string.Empty,
            inThought);
    }

    private static void AppendChunk(bool inThought, StringBuilder textOut, StringBuilder thinkOut, string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return;
        }

        if (inThought)
        {
            thinkOut.Append(content);
            return;
        }

        textOut.Append(content);
    }

    private static int FindTag(string text, string[] tags, out int tagLength)
    {
        int bestIndex = -1;
        tagLength = 0;

        foreach (string tag in tags)
        {
            int index = text.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && (bestIndex < 0 || index < bestIndex))
            {
                bestIndex = index;
                tagLength = tag.Length;
            }
        }

        return bestIndex;
    }

    private static int FindTrailingPartial(string text, string[] tags)
    {
        int maxLength = 0;
        foreach (string tag in tags)
        {
            int candidateLength = Math.Min(text.Length, tag.Length - 1);
            for (int length = candidateLength; length > 0; length--)
            {
                if (!text.EndsWith(tag[..length], StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                maxLength = Math.Max(maxLength, length);
                break;
            }
        }

        return maxLength;
    }

}
