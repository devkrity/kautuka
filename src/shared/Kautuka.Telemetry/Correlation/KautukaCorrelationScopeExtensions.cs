using Microsoft.Extensions.Logging;

namespace Kautuka.Telemetry.Correlation;

public static class KautukaCorrelationScopeExtensions
{
    public const string CorrelationHeaderName = "X-Correlation-ID";

    public static IDisposable BeginKautukaCorrelationScope(this ILogger logger, params (string Key, object? Value)[] properties)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(properties);

        Dictionary<string, object?> scopeValues = [];
        foreach ((string key, object? value) in properties)
        {
            if (string.IsNullOrWhiteSpace(key) || value is null)
            {
                continue;
            }

            scopeValues[key] = value;
        }

        if (scopeValues.Count == 0)
        {
            return NoOpScope.Instance;
        }

        return logger.BeginScope(scopeValues) ?? NoOpScope.Instance;
    }

    private sealed class NoOpScope : IDisposable
    {
        public static readonly NoOpScope Instance = new();

        public void Dispose()
        {
        }
    }
}
