using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.Gateway.Domain.Connectors;

/// <summary>Local metadata seeding only; never probes a provider or starts a container.</summary>
internal sealed partial class RemoteMcpPresetBootstrapService(IGatewayCatalogStore catalog,
    ILogger<RemoteMcpPresetBootstrapService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool complete = true;
            foreach ((string id, GatewayConnectorUpsertRequest definition) in RemoteMcpPresets.Create())
            {
                try
                {
                    bool created = await catalog.SeedRemoteMcpPresetAsync(id, definition, stoppingToken).ConfigureAwait(false);
                    if (created && logger.IsEnabled(LogLevel.Information))
                        RemoteMcpAudit.Record(logger, "connector.created", "gateway-system", id, "system", "succeeded");
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception exception) when (exception is InvalidOperationException or IOException
                    or Microsoft.Data.Sqlite.SqliteException or Grpc.Core.RpcException)
                {
                    complete = false;
                    LogRetry(logger, id);
                }
            }
            if (complete) return;
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(EventId = 7341, Level = LogLevel.Warning, Message = "Remote MCP preset {ConnectorId} could not be registered; local metadata registration will retry.")]
    private static partial void LogRetry(ILogger logger, string connectorId);
}
