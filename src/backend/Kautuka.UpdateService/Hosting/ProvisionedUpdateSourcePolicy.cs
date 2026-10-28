using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kautuka.UpdateService.Hosting;

public sealed record ProvisionedUpdateSourceDocument(
    int SchemaVersion,
    string? ManifestUrl,
    string? PreviewManifestUrl,
    string? StableManifestUrl,
    string? CatalogManifestUrl);

public static class ProvisionedUpdateSourcePolicy
{
    public const string DefaultSourceDocumentPath = "/etc/kautuka/update-source.json";
    public const string DefaultManifestPublicKeyPath = "/etc/kautuka/manifest-public-key.pem";

    public static ProvisionedUpdateSourceDocument? Resolve(string? sourceDocumentPath = null)
    {
        string resolvedSourceDocumentPath = string.IsNullOrWhiteSpace(sourceDocumentPath)
            ? DefaultSourceDocumentPath
            : sourceDocumentPath;

        if (!File.Exists(resolvedSourceDocumentPath))
        {
            return null;
        }

        using FileStream stream = File.OpenRead(resolvedSourceDocumentPath);
        ProvisionedUpdateSourceDocument? document = JsonSerializer.Deserialize(
            stream,
            ProvisionedUpdateSourcePolicyJsonContext.Default.ProvisionedUpdateSourceDocument);

        if (document is null)
        {
            throw new InvalidOperationException($"Provisioned update source document was empty: {resolvedSourceDocumentPath}");
        }

        if (document.SchemaVersion != 1)
        {
            throw new InvalidOperationException(
                $"Provisioned update source schema '{document.SchemaVersion}' is not supported at {resolvedSourceDocumentPath}.");
        }

        if (string.IsNullOrWhiteSpace(document.ManifestUrl)
            && string.IsNullOrWhiteSpace(document.PreviewManifestUrl)
            && string.IsNullOrWhiteSpace(document.StableManifestUrl)
            && string.IsNullOrWhiteSpace(document.CatalogManifestUrl))
        {
            return null;
        }

        return document;
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ProvisionedUpdateSourceDocument))]
internal partial class ProvisionedUpdateSourcePolicyJsonContext : JsonSerializerContext
{
}
