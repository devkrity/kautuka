using System.Text.Json;

using Microsoft.Extensions.Configuration;

namespace Kautuka.Hosting.Hosting;

/// <summary>Release-independent deployment settings shared by host startup and maintenance paths.</summary>
public static class KautukaInstalledDeploymentConfiguration
{
    public static void Add(ConfigurationManager configuration,
        string settingsPath = "/etc/kautuka/manager.json",
        string contractPath = "/etc/kautuka/service-scope.json")
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.AddJsonFile(settingsPath, optional: true, reloadOnChange: false);
        if (!File.Exists(contractPath)) return;
        using JsonDocument contract = JsonDocument.Parse(File.ReadAllText(contractPath));
        if (!contract.RootElement.TryGetProperty("applicationServices", out JsonElement mode)) return;
        if (mode.ValueKind != JsonValueKind.String || mode.GetString() is not ("host" or "containers"))
            throw new InvalidOperationException("Unsupported installed application service layout.");
        if (mode.GetString() != "containers") return;
        if (!File.Exists(settingsPath))
            throw new InvalidOperationException("The installed container deployment requires its persistent settings file.");
        using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(settingsPath));
        if (!Enabled(settings.RootElement, "CoreServices") || !Enabled(settings.RootElement, "ContainerEgress"))
            throw new InvalidOperationException("The installed container deployment requires enabled core and egress settings.");
    }

    private static bool Enabled(JsonElement root, string section)
        => root.TryGetProperty(section, out JsonElement value) && value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("Enabled", out JsonElement enabled) && enabled.ValueKind == JsonValueKind.True;
}
