namespace Kautuka.ChannelContracts.Channels;

public static class ChannelMessageTemplates
{
    public static ChannelSemanticMessageDocument WithPrompt(ChannelSemanticMessageDocument document, string? prompt)
    {
        ArgumentNullException.ThrowIfNull(document);
        string text = string.IsNullOrWhiteSpace(prompt) ? "Original prompt unavailable." : prompt.Trim();
        string preview = text.Replace('\n', ' ').Replace('\r', ' ');
        if (preview.Length > 100) preview = preview[..97] + "…";
        int position = document.Blocks.Count > 0 && document.Blocks[0].Type == ChannelSemanticBlockTypes.Context ? 1 : 0;
        return document with
        {
            FallbackText = "Prompt: " + text + "\n\n" + document.FallbackText,
            Blocks = [.. document.Blocks.Take(position),
                new(ChannelSemanticBlockTypes.Details, Text: "Prompt: " + preview,
                    Blocks: [new(ChannelSemanticBlockTypes.Section, Text: text)]),
                .. document.Blocks.Skip(position)],
        };
    }

    public static ChannelSemanticMessageDocument WithSession(ChannelSemanticMessageDocument document, string? sessionName)
    {
        ArgumentNullException.ThrowIfNull(document);
        return string.IsNullOrWhiteSpace(sessionName) ? document : document with
        {
            FallbackText = "Session: " + sessionName + "\n" + document.FallbackText,
            Blocks = [new(ChannelSemanticBlockTypes.Context, Text: "Session: " + sessionName), .. document.Blocks],
        };
    }
    public static ChannelSemanticMessageDocument AgentResponse(string text, string? senderDisplayName = null)
        => Create("agent_response", senderDisplayName, text, [new(ChannelSemanticBlockTypes.Section, Text: text)]);

    public static ChannelSemanticMessageDocument UserMessage(string text, string? senderDisplayName = null)
        => Create("user_message", senderDisplayName, text, [new(ChannelSemanticBlockTypes.Section, Text: text)]);

    public static ChannelSemanticMessageDocument ApprovalRequest(
        string title,
        string prompt,
        string approveCallbackToken,
        string denyCallbackToken,
        string? senderDisplayName = null)
        => ApprovalRequest(title, prompt,
        [
            new("approve", ChannelSemanticActionTypes.Button, "Approve", approveCallbackToken, ChannelSemanticStyles.Primary, Required: true),
            new("deny", ChannelSemanticActionTypes.Button, "Deny", denyCallbackToken, ChannelSemanticStyles.Danger, Required: true),
        ], senderDisplayName);

    public static ChannelSemanticMessageDocument ApprovalRequest(
        string title,
        string prompt,
        IReadOnlyList<ChannelSemanticAction> actions,
        string? senderDisplayName = null)
        => Create("approval_request", senderDisplayName, $"{title}\n{prompt}\n{string.Join(' ', actions.Select(action => $"[{action.Label}]"))}",
        [
            new(ChannelSemanticBlockTypes.Header, Text: title),
            new(ChannelSemanticBlockTypes.Section, Text: prompt),
            new(ChannelSemanticBlockTypes.Actions, Actions: actions),
        ]);

    public static ChannelSemanticMessageDocument ApprovalResolution(string title, string state, string? detail = null, string? senderDisplayName = null)
        => Create("approval_resolution", senderDisplayName, $"{title}: {state}{(string.IsNullOrWhiteSpace(detail) ? string.Empty : $" - {detail}")}",
        [new(ChannelSemanticBlockTypes.Header, Text: title), new(ChannelSemanticBlockTypes.Fields, Fields: [new("Status", state), new("Detail", detail ?? "—")])], state);

    public static ChannelSemanticMessageDocument WorkflowReviewResolution(string title, string outcome, string? senderDisplayName = null)
        => Create("approval_resolution", senderDisplayName, $"{title}\n{outcome}",
        [new(ChannelSemanticBlockTypes.Header, Text: title), new(ChannelSemanticBlockTypes.Section, Text: outcome)], outcome);

    public static ChannelSemanticMessageDocument SessionPanel(string fallback, IReadOnlyList<ChannelSemanticBlock> blocks)
        => Create("session_panel", null, fallback, blocks);

    public static ChannelSemanticMessageDocument RunProgress(string title, string status, string? detail = null)
        => Create("run_progress", null, $"{title}: {status}", [new(ChannelSemanticBlockTypes.Header, Text: title), new(ChannelSemanticBlockTypes.Fields, Fields: [new("Status", status), new("Detail", detail ?? "—")])]);

    public static ChannelSemanticMessageDocument CommandHelp(string title, IReadOnlyList<ChannelSemanticField> commands)
        => Create("command_help", null, title, [new(ChannelSemanticBlockTypes.Header, Text: title), new(ChannelSemanticBlockTypes.Fields, Fields: commands)]);

    public static ChannelSemanticMessageDocument Error(string title, string safeDetail)
        => Create("error", null, $"{title}: {safeDetail}", [new(ChannelSemanticBlockTypes.Header, Text: title), new(ChannelSemanticBlockTypes.Section, Text: safeDetail)], "error");

    public static ChannelSemanticMessageDocument FileDelivery(string title, string fileName, string? detail = null)
        => Create("file_delivery", null, $"{title}: {fileName}", [new(ChannelSemanticBlockTypes.Header, Text: title), new(ChannelSemanticBlockTypes.Fields, Fields: [new("File", fileName), new("Detail", detail ?? "Ready")])]);

    public static ChannelSemanticMessageDocument StatusSummary(string title, IReadOnlyList<ChannelSemanticField> fields, string? terminalState = null)
        => Create("status_summary", null, title, [new(ChannelSemanticBlockTypes.Header, Text: title), new(ChannelSemanticBlockTypes.Fields, Fields: fields)], terminalState);

    private static ChannelSemanticMessageDocument Create(string template, string? sender, string fallback, IReadOnlyList<ChannelSemanticBlock> blocks, string? terminal = null)
        => new(ChannelSemanticContractVersions.MessageDocument, template, sender, fallback, blocks, terminal);
}
