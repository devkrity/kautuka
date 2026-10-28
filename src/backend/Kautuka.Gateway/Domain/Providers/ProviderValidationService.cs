using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.Gateway.Domain.Providers;

internal sealed class ProviderValidationService : IProviderValidationService
{
    private readonly IGatewayCatalogStore catalog;
    private readonly KautukaAiClientFactory aiClientFactory;
    private readonly IProviderTypeCatalog providerTypeCatalog;
    private readonly ILogger<ProviderValidationService> logger;

    public ProviderValidationService(
        IGatewayCatalogStore catalog,
        KautukaAiClientFactory aiClientFactory,
        IProviderTypeCatalog providerTypeCatalog,
        ILogger<ProviderValidationService> logger)
    {
        this.catalog = catalog;
        this.aiClientFactory = aiClientFactory;
        this.providerTypeCatalog = providerTypeCatalog;
        this.logger = logger;
    }

    public async Task<GatewayProviderTestResult> TestProviderAsync(JsonElement body, CancellationToken cancellationToken = default)
    {
        if (!TryResolveProviderRequest(body, out string providerId, out string providerType, out JsonObject settings, out string errorMessage))
        {
            return new GatewayProviderTestResult(false, errorMessage);
        }

        try
        {
            return await ValidateProviderConnectivityAsync(providerId, providerType, settings, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            ProviderValidationServiceLog.ProviderTestFailed(logger, ex, providerId, providerType);
            return new GatewayProviderTestResult(false, $"Provider test failed for '{providerId}': {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            ProviderValidationServiceLog.ProviderTestFailed(logger, ex, providerId, providerType);
            return new GatewayProviderTestResult(false, $"Provider test failed for '{providerId}': {ex.Message}");
        }
        catch (JsonException ex)
        {
            ProviderValidationServiceLog.ProviderTestFailed(logger, ex, providerId, providerType);
            return new GatewayProviderTestResult(false, $"Provider test failed for '{providerId}': {ex.Message}");
        }
        catch (IOException ex)
        {
            ProviderValidationServiceLog.ProviderTestFailed(logger, ex, providerId, providerType);
            return new GatewayProviderTestResult(false, $"Provider test failed for '{providerId}': {ex.Message}");
        }
    }

    private bool TryResolveProviderRequest(
        JsonElement body,
        out string providerId,
        out string providerType,
        out JsonObject settings,
        out string errorMessage)
    {
        providerId = body.GetStringProperty("provider_id", string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(providerId))
        {
            providerId = body.GetStringProperty("id", string.Empty).Trim();
        }

        GatewayProvider? existingProvider = null;
        if (!string.IsNullOrWhiteSpace(providerId) && catalog.TryGetProvider(providerId, out GatewayProvider? resolved))
        {
            existingProvider = resolved;
        }

        providerType = ProviderTypeCatalogSettings.NormalizeType(body.GetStringProperty("type", existingProvider?.Type ?? string.Empty));
        settings = existingProvider is null
            ? new JsonObject()
            : JsonObject.Create(JsonSerializer.SerializeToElement(existingProvider.Settings, KautukaJsonContext.Default.JsonObject)) ?? new JsonObject();

        if (body.TryGetProperty("settings", out JsonElement settingsElement) && settingsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty prop in settingsElement.EnumerateObject())
            {
                settings[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
            }
        }

        if (existingProvider is not null)
        {
            settings = GatewayState.PreserveSecretSetting(settings, existingProvider.Settings, "api_key");
        }
        else
        {
            settings = ProviderTypeCatalogSettings.ApplyCreateDefaults(settings, providerType, providerTypeCatalog);
        }

        providerId = string.IsNullOrWhiteSpace(providerId) ? providerType : providerId;

        if (string.IsNullOrWhiteSpace(providerType))
        {
            errorMessage = "Provider type is required.";
            return false;
        }

        if (!ProviderTypeCatalogSettings.TryValidate(settings, providerType, providerTypeCatalog, out errorMessage))
        {
            return false;
        }

        errorMessage = string.Empty;
        return true;
    }

    private async Task<GatewayProviderTestResult> ValidateProviderConnectivityAsync(
        string providerId,
        string providerType,
        JsonObject settings,
        CancellationToken ct)
    {
        try
        {
            IReadOnlyList<DiscoveredModelDefinition> discoveredModels = await aiClientFactory
                .DiscoverModelsAsync(providerId, providerType, settings, throwOnFailure: true, ct)
                .ConfigureAwait(false);

            return discoveredModels.Count > 0
                ? new GatewayProviderTestResult(true, $"Provider '{providerId}' connected successfully and model discovery returned {discoveredModels.Count} model(s).")
                : new GatewayProviderTestResult(true, $"Provider '{providerId}' connected successfully and model discovery returned 0 models.");
        }
        catch (HttpRequestException ex)
        {
            ProviderValidationServiceLog.GenericConnectivityCheckFailed(logger, ex, providerId);
            return new GatewayProviderTestResult(false, $"Provider '{providerId}' connectivity check failed: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            ProviderValidationServiceLog.GenericConnectivityCheckFailed(logger, ex, providerId);
            return new GatewayProviderTestResult(false, $"Provider '{providerId}' connectivity check failed: {ex.Message}");
        }
        catch (JsonException ex)
        {
            ProviderValidationServiceLog.GenericConnectivityCheckFailed(logger, ex, providerId);
            return new GatewayProviderTestResult(false, $"Provider '{providerId}' connectivity check failed: {ex.Message}");
        }
        catch (IOException ex)
        {
            ProviderValidationServiceLog.GenericConnectivityCheckFailed(logger, ex, providerId);
            return new GatewayProviderTestResult(false, $"Provider '{providerId}' connectivity check failed: {ex.Message}");
        }
        catch (System.ClientModel.ClientResultException ex)
        {
            ProviderValidationServiceLog.GenericConnectivityCheckFailed(logger, ex, providerId);
            return new GatewayProviderTestResult(false, $"Provider '{providerId}' returned an error: {ex.Message}");
        }
    }
}

internal static partial class ProviderValidationServiceLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Provider test failed for {ProviderId} ({ProviderType})")]
    public static partial void ProviderTestFailed(ILogger logger, Exception exception, string providerId, string providerType);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Generic provider connectivity check failed for {ProviderId}")]
    public static partial void GenericConnectivityCheckFailed(ILogger logger, Exception exception, string providerId);
}
