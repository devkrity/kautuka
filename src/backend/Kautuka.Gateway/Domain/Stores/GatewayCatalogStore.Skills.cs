using System.Globalization;
using System.Text.Json;

using Kautuka.Gateway.Domain.Skills;

namespace Kautuka.Gateway.Domain.Stores;

internal sealed partial class GatewayCatalogStore
{
    public IReadOnlyList<GatewaySkill> ListSkills() => [.. state.Skills.Values];

    public bool TryGetSkill(string id, out GatewaySkill? skill) =>
        state.Skills.TryGetValue(id, out skill);

    public GatewaySkill SaveSkill(string skillId, JsonElement body, string scope, string? ownerUserId)
    {
        GatewaySkill? caseVariant = state.Skills.Values.FirstOrDefault(skill =>
            string.Equals(skill.Id, skillId, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(skill.Id, skillId, StringComparison.Ordinal));
        if (caseVariant is not null)
        {
            throw new InvalidOperationException($"Skill id '{skillId}' differs only by letter case from '{caseVariant.Id}'.");
        }

        GatewaySkill? existing = state.Skills.TryGetValue(skillId, out GatewaySkill? found) ? found : null;
        string name = body.GetStringProperty("name", existing?.Name ?? skillId);
        string description = body.GetStringProperty("description", existing?.Description ?? string.Empty);
        string packageHash = body.GetStringProperty("packageHash", existing?.PackageHash ?? string.Empty);
        string status = body.GetStringProperty("status", existing?.Status ?? "ready");
        string packagePath = skillPackageService.ConfinePackagePath(
            skillId,
            body.GetStringProperty("packagePath", existing?.PackagePath ?? string.Empty),
            existing?.PackagePath);
        bool hasResources = body.GetBoolProperty("hasResources", existing?.HasResources ?? false);
        bool hasScripts = body.GetBoolProperty("hasScripts", existing?.HasScripts ?? false);
        bool enabled = body.GetBoolProperty("enabled", existing?.Enabled ?? true);
        string[] envKeys = ParseEnvKeys(body, existing?.EnvKeys);
        string importTimestamp = body.GetStringProperty("importTimestamp", existing?.ImportTimestamp ?? DateTimeOffset.UtcNow.ToString("O"));
        Dictionary<string, string> sourceMetadata = ParseSourceMetadata(body, existing?.SourceMetadata);
        string scopeValue = existing?.Scope ?? scope;
        string? ownerUserIdValue = existing?.OwnerUserId ?? ownerUserId;
        if (BuiltInSkillCatalog.IsSystemLockedSkill(skillId))
        {
            scopeValue = GatewayOwnershipScopes.System;
            ownerUserIdValue = null;
        }

        var skill = new GatewaySkill(
            skillId,
            name,
            description,
            packageHash,
            status,
            packagePath,
            hasResources,
            hasScripts,
            enabled,
            envKeys,
            importTimestamp,
            sourceMetadata,
            scopeValue,
            ownerUserIdValue);

        topologyWriter.SaveSkill(new SkillDefinition
        {
            SkillId = skill.Id,
            Name = skill.Name,
            Description = skill.Description,
            PackageHash = skill.PackageHash,
            Status = skill.Status,
            PackagePath = skill.PackagePath,
            HasResources = skill.HasResources,
            HasScripts = skill.HasScripts,
            Enabled = skill.Enabled,
            EnvKeys = [.. skill.EnvKeys ?? []],
            Scope = skill.Scope,
            OwnerUserId = skill.OwnerUserId,
            ImportTimestamp = DateTimeOffset.TryParse(skill.ImportTimestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset timestamp) ? timestamp : DateTimeOffset.UtcNow,
            SourceMetadata = new Dictionary<string, string>(skill.SourceMetadata, StringComparer.OrdinalIgnoreCase),
        });

        state.Skills[skillId] = skill;
        return skill;
    }

    public bool DeleteSkill(string skillId)
    {
        if (BuiltInSkillCatalog.IsSystemLockedSkill(skillId))
        {
            return false;
        }

        GatewaySkill? existing = state.Skills.TryGetValue(skillId, out GatewaySkill? found) ? found : null;
        if (existing is null)
        {
            return false;
        }

        // Persist every affected agent definition before deleting the skill authority. A failure can
        // then leave an existing-but-unattached skill, never an agent definition referencing a
        // missing skill after restart.
        foreach ((string agentId, GatewayAgent agent) in state.Agents.ToArray())
        {
            AgentSkillAttachmentDefinition[] remainingAttachments = [.. agent.SkillAttachments
                .Where(attachment => !string.Equals(attachment.SkillId, skillId, StringComparison.OrdinalIgnoreCase))];
            if (remainingAttachments.Length != agent.SkillAttachments.Length)
            {
                GatewayAgent updatedAgent = agent with { SkillAttachments = remainingAttachments };
                string storageLocatorKey = state.AgentStorageLocators.TryGetValue(agentId, out string? locator)
                    ? locator
                    : agentDefinitionStore.GetStorageLocatorKey(agentId);
                long revision = agentDefinitionStore.SaveDefinition(
                    storageLocatorKey,
                    CreateAgentDefinition(updatedAgent),
                    CreateAgentDefinitionSnapshotMetadata(updatedAgent));
                state.Agents[agentId] = updatedAgent with { Revision = revision };
            }
        }

        topologyWriter.DeleteSkill(skillId);
        bool removed = state.Skills.TryRemove(skillId, out _);
        if (!removed)
        {
            throw new InvalidOperationException($"Skill '{skillId}' disappeared while its deletion was being committed.");
        }

        if (!string.IsNullOrWhiteSpace(existing.PackagePath))
        {
            skillPackageService.DeletePackage(skillId, existing.PackagePath);
        }

        state.TopologyHash = state.ComputeTopologyHashFromState();
        return true;
    }

    private static Dictionary<string, string> ParseSourceMetadata(JsonElement body, IReadOnlyDictionary<string, string>? current)
    {
        var metadata = current is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(current, StringComparer.OrdinalIgnoreCase);

        if (!body.TryGetProperty("sourceMetadata", out JsonElement sourceMetadata) || sourceMetadata.ValueKind != JsonValueKind.Object)
        {
            return metadata;
        }

        metadata.Clear();
        foreach (JsonProperty property in sourceMetadata.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                metadata[property.Name] = property.Value.GetString() ?? string.Empty;
            }
        }

        return metadata;
    }

    private static string[] ParseEnvKeys(JsonElement body, IReadOnlyList<string>? current)
    {
        if (!body.TryGetProperty("envKeys", out JsonElement envKeysElement) || envKeysElement.ValueKind != JsonValueKind.Array)
        {
            return current is { Count: > 0 } ? [.. current] : [];
        }

        return [.. envKeysElement
            .EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString()?.Trim())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .Distinct(StringComparer.Ordinal)];
    }
}
