using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Providers;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Infrastructure.Credentials;
using Kautuka.Manager.Protos;
using System.Net;
using Grpc.Core;

namespace Kautuka.Gateway.Endpoints.Settings;

internal static partial class GatewaySettingsEndpointMappings
{

    private static GatewayProviderListItem ToProviderListItem(GatewayProvider provider, bool hasApiKey)
        => new(provider.Id, provider.Type, provider.Settings, provider.CreatedAt, hasApiKey);

    private static void MapProviderSettingsEndpoints(WebApplication app)
    {
        app.MapGet("/api/settings/provider-types", (KautukaAiClientFactory aiClientFactory, IProviderTypeCatalog providerTypeCatalog, IGatewayAuthGuard auth, HttpContext context) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out _);
            if (unauthorized is not null)
            {
                return unauthorized;
            }

            GatewayProviderTypeItem[] items = [.. aiClientFactory.ListProviderTypes()
                .Select(t =>
                {
                    providerTypeCatalog.TryGet(t.Type, out AiProviderCatalog.ProviderMetadata? meta);
                    return new GatewayProviderTypeItem(
                        t.Type,
                        t.FriendlyName,
                        t.RequiresApiKey,
                        t.RequiresBaseUrl,
                        t.SupportsHttpTimeoutSeconds,
                        meta?.DefaultBaseUrl);
                })];
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
            IResult? unauthorized = auth.RequireAdmin(context, out GatewayUser? adminUser);
            if (unauthorized is not null || adminUser is null)
            {
                return unauthorized ?? Results.Unauthorized();
            }

            string? testToken = null;
            IManagerGrpcService? manager = null;
            var actor = new NetworkAccessActorMessage { UserId = adminUser.Id, IsAdmin = true };
            bool temporary = body.TryGetProperty("allowTemporaryNetworkAccess", out JsonElement temporaryValue) && temporaryValue.ValueKind == JsonValueKind.True;
            if (temporary)
            {
                var catalog = context.RequestServices.GetRequiredService<IGatewayCatalogStore>();
                var types = context.RequestServices.GetRequiredService<IProviderTypeCatalog>();
                Uri? target = ResolveProviderTarget(body, catalog, types);
                if (target is null) return Results.BadRequest(new GatewayErrorResponse("Provider endpoint is invalid."));
                manager = context.RequestServices.GetRequiredService<IManagerGrpcService>();
                var grant = await manager.Client.BeginProviderTestAsync(new NetworkAccessProviderTestRequest
                {
                    Actor = actor,
                    Host = target.IdnHost,
                    Port = target.Port,
                    AllowLan = IsLocalProviderTarget(target),
                }, cancellationToken: ct);
                testToken = grant.Token;
            }
            using IDisposable nacScope = Kautuka.NetworkAccess.Client.NetworkAccessContext.BeginScope(
                userId: adminUser.Id,
                agentId: "gateway",
                purpose: "provider-test",
                service: "ai-provider",
                correlationId: testToken);
            GatewayProviderTestResult result;
            try { result = await providerValidationService.TestProviderAsync(body, ct).ConfigureAwait(false); }
            finally
            {
                if (manager is not null && testToken is not null)
                    await manager.Client.EndProviderTestAsync(new NetworkAccessProviderTestEndRequest { Actor = actor, Token = testToken }, cancellationToken: CancellationToken.None);
            }
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
            IResult? unauthorized = auth.RequireAdmin(context, out GatewayUser? adminUser);
            if (unauthorized is not null || adminUser is null)
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

            JsonObject proposedSettings = bodyElement.GetObjectProperty("settings");
            bool providerExists = catalog.TryGetProvider(providerId, out GatewayProvider? previousProvider) && previousProvider is not null;
            if (!providerExists) proposedSettings = ProviderTypeCatalogSettings.ApplyCreateDefaults(proposedSettings, providerType, providerTypeCatalog);
            if (!ProviderTypeCatalogSettings.TryValidate(proposedSettings, providerType, providerTypeCatalog, out string validationError))
            {
                return Results.Json(new GatewayErrorResponse(validationError), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
            }
            string? proposedBaseUrl = AiProviderSettings.GetString(proposedSettings, "base_url");
            if (string.IsNullOrWhiteSpace(proposedBaseUrl) && providerTypeCatalog.TryGet(providerType, out AiProviderCatalog.ProviderMetadata? targetMetadata))
                proposedBaseUrl = targetMetadata?.DefaultBaseUrl;
            Uri? providerTarget = Uri.TryCreate(proposedBaseUrl, UriKind.Absolute, out Uri? proposedUri) && proposedUri.Scheme is "http" or "https" ? proposedUri : null;
            if (providerTarget is null)
            {
                return Results.Json(new GatewayErrorResponse("Provider endpoint is invalid."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
            }

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

            var manager = context.RequestServices.GetRequiredService<IManagerGrpcService>();
            var actor = new NetworkAccessActorMessage { UserId = adminUser.Id, IsAdmin = true };
            NetworkAccessListRulesResponse rules;
            try { rules = await manager.Client.ListRulesAsync(new NetworkAccessListRulesRequest { Actor = actor }, cancellationToken: ct); }
            catch (RpcException)
            {
                return Results.Json(new GatewayErrorResponse("Provider was not saved because Manager could not confirm the network rule."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status503ServiceUnavailable);
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

            try
            {
                bool allowLan = IsLocalProviderTarget(providerTarget);
                string destKind = IPAddress.TryParse(providerTarget.IdnHost, out _) ? "ip" : "host";
                bool alreadyAllowed = rules.Rules.Any(rule => rule.ScopeKind == "system" && rule.Destination.Equals(providerTarget.IdnHost, StringComparison.OrdinalIgnoreCase)
                    && rule.Port == providerTarget.Port && rule.DestKind == destKind && rule.Allowed && !rule.Paused && (!allowLan || rule.AllowLan));
                if (!alreadyAllowed) await manager.Client.SetRuleAsync(new NetworkAccessSetRuleRequest
                {
                    Actor = actor,
                    ScopeKind = "system",
                    Destination = providerTarget.IdnHost,
                    DestKind = destKind,
                    Port = providerTarget.Port,
                    Allowed = true,
                    AllowLan = allowLan,
                    ExpectedRevision = rules.Revision,
                }, cancellationToken: ct);
            }
            catch (RpcException)
            {
                if (previousProvider is null) catalog.DeleteProvider(saved.Id);
                else
                {
                    var restore = new JsonObject { ["id"] = previousProvider.Id, ["type"] = previousProvider.Type, ["settings"] = previousProvider.Settings.DeepClone() };
                    using JsonDocument restoreDocument = JsonDocument.Parse(restore.ToJsonString());
                    catalog.SaveProvider(restoreDocument.RootElement);
                }
                return Results.Json(new GatewayErrorResponse("Provider was not saved because Manager could not update the network rule."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                await credentialResolver.SetSystemAsync(
                    new Dictionary<string, string> { [$"provider.{saved.Id}.api_key"] = apiKey }, ct).ConfigureAwait(false);
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

    private static Uri? ResolveProviderTarget(JsonElement body, IGatewayCatalogStore catalog, IProviderTypeCatalog types)
    {
        string id = body.GetStringProperty("id", body.GetStringProperty("provider_id", string.Empty));
        catalog.TryGetProvider(id, out GatewayProvider? existing);
        string type = ProviderTypeCatalogSettings.NormalizeType(body.GetStringProperty("type", existing?.Type ?? string.Empty));
        bool hasBaseUrl = body.TryGetProperty("settings", out JsonElement settings) && settings.ValueKind == JsonValueKind.Object
            && settings.TryGetProperty("base_url", out _);
        string? baseUrl = hasBaseUrl ? settings.GetStringProperty("base_url", string.Empty) : null;
        if (!hasBaseUrl && existing is not null) baseUrl = AiProviderSettings.GetString(existing.Settings, "base_url");
        if (string.IsNullOrWhiteSpace(baseUrl) && types.TryGet(type, out AiProviderCatalog.ProviderMetadata? metadata)) baseUrl = metadata?.DefaultBaseUrl;
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https" ? uri : null;
    }

    private static bool IsLocalProviderTarget(Uri uri)
    {
        if (uri.IsLoopback) return true;
        if (!IPAddress.TryParse(uri.IdnHost, out IPAddress? address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        byte[] bytes = address.GetAddressBytes();
        return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168);
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
