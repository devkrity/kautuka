using Grpc.Core;

namespace Kautuka.Telemetry.Correlation;

public static class KautukaGrpcCorrelationMetadata
{
    public const string CorrelationHeaderName = "x-correlation-id";

    public static Metadata CreateClientHeaders()
    {
        Metadata headers = [];
        headers.Add(CorrelationHeaderName, Guid.NewGuid().ToString("N"));
        return headers;
    }

    public static string? GetCorrelationId(Metadata? headers)
    {
        if (headers is null)
        {
            return null;
        }

        Metadata.Entry? entry = headers.FirstOrDefault(static item =>
            string.Equals(item.Key, CorrelationHeaderName, StringComparison.OrdinalIgnoreCase));

        return string.IsNullOrWhiteSpace(entry?.Value) ? null : entry.Value;
    }
}
