using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Providers;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Infrastructure.Credentials;

namespace Kautuka.Gateway.Endpoints.Settings;

internal static partial class GatewaySettingsEndpointMappings
{

    private static GatewayProviderListItem ToProviderListItem(GatewayProvider provider, bool hasApiKey)
        => new(provider.Id, provider.Type, provider.Settings, provider.CreatedAt, hasApiKey);

    private static void MapProviderSettingsEndpoints(WebApplication app)
    {
        app.MapGet("/api/settings/provider-types", (KautukaAiClientFactory aiClientFactory, IGatewayAuthGuard auth, HttpContext context) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out _);
            if (unauthorized is not null)
            {
                return unauthorized;
            }

            GatewayProviderTypeItem[] items = [.. aiClientFactory.ListProviderTypes()
                .Select(t => new GatewayProviderTypeItem(
                    t.Type,
                    t.FriendlyName,
                    t.RequiresApiKey,
                    t.RequiresBaseUrl,
                    t.SupportsHttpTimeoutSeconds))];
            return Results.Json(new GatewayProviderTypesResponse(items), KautukaJsonContext.Default.GatewayProviderTypesResponse);
        });

        app.MapGet("/api/settings/providers", async (
            IGatewayCatalogStore catalog,
            CredentialResolverService credentialResolver,
            IProviderTypeCatalog providerTypeCatalog,
            IGatewayAuthGuard auth,
            HttpContext context,
            CancellationToken ct) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out _);
            if (unauthorized is not null)
            {
                return unauthorized;
            }

            IReadOnlyList<GatewayProvider> providers = catalog.ListProviders();
            bool anyProviderRequiresApiKey = providers.Any(provider =>
                providerTypeCatalog.TryGet(provider.Type, out AiProviderCatalog.ProviderMetadata? metadata)
                && metadata?.RequiresApiKey is true);
            IReadOnlyList<string> systemKeys = anyProviderRequiresApiKey
                ? await credentialResolver.ListSystemKeysAsync(ct).ConfigureAwait(false)
                : [];
            var keySet = new HashSet<string>(systemKeys, StringComparer.Ordinal);

            return Results.Json(
                new GatewayProvidersResponse([.. providers
                    .Select(p => ToProviderListItem(p, keySet.Contains($"provider.{p.Id}.api_key")))]),
                KautukaJsonContext.Default.GatewayProvidersResponse);
        });

        app.MapPost("/api/settings/providers/test", async (JsonElement body, IProviderValidationService providerValidationService, IGatewayAuthGuard auth, HttpContext context, CancellationToken ct) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out _);
            if (unauthorized is not null)
            {
                return unauthorized;
            }

            GatewayProviderTestResult result = await providerValidationService.TestProviderAsync(body, ct).ConfigureAwait(false);
            return Results.Json(
                new GatewayProviderTestResponse(result.Success, result.Message),
                KautukaJsonContext.Default.GatewayProviderTestResponse,
                statusCode: result.Success ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest);
        })
        .Produces<GatewayProviderTestResponse>(StatusCodes.Status200OK)
        .Produces<GatewayProviderTestResponse>(StatusCodes.Status400BadRequest);

        app.MapPost("/api/settings/providers/save", async (
            HttpContext context,
            IGatewayCatalogStore catalog,
            CredentialResolverService credentialResolver,
            IProviderTypeCatalog providerTypeCatalog,
            IGatewayAuthGuard auth,
            CancellationToken ct) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out _);
            if (unauthorized is not null)
            {
                return unauthorized;
            }

            JsonElement bodyElement = await context.Request.ReadFromJsonAsync(
                KautukaJsonContext.Default.JsonElement, ct).ConfigureAwait(false);

            // Extract api_key before stripping — it must not be stored in the catalog.
            string? apiKey = null;
            if (bodyElement.TryGetProperty("settings", out JsonElement settingsEl)
                && settingsEl.TryGetProperty("api_key", out JsonElement apiKeyEl))
            {
                apiKey = apiKeyEl.GetString();
            }

            string providerId = bodyElement.GetStringProperty("id", string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(apiKey)
                && !string.IsNullOrWhiteSpace(providerId)
                && catalog.TryGetProvider(providerId, out GatewayProvider? existingWithKey)
                && existingWithKey is not null)
            {
                apiKey = AiProviderSettings.GetString(existingWithKey.Settings, "api_key");
            }

            string providerType = ProviderTypeCatalogSettings.NormalizeType(
                bodyElement.GetStringProperty("type", string.Empty));
            bool requiresApiKey = providerTypeCatalog.TryGet(
                providerType,
                out AiProviderCatalog.ProviderMetadata? providerMetadata)
                && providerMetadata?.RequiresApiKey is true;
            bool hasApiKey = !string.IsNullOrWhiteSpace(apiKey);

            if (requiresApiKey && !hasApiKey)
            {
                if (string.IsNullOrWhiteSpace(providerId)
                    || !catalog.TryGetProvider(providerId, out GatewayProvider? existingProvider)
                    || existingProvider is null)
                {
                    return Results.Json(
                        new GatewayErrorResponse($"Provider type '{providerType}' requires an API key."),
                        KautukaJsonContext.Default.GatewayErrorResponse,
                        statusCode: StatusCodes.Status400BadRequest);
                }

                IReadOnlyList<string> existingKeys = await credentialResolver.ListSystemKeysAsync(ct).ConfigureAwait(false);
                hasApiKey = existingKeys.Contains($"provider.{providerId}.api_key", StringComparer.Ordinal);
                if (!hasApiKey)
                {
                    return Results.Json(
                        new GatewayErrorResponse($"Provider type '{providerType}' requires an API key."),
                        KautukaJsonContext.Default.GatewayErrorResponse,
                        statusCode: StatusCodes.Status400BadRequest);
                }
            }

            if (!string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(providerId))
            {
                await credentialResolver.SetSystemAsync(
                    new Dictionary<string, string> { [$"provider.{providerId}.api_key"] = apiKey },
                    ct).ConfigureAwait(false);
            }

            GatewayProvider saved;
            try
            {
                saved = catalog.SaveProvider(StripApiKeyFromBody(bodyElement));
            }
            catch (ProviderConfigurationException ex)
            {
                return Results.Json(
                    new GatewayErrorResponse(ex.Message),
                    KautukaJsonContext.Default.GatewayErrorResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (!string.IsNullOrWhiteSpace(apiKey)
                && !string.Equals(saved.Id, providerId, StringComparison.Ordinal))
            {
                await credentialResolver.SetSystemAsync(
                    new Dictionary<string, string> { [$"provider.{saved.Id}.api_key"] = apiKey },
                    ct).ConfigureAwait(false);
            }

            return Results.Json(
                new GatewayProviderResponse(ToProviderListItem(saved, hasApiKey)),
                KautukaJsonContext.Default.GatewayProviderResponse);
        });

        app.MapDelete("/api/settings/providers/{providerId}", (string providerId, IGatewayCatalogStore catalog, IGatewayAuthGuard auth, HttpContext context) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out _);
            if (unauthorized is not null)
            {
                return unauthorized;
            }

            return catalog.DeleteProvider(providerId)
                ? Results.Json(new GatewayBooleanSuccessResponse(true), KautukaJsonContext.Default.GatewayBooleanSuccessResponse)
                : Results.Json(new GatewayErrorResponse("Provider not found"), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
        });
    }

    private static JsonElement StripApiKeyFromBody(JsonElement body)
    {
        JsonNode? node = JsonNode.Parse(body.GetRawText());
        if (node is JsonObject obj && obj["settings"] is JsonObject settings)
        {
            settings.Remove("api_key");
        }
        using JsonDocument doc = JsonDocument.Parse((node ?? JsonNode.Parse("null"))!.ToJsonString());
        return doc.RootElement.Clone();
    }
}
