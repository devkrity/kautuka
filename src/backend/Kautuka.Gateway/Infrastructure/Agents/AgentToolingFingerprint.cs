using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Kautuka.Gateway.Domain.Skills;
using Kautuka.Manager.Protos;

namespace Kautuka.Gateway.Infrastructure.Agents;

internal static class AgentToolingFingerprint
{
    public static string Compute(AgentDefinition agent, PlatformConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(config);

        Dictionary<string, SkillDefinition> skills = SkillIdentity.Index(config.Skills, static skill => skill.SkillId);
        var builder = new StringBuilder();
        Append(builder, "v1");
        Append(builder, agent.SuperExecMode);
        Append(builder, (agent.Sandbox?.Enabled ?? false) ? "1" : "0");

        ApprovalPolicy policy = agent.ApprovalPolicy ?? new ApprovalPolicy();
        foreach (string mcpId in policy.RequireMcpIds.Order(StringComparer.Ordinal))
        {
            Append(builder, "require-mcp");
            Append(builder, mcpId);
        }

        foreach ((string connectorId, IReadOnlyList<string> filters) in policy.RequireMcpFunctionFilters
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            Append(builder, "require-filter");
            Append(builder, connectorId);
            foreach (string filter in filters.Order(StringComparer.Ordinal))
            {
                Append(builder, filter);
            }
        }

        foreach ((string connectorId, IReadOnlyList<string> disabled) in policy.DisabledMcpFunctions
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            Append(builder, "disabled");
            Append(builder, connectorId);
            foreach (string toolName in disabled.Order(StringComparer.Ordinal))
            {
                Append(builder, toolName);
            }
        }

        foreach (string agentToolId in policy.AgentToolIds.Order(StringComparer.Ordinal))
        {
            Append(builder, "aaat");
            Append(builder, agentToolId);
        }

        foreach (string agentToolId in policy.ApprovalRequiredAgentToolIds.Order(StringComparer.Ordinal))
        {
            Append(builder, "aaat-ask");
            Append(builder, agentToolId);
        }

        foreach (AgentMcpBindingDefinition binding in agent.McpBindings
            .Where(static binding => !string.IsNullOrWhiteSpace(binding.ConnectorId))
            .OrderBy(static binding => binding.ConnectorId, StringComparer.Ordinal)
            .ThenBy(static binding => binding.Context, StringComparer.Ordinal))
        {
            Append(builder, "binding");
            Append(builder, binding.ConnectorId);
            Append(builder, NormalizeExecutionContext(binding.Context).ToString());
        }

        foreach (AgentSkillAttachmentDefinition attachment in agent.SkillAttachments
            .Where(static attachment => !string.IsNullOrWhiteSpace(attachment.SkillId))
            .OrderBy(static attachment => attachment.SkillId, StringComparer.Ordinal))
        {
            Append(builder, "skill");
            Append(builder, attachment.SkillId);
            Append(builder, attachment.Mode.ToString());
            Append(builder, skills.TryGetValue(attachment.SkillId, out SkillDefinition? skill) ? skill.PackageHash : string.Empty);
        }

        HashSet<string> boundConnectorIds = agent.McpBindings
            .Select(static binding => binding.ConnectorId)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (McpServerDefinition server in config.McpServers
            .Where(server => boundConnectorIds.Contains(server.Id))
            .OrderBy(static server => server.Id, StringComparer.OrdinalIgnoreCase))
        {
            AppendConnector(builder, server);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    public static string WithCaller(
        string configFingerprint,
        string? userId,
        IReadOnlyList<(string ConnectorId, string Endpoint)> endpoints)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configFingerprint);
        if (string.IsNullOrEmpty(userId) && endpoints.Count == 0)
        {
            return configFingerprint;
        }

        var builder = new StringBuilder();
        Append(builder, "caller");
        Append(builder, userId ?? string.Empty);
        foreach ((string connectorId, string endpoint) in endpoints
            .OrderBy(static pair => pair.ConnectorId, StringComparer.Ordinal)
            .ThenBy(static pair => pair.Endpoint, StringComparer.Ordinal))
        {
            Append(builder, "endpoint");
            Append(builder, connectorId);
            Append(builder, endpoint);
        }

        string caller = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
        return string.Concat(configFingerprint, caller);
    }

    private static void AppendConnector(StringBuilder builder, McpServerDefinition server)
    {
        Append(builder, "connector");
        Append(builder, server.Id);
        Append(builder, server.Transport.ToString());
        Append(builder, server.HttpUrl?.AbsoluteUri ?? string.Empty);
        Append(builder, server.ContainerImage ?? string.Empty);
        Append(builder, server.ContainerCommand ?? string.Empty);
        foreach (string argument in server.ContainerArgs)
        {
            Append(builder, argument);
        }

        Append(builder, server.ContainerSidecarName ?? string.Empty);
        Append(builder, server.McpProtocolVersion ?? string.Empty);
        Append(builder, server.BundlePath ?? string.Empty);
        Append(builder, server.ServerType ?? string.Empty);
        Append(builder, server.RuntimeArtifactType ?? string.Empty);
        Append(builder, server.RuntimeArtifactSourceDigest ?? string.Empty);
        Append(builder, server.ReadinessTimeoutSeconds?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
        Append(builder, server.JitCooldownSeconds?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
        Append(builder, server.HealthcheckUrl?.AbsoluteUri ?? string.Empty);
        Append(builder, server.HealthcheckIntervalMilliseconds?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
        foreach (string envKey in server.EnvKeys.Order(StringComparer.Ordinal))
        {
            Append(builder, envKey);
        }

        Append(builder, server.Enabled ? "1" : "0");
        Append(builder, server.IsInternalSystemConnector ? "1" : "0");
    }

    private static WorkspaceMcpExecutionContext NormalizeExecutionContext(string? context)
        => McpAgentToolProvider.NormalizeBindingContext(context);

    private static void Append(StringBuilder builder, string value)
        => builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append('|');
}
