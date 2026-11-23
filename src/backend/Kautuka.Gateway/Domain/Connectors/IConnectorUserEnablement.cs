namespace Kautuka.Gateway.Domain.Connectors;

internal interface IConnectorUserEnablement
{
    bool IsEnabled(string? userId, string connectorId, bool defaultEnabled);
    void ForgetConnector(string connectorId);
}
