using System.Text.Json.Nodes;

namespace Kautuka.Gateway.Infrastructure.AI;

internal sealed class ProviderConfigurationException : Exception
{
    public ProviderConfigurationException()
    {
    }

    public ProviderConfigurationException(string message)
        : base(message)
    {
    }

    public ProviderConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal static class ProviderTypeCatalogSettings
{
    private const int maxHttpTimeoutSeconds = 3600;

    public static string NormalizeType(string providerType)
        => new(providerType.Trim().Select(char.ToLowerInvariant).ToArray());

    public static JsonObject ApplyCreateDefaults(JsonObject settings, string providerType, IProviderTypeCatalog? catalog)
    {
        if (catalog is null || !catalog.TryGet(providerType, out AiProviderCatalog.ProviderMetadata? metadata) || metadata is null)
        {
            return settings;
        }

        if (string.IsNullOrWhiteSpace(AiProviderSettings.GetString(settings, "base_url"))
            && !string.IsNullOrWhiteSpace(metadata.DefaultBaseUrl))
        {
            settings["base_url"] = metadata.DefaultBaseUrl;
        }

        if (settings["http_timeout_seconds"] is null && metadata.DefaultHttpTimeoutSeconds is int defaultTimeoutSeconds)
        {
            settings["http_timeout_seconds"] = defaultTimeoutSeconds;
        }

        return settings;
    }

    public static bool TryValidate(
        JsonObject settings,
        string providerType,
        IProviderTypeCatalog? catalog,
        out string error)
    {
        if (string.IsNullOrWhiteSpace(providerType))
        {
            error = "Provider type is required.";
            return false;
        }

        if (catalog is null || !catalog.TryGet(providerType, out AiProviderCatalog.ProviderMetadata? metadata) || metadata is null)
        {
            error = $"Provider type '{providerType}' is not supported.";
            return false;
        }

        string? baseUrl = AiProviderSettings.GetString(settings, "base_url");
        if (metadata.RequiresBaseUrl && string.IsNullOrWhiteSpace(baseUrl))
        {
            error = $"Provider type '{providerType}' requires a base URL.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(baseUrl)
            && (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        {
            error = "Provider base URL must be an absolute HTTP or HTTPS URL.";
            return false;
        }

        if (settings["http_timeout_seconds"] is JsonNode timeoutNode)
        {
            if (!int.TryParse(timeoutNode.ToString(), out int timeoutSeconds)
                || timeoutSeconds is < 1 or > maxHttpTimeoutSeconds)
            {
                error = $"Provider HTTP timeout must be between 1 and {maxHttpTimeoutSeconds} seconds.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }
}
