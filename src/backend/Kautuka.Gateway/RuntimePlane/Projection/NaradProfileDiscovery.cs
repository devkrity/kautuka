using System.Text.Json.Nodes;

using Narad.Contracts;

namespace Kautuka.Gateway.RuntimePlane.Projection;

internal static class NaradProfileDiscovery
{
    internal const int DefaultHistoryLimit = 500;
    internal const int MaxHistoryLimit = 500;
    internal const int DefaultHeartbeatIntervalMs = 15_000;
    internal const int DefaultLeaseDurationMs = 60_000;

    public static readonly IReadOnlyList<string> SessionActiveProfiles =
    [
        "narad.core/v1",
        "narad.conversation/v1",
        "narad.graph/v1",
        "narad.progress/v1",
        "narad.ui/v1",
    ];

    public static readonly IReadOnlyList<string> WorkflowActiveProfiles =
    [
        "narad.core/v1",
        "narad.workflow/v1",
        "narad.graph/v1",
        "narad.progress/v1",
    ];

    public static JsonObject BuildSessionCapabilities(long currentSessionSequence, string? activeRootRunId = null)
        => BuildCapabilities(SessionActiveProfiles, currentSessionSequence, activeRootRunId, workflow: false);

    public static JsonObject BuildWorkflowCapabilities(long currentSessionSequence)
        => BuildCapabilities(WorkflowActiveProfiles, currentSessionSequence, activeRootRunId: null, workflow: true);

    public static JsonObject BuildCapabilities(long currentSessionSequence, string? activeRootRunId = null)
        => BuildSessionCapabilities(currentSessionSequence, activeRootRunId);

    private static JsonObject BuildCapabilities(
        IReadOnlyList<string> activeProfiles,
        long currentSessionSequence,
        string? activeRootRunId,
        bool workflow)
        => new()
        {
            ["protocolVersion"] = NaradProtocolVersions.V1,
            ["activeProfiles"] = new JsonArray([.. activeProfiles.Select(static profile => (JsonNode?)profile)]),
            ["historyLimit"] = MaxHistoryLimit,
            ["defaultHistoryLimit"] = DefaultHistoryLimit,
            ["heartbeatIntervalMs"] = DefaultHeartbeatIntervalMs,
            ["currentSessionSequence"] = currentSessionSequence,
            ["activeRootRunId"] = activeRootRunId,
            ["graphSchemaVersion"] = NaradGraphSchema.Version,
            ["features"] = new JsonObject
            {
                ["sessionWatch"] = true,
                ["workspaceWatch"] = true,
                ["clientExecutor"] = false,
                ["workflow"] = workflow,
            },
        };
}
