using Kautuka.Gateway.Domain.Sandbox;

namespace Kautuka.Gateway.Infrastructure.Agents;

internal sealed record AgentToolContext(
    AgentDefinition AgentDefinition,
    string AgentId,
    string? UserId,
    IWorkspaceRuntimeTool WorkspaceTool,
    WorkspaceSessionBinding WorkspaceBinding,
    IReadOnlyList<EffectiveSkillManifestEntry>? EffectiveSkillManifest = null,
    string SessionId = "");
