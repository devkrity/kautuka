// File: /home/ash/sources/kautuka/src/tui/Kautuka.Tui/Features/Services/ServiceUnitDefinition.cs | Namespace: Kautuka.Tui.Features.Services
using Kautuka.Tui.Common;

namespace Kautuka.Tui.Features.Services;

public sealed record ServiceUnitDefinition(string Key, string UnitName, string DisplayName);

public static class SystemServiceCatalog
{
    private static readonly ServiceUnitDefinition[] definitions =
    [
        new("manager", "kautuka-manager.service", "Manager"),
        new("gateway", "kautuka-gateway.service", "Gateway"),
        new("credentialshost", "kautuka-credentialshost.service", "Credentials Host"),
        new("channelhost", "kautuka-channelhost.service", "Channel Host"),
        new("webhost", "kautuka-webhost.service", "Web Host"),
    ];

    public static IReadOnlyList<ServiceUnitDefinition> All => definitions;

    public static Result<ServiceUnitDefinition> Resolve(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        ServiceUnitDefinition? match = definitions.FirstOrDefault(definition =>
            definition.Key.Equals(value, StringComparison.OrdinalIgnoreCase)
            || definition.UnitName.Equals(value, StringComparison.OrdinalIgnoreCase));

        return match is null
            ? Results.Failure<ServiceUnitDefinition>($"Unknown service '{value}'.")
            : Results.Success(match);
    }
}
