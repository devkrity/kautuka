namespace Kautuka.ChannelContracts.Channels;

public static class ChannelSemanticContractVersions
{
    public const string MessageDocument = "kautuka.channel-message/v1";
    public const string Capabilities = "kautuka.channel-capabilities/v1";
    public const string Interaction = "kautuka.channel-interaction/v1";
}

public static class ChannelSemanticBlockTypes
{
    public const string Header = "header";
    public const string Heading = "heading";
    public const string Section = "section";
    public const string Fields = "fields";
    public const string Context = "context";
    public const string Divider = "divider";
    public const string Image = "image";
    public const string Table = "table";
    public const string Actions = "actions";
    public const string Details = "details";
}

public static class ChannelSemanticActionTypes
{
    public const string Button = "button";
    public const string Select = "select";
    public const string Input = "input";
}

public static class ChannelSemanticStyles
{
    public const string Default = "default";
    public const string Primary = "primary";
    public const string Success = "success";
    public const string Danger = "danger";
}

public sealed record ChannelSemanticMessageAction(
    string ConversationId,
    string? ConversationThreadId,
    ChannelSemanticMessageDocument Document,
    string? InteractionId = null,
    string? MessageKey = null,
    long? Revision = null,
    string? TransportMessageId = null,
    string? ReplyToMessageId = null,
    string? PromptSessionId = null,
    string? PromptRootRunId = null,
    bool BringToFront = false,
    string? TopicTitle = null,
    string? TopicTargetKey = null,
    string? PromptText = null,
    string? PromptSessionLabel = null);

public sealed record ChannelSemanticMessageDocument(
    string Version,
    string Template,
    string? SenderDisplayName,
    string? FallbackText,
    IReadOnlyList<ChannelSemanticBlock> Blocks,
    string? TerminalState = null);

public sealed record ChannelSemanticBlock(
    string Type,
    string? Text = null,
    IReadOnlyList<ChannelSemanticField>? Fields = null,
    IReadOnlyList<ChannelSemanticAction>? Actions = null,
    Uri? ImageUrl = null,
    string? AltText = null,
    IReadOnlyList<string>? TableHeaders = null,
    IReadOnlyList<ChannelSemanticTableRow>? TableRows = null,
    IReadOnlyList<ChannelSemanticBlock>? Blocks = null,
    bool Expanded = false);

public sealed record ChannelSemanticField(string Label, string Value);

public sealed record ChannelSemanticAction(
    string ActionId,
    string Type,
    string Label,
    string CallbackToken,
    string? Style = null,
    IReadOnlyList<ChannelSemanticOption>? Options = null,
    bool Required = false);

public sealed record ChannelSemanticOption(string Label, string Value);

public sealed record ChannelSemanticTableRow(IReadOnlyList<string> Cells);

public sealed record ChannelDurableInteractionDocument(
    string Version,
    string InteractionId,
    string ActionId,
    string CallbackToken,
    string? SelectedValue,
    string DocumentVersion,
    DateTimeOffset ExpiresAt,
    string TenantId,
    string OwnerUserId,
    string RegistrationId,
    string ConversationId,
    string? ConversationThreadId,
    string SessionId,
    string Purpose,
    string? ApprovalRequestId,
    IReadOnlyList<string>? AllowedValues,
    string? TerminalState);

public sealed record ChannelReactionAction(
    string ConversationId,
    string MessageId,
    string Reaction);

public sealed record ChannelStreamMessageAction(
    string StreamId,
    long Sequence,
    string ConversationId,
    string? ConversationThreadId,
    string? TransportMessageId,
    string AccumulatedText,
    string? SenderDisplayName,
    bool IsFinal);

public sealed record ChannelOutboundFileAction(
    string UploadId,
    string ConversationId,
    string? ConversationThreadId,
    string SafeFileName,
    string ContentType,
    long DeclaredSizeBytes,
    string? Caption,
    string? SenderDisplayName);

public sealed record ChannelEphemeralMessageAction(
    string ConversationId,
    string? ConversationThreadId,
    string ExternalUserId,
    ChannelSemanticMessageDocument Document);

public sealed record ChannelModalAction(
    string TriggerId,
    string ConversationId,
    string? ConversationThreadId,
    string Title,
    string CallbackToken,
    IReadOnlyList<ChannelSemanticAction> Inputs,
    string SubmitLabel = "Submit",
    string CloseLabel = "Cancel");

public sealed record ChannelProviderCapabilities(
    string Version,
    string ProviderKey,
    bool Streaming,
    bool Edits,
    bool Deletes,
    bool Typing,
    bool Reactions,
    bool Modals,
    bool EphemeralMessages,
    bool Files,
    bool InboundFiles,
    bool History,
    bool UserLookup,
    bool Commands,
    bool SuggestedPrompts,
    bool ThreadTitles,
    int MaximumMessageCharacters,
    int MaximumActions,
    int MaximumActionLabelCharacters,
    int MaximumCallbackTokenBytes,
    int InteractionAcknowledgementDeadlineSeconds,
    int MaximumBlocks,
    int MaximumActionRows,
    int MaximumSelectOptions,
    long MaximumFileBytes);
