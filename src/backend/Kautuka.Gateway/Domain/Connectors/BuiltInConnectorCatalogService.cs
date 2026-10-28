using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.Gateway.Domain.Connectors;

internal sealed partial class BuiltInConnectorCatalogService(
    ConnectorAuthCatalog authCatalog,
    IGatewayCatalogStore catalog,
    IGatewaySettingsStore settings,
    ILogger<BuiltInConnectorCatalogService> logger) : IHostedService
{
    private const string seededSettingKey = "connector.askFirstSeeded";

    [LoggerMessage(EventId = 7210, Level = LogLevel.Information, Message = "Registered built-in connector {ConnectorId}")]
    private static partial void LogRegistered(ILogger logger, string connectorId);

    [LoggerMessage(EventId = 7211, Level = LogLevel.Warning, Message = "Failed to register built-in connector {ConnectorId}")]
    private static partial void LogRegisterFailed(ILogger logger, Exception exception, string connectorId);

    [LoggerMessage(EventId = 7212, Level = LogLevel.Warning, Message = "Failed to seed Ask me first for {ConnectorId} on agent {AgentId}")]
    private static partial void LogSeedFailed(ILogger logger, Exception exception, string connectorId, string agentId);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (ConnectorAuthProfile profile in authCatalog.Profiles)
        {
            try
            {
                bool exists = catalog.TryGetMcpServer(profile.Id, out GatewayMcpServer? existing) && existing is not null;
                await catalog.SaveSystemMcpServerAsync(
                    profile.Id,
                    ManagedConnectorSettings.Registration(profile, exists ? existing : null),
                    cancellationToken).ConfigureAwait(false);
                LogRegistered(logger, profile.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogRegisterFailed(logger, ex, profile.Id);
            }
        }

        await SeedAskFirstAsync(authCatalog, cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task SeedAskFirstAsync(ConnectorAuthCatalog auth, CancellationToken cancellationToken)
    {
        JsonObject root = LoadSeeded();
        bool changed = false;
        foreach (ConnectorAuthProfile profile in auth.Profiles)
        {
            if (profile.AskFirst.Length == 0)
            {
                continue;
            }

            JsonObject connector = root[profile.Id] as JsonObject ?? new JsonObject();
            root[profile.Id] = connector;
            foreach (string tool in profile.AskFirst)
            {
                var seeded = ReadSeeded(connector, tool);
                foreach (GatewayAgent agent in catalog.ListAgents())
                {
                    GatewayApprovalConfigPayload current = GatewayAgentSettingsMapper.NormalizeStoredApprovalConfig(agent.ApprovalConfig);
                    GatewayApprovalConfigPayload? next = ConnectorAskFirstSeed.Next(current, profile.Id, tool, seeded, agent.Id);
                    if (next is null)
                    {
                        continue;
                    }

                    try
                    {
                        _ = await catalog.SaveAgentAsync(
                            agent.Id,
                            new GatewayAgentUpsertRequest
                            {
                                Name = agent.Name,
                                ModelId = agent.ModelId,
                                SystemPrompt = agent.SystemPrompt,
                                SuperExecMode = agent.SuperExecMode,
                                McpBindings = [.. (agent.McpBindings ?? []).Select(static binding => new GatewayAgentMcpBindingPayload
                                {
                                    ConnectorId = binding.ConnectorId,
                                    Context = binding.Context,
                                })],
                                ApprovalConfig = next,
                            },
                            string.IsNullOrWhiteSpace(agent.OwnerUserId) ? "system" : agent.OwnerUserId,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _ = seeded.Remove(agent.Id);
                        LogSeedFailed(logger, ex, profile.Id, agent.Id);
                    }
                }

                connector[tool] = ToArray(seeded);
                changed = true;
            }
        }

        if (changed)
        {
            settings.WriteSettingJson(seededSettingKey, root.ToJsonString());
        }
    }

    private JsonObject LoadSeeded()
    {
        string? raw = settings.ReadSettingJson(seededSettingKey);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            return JsonNode.Parse(raw) as JsonObject ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private static HashSet<string> ReadSeeded(JsonObject connector, string tool)
    {
        var seeded = new HashSet<string>(StringComparer.Ordinal);
        if (connector[tool] is not JsonArray array)
        {
            return seeded;
        }

        foreach (JsonNode? item in array)
        {
            if (item is JsonValue value && value.TryGetValue(out string? agentId) && !string.IsNullOrWhiteSpace(agentId))
            {
                _ = seeded.Add(agentId);
            }
        }

        return seeded;
    }

    private static JsonArray ToArray(HashSet<string> seeded)
    {
        var array = new JsonArray();
        foreach (string agentId in seeded.Order(StringComparer.Ordinal))
        {
            JsonNode? created = JsonValue.Create(agentId);
            if (created is not null)
            {
                array.Add(created);
            }
        }

        return array;
    }
}
