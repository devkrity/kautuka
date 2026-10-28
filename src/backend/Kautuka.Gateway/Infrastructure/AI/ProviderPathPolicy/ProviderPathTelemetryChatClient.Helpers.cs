using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Kautuka.Gateway.Domain.Execution;

using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Infrastructure.AI.ProviderPathPolicy;

internal sealed partial class ProviderPathTelemetryChatClient
{
    private async Task<ChatResponse> RunPromptedJsonGetResponseAsync(
        IEnumerable<ChatMessage> originalMessages,
        ChatOptions original,
        CancellationToken ct)
    {
        var toolFunctions = (original.Tools ?? [])
            .OfType<AIFunction>()
            .ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

        var strippedOptions = new ChatOptions
        {
            MaxOutputTokens = original.MaxOutputTokens,
            Temperature = original.Temperature,
        };

        IReadOnlyList<AITool> toolList = (IReadOnlyList<AITool>?)original.Tools ?? [];
        List<ChatMessage> messages = [.. BuildMessagesWithToolSchema(originalMessages, toolList)];

        const int maxTurns = 6;
        for (int turn = 0; turn < maxTurns; turn++)
        {
            usageCaptureAccessor.RecordProviderRequestStarted();
            ChatResponse response = await base.GetResponseAsync(messages, strippedOptions, ct).ConfigureAwait(false);
            if (response.Messages.SelectMany(static message => message.Contents).Any())
            {
                usageCaptureAccessor.RecordFirstModelOutput(DateTimeOffset.UtcNow);
            }
            string text = response.Text?.Trim() ?? string.Empty;

            if (!TryParseJsonToolCall(text, out string? toolName, out JsonElement toolArgs)
                || toolName is null)
            {
                string reasoningText = string.Join("\n", response.Messages
                    .SelectMany(static m => m.Contents)
                    .OfType<TextReasoningContent>()
                    .Where(static r => !string.IsNullOrWhiteSpace(r.Text))
                    .Select(static r => r.Text!));

                if (!TryParseJsonToolCall(reasoningText, out toolName, out toolArgs)
                    || toolName is null)
                {
                    ProviderPathTelemetryChatClientLog.PromptedJsonNoToolCallFound(logger, turn + 1, text.Length, reasoningText.Length, pathStoreKey);

                    if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(reasoningText))
                    {
                        ProviderPathTelemetryChatClientLog.PromptedJsonInjectingSummaryPrompt(logger, turn + 1, pathStoreKey);
                        messages.Add(new ChatMessage(ChatRole.User,
                            "Please provide a plain text summary of what happened and what the current state is."));
                        usageCaptureAccessor.RecordProviderRequestStarted();
                        ChatResponse summaryResponse = await base.GetResponseAsync(messages, strippedOptions, ct).ConfigureAwait(false);
                        if (summaryResponse.Messages.SelectMany(static message => message.Contents).Any())
                        {
                            usageCaptureAccessor.RecordFirstModelOutput(DateTimeOffset.UtcNow);
                        }
                        await store.RecordNativeToolSuccessAsync(pathKey, ct).ConfigureAwait(false);
                        return summaryResponse;
                    }

                    // Non-empty text but no tool call — model produced a final answer; return it.
                    await store.RecordNativeToolSuccessAsync(pathKey, ct).ConfigureAwait(false);
                    return response;
                }
            }

            if (!toolFunctions.TryGetValue(toolName!, out AIFunction? func))
            {
                ProviderPathTelemetryChatClientLog.PromptedJsonUnknownTool(logger, toolName!, pathStoreKey);
                await store.RecordNativeToolSuccessAsync(pathKey, ct).ConfigureAwait(false);
                return response;
            }

            ProviderPathTelemetryChatClientLog.PromptedJsonDispatchingTool(logger, toolName!, turn + 1, pathStoreKey);

            var argsDict = toolArgs.ValueKind == JsonValueKind.Object
                ? toolArgs.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value)
                : [];
            var funcArgs = new AIFunctionArguments(argsDict);

            string toolResult;
            try
            {
                object? raw = await func.InvokeAsync(funcArgs, ct).ConfigureAwait(false);
                toolResult = raw switch
                {
                    null => "(no output)",
                    string s => s,
                    JsonNode node => node.ToJsonString(),
                    JsonElement element => ChatToolResultProcessor.ExtractToolResultText(toolName!, element),
                    _ => ChatToolResultProcessor.SerializeToolResult(raw, toolName!) ?? "(no output)",
                };
            }
            catch (Exception)
            {
                throw;
            }

            messages.Add(new ChatMessage(ChatRole.Assistant, text));
            messages.Add(new ChatMessage(ChatRole.User, $"[Tool result for {toolName}]\n{toolResult}"));
        }

        ProviderPathTelemetryChatClientLog.PromptedJsonExceededMaxTurns(logger, maxTurns, pathStoreKey);
        messages.Add(new ChatMessage(ChatRole.User,
            "Please provide a plain text summary of what happened and what the current state is."));
        usageCaptureAccessor.RecordProviderRequestStarted();
        ChatResponse finalResponse = await base.GetResponseAsync(messages, strippedOptions, ct).ConfigureAwait(false);
        if (finalResponse.Messages.SelectMany(static message => message.Contents).Any())
        {
            usageCaptureAccessor.RecordFirstModelOutput(DateTimeOffset.UtcNow);
        }
        await store.RecordNativeToolSuccessAsync(pathKey, ct).ConfigureAwait(false);
        return finalResponse;
    }

    private static List<ChatMessage> BuildMessagesWithToolSchema(
        IEnumerable<ChatMessage> original,
        IReadOnlyList<AITool>? tools)
    {
        var schema = new StringBuilder();
        schema.AppendLine("You have access to the following tools. When you need to use a tool, respond ONLY with a JSON object in this exact format (no other text):");
        schema.AppendLine("{\"tool\":\"<tool_name>\",\"arguments\":{\"<param_name>\":\"<value>\",...}}");
        schema.AppendLine("Do NOT include any prose before or after the JSON. After receiving the tool result, continue with your task or provide a final answer as plain text.");
        schema.AppendLine("Available tools:");
        foreach (AITool tool in tools ?? [])
        {
            schema.AppendLine(CultureInfo.InvariantCulture, $"- {tool.Name}: {tool.Description}");
            if (tool is AIFunction func)
            {
                AppendParameterSchema(schema, func.JsonSchema);
            }
        }

        var messages = original.ToList();
        int sysIdx = messages.FindIndex(m => m.Role == ChatRole.System);
        if (sysIdx >= 0)
        {
            string existing = messages[sysIdx].Text ?? string.Empty;
            messages[sysIdx] = new ChatMessage(ChatRole.System, $"{existing}\n\n{schema}");
        }
        else
        {
            messages.Insert(0, new ChatMessage(ChatRole.System, schema.ToString()));
        }

        return messages;
    }

    private static void AppendParameterSchema(StringBuilder sb, JsonElement jsonSchema)
    {
        if (jsonSchema.ValueKind != JsonValueKind.Object) return;
        if (!jsonSchema.TryGetProperty("properties", out JsonElement props)
            || props.ValueKind != JsonValueKind.Object) return;

        HashSet<string> required = [];
        if (jsonSchema.TryGetProperty("required", out JsonElement req) && req.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement r in req.EnumerateArray())
            {
                if (r.GetString() is string s) required.Add(s);
            }
        }

        foreach (JsonProperty prop in props.EnumerateObject())
        {
            string typeName = "string";
            string desc = string.Empty;
            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                if (prop.Value.TryGetProperty("type", out JsonElement typeEl))
                    typeName = typeEl.GetString() ?? "string";
                if (prop.Value.TryGetProperty("description", out JsonElement descEl))
                    desc = descEl.GetString() ?? string.Empty;
            }
            string req2 = required.Contains(prop.Name) ? ", required" : ", optional";
            string descPart = string.IsNullOrWhiteSpace(desc) ? string.Empty : $": {desc}";
            sb.AppendLine(CultureInfo.InvariantCulture, $"    - {prop.Name} ({typeName}{req2}){descPart}");
        }
    }

    private static bool TryParseJsonToolCall(string text, out string? toolName, out JsonElement arguments)
    {
        toolName = null;
        arguments = default;

        if (string.IsNullOrWhiteSpace(text)) return false;

        string json = text;
        Match fence = Regex.Match(text, @"```(?:json)?\s*([\s\S]*?)\s*```", RegexOptions.IgnoreCase);
        if (fence.Success)
            json = fence.Groups[1].Value.Trim();

        json = Regex.Replace(json,
            @"<[|/]*tool_call[|/]*>",
            string.Empty,
            RegexOptions.IgnoreCase).Trim();

        if (json.Length == 0 || json[0] != '{')
        {
            Match jsonBlock = Regex.Match(json, @"(\{[\s\S]*\})", RegexOptions.RightToLeft);
            if (!jsonBlock.Success) return false;
            json = jsonBlock.Groups[1].Value.Trim();
        }

        if (json.Length == 0 || json[0] != '{') return false;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            string? name = null;
            foreach (string key in new[] { "tool", "name", "function" })
            {
                if (root.TryGetProperty(key, out JsonElement keyEl) && keyEl.ValueKind == JsonValueKind.String)
                {
                    name = keyEl.GetString();
                    if (!string.IsNullOrWhiteSpace(name)) break;
                }
            }

            if (string.IsNullOrWhiteSpace(name)) return false;

            toolName = name;
            arguments = default;
            foreach (string key in new[] { "arguments", "parameters", "params", "input" })
            {
                if (root.TryGetProperty(key, out JsonElement argsEl) && argsEl.ValueKind == JsonValueKind.Object)
                {
                    arguments = argsEl.Clone();
                    break;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
