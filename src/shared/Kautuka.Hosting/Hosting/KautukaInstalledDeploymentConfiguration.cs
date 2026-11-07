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
        // Exists/optional providers also report inaccessible files as absent.
        // Only genuine absence may select the legacy deployment defaults.
        byte[]? contractBytes = ReadOptional(contractPath);
        byte[]? settingsBytes = ReadOptional(settingsPath);
        if (settingsBytes is not null)
        {
            using var stream = new MemoryStream(settingsBytes, writable: false);
            configuration.AddJsonStream(stream);
        }
        if (contractBytes is null) return;
        using JsonDocument contract = JsonDocument.Parse(contractBytes);
        if (!contract.RootElement.TryGetProperty("applicationServices", out JsonElement mode)) return;
        if (mode.ValueKind != JsonValueKind.String || mode.GetString() is not ("host" or "containers"))
            throw new InvalidOperationException("Unsupported installed application service layout.");
        if (mode.GetString() != "containers") return;
        if (settingsBytes is null)
            throw new InvalidOperationException("The installed container deployment requires its persistent settings file.");
        using JsonDocument settings = JsonDocument.Parse(settingsBytes);
        if (!Enabled(settings.RootElement, "CoreServices") || !Enabled(settings.RootElement, "ContainerEgress"))
            throw new InvalidOperationException("The installed container deployment requires enabled core and egress settings.");
    }

    private static byte[]? ReadOptional(string path)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            // Preserve File.ReadAllText's acceptance of Windows UTF-8 files.
            return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
                ? bytes.AsSpan(3).ToArray() : bytes;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static bool Enabled(JsonElement root, string section)
        => root.TryGetProperty(section, out JsonElement value) && value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("Enabled", out JsonElement enabled) && enabled.ValueKind == JsonValueKind.True;
}
