namespace Kautuka.Gateway.Domain.State;

internal sealed record GatewayDataAuthority(
    string DataClass,
    string Authority,
    string Projection,
    string Cache,
    IReadOnlyList<string> DeliveryTargets,
    string CommitPoint,
    string RepairStrategy);

internal static class GatewayDataAuthorityCatalog
{
    public static IReadOnlyList<GatewayDataAuthority> Entries { get; } =
    [
        new(
            "providers",
            "kautuka.db/providers",
            "GatewayState.Providers",
            "GatewayState.Providers",
            ["Manager desired state"],
            "Provider repository write commit",
            "Reload the projection from kautuka.db and republish desired state"),
        new(
            "models",
            "kautuka.db/models",
            "GatewayState.Models",
            "GatewayState.Models",
            ["Manager desired state"],
            "Model repository write commit",
            "Reload the projection from kautuka.db and republish desired state"),
        new(
            "agents",
            "per-agent agent.db/agent_definition_current",
            "kautuka.db/agents registry",
            "GatewayState.Agents",
            ["Agent workspace", "Manager desired state"],
            "Atomic agent definition and registry transaction commit",
            "Rebuild registry, workspace, and Manager desired state from the current agent definition"),
        new(
            "skills",
            "kautuka.db/skills",
            "GatewayState.Skills",
            "GatewayState.Skills",
            ["Agent workspaces"],
            "Skill repository transaction commit",
            "Reload the projection from kautuka.db and rebuild affected agent workspaces"),
        new(
            "users",
            "kautuka.db/runtime_users",
            "GatewayState.Users",
            "GatewayState.Users",
            ["Authentication sessions"],
            "User command repository transaction commit",
            "Reload the user projection from kautuka.db and revoke invalid authentication sessions"),
        new(
            "sessions",
            "per-agent agent.db/runtime_sessions and runtime_session_messages",
            "GatewayState.Sessions",
            "GatewayState.Sessions",
            ["Conversation asset bindings"],
            "Agent runtime session repository transaction commit",
            "Discard and reload the session projection; reconcile orphaned asset bindings"),
        new(
            "runs",
            "per-agent agent.db/runtime_runs",
            "GatewayState.Runs",
            "GatewayState.Runs",
            ["Canonical runtime event stream"],
            "Agent runtime run repository transaction commit",
            "Reload runs and rebuild runtime views from canonical events"),
        new(
            "memory",
            "per-agent agent.db/runtime_memory_facts",
            "GatewayMemoryStore",
            "GatewayMemoryStore and runtime_memory_embeddings",
            ["Embedding index"],
            "Agent memory fact repository transaction commit",
            "Reload facts and rebuild embeddings from authoritative facts"),
        new(
            "agent_tool_surface",
            "per-agent agent.db/agent_tool_surface_current",
            "AgentToolCatalog L1 cache",
            "AgentToolCatalog L1 cache",
            ["Agent runtime tool surface"],
            "Complete successful tool-surface rebuild commit",
            "Rebuild the catalogue from current agent tooling and bound connector/skill definitions"),
        new(
            "approvals",
            "kautuka.db/tool_approval_requests",
            "Canonical approval runtime events",
            "None",
            ["Channel approval presentation", "Continuation jobs"],
            "Approval repository transaction commit",
            "Reconcile channel presentation and continuation jobs from persisted approval state"),
        new(
            "approval_bypass",
            "in-process ApprovalBypassRegistry",
            "None",
            "ApprovalBypassRegistry",
            ["Approval resolution"],
            "In-memory registry mutation",
            "Re-register bypass from durable resolution side effects on restart"),
        new(
            "run_stop",
            "per-agent narad_runtime_journal run.stop.requested authority events",
            "RunStopRegistry",
            "RunStopRegistry",
            ["Run stop command handling"],
            "Narad authority journal append",
            "Reload stop signals from Narad root history on restart"),
        new(
            "configuration",
            "kautuka.db/system_settings and config_snapshots",
            "Live normalized configuration projection plus immutable config_snapshots revisions",
            "GatewayState configuration projection",
            ["Manager desired state", "ChannelHost owner targets"],
            "Configuration repository transaction commit",
            "Reload configuration and republish downstream desired state"),
    ];

    public static GatewayDataAuthority Get(string dataClass)
        => Entries.Single(entry => string.Equals(entry.DataClass, dataClass, StringComparison.Ordinal));
}
