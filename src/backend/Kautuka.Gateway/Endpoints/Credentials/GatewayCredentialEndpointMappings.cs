using Kautuka.Gateway.Domain.Connectors;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Infrastructure.Credentials;

namespace Kautuka.Gateway.Endpoints.Credentials;

internal static class GatewayCredentialEndpointMappings
{
    public static WebApplication MapGatewayCredentialEndpoints(this WebApplication app)
    {
        // ── User credential endpoints ─────────────────────────────────────────────
        app.MapGet("/api/credentials/user", async (
            CredentialResolverService credentialResolver,
            IGatewayAuthGuard auth,
            HttpContext context,
            CancellationToken ct) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
                return Results.Unauthorized();

            IReadOnlyList<string> keys = await credentialResolver.ListUserKeysAsync(user.Id, ct).ConfigureAwait(false);
            return Results.Json(new CredentialCollectionResponse("user", [.. keys]), KautukaJsonContext.Default.CredentialCollectionResponse);
        });

        app.MapPut("/api/credentials/user", async (
            CredentialResolverService credentialResolver,
            IGatewayAuthGuard auth,
            HttpContext context,
            CancellationToken ct) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
                return Results.Unauthorized();

            SetCredentialsRequest? body = await context.Request.ReadFromJsonAsync(
                KautukaJsonContext.Default.SetCredentialsRequest, ct).ConfigureAwait(false);

            if (body?.Entries is not { Count: > 0 })
                return Results.Json(new CredentialUpdateResponse("No entries provided."), KautukaJsonContext.Default.CredentialUpdateResponse, statusCode: StatusCodes.Status400BadRequest);

            await credentialResolver.SetUserAsync(user.Id, body.Entries, ct).ConfigureAwait(false);
            return Results.Json(new CredentialUpdateResponse("User credentials updated."), KautukaJsonContext.Default.CredentialUpdateResponse);
        });

        app.MapDelete("/api/credentials/user/{key}", async (
            string key,
            CredentialResolverService credentialResolver,
            IGatewayAuthGuard auth,
            HttpContext context,
            CancellationToken ct) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
                return Results.Unauthorized();

            bool deleted = await credentialResolver.DeleteUserAsync(user.Id, key, ct).ConfigureAwait(false);
            return deleted ? Results.Ok() : Results.NotFound();
        });

        app.MapGet("/api/credentials/users/{userId}", async (
            string userId,
            CredentialResolverService credentialResolver,
            IGatewayAuthGuard auth,
            HttpContext context,
            CancellationToken ct) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out GatewayUser? admin);
            if (unauthorized is not null)
                return unauthorized;

            IReadOnlyList<string> keys = await credentialResolver.ListUserKeysAsAdminAsync(admin!.Id, userId, ct).ConfigureAwait(false);
            return Results.Json(new CredentialCollectionResponse("user", [.. keys]), KautukaJsonContext.Default.CredentialCollectionResponse);
        });

        app.MapPut("/api/credentials/users/{userId}", async (
            string userId,
            CredentialResolverService credentialResolver,
            IGatewayAuthGuard auth,
            HttpContext context,
            CancellationToken ct) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out GatewayUser? admin);
            if (unauthorized is not null)
                return unauthorized;

            SetCredentialsRequest? body = await context.Request.ReadFromJsonAsync(
                KautukaJsonContext.Default.SetCredentialsRequest, ct).ConfigureAwait(false);

            if (body?.Entries is not { Count: > 0 })
                return Results.Json(new CredentialUpdateResponse("No entries provided."), KautukaJsonContext.Default.CredentialUpdateResponse, statusCode: StatusCodes.Status400BadRequest);

            await credentialResolver.SetUserAsAdminAsync(admin!.Id, userId, body.Entries, ct).ConfigureAwait(false);
            return Results.Json(new CredentialUpdateResponse("User credentials updated."), KautukaJsonContext.Default.CredentialUpdateResponse);
        });

        app.MapDelete("/api/credentials/users/{userId}/{key}", async (
            string userId,
            string key,
            CredentialResolverService credentialResolver,
            IGatewayAuthGuard auth,
            HttpContext context,
            CancellationToken ct) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out GatewayUser? admin);
            if (unauthorized is not null)
                return unauthorized;

            bool deleted = await credentialResolver.DeleteUserAsAdminAsync(admin!.Id, userId, key, ct).ConfigureAwait(false);
            return deleted ? Results.Ok() : Results.NotFound();
        });

        // ── System credential endpoints (admin only) ──────────────────────────────
        app.MapGet("/api/credentials/system", async (
            CredentialResolverService credentialResolver,
            IGatewayAuthGuard auth,
            HttpContext context,
            CancellationToken ct) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out _);
            if (unauthorized is not null)
                return unauthorized;

            IReadOnlyList<string> keys = await credentialResolver.ListSystemKeysAsync(ct).ConfigureAwait(false);
            return Results.Json(new CredentialCollectionResponse("system", [.. keys]), KautukaJsonContext.Default.CredentialCollectionResponse);
        });

        app.MapPut("/api/credentials/system", async (
            CredentialResolverService credentialResolver,
            IGatewayAuthGuard auth,
            HttpContext context,
            CancellationToken ct) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out _);
            if (unauthorized is not null)
                return unauthorized;

            SetCredentialsRequest? body = await context.Request.ReadFromJsonAsync(
                KautukaJsonContext.Default.SetCredentialsRequest, ct).ConfigureAwait(false);

            if (body?.Entries is not { Count: > 0 })
                return Results.Json(new CredentialUpdateResponse("No entries provided."), KautukaJsonContext.Default.CredentialUpdateResponse, statusCode: StatusCodes.Status400BadRequest);

            await credentialResolver.SetSystemAsync(body.Entries, ct).ConfigureAwait(false);
            return Results.Json(new CredentialUpdateResponse("System credentials updated."), KautukaJsonContext.Default.CredentialUpdateResponse);
        });

        app.MapDelete("/api/credentials/system/{key}", async (
            string key,
            CredentialResolverService credentialResolver,
            IGatewayAuthGuard auth,
            HttpContext context,
            CancellationToken ct) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out _);
            if (unauthorized is not null)
                return unauthorized;

            bool deleted = await credentialResolver.DeleteSystemAsync(key, ct).ConfigureAwait(false);
            return deleted ? Results.Ok() : Results.NotFound();
        });

        // ── Connector credential endpoints (user scoped) ──────────────────────────
        // Connector env vars are stored in the authenticated user's credential bag using the raw env key name.
        // GET returns which of the connector's declared env_keys are configured (not the values).
        // PUT saves values as user credentials for runtime resolution.

        app.MapGet("/api/credentials/connector/{connectorId}", async (
            string connectorId,
            CredentialResolverService credentialResolver,
            IGatewayCatalogStore catalog,
            IGatewayAuthGuard auth,
            HttpContext context,
            CancellationToken ct) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
                return Results.Unauthorized();

            if (!catalog.TryGetMcpServer(connectorId, out GatewayMcpServer? server) || server is null)
                return Results.Json(new GatewayErrorResponse("Connector not found"), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);

            if (!GatewayOwnershipAccess.CanAccessConnector(user, server))
                return Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);

            if (server.EnvKeys is not { Count: > 0 })
            {
                string httpUrlOverrideKey = ConnectorCredentialKeys.BuildHttpUrlOverrideKey(connectorId);
                IReadOnlyDictionary<string, string> overrideResolved = await credentialResolver
                    .GetResolvedAsync(user.Id, [httpUrlOverrideKey], ct)
                    .ConfigureAwait(false);
                _ = overrideResolved.TryGetValue(httpUrlOverrideKey, out string? resolvedOverrideUrl);

                return Results.Json(new ConnectorCredentialStatusResponse(connectorId, [], [], resolvedOverrideUrl), KautukaJsonContext.Default.ConnectorCredentialStatusResponse);
            }

            IReadOnlyList<string> userKeys = await credentialResolver.ListUserKeysAsync(user.Id, ct).ConfigureAwait(false);
            var userKeySet = new HashSet<string>(userKeys, StringComparer.Ordinal);
            string? httpUrlOverride = null;
            string overrideKey = ConnectorCredentialKeys.BuildHttpUrlOverrideKey(connectorId);
            if (userKeySet.Contains(overrideKey))
            {
                IReadOnlyDictionary<string, string> overrideResolved = await credentialResolver
                    .GetResolvedAsync(user.Id, [overrideKey], ct)
                    .ConfigureAwait(false);
                _ = overrideResolved.TryGetValue(overrideKey, out httpUrlOverride);
            }

            (string[] configured, string[] missing) = BuildConfiguredAndMissingKeys(server.EnvKeys, userKeySet);

            return Results.Json(new ConnectorCredentialStatusResponse(connectorId, configured, missing, httpUrlOverride), KautukaJsonContext.Default.ConnectorCredentialStatusResponse);
        });

        app.MapPut("/api/credentials/connector/{connectorId}", async (
            string connectorId,
            CredentialResolverService credentialResolver,
            IGatewayCatalogStore catalog,
            IGatewayAuthGuard auth,
            HttpContext context,
            CancellationToken ct) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
                return Results.Unauthorized();

            if (!catalog.TryGetMcpServer(connectorId, out GatewayMcpServer? server) || server is null)
                return Results.Json(new GatewayErrorResponse("Connector not found"), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);

            if (!GatewayOwnershipAccess.CanAccessConnector(user, server))
                return Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);

            SetCredentialsRequest? body = await context.Request.ReadFromJsonAsync(
                KautukaJsonContext.Default.SetCredentialsRequest, ct).ConfigureAwait(false);

            bool hasEntries = body?.Entries is { Count: > 0 };
            bool hasHttpUrlOverride = string.Equals(server.Transport, "http", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(body?.HttpUrl);

            if (!hasEntries && !hasHttpUrlOverride)
                return Results.Json(new CredentialUpdateResponse("No entries provided."), KautukaJsonContext.Default.CredentialUpdateResponse, statusCode: StatusCodes.Status400BadRequest);

            // Only accept keys that are declared in the connector's env_keys.
            Dictionary<string, string> filtered = FilterAllowedEntries(body?.Entries, server.EnvKeys);

            if (string.Equals(server.Transport, "http", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(body?.HttpUrl))
            {
                if (!GatewayOwnershipAccess.IsAdmin(user) && ProbeNetworkPolicy.IsConfirmedPrivate(body.HttpUrl))
                {
                    return Results.Json(
                        new GatewayErrorResponse("Connector destination is not allowed."),
                        KautukaJsonContext.Default.GatewayErrorResponse,
                        statusCode: StatusCodes.Status400BadRequest);
                }

                filtered[ConnectorCredentialKeys.BuildHttpUrlOverrideKey(connectorId)] = body.HttpUrl.Trim();
            }

            if (filtered.Count == 0)
                return Results.Json(new CredentialUpdateResponse("No valid entries to save."), KautukaJsonContext.Default.CredentialUpdateResponse, statusCode: StatusCodes.Status400BadRequest);

            int count = await credentialResolver.SetUserAsync(user.Id, filtered, ct).ConfigureAwait(false);
            return Results.Json(new CredentialUpdateResponse($"{count} credential(s) saved."), KautukaJsonContext.Default.CredentialUpdateResponse);
        });

        return app;
    }

    private static (string[] Configured, string[] Missing) BuildConfiguredAndMissingKeys(IReadOnlyList<string>? declaredKeys, HashSet<string> userKeys)
    {
        if (declaredKeys is not { Count: > 0 })
        {
            return ([], []);
        }

        string[] configured = [.. declaredKeys.Where(userKeys.Contains)];
        string[] missing = [.. declaredKeys.Where(static key => !string.IsNullOrWhiteSpace(key)).Where(key => !userKeys.Contains(key))];
        return (configured, missing);
    }

    private static Dictionary<string, string> FilterAllowedEntries(
        Dictionary<string, string>? entries,
        IReadOnlyList<string>? declaredKeys)
    {
        if (entries is null || entries.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var allowed = new HashSet<string>(declaredKeys ?? [], StringComparer.Ordinal);
        return new Dictionary<string, string>(
            entries.Where(kv => allowed.Contains(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value)),
            StringComparer.Ordinal);
    }
}
