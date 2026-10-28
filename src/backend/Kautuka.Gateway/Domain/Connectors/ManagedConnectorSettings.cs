namespace Kautuka.Gateway.Domain.Connectors;

/// <summary>
/// Launch settings for a Kautuka sign-in connector come from its descriptor.
/// The owner can turn the connector on or off. Sign-in is separate.
/// </summary>
internal static class ManagedConnectorSettings
{
    public static GatewayConnectorUpsertRequest Registration(ConnectorAuthProfile profile, GatewayMcpServer? existing)
    {
        bool bundled = existing is not null && !string.IsNullOrWhiteSpace(existing.BundlePath);
        return new GatewayConnectorUpsertRequest
        {
            Name = profile.Name,
            Description = profile.Description,
            PresetId = profile.Id,
            Transport = bundled ? existing!.Transport : "container",
            ContainerImage = bundled ? existing!.ContainerImage : null,
            ContainerCommand = bundled && !string.IsNullOrWhiteSpace(existing!.ContainerCommand)
                ? existing.ContainerCommand
                : profile.EntryPoint,
            ContainerArgs = bundled ? [.. existing!.ContainerArgs] : [],
            ContainerSidecarName = bundled ? existing!.ContainerSidecarName : null,
            BundlePath = bundled ? existing!.BundlePath : null,
            ServerType = bundled && !string.IsNullOrWhiteSpace(existing!.ServerType) ? existing.ServerType : "binary",
            ReadinessTimeoutSeconds = bundled ? existing!.ReadinessTimeoutSeconds : null,
            JitCooldownSeconds = bundled ? existing!.JitCooldownSeconds : null,
            HealthcheckUrl = bundled ? existing!.HealthcheckUrl : null,
            HealthcheckIntervalMilliseconds = bundled ? existing!.HealthcheckIntervalMilliseconds : null,
            McpProtocolVersion = bundled ? existing!.McpProtocolVersion : null,
            EnvKeys = [profile.RefreshCredentialKey],
            Enabled = existing?.Enabled ?? true,
            ToolNames = profile.ToolNames,
            ToolCount = profile.ToolNames.Length,
        };
    }

    public static GatewayConnectorUpsertRequest WithEnabled(GatewayMcpServer existing, bool? enabled)
        => new()
        {
            Name = existing.Name,
            Description = existing.Description,
            IconUrl = existing.IconUrl,
            PresetId = existing.PresetId,
            Transport = existing.Transport,
            HttpUrl = existing.HttpUrl,
            ContainerImage = existing.ContainerImage,
            ContainerCommand = existing.ContainerCommand,
            ContainerArgs = existing.ContainerArgs,
            ContainerSidecarName = existing.ContainerSidecarName,
            BundlePath = existing.BundlePath,
            ServerType = existing.ServerType,
            RuntimeArtifactType = existing.RuntimeArtifactType,
            RuntimeArtifactSourceDigest = existing.RuntimeArtifactSourceDigest,
            RuntimeArtifactBuildStatus = existing.RuntimeArtifactBuildStatus,
            ReadinessTimeoutSeconds = existing.ReadinessTimeoutSeconds,
            JitCooldownSeconds = existing.JitCooldownSeconds,
            HealthcheckUrl = existing.HealthcheckUrl,
            HealthcheckIntervalMilliseconds = existing.HealthcheckIntervalMilliseconds,
            McpProtocolVersion = existing.McpProtocolVersion,
            EnvKeys = existing.EnvKeys is null ? null : [.. existing.EnvKeys],
            Enabled = enabled ?? existing.Enabled,
            ToolCount = existing.ToolCount,
            ToolNames = existing.ToolNames is null ? null : [.. existing.ToolNames],
        };
}
