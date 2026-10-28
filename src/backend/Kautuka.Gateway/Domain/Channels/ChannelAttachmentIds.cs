namespace Kautuka.Gateway.Domain.Channels;

internal enum ChannelAttachmentRole
{
    Chat,
    Agent,
    Workflows,
    Workflow,
}

internal readonly record struct ChannelAttachmentIdentity(
    string ProviderKey,
    string OwnerUserId,
    ChannelAttachmentRole Role,
    string? SubjectId)
{
    public bool IsChat => Role == ChannelAttachmentRole.Chat;
}

internal sealed class DuplicateChannelBotTokenException : InvalidOperationException
{
    public const string Detail = "duplicate_bot_token";

    public DuplicateChannelBotTokenException()
        : base(Detail)
    {
    }

    public DuplicateChannelBotTokenException(string message)
        : base(message)
    {
    }

    public DuplicateChannelBotTokenException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Attachment ids are <c>{provider}:{userId}:default</c>, <c>:agent:{agentId}</c>,
/// <c>:workflows</c>, or <c>:workflow:{workflowId}</c>. User ids do not contain colons.
/// </summary>
internal static class ChannelAttachmentIds
{
    public static bool TryParse(string? attachmentId, out ChannelAttachmentIdentity identity)
    {
        identity = default;
        if (string.IsNullOrWhiteSpace(attachmentId))
        {
            return false;
        }

        int providerSplit = attachmentId.IndexOf(':', StringComparison.Ordinal);
        if (providerSplit <= 0 || providerSplit >= attachmentId.Length - 1)
        {
            return false;
        }

        string providerKey = attachmentId[..providerSplit];
        string rest = attachmentId[(providerSplit + 1)..];
        int userSplit = rest.IndexOf(':', StringComparison.Ordinal);
        if (userSplit <= 0 || userSplit >= rest.Length - 1)
        {
            return false;
        }

        string ownerUserId = rest[..userSplit];
        string rolePart = rest[(userSplit + 1)..];
        if (rolePart == "default")
        {
            identity = new ChannelAttachmentIdentity(providerKey, ownerUserId, ChannelAttachmentRole.Chat, null);
            return true;
        }

        if (rolePart == "workflows")
        {
            identity = new ChannelAttachmentIdentity(providerKey, ownerUserId, ChannelAttachmentRole.Workflows, null);
            return true;
        }

        if (rolePart.StartsWith("agent:", StringComparison.Ordinal))
        {
            string subjectId = rolePart["agent:".Length..];
            if (string.IsNullOrWhiteSpace(subjectId))
            {
                return false;
            }

            identity = new ChannelAttachmentIdentity(providerKey, ownerUserId, ChannelAttachmentRole.Agent, subjectId);
            return true;
        }

        if (rolePart.StartsWith("workflow:", StringComparison.Ordinal))
        {
            string subjectId = rolePart["workflow:".Length..];
            if (string.IsNullOrWhiteSpace(subjectId))
            {
                return false;
            }

            identity = new ChannelAttachmentIdentity(providerKey, ownerUserId, ChannelAttachmentRole.Workflow, subjectId);
            return true;
        }

        return false;
    }

    public static bool IsChat(string? attachmentId)
        => TryParse(attachmentId, out ChannelAttachmentIdentity identity) && identity.IsChat;

    public static string Chat(string providerKey, string userId) => $"{providerKey}:{userId}:default";

    public static string Agent(string providerKey, string userId, string agentId) => $"{providerKey}:{userId}:agent:{agentId}";

    public static string Workflows(string providerKey, string userId) => $"{providerKey}:{userId}:workflows";

    public static string Workflow(string providerKey, string userId, string workflowId) => $"{providerKey}:{userId}:workflow:{workflowId}";

    public static string? For(string providerKey, string userId, ChannelAttachmentRole role, string? subjectId)
        => role switch
        {
            ChannelAttachmentRole.Chat => Chat(providerKey, userId),
            ChannelAttachmentRole.Agent when !string.IsNullOrWhiteSpace(subjectId) => Agent(providerKey, userId, subjectId),
            ChannelAttachmentRole.Workflows => Workflows(providerKey, userId),
            ChannelAttachmentRole.Workflow when !string.IsNullOrWhiteSpace(subjectId) => Workflow(providerKey, userId, subjectId),
            _ => null,
        };
}
