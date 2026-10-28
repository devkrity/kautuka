using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.Gateway.Domain.Autopilot;

internal static class AutopilotRunInput
{
    public static JsonObject Create(GatewayAutopilotSchedule schedule, IGatewayWorkflowStore workflowStore)
    {
        JsonObject payload = new()
        {
            ["schedulePromptContext"] = schedule.SchedulePromptContext,
            ["workspaceFilePaths"] = JsonSerializer.SerializeToNode(schedule.WorkspaceFilePaths, KautukaJsonContext.Default.StringArray),
        };
        if (string.Equals(schedule.TargetType, AutopilotTargetTypes.Workflow, StringComparison.Ordinal)
            && workflowStore.GetDefinition(schedule.TargetId) is { } definition)
        {
            payload["workflowName"] = definition.Name;
            payload["workflowRevision"] = definition.Revision;
            payload["startValues"] = JsonNode.Parse(schedule.SchedulePromptContext);
        }
        return payload;
    }
}
