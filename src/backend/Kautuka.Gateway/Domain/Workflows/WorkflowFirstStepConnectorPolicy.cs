using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.Gateway.Domain.Workflows;

internal static class WorkflowFirstStepConnectorPolicy
{
    public const string MailConnectorRequiredMessage = "The first step's agent needs an email connector (Outlook or Gmail) to read email attachments.";

    public static bool RequiresMailConnector(GatewayWorkflowDefinition definition)
        => WorkflowSchemaSubset.DeclaresProperty(definition.InputSchema, "receivedAfter");

    public static bool FirstStepHasMailConnector(GatewayWorkflowDefinition definition, IGatewayCatalogStore catalog)
    {
        if (definition.Nodes.Length == 0
            || !catalog.TryGetAgent(definition.Nodes[0].AgentId, out GatewayAgent? agent)
            || agent?.McpBindings is null)
        {
            return false;
        }

        foreach (GatewayAgentMcpBinding binding in agent.McpBindings)
        {
            if (!catalog.TryGetMcpServer(binding.ConnectorId, out GatewayMcpServer? server)
                || server is null
                || !server.Enabled)
            {
                continue;
            }

            if (server.Id.EndsWith("kautuka-m365-mail", StringComparison.Ordinal)
                || server.Id.EndsWith("kautuka-google-mail", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static bool ValidateMailConnector(GatewayWorkflowDefinition definition, IGatewayCatalogStore catalog, out string? error)
    {
        error = RequiresMailConnector(definition) && !FirstStepHasMailConnector(definition, catalog)
            ? MailConnectorRequiredMessage
            : null;
        return error is null;
    }
}
