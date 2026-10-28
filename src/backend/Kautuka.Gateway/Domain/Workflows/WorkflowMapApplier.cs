using System.Text.Json.Nodes;

namespace Kautuka.Gateway.Domain.Workflows;

internal sealed record WorkflowStateMap(string From, string To);

internal static class WorkflowMapApplier
{
    public static JsonObject Apply(
        JsonObject working,
        JsonNode source,
        IReadOnlyList<WorkflowStateMap> maps)
    {
        ArgumentNullException.ThrowIfNull(working);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(maps);

        JsonObject current = JsonNode.Parse(working.ToJsonString())!.AsObject();
        foreach (WorkflowStateMap map in maps)
        {
            JsonNode? value = WorkflowJsonPointer.Get(source, map.From);
            if (value is null)
            {
                throw new WorkflowMapException($"Map source '{map.From}' did not resolve.");
            }

            current = WorkflowJsonPointer.Set(current, map.To, value);
        }

        return current;
    }
}
