using System.Text.Json;

namespace Kautuka.Gateway.Infrastructure.Updates;

internal sealed class UpdateOperationHistoryReader(IConfiguration configuration)
{
    private readonly string historyFilePath = Path.Combine(
        Path.GetFullPath(KautukaHostPathPolicy.ResolveServiceStateRoot(configuration, "updateService")),
        "update-operations.json");

    public async ValueTask<GatewayRuntimeUpdateOperationPayload[]> GetHistoryAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(historyFilePath))
        {
            return [];
        }

        try
        {
            using FileStream stream = File.OpenRead(historyFilePath);
            GatewayRuntimeUpdateOperationPayload[]? records = await JsonSerializer.DeserializeAsync(
                stream,
                KautukaJsonContext.Default.GatewayRuntimeUpdateOperationPayloadArray,
                cancellationToken).ConfigureAwait(false);
            return records ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
