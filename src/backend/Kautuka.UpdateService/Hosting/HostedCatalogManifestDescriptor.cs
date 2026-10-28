using System.Text.Json.Serialization;

namespace Kautuka.UpdateService.Hosting;

internal sealed class HostedCatalogManifestDescriptor
{
    public int SchemaVersion { get; init; }

    public string? GeneratedAtUtc { get; init; }

    public PlatformReleaseSignatureDescriptor? Signature { get; init; }

    public IReadOnlyList<HostedCatalogManifestEntryDescriptor> Entries { get; init; } = [];
}

internal sealed record HostedCatalogManifestEntryDescriptor(
    string Id,
    string Kind,
    string Title,
    string Summary,
    string Publisher,
    string Version,
    string? ArtifactUrl,
    string? ArtifactPath,
    string? ArtifactSha256,
    string? TrustKind,
    bool Recommended);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(HostedCatalogManifestDescriptor))]
internal sealed partial class HostedCatalogManifestDescriptorJsonContext : JsonSerializerContext
{
}
