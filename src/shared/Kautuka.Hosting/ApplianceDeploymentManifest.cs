using System.Collections.Frozen;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kautuka.Hosting;

public sealed record ApplianceDeploymentDescriptor(int SchemaVersion, int HookAbi, IReadOnlyDictionary<string, string> Images);

/// <summary>Strict parser for the signed appliance deployment portion of a platform manifest.</summary>
public static partial class ApplianceDeploymentManifest
{
    private static readonly string[] RequiredRoles = ["credentialshost", "channelhost", "gateway", "webhost", "updateservice"];



    public static ApplianceDeploymentDescriptor? Parse(ReadOnlyMemory<byte> manifest)
    {
        using JsonDocument document = JsonDocument.Parse(manifest);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Platform manifest must be an object.");
        JsonElement? section = null;
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (!property.Name.Equals("applianceDeployment", StringComparison.OrdinalIgnoreCase)) continue;
            if (section.HasValue || property.Name != "applianceDeployment") throw new InvalidDataException("Duplicate or noncanonical applianceDeployment property.");
            section = property.Value;
        }
        if (!section.HasValue) return null;
        JsonElement deployment = section.Value;
        if (deployment.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid appliance deployment.");
        JsonElement schema = default, abi = default, services = default;
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in deployment.EnumerateObject())
        {
            if (!fields.Add(property.Name)) throw new InvalidDataException("Duplicate appliance deployment property.");
            switch (property.Name)
            {
                case "schemaVersion": schema = property.Value; break;
                case "hookAbi": abi = property.Value; break;
                case "services": services = property.Value; break;
                default: throw new InvalidDataException("Unknown appliance deployment property.");
            }
        }
        if (schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int version) || version != 1 ||
            abi.ValueKind != JsonValueKind.Number || !abi.TryGetInt32(out int hookAbi) || hookAbi <= 0 ||
            services.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Invalid appliance deployment schema, hook ABI or services.");
        var images = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty service in services.EnumerateObject())
        {
            if (!RequiredRoles.Contains(service.Name, StringComparer.Ordinal) || service.Value.ValueKind != JsonValueKind.Object || images.ContainsKey(service.Name))
                throw new InvalidDataException("Unknown, duplicate or invalid appliance service.");
            string? image = null;
            foreach (JsonProperty field in service.Value.EnumerateObject())
            {
                if (field.Name != "image" || image is not null || field.Value.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("Unknown, duplicate or invalid appliance service property.");
                image = field.Value.GetString();
            }
            if (image is null || !IsPinnedImage(image)) throw new InvalidDataException("Invalid appliance image reference.");
            images.Add(service.Name, image);
        }
        if (images.Count != RequiredRoles.Length) throw new InvalidDataException("Missing appliance service.");
        return new(version, hookAbi, images.ToFrozenDictionary(StringComparer.Ordinal));
    }

    private static bool IsPinnedImage(string image)
    {
        if (image.Length > 512) return false;
        Match match = ImageReference().Match(image);
        if (!match.Success) return false;
        string port = match.Groups["port"].Value;
        return port.Length == 0 || (int.TryParse(port, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int number) && number is >= 1 and <= 65535);
    }

    [GeneratedRegex(@"\A(?:localhost|[a-z0-9](?:[a-z0-9-]*[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]*[a-z0-9])?)+)(?::(?<port>[1-9][0-9]{0,4}))?/[a-z0-9]+(?:(?:[.]|__?|[-]+)[a-z0-9]+)*(?:/[a-z0-9]+(?:(?:[.]|__?|[-]+)[a-z0-9]+)*)*@sha256:[0-9a-f]{64}\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ImageReference();
}
