using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Execution;

internal sealed class AssistantMessageLifecycle(string messageId)
{
    public string MessageId { get; } = messageId;

    public string ReasoningMessageId { get; } = NaradEventBodies.BuildReasoningMessageId(messageId);

    public bool MessageOpen { get; set; }

    public bool ReasoningOpen { get; set; }
}
