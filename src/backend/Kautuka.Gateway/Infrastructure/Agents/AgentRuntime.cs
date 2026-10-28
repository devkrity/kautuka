using System.Diagnostics.CodeAnalysis;

using Kautuka.Gateway.Domain.Execution;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Infrastructure.Agents;

internal enum AgentRuntimeError
{
    AgentNotFound,
    ModelNotFound,
    ProviderNotFound,
    ProviderUnavailable,
    ClientCreationFailed,
}

internal sealed class AgentRuntime(
    AIAgent agent,
    AgentSession session,
    string sessionId,
    string agentId,
    string agentName,
    ModelCapabilityProfile capabilityProfile,
    ProviderPathKey pathKey,
    IChatClient chatClient,
    string superExecMode,
    bool sandboxEnabled,
    EffectiveRunBehavior effectiveBehavior,
    bool requireApprovalForAllTools)
{
    public AIAgent Agent { get; } = agent;
    public AgentSession Session { get; } = session;
    internal string SessionId { get; } = sessionId;
    internal string AgentId { get; } = agentId;
    internal string AgentName { get; } = agentName;
    public ModelCapabilityProfile CapabilityProfile { get; } = capabilityProfile;
    public EffectiveCapabilities Capabilities { get; } = capabilityProfile.GetEffective();
    /// <summary>Provider path key for recording compatibility observations.</summary>
    internal ProviderPathKey PathKey { get; } = pathKey;
    internal IChatClient ChatClient { get; } = chatClient;
    internal string SuperExecMode { get; } = superExecMode;
    internal bool SandboxEnabled { get; } = sandboxEnabled;
    internal EffectiveRunBehavior EffectiveBehavior { get; } = effectiveBehavior;
    internal bool RequireApprovalForAllTools { get; } = requireApprovalForAllTools;
}

internal sealed class AgentRuntimeResult
{
    private AgentRuntimeResult() { }

    public AgentRuntime? Runtime { get; private init; }
    public AgentRuntimeError? Error { get; private init; }
    public string? ErrorMessage { get; private init; }

    [MemberNotNullWhen(true, nameof(Runtime))]
    [MemberNotNullWhen(false, nameof(ErrorMessage))]
    public bool IsSuccess => Runtime is not null;

    public static AgentRuntimeResult Success(AgentRuntime runtime) =>
        new() { Runtime = runtime };

    public static AgentRuntimeResult Failure(AgentRuntimeError error, string message) =>
        new() { Error = error, ErrorMessage = message };
}
