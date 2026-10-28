using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Assets;
using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Sandbox;
using Kautuka.Gateway.Domain.Skills;

namespace Kautuka.Gateway.Domain.Stores;

internal sealed partial class GatewayCatalogStore : IGatewayCatalogStore
{
    private readonly GatewayState state;
    private readonly IAgentDefinitionStore agentDefinitionStore;
    private readonly ITopologyConfigurationWriter topologyWriter;
    private readonly IWorkspaceRuntimeTool workspaceTool;
    private readonly ISkillPackageService skillPackageService;
    private readonly IWorkspaceDesiredStatePublisher desiredStatePublisher;
    private readonly IGatewayContinuationStore continuationStore;
    private readonly IConversationAssetStore assetStore;
    private readonly IGatewaySettingsStore? settingsStore;
    private readonly IProviderTypeCatalog? providerTypeCatalog;
    private readonly IRuntimeEventLifecycle? runtimeEventLifecycle;

    public GatewayCatalogStore(
        GatewayState state,
        IAgentDefinitionStore agentDefinitionStore,
        ITopologyConfigurationWriter topologyWriter,
        IWorkspaceRuntimeTool workspaceTool,
        ISkillPackageService skillPackageService,
        IWorkspaceDesiredStatePublisher desiredStatePublisher,
        IGatewayContinuationStore continuationStore,
        IConversationAssetStore assetStore)
        : this(
            state,
            agentDefinitionStore,
            topologyWriter,
            workspaceTool,
            skillPackageService,
            desiredStatePublisher,
            continuationStore,
            assetStore,
            null,
            null,
            null)
    {
    }

    public GatewayCatalogStore(
        GatewayState state,
        IAgentDefinitionStore agentDefinitionStore,
        ITopologyConfigurationWriter topologyWriter,
        IWorkspaceRuntimeTool workspaceTool,
        ISkillPackageService skillPackageService,
        IWorkspaceDesiredStatePublisher desiredStatePublisher,
        IGatewayContinuationStore continuationStore,
        IConversationAssetStore assetStore,
        IGatewaySettingsStore? settingsStore,
        IProviderTypeCatalog? providerTypeCatalog = null,
        IRuntimeEventLifecycle? runtimeEventLifecycle = null)
    {
        this.state = state;
        this.agentDefinitionStore = agentDefinitionStore;
        this.topologyWriter = topologyWriter;
        this.workspaceTool = workspaceTool;
        this.skillPackageService = skillPackageService;
        this.desiredStatePublisher = desiredStatePublisher;
        this.continuationStore = continuationStore;
        this.assetStore = assetStore;
        this.settingsStore = settingsStore;
        this.providerTypeCatalog = providerTypeCatalog;
        this.runtimeEventLifecycle = runtimeEventLifecycle;
    }

    public bool TryGetProvider(string id, out GatewayProvider? provider) =>
        state.Providers.TryGetValue(id, out provider);

    public IReadOnlyList<GatewayProvider> ListProviders() =>
        [.. state.Providers.Values];

    public GatewayProvider SaveProvider(JsonElement body)
    {
        string id = body.GetStringProperty("id", "").Trim();
        string type = ProviderTypeCatalogSettings.NormalizeType(body.GetStringProperty("type", ""));
        if (string.IsNullOrWhiteSpace(id))
        {
            id = $"provider-{Guid.NewGuid():N}";
        }

        JsonObject settings = body.GetObjectProperty("settings");
        bool isExistingProvider = state.Providers.TryGetValue(id, out GatewayProvider? existingProvider) && existingProvider is not null;
        if (!isExistingProvider)
        {
            settings = ProviderTypeCatalogSettings.ApplyCreateDefaults(settings, type, providerTypeCatalog);
        }

        settings.Remove("api_key");

        if (providerTypeCatalog is not null
            && !ProviderTypeCatalogSettings.TryValidate(settings, type, providerTypeCatalog, out string validationError))
        {
            throw new ProviderConfigurationException(validationError);
        }

        topologyWriter.SaveProvider(new ProviderDefinition
        {
            Id = id,
            Type = type,
            Settings = ToSettingsDictionary(settings),
        });

        var provider = new GatewayProvider(id, id, type, settings, Timestamp());
        state.Providers[id] = provider;
        return provider;
    }

    public bool DeleteProvider(string id)
    {
        topologyWriter.DeleteProvider(id);
        bool removed = state.Providers.TryRemove(id, out _);
        return removed;
    }

    public bool TryGetModel(string id, out GatewayModel? model) =>
        state.Models.TryGetValue(id, out model);

    public IReadOnlyList<GatewayModel> ListModels() =>
        [.. state.Models.Values];

    public GatewayModel SaveModel(JsonElement body)
    {
        string id = body.GetStringProperty("id", "").Trim();
        if (string.IsNullOrWhiteSpace(id))
        {
            id = $"model-{Guid.NewGuid():N}";
        }

        string providerId = body.GetStringProperty("providerId", "");
        string modelName = body.GetStringProperty("modelName", "");
        JsonObject settings = body.GetObjectProperty("settings");
        GatewayModelCapabilities? capabilities = TryReadModelCapabilities(body)
            ?? (state.Models.TryGetValue(id, out GatewayModel? existing)
                ? existing?.Capabilities
                : null);

        topologyWriter.SaveModel(new ModelDefinition
        {
            Id = id,
            ProviderId = providerId,
            ModelName = modelName,
            Settings = ToSettingsDictionary(settings),
            Capabilities = capabilities,
        });

        var model = new GatewayModel(id, providerId, modelName, settings, capabilities, Timestamp());
        state.Models[id] = model;
        return model;
    }

    public void UpdateModelCapabilities(string modelId, GatewayModelCapabilities capabilities)
    {
        if (state.Models.TryGetValue(modelId, out GatewayModel? model) && model is not null)
        {
            topologyWriter.SaveModel(new ModelDefinition
            {
                Id = model.Id,
                ProviderId = model.ProviderId,
                ModelName = model.ModelName,
                Settings = ToSettingsDictionary(model.Settings),
                Capabilities = capabilities,
            });
            state.Models[modelId] = model with { Capabilities = capabilities };
        }
    }

    public bool DeleteModel(string id)
    {
        topologyWriter.DeleteModel(id);
        bool removed = state.Models.TryRemove(id, out _);
        return removed;
    }

    private void RebuildAgentWorkspacesForAllUsers(string agentId)
    {
        workspaceTool.RebuildAgentWorkspace(agentId);
    }

    private Task<WorkspaceRuntimeContext> RebuildAgentWorkspacesForAllUsersAsync(string agentId, CancellationToken cancellationToken = default)
        => workspaceTool.RebuildAgentWorkspaceAsync(agentId, cancellationToken: cancellationToken);

    private void DeleteAgentWorkspacesForAllUsers(string agentId)
    {
        workspaceTool.DeleteAgentWorkspace(agentId);
    }

    private Task<bool> DeleteAgentWorkspacesForAllUsersAsync(string agentId, CancellationToken cancellationToken = default)
        => workspaceTool.DeleteAgentWorkspaceAsync(agentId, cancellationToken);

}
