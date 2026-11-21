using System.Text.Json;

using Kautuka.Hosting;
using Kautuka.Telemetry.Security;

namespace Kautuka.Manager.Hosting;

/// <summary>Authenticates a selected Manager release before its service image map may be used.</summary>
internal sealed record VerifiedApplianceRelease(string ReleaseId, string Version, ApplianceDeploymentDescriptor Deployment)
{
    private const int SupportedHookAbi = 1;

    internal static VerifiedApplianceRelease Verify(TrustedApplianceReleaseInput input)
    {
        if (!ReleaseManifestSignatureVerifier.TryVerify(input.ManifestBytes, input.SignatureContent, input.PublicKeyPem, out string? error))
        {
            throw new InvalidDataException(error ?? "Appliance release signature verification failed.");
        }

        using JsonDocument manifest = JsonDocument.Parse(input.ManifestBytes);
        JsonElement root = manifest.RootElement;
        JsonElement schema = Required(root, "schemaVersion");
        if (schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int versionNumber) || versionNumber != 2)
        {
            throw new InvalidDataException("Unsupported platform release manifest schema.");
        }

        string releaseId = RequiredString(root, "releaseId");
        string version = RequiredString(root, "version");
        string source = RequiredString(root, "sourceSha");
        if (!IsReleaseToken(releaseId) || !IsReleaseToken(version)
            || !string.Equals(releaseId, input.InstalledReleaseId, StringComparison.Ordinal)
            || !string.Equals(version, input.VersionDirectoryName, StringComparison.Ordinal)
            || !string.Equals(RequiredString(root, "releaseTag"), "v" + version, StringComparison.Ordinal)
            || source.Length != 40 || !source.All(char.IsAsciiHexDigit))
        {
            throw new InvalidDataException("Signed appliance release identity does not match the selected Manager release.");
        }

        JsonElement signature = Required(root, "signature");
        if (RequiredString(signature, "algorithm") != "ecdsa-p256-sha256" || RequiredString(signature, "format") != "base64")
        {
            throw new InvalidDataException("Unsupported appliance release signature metadata.");
        }

        ApplianceDeploymentDescriptor deployment = ApplianceDeploymentManifest.Parse(input.ManifestBytes)
            ?? throw new InvalidDataException("The signed release has no appliance deployment.");
        if (deployment.HookAbi != SupportedHookAbi)
        {
            throw new InvalidDataException("The signed appliance deployment requires an unsupported OCI hook ABI.");
        }

        return new(releaseId, version, deployment);
    }

    private static bool IsReleaseToken(string value)
        => value.Length is > 0 and <= 128 && char.IsAsciiLetterOrDigit(value[0])
            && value.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    private static string RequiredString(JsonElement parent, string name)
    {
        JsonElement property = Required(parent, name);
        return property.ValueKind == JsonValueKind.String && property.GetString() is string value
            ? value : throw new InvalidDataException($"Invalid appliance release property '{name}'.");
    }

    private static JsonElement Required(JsonElement parent, string name)
    {
        JsonElement result = default;
        if (parent.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in parent.EnumerateObject())
            {
                if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (result.ValueKind != JsonValueKind.Undefined || property.Name != name)
                    throw new InvalidDataException($"Duplicate or noncanonical appliance release property '{name}'.");
                result = property.Value;
            }
        }
        return result.ValueKind != JsonValueKind.Undefined
            ? result : throw new InvalidDataException($"Missing appliance release property '{name}'.");
    }
}
