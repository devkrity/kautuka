using System.Text.Json.Serialization;

namespace Kautuka.UpdateService.Hosting;

internal sealed class PlatformReleaseDescriptor
{
    public int SchemaVersion { get; init; }

    public string? ReleaseId { get; init; }

    public string? ReleaseTag { get; init; }

    public string? Channel { get; init; }

    public string? Version { get; init; }

    public string? PublishedAt { get; init; }

    public string? MinimumUpdaterVersion { get; init; }

    public string? PlatformAssetName { get; init; }

    public string? GeneratedAtUtc { get; init; }

    public PlatformReleaseSignatureDescriptor? Signature { get; init; }

    public IReadOnlyList<PlatformReleaseArtifactDescriptor> Artifacts { get; init; } = [];

    public IReadOnlyList<PlatformReleaseRuntimeImageDescriptor> RuntimeImages { get; init; } = [];
}

internal sealed record PlatformReleaseArtifactDescriptor(
    string Name,
    string Platform,
    string? Path,
    string? Url,
    string Sha256,
    long Size);

internal sealed record PlatformReleaseRuntimeImageDescriptor(
    string Id,
    string Image,
    string? Archive);

internal sealed record PlatformReleaseSignatureDescriptor(
    string Algorithm,
    string Path,
    string? Url,
    string? Format);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PlatformReleaseDescriptor))]
[JsonSerializable(typeof(PlatformReleaseRuntimeImageDescriptor))]
internal sealed partial class PlatformReleaseDescriptorJsonContext : JsonSerializerContext
{
}
