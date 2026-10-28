using System.Text.Json;

namespace Kautuka.Gateway.Infrastructure.Appliance;

internal sealed class SidecarStagingStatusReader(IConfiguration configuration)
{
    private readonly string stampPath = Path.Combine(
        Path.GetFullPath(KautukaHostPathPolicy.ResolveSharedStateRoot(configuration)),
        "sidecars",
        "last-staged.json");

    public async ValueTask<(DateTimeOffset? At, int? SidecarCount)> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(stampPath))
        {
            return (null, null);
        }

        try
        {
            using FileStream stream = File.OpenRead(stampPath);
            SidecarStagingStampPayload? stamp = await JsonSerializer.DeserializeAsync(
                stream,
                SidecarStagingStampPayloadJsonContext.Default.SidecarStagingStampPayload,
                cancellationToken).ConfigureAwait(false);

            return stamp is null ? (null, null) : (stamp.At, stamp.SidecarCount);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return (null, null);
        }
    }
}

internal sealed record SidecarStagingStampPayload(DateTimeOffset At, int SidecarCount);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(SidecarStagingStampPayload))]
internal sealed partial class SidecarStagingStampPayloadJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
