using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kautuka.Gateway.Domain.Stores;

internal sealed partial class GatewayCatalogStore
{
    private const string exposedAgentToolLeafValidationMessage = "Agents exposed as tools cannot invoke other agents as tools. Remove agent tool selections or disable Expose as tool.";
    private const string sandboxConnectorBindingValidationMessage = "Agent sandbox execution bindings require sandbox to be enabled for that agent.";
    private const string sandboxSkillAttachmentValidationMessage = "Active skill attachments require sandbox to be enabled for that agent.";
    private const string defaultMcpBindingContext = "OwnContext";

    public bool TryGetAgent(string id, out GatewayAgent? agent) =>
        state.Agents.TryGetValue(id, out agent);

    public IReadOnlyList<GatewayAgent> ListAgents() =>
        [.. state.Agents.Values];

    public GatewayAgent SaveAgent(string agentId, GatewayAgentUpsertRequest request, string ownerUserId)
        => SaveAgentAsync(agentId, request, ownerUserId).GetAwaiter().GetResult();

    public async Task<GatewayAgent> SaveAgentAsync(string agentId, GatewayAgentUpsertRequest request, string ownerUserId, CancellationToken cancellationToken = default)
    {
        GatewayAgent? existingAgent = state.Agents.TryGetValue(agentId, out GatewayAgent? currentAgent)
            ? currentAgent
            : null;

        string name = string.IsNullOrWhiteSpace(request.Name) ? agentId : request.Name;
        string modelId = request.ModelId;
        string systemPrompt = request.SystemPrompt;
        string superExecMode = string.IsNullOrWhiteSpace(request.SuperExecMode) ? "disabled" : request.SuperExecMode;
        string scope = existingAgent?.Scope ?? GatewayOwnershipScopes.User;
        bool enabled = existingAgent?.Enabled ?? true;
        GatewayAgentMcpBinding[] mcpBindings = NormalizeMcpBindings(request);
        AgentSkillAttachmentDefinition[] skillAttachments = GatewayAgentSettingsMapper.NormalizeSkillAttachments(
            request.SkillAttachments,
            existingAgent?.SkillAttachments);
        GatewayAgentSessionPayload sessionPayload = GatewayAgentSettingsMapper.NormalizeSession(request.Session, existingAgent?.Session);
        GatewayAgentPlanningPayload planningPayload = GatewayAgentSettingsMapper.NormalizePlanning(request.Planning, existingAgent?.Planning);
        GatewayAgentSandboxPayload sandboxPayload = GatewayAgentSettingsMapper.NormalizeSandbox(request.Sandbox, existingAgent?.Sandbox);
        GatewayApprovalConfigPayload approvalConfigPayload = GatewayAgentSettingsMapper.NormalizeApprovalConfig(request.ApprovalConfig, existingAgent?.ApprovalConfig);
        GatewayAgentToolExposurePayload toolExposurePayload = GatewayAgentSettingsMapper.NormalizeToolExposure(request.ToolExposure, existingAgent?.ToolExposure);
        GatewayAgentRuntimePayload runtimePayload = GatewayAgentSettingsMapper.NormalizeRuntime(request.Runtime, existingAgent?.Runtime);

        if (toolExposurePayload.ExposedAsTool && approvalConfigPayload.AgentToolIds.Length > 0)
        {
            throw new ArgumentException(exposedAgentToolLeafValidationMessage);
        }

        if (!sandboxPayload.Enabled
            && mcpBindings.Any(static binding => string.Equals(binding.Context, "AgentContext", StringComparison.OrdinalIgnoreCase)
                || string.Equals(binding.Context, "agent", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(sandboxConnectorBindingValidationMessage);
        }

        if (!sandboxPayload.Enabled
            && skillAttachments.Any(static attachment => attachment.Mode != SkillAttachmentMode.Disabled))
        {
            throw new ArgumentException(sandboxSkillAttachmentValidationMessage);
        }

        EnsureCallerCanUseBindings(ownerUserId, mcpBindings, skillAttachments);
        EnsureCallerCanUseAgentTools(ownerUserId, approvalConfigPayload.AgentToolIds);

        JsonObject persona = ResolveStoredPersonaProfile(request.Persona, existingAgent?.Persona);
        JsonObject session = GatewayAgentSettingsMapper.ToJsonObject(sessionPayload);
        JsonObject planning = GatewayAgentSettingsMapper.ToJsonObject(planningPayload);
        JsonObject sandbox = GatewayAgentSettingsMapper.ToJsonObject(sandboxPayload);
        JsonObject approvalConfig = GatewayAgentSettingsMapper.ToJsonObject(approvalConfigPayload);
        JsonObject toolExposure = GatewayAgentSettingsMapper.ToJsonObject(toolExposurePayload);
        JsonObject runtime = GatewayAgentSettingsMapper.ToJsonObject(runtimePayload);

        // Validate exposure description length
        string? exposureDescription = toolExposurePayload.Description;
        if (exposureDescription is { Length: > 200 })
        {
            throw new ArgumentException("toolExposure.description must be 200 characters or fewer.");
        }

        var agent = new GatewayAgent(
            agentId,
            name,
            modelId,
            systemPrompt,
            superExecMode,
            session,
            planning,
            sandbox,
            skillAttachments,
            ownerUserId,
            Timestamp(),
            approvalConfig,
            toolExposure,
            scope,
            enabled,
            persona,
            mcpBindings,
            runtime);

        AgentDefinition definition = CreateAgentDefinition(agent);
        AgentDefinitionSnapshotMetadata snapshotMetadata = CreateAgentDefinitionSnapshotMetadata(agent);
        string storageLocatorKey = agentDefinitionStore.GetStorageLocatorKey(agentId);
        AgentRegistryEntry registryEntry = CreateAgentRegistryEntry(agent, storageLocatorKey);

        if (agentDefinitionStore is IAtomicAgentDefinitionStore { SupportsAtomicRegistryTransactions: true } atomicAgentDefinitionStore)
        {
            long revision = atomicAgentDefinitionStore.SaveDefinitionAndRegistryAtomically(registryEntry, definition, snapshotMetadata);
            agent = agent with { Revision = revision };
        }
        else
        {
            long revision = agentDefinitionStore.SaveDefinition(storageLocatorKey, definition, snapshotMetadata);
            topologyWriter.SaveAgent(registryEntry);
            agent = agent with { Revision = revision };
        }

        state.AgentStorageLocators[agentId] = storageLocatorKey;

        state.Agents[agentId] = agent;
        await RebuildAgentWorkspacesForAllUsersAsync(agentId, cancellationToken).ConfigureAwait(false);
        state.TopologyHash = state.ComputeTopologyHashFromState();
        await desiredStatePublisher.TryPublishCurrentAsync("agent-save", cancellationToken).ConfigureAwait(false);
        return agent;
    }

    public bool DeleteAgent(string agentId)
        => DeleteAgentAsync(agentId).GetAwaiter().GetResult();

    public async Task<bool> DeleteAgentAsync(string agentId, CancellationToken cancellationToken = default)
    {
        bool isReferencedAsAgentTool = state.Agents.Values.Any(candidate =>
            !string.Equals(candidate.Id, agentId, StringComparison.Ordinal)
            && GatewayAgentSettingsMapper.NormalizeStoredApprovalConfig(candidate.ApprovalConfig)
                .AgentToolIds.Contains(agentId, StringComparer.Ordinal));
        if (isReferencedAsAgentTool)
        {
            return false;
        }

        string storageLocatorKey = state.AgentStorageLocators.TryGetValue(agentId, out string? configuredStorageLocatorKey)
            ? configuredStorageLocatorKey
            : agentDefinitionStore.GetStorageLocatorKey(agentId);
        if (runtimeEventLifecycle is not null)
        {
            await runtimeEventLifecycle.QuiesceAgentAsync(agentId, cancellationToken).ConfigureAwait(false);
        }

        bool deleted;
        try
        {
            deleted = agentDefinitionStore is IAtomicAgentDefinitionStore { SupportsAtomicRegistryTransactions: true } atomicAgentDefinitionStore
                ? atomicAgentDefinitionStore.DeleteStoreAndRegistryAtomically(agentId, storageLocatorKey)
                : agentDefinitionStore.DeleteStore(storageLocatorKey);
        }
        catch
        {
            runtimeEventLifecycle?.ResumeAgent(agentId);
            throw;
        }

        if (!deleted)
        {
            runtimeEventLifecycle?.ResumeAgent(agentId);
            return false;
        }

        runtimeEventLifecycle?.ForgetAgent(agentId);

        IReadOnlyList<string> removedSessionIds = state.RemoveAgentRuntimeState(agentId);
        continuationStore.DeleteBySessions(removedSessionIds);
        assetStore.DeleteSessionAssets(removedSessionIds);

        if (agentDefinitionStore is not IAtomicAgentDefinitionStore { SupportsAtomicRegistryTransactions: true })
        {
            topologyWriter.DeleteAgent(agentId);
        }
        state.AgentStorageLocators.TryRemove(agentId, out _);
        bool removed = state.Agents.TryRemove(agentId, out _);

        try
        {
            await DeleteAgentWorkspacesForAllUsersAsync(agentId, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }
        catch (IOException)
        {
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        if (removed)
        {
            state.TopologyHash = state.ComputeTopologyHashFromState();
            await desiredStatePublisher.TryPublishCurrentAsync("agent-delete", cancellationToken).ConfigureAwait(false);
        }

        return removed;
    }

    public GatewayAgentRevisionsResponse? TryListAgentRevisions(string agentId)
    {
        if (!state.Agents.TryGetValue(agentId, out GatewayAgent? agent) || agent is null)
        {
            return null;
        }

        string storageLocatorKey = ResolveStorageLocatorKey(agentId);
        IReadOnlyList<AgentDefinitionRevision> revisions = agentDefinitionStore.ListDefinitionRevisions(storageLocatorKey);
        Dictionary<long, List<AgentDefinitionSettingSnapshot>> settingsByRevision = agentDefinitionStore
            .ListAllDefinitionSettings(storageLocatorKey)
            .GroupBy(static setting => setting.Revision)
            .ToDictionary(static group => group.Key, static group => group.ToList());

        IReadOnlyList<AgentDefinitionSettingSnapshot>? previousSettings = null;
        var items = new List<GatewayAgentRevisionListItem>(revisions.Count);
        foreach (AgentDefinitionRevision revision in revisions.OrderBy(static item => item.Revision))
        {
            IReadOnlyList<AgentDefinitionSettingSnapshot> currentSettings = settingsByRevision.TryGetValue(revision.Revision, out List<AgentDefinitionSettingSnapshot>? settings)
                ? settings
                : [];
            IReadOnlyList<AgentDefinitionSettingChange> changes = previousSettings is null
                ? []
                : AgentDefinitionRevisionDiffer.Diff(previousSettings, currentSettings);
            items.Add(new GatewayAgentRevisionListItem(
                revision.Revision,
                revision.CreatedAt,
                [.. changes.Select(static change => change.SettingId)],
                [.. changes.Select(static change => change.SettingName)]));
            previousSettings = currentSettings;
        }

        items.Reverse();
        long currentRevision = agent.Revision > 0
            ? agent.Revision
            : revisions.Count == 0 ? 0 : revisions.Max(static item => item.Revision);
        return new GatewayAgentRevisionsResponse(agentId, currentRevision, [.. items]);
    }

    public GatewayAgentRevisionDiffResponse? TryGetAgentRevisionDiff(string agentId, long revision, long? fromRevision)
    {
        if (!state.Agents.ContainsKey(agentId))
        {
            return null;
        }

        string storageLocatorKey = ResolveStorageLocatorKey(agentId);
        IReadOnlyList<AgentDefinitionRevision> revisions = agentDefinitionStore.ListDefinitionRevisions(storageLocatorKey);
        AgentDefinitionRevision? toRevision = revisions.FirstOrDefault(item => item.Revision == revision);
        if (toRevision is null)
        {
            return null;
        }

        if (fromRevision is { } explicitFrom && revisions.All(item => item.Revision != explicitFrom))
        {
            return null;
        }

        long? from = fromRevision ?? (revision > 1 ? revision - 1 : null);
        if (from is null)
        {
            return new GatewayAgentRevisionDiffResponse(agentId, null, revision, toRevision.CreatedAt, []);
        }

        IReadOnlyList<AgentDefinitionSettingChange> changes = AgentDefinitionRevisionDiffer.Diff(
            agentDefinitionStore.ListDefinitionSettings(storageLocatorKey, from.Value),
            agentDefinitionStore.ListDefinitionSettings(storageLocatorKey, revision));
        return new GatewayAgentRevisionDiffResponse(
            agentId,
            from,
            revision,
            toRevision.CreatedAt,
            [.. changes.Select(static change => new GatewayAgentRevisionChange(
                change.SettingId,
                change.SettingName,
                ToJsonElement(change.Before),
                ToJsonElement(change.After)))]);
    }

    private string ResolveStorageLocatorKey(string agentId)
        => state.AgentStorageLocators.TryGetValue(agentId, out string? locator)
            ? locator
            : agentDefinitionStore.GetStorageLocatorKey(agentId);

    private static JsonElement? ToJsonElement(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        return JsonSerializer.Deserialize(node.ToJsonString(), KautukaJsonContext.Default.JsonElement);
    }

    private static AgentDefinition CreateAgentDefinition(GatewayAgent agent)
    {
        GatewayAgentSessionPayload sessionPayload = GatewayAgentSettingsMapper.NormalizeStoredSession(agent.Session);
        GatewayAgentPlanningPayload planningPayload = GatewayAgentSettingsMapper.NormalizeStoredPlanning(agent.Planning);
        GatewayAgentSandboxPayload sandboxPayload = GatewayAgentSettingsMapper.NormalizeStoredSandbox(agent.Sandbox);
        GatewayApprovalConfigPayload approvalConfigPayload = GatewayAgentSettingsMapper.NormalizeStoredApprovalConfig(agent.ApprovalConfig);
        GatewayAgentToolExposurePayload toolExposurePayload = GatewayAgentSettingsMapper.NormalizeStoredToolExposure(agent.ToolExposure);
        AgentSessionDefinition sessionDefinition = GatewayAgentSettingsMapper.ToDefinition(sessionPayload);

        return new AgentDefinition
        {
            Id = agent.Id,
            Scope = agent.Scope,
            Enabled = agent.Enabled,
            Name = agent.Name,
            ModelId = agent.ModelId,
            SystemPrompt = agent.SystemPrompt,
            SuperExecMode = agent.SuperExecMode,
            McpBindings = [.. (agent.McpBindings ?? []).Select(static binding => new AgentMcpBindingDefinition
            {
                ConnectorId = binding.ConnectorId,
                Context = binding.Context,
            })],
            SkillAttachments = [.. agent.SkillAttachments],
            Session = sessionDefinition,
            Sandbox = GatewayAgentSettingsMapper.ToDefinition(sandboxPayload),
            ApprovalPolicy = GatewayAgentSettingsMapper.ToDefinition(approvalConfigPayload),
            Persona = agent.Persona?.DeepClone() as JsonObject ?? new JsonObject(),
            ToolExposure = GatewayAgentSettingsMapper.ToDefinition(toolExposurePayload),
            Runtime = GatewayAgentSettingsMapper.ToDefinition(GatewayAgentSettingsMapper.NormalizeStoredRuntime(agent.Runtime)),
        };
    }

    private static AgentRegistryEntry CreateAgentRegistryEntry(GatewayAgent agent, string storageLocatorKey)
    {
        return new AgentRegistryEntry(
            agent.Id,
            agent.Scope,
            agent.Enabled,
            agent.OwnerUserId,
            storageLocatorKey);
    }

    private AgentDefinitionSnapshotMetadata CreateAgentDefinitionSnapshotMetadata(GatewayAgent agent)
    {
        state.Models.TryGetValue(agent.ModelId, out GatewayModel? model);

        Dictionary<string, string> connectorNames = (agent.McpBindings ?? [])
            .Select(static binding => binding.ConnectorId)
            .Where(static connectorId => !string.IsNullOrWhiteSpace(connectorId))
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(
                static id => id,
                id => state.McpServers.TryGetValue(id, out GatewayMcpServer? server) ? server.Name : string.Empty,
                StringComparer.Ordinal);

        Dictionary<string, string> skillNames = agent.SkillAttachments
            .Select(static attachment => attachment.SkillId)
            .Where(static skillId => !string.IsNullOrWhiteSpace(skillId))
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(
                static id => id,
                id => state.Skills.TryGetValue(id, out GatewaySkill? skill) ? skill.Name : string.Empty,
                StringComparer.Ordinal);

        return new AgentDefinitionSnapshotMetadata(
            model?.ModelName,
            model?.ProviderId,
            connectorNames,
            skillNames);
    }

    private void EnsureCallerCanUseAgentTools(string ownerUserId, IReadOnlyList<string> agentToolIds)
    {
        foreach (string agentToolId in agentToolIds)
        {
            if (!state.Agents.TryGetValue(agentToolId, out GatewayAgent? target)
                || GatewayOwnershipAccess.CallerCanUse(ownerUserId, target.Scope, target.OwnerUserId))
            {
                continue;
            }

            throw new ArgumentException($"Agent '{agentToolId}' is not available to this user.");
        }
    }

    private void EnsureCallerCanUseBindings(
        string ownerUserId,
        GatewayAgentMcpBinding[] mcpBindings,
        AgentSkillAttachmentDefinition[] skillAttachments)
    {
        foreach (GatewayAgentMcpBinding binding in mcpBindings)
        {
            GatewayMcpServer? server = state.McpServers.Values.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, binding.ConnectorId, StringComparison.Ordinal));
            if (server is not null
                && !GatewayOwnershipAccess.CallerCanUse(ownerUserId, server.Scope, server.OwnerUserId))
            {
                throw new ArgumentException($"Connector '{binding.ConnectorId}' is not available to this user.");
            }
        }

        foreach (AgentSkillAttachmentDefinition attachment in skillAttachments)
        {
            if (attachment.Mode == SkillAttachmentMode.Disabled)
            {
                continue;
            }

            GatewaySkill? skill = state.Skills.Values.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, attachment.SkillId, StringComparison.Ordinal));
            if (skill is not null
                && !GatewayOwnershipAccess.CallerCanUse(ownerUserId, skill.Scope, skill.OwnerUserId))
            {
                throw new ArgumentException($"Skill '{attachment.SkillId}' is not available to this user.");
            }
        }
    }

    private static GatewayAgentMcpBinding[] NormalizeMcpBindings(GatewayAgentUpsertRequest request)
    {
        return [.. (request.McpBindings ?? [])
            .Where(static binding => !string.IsNullOrWhiteSpace(binding.ConnectorId))
            .Select(static binding => new GatewayAgentMcpBinding(
                binding.ConnectorId.Trim(),
                string.IsNullOrWhiteSpace(binding.Context) ? defaultMcpBindingContext : binding.Context.Trim()))
            .Distinct()];
    }

    private JsonObject ResolveStoredPersonaProfile(JsonObject? requestedPersona, JsonObject? existingPersona)
    {
        if (settingsStore is not null)
        {
            return settingsStore.NormalizeAgentPersonaProfile(requestedPersona ?? existingPersona);
        }

        JsonObject definitions = GatewayPersonaProfileMapper.CreateDefaultDefinitions();
        if (requestedPersona is not null)
        {
            return GatewayPersonaProfileMapper.NormalizeStoredProfile(requestedPersona, definitions);
        }

        try
        {
            return GatewayPersonaProfileMapper.NormalizeStoredProfile(existingPersona, definitions);
        }
        catch (ArgumentException)
        {
            return GatewayPersonaProfileMapper.BuildDefaultProfile(definitions);
        }
    }

}
