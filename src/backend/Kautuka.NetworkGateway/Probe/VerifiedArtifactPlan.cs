using System.Text.Json;
using System.Text.Json.Serialization;

using Kautuka.Telemetry.Security;

namespace Kautuka.NetworkGateway.Probe;

/// <summary>Fixture-only signed acquisition plan. Not a production release/update format.</summary>
internal sealed record VerifiedArtifactPlan(string ReleaseId, string ManifestDigest, string ConfigurationDigest)
{
    internal const string Registry = "10.231.252.2:20443";
    internal const string Repository = "ktng/probe";
    public string ImageReference => $"{Registry}/{Repository}@{ManifestDigest}";

    public static VerifiedArtifactPlan Verify(byte[] manifest, string signature, string trustedPublicKey)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(trustedPublicKey);
        if (manifest.Length > 16384 || signature.Length > 4096 || trustedPublicKey.Length > 4096
            || !ReleaseManifestSignatureVerifier.TryVerify(manifest, signature, trustedPublicKey, out _))
        {
            throw new InvalidDataException("Fixture release signature was not accepted.");
        }

        // Authenticate exact bytes first, then reject ambiguous/expanded interpretation.
        using JsonDocument document = JsonDocument.Parse(manifest, new JsonDocumentOptions { MaxDepth = 4 });
        JsonElement root = document.RootElement;
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Fixture release must be an object.");
        }
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!fields.TryAdd(property.Name, property.Value))
            {
                throw new InvalidDataException("Duplicate fixture release field.");
            }
        }
        if (fields.Count != 8 || !fields.TryGetValue("schemaVersion", out JsonElement schema)
            || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int version) || version != 1
            || Text("registry") != Registry || Text("repository") != Repository
            || Text("platform") != "linux/amd64" || Text("compatibility") != "ktng-probe-v1")
        {
            throw new InvalidDataException("Unsupported fixture release scope or compatibility.");
        }
        string releaseId = Text("releaseId");
        string digest = Text("manifestDigest");
        string configuration = Text("configurationDigest");
        if (!releaseId.StartsWith("ktng-", StringComparison.Ordinal) || releaseId.Length != 37
            || !IsHex(releaseId.AsSpan(5)) || !IsDigest(digest) || !IsDigest(configuration))
        {
            throw new InvalidDataException("Invalid immutable fixture release identity.");
        }
        return new VerifiedArtifactPlan(releaseId, digest, configuration);

        string Text(string name)
        {
            if (!fields.TryGetValue(name, out JsonElement field) || field.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("Missing or invalid fixture release field.");
            }
            return field.GetString()!;
        }
    }

    private static bool IsDigest(string value)
        => value.Length == 71 && value.StartsWith("sha256:", StringComparison.Ordinal) && IsHex(value.AsSpan(7));

    private static bool IsHex(ReadOnlySpan<char> value)
    {
        foreach (char character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }
        return true;
    }
}

[JsonSerializable(typeof(VerifiedArtifactPlan))]
internal sealed partial class VerifiedArtifactPlanJsonContext : JsonSerializerContext;
