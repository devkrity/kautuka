using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Infrastructure.Connectors;

namespace Kautuka.Gateway.Domain.Workflows;

internal static class WorkflowFirstStepConnectorPolicy
{
    public const string MailConnectorRequiredMessage = "The first step's agent needs an email connector (Outlook or Gmail) to read email attachments.";

    public static bool RequiresMailConnector(GatewayWorkflowDefinition definition)
        => WorkflowSchemaSubset.DeclaresProperty(definition.InputSchema, "receivedAfter");

    public static bool FirstStepHasMailConnector(GatewayWorkflowDefinition definition, IGatewayCatalogStore catalog,
        string? userId = null, ConnectorUserEnablement? enablement = null)
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
                || !(enablement?.IsEnabled(userId, server.Id, server.Enabled) ?? server.Enabled))
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

    public static bool ValidateMailConnector(GatewayWorkflowDefinition definition, IGatewayCatalogStore catalog, out string? error,
        string? userId = null, ConnectorUserEnablement? enablement = null)
    {
        error = RequiresMailConnector(definition) && !FirstStepHasMailConnector(definition, catalog, userId, enablement)
            ? MailConnectorRequiredMessage
            : null;
        return error is null;
    }
}
