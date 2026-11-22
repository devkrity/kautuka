using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Providers;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Infrastructure.Credentials;
using Kautuka.Manager.Protos;
using System.Net;
using Grpc.Core;

namespace Kautuka.Gateway.Endpoints.Settings;

internal sealed record ProviderPermissionConfirmation(string Detail, string Destination, int Port, bool AllowLan, string Revision);
internal sealed record ProviderSaveConflict(string Detail, string Code);

internal static partial class GatewaySettingsEndpointMappings
{
    private static readonly SemaphoreSlim providerWrites = new(1);

    private static async ValueTask<object?> SerializeProviderWriteAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        await providerWrites.WaitAsync(context.HttpContext.RequestAborted).ConfigureAwait(false);
        try { return await next(context).ConfigureAwait(false); }
        finally { providerWrites.Release(); }
    }

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
            string? proposedBaseUrl = ProviderTypeCatalogSettings.GetEffectiveBaseUrl(proposedSettings, providerType, providerTypeCatalog);
            Uri? providerTarget = Uri.TryCreate(proposedBaseUrl, UriKind.Absolute, out Uri? proposedUri) && proposedUri.Scheme is "http" or "https" ? proposedUri : null;
            if (providerTarget is null)
            {
                return Results.Json(new GatewayErrorResponse("Provider endpoint is invalid."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
            }

            bool sameCredentialDestination = previousProvider is null
                || ProviderTypeCatalogSettings.HasSameCredentialDestination(previousProvider, providerType, proposedSettings, providerTypeCatalog);
            bool hasStoredKey = false;
            if (!providerExists || (!hasApiKey && (requiresApiKey || !sameCredentialDestination)))
            {
                try
                {
                    IReadOnlyList<string> existingKeys = await credentialResolver.ListSystemKeysAsync(ct).ConfigureAwait(false);
                    hasStoredKey = existingKeys.Contains($"provider.{providerId}.api_key", StringComparer.Ordinal);
                }
                catch (RpcException)
                {
                    return Results.Json(new GatewayErrorResponse("Provider settings were not saved because the credential store is unavailable."),
                        KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            }

            if (!providerExists && hasStoredKey)
                return Results.Json(new ProviderSaveConflict("This provider ID still has a saved credential. Choose a different provider ID.", "provider_id_reserved"),
                    KautukaJsonContext.Default.ProviderSaveConflict, statusCode: StatusCodes.Status409Conflict);

            if (previousProvider is not null && !sameCredentialDestination && (requiresApiKey || hasApiKey || hasStoredKey
                    || (providerTypeCatalog.TryGet(previousProvider.Type, out AiProviderCatalog.ProviderMetadata? previousMetadata)
                        && previousMetadata?.RequiresApiKey is true)))
            {
                return Results.BadRequest(new GatewayErrorResponse("Create a new provider to use a different credential destination."));
            }

            hasApiKey |= providerExists && hasStoredKey;
            if (requiresApiKey && !hasApiKey)
                return Results.BadRequest(new GatewayErrorResponse($"Provider type '{providerType}' requires an API key."));

            bool allowLan = IsLocalProviderTarget(providerTarget);
            string destKind = IPAddress.TryParse(providerTarget.IdnHost, out _) ? "ip" : "host";
            var permissionRequest = new NetworkAccessProviderPermissionRequest
            {
                Actor = new NetworkAccessActorMessage { UserId = adminUser.Id, IsAdmin = true },
                Destination = providerTarget.IdnHost,
                DestKind = destKind,
                Port = providerTarget.Port,
                AllowLan = allowLan,
            };
            if (bodyElement.TryGetProperty("networkPermission", out JsonElement confirmation) && confirmation.ValueKind == JsonValueKind.Object
                && confirmation.GetStringProperty("destination", string.Empty) == permissionRequest.Destination
                && confirmation.TryGetProperty("port", out JsonElement confirmedPort) && confirmedPort.ValueKind == JsonValueKind.Number
                && confirmedPort.TryGetInt32(out int port) && port == permissionRequest.Port
                && confirmation.TryGetProperty("allowLan", out JsonElement confirmedLan)
                && confirmedLan.ValueKind == (allowLan ? JsonValueKind.True : JsonValueKind.False)
                && long.TryParse(confirmation.GetStringProperty("revision", string.Empty), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out long revision))
            {
                permissionRequest.ConfirmedRevision = revision;
            }

            NetworkAccessProviderPermissionResponse permission;
            try
            {
                permission = await context.RequestServices.GetRequiredService<IManagerGrpcService>().Client
                    .EnsureProviderPermissionAsync(permissionRequest, cancellationToken: ct).ConfigureAwait(false);
            }
            catch (RpcException error) when (error.StatusCode == StatusCode.InvalidArgument)
            {
                return Results.BadRequest(new GatewayErrorResponse("Network Access cannot allow this provider destination. Check its hostname or IP address and port."));
            }
            catch (RpcException)
            {
                return Results.Json(new GatewayErrorResponse("Provider was not saved because Manager could not apply its network permission."),
                    KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            if (permission.Status == "confirmation_required")
                return Results.Json(new ProviderPermissionConfirmation(
                    permissionRequest.HasConfirmedRevision
                        ? "Network Access changed while you were confirming. Try saving again."
                        : "Confirm systemwide network access before saving this provider.",
                    permissionRequest.Destination, permissionRequest.Port,
                    allowLan, permission.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    KautukaJsonContext.Default.ProviderPermissionConfirmation, statusCode: StatusCodes.Status409Conflict);
            if (permission.Status == "restricted")
                return Results.Conflict(new GatewayErrorResponse(
                    "Network Access limits this provider destination. Review the existing rule in Network Access before saving."));
            if (permission.Status != "allowed")
                return Results.Json(new GatewayErrorResponse("Manager returned an unknown provider network decision."),
                    KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status503ServiceUnavailable);

            if (providerId.Length == 0)
                providerId = $"provider-{Guid.NewGuid():N}";
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                try
                {
                    int written = await credentialResolver.SetSystemAsync(
                        new Dictionary<string, string> { [$"provider.{providerId}.api_key"] = apiKey }, ct).ConfigureAwait(false);
                    if (written != 1)
                        throw new RpcException(new Status(StatusCode.Unavailable, "The credential store did not acknowledge the provider key."));
                }
                catch (RpcException)
                {
                    return Results.Json(new GatewayErrorResponse("Provider settings were not saved because the credential store could not confirm the key. Check Network Access for any permission you approved."),
                        KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            }
            GatewayProvider saved;
            try
            {
                proposedSettings.Remove("api_key");
                proposedSettings.Remove("apiKey");
                proposedSettings["base_url"] = proposedBaseUrl;
                var providerBody = JsonObject.Create(bodyElement) ?? new JsonObject();
                providerBody["id"] = providerId;
                providerBody.Remove("networkPermission");
                providerBody["settings"] = proposedSettings;
                saved = catalog.SaveProvider(JsonSerializer.SerializeToElement(providerBody, KautukaJsonContext.Default.JsonObject));
            }
            catch (ProviderConfigurationException ex)
            {
                return Results.Json(
                    new GatewayErrorResponse(ex.Message),
                    KautukaJsonContext.Default.GatewayErrorResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            return Results.Json(
                new GatewayProviderResponse(ToProviderListItem(saved, hasApiKey)),
                KautukaJsonContext.Default.GatewayProviderResponse);
        }).AddEndpointFilter(SerializeProviderWriteAsync);

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
        }).AddEndpointFilter(SerializeProviderWriteAsync);
    }

    private static Uri? ResolveProviderTarget(JsonElement body, IGatewayCatalogStore catalog, IProviderTypeCatalog types)
    {
        string id = body.GetStringProperty("provider_id", string.Empty).Trim();
        if (id.Length == 0) id = body.GetStringProperty("id", string.Empty).Trim();
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

}
