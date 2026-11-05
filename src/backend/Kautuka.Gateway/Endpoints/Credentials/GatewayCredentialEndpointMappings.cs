using Kautuka.Gateway.Domain.Connectors;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Infrastructure.Credentials;

namespace Kautuka.Gateway.Endpoints.Credentials;

internal static class GatewayCredentialEndpointMappings
{
    private static bool IsInternalCredential(string key)
        => key.StartsWith("kautuka.internal.", StringComparison.OrdinalIgnoreCase);

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
            return Results.Json(new CredentialCollectionResponse("user", [.. keys.Where(key => !ConnectorCredentialKeys.IsReservedConnectorKey(key))]), KautukaJsonContext.Default.CredentialCollectionResponse);
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

            if (body.Entries.Keys.Any(ConnectorCredentialKeys.IsReservedConnectorKey))
                return Results.Json(new CredentialUpdateResponse("Connector credentials must be managed through their connector."), KautukaJsonContext.Default.CredentialUpdateResponse, statusCode: StatusCodes.Status400BadRequest);

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

            if (ConnectorCredentialKeys.IsReservedConnectorKey(key)) return Results.NotFound();
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
            return Results.Json(new CredentialCollectionResponse("user", [.. keys.Where(key => !ConnectorCredentialKeys.IsReservedConnectorKey(key))]), KautukaJsonContext.Default.CredentialCollectionResponse);
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

            if (body.Entries.Keys.Any(ConnectorCredentialKeys.IsReservedConnectorKey))
                return Results.Json(new CredentialUpdateResponse("Connector credentials must be managed through their connector."), KautukaJsonContext.Default.CredentialUpdateResponse, statusCode: StatusCodes.Status400BadRequest);

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

            if (ConnectorCredentialKeys.IsReservedConnectorKey(key)) return Results.NotFound();
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
            return Results.Json(new CredentialCollectionResponse("system", [.. keys.Where(key => !IsInternalCredential(key) && !ConnectorCredentialKeys.IsReservedConnectorKey(key))]), KautukaJsonContext.Default.CredentialCollectionResponse);
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

            if (body.Entries.Keys.Any(key => IsInternalCredential(key) || ConnectorCredentialKeys.IsReservedConnectorKey(key)))
                return Results.Json(new CredentialUpdateResponse("Internal credentials cannot be changed here."), KautukaJsonContext.Default.CredentialUpdateResponse, statusCode: StatusCodes.Status400BadRequest);

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

            if (IsInternalCredential(key) || ConnectorCredentialKeys.IsReservedConnectorKey(key))
                return Results.NotFound();

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

            if (string.Equals(server.Transport, "http", StringComparison.OrdinalIgnoreCase))
            {
                string remoteOverrideKey = ConnectorCredentialKeys.BuildHttpUrlOverrideKey(connectorId, server.CredentialNamespace);
                IReadOnlyDictionary<string, string> overrides = await credentialResolver.ResolveRemoteAsync(server.CredentialAssociationId, connectorId, server.CredentialNamespace, user.Id, [remoteOverrideKey], false, ct).ConfigureAwait(false);
                overrides.TryGetValue(remoteOverrideKey, out string? overrideUrl);
                bool personalDestination = Uri.TryCreate(overrideUrl, UriKind.Absolute, out Uri? personalUri)
                    && personalUri.Scheme is "http" or "https" && string.IsNullOrEmpty(personalUri.UserInfo)
                    && (!ProbeNetworkPolicy.IsConfirmedPrivate(overrideUrl) || GatewayOwnershipAccess.IsAdmin(user))
                    && !string.Equals(personalUri.AbsoluteUri, server.HttpUrl, StringComparison.Ordinal);
                Uri? effectiveEndpoint = personalDestination ? personalUri : Uri.TryCreate(server.HttpUrl, UriKind.Absolute, out Uri? configuredUri) ? configuredUri : null;
                bool allowSystem = !personalDestination && !string.Equals(server.Scope, GatewayOwnershipScopes.User, StringComparison.OrdinalIgnoreCase);
                string[] keys = effectiveEndpoint is null || server.CredentialNamespace is null ? [] : [.. (server.Credentials ?? []).Select(c => ConnectorCredentialKeys.BuildRemoteCredentialKey(server.CredentialNamespace, effectiveEndpoint, c.Key))];
                (IReadOnlyList<string> users, IReadOnlyList<string> systems) = server.CredentialAssociationId is null
                    ? (Array.Empty<string>(), Array.Empty<string>())
                    : await credentialResolver.GetAssociationPresenceAsync(server.CredentialAssociationId, connectorId, server.CredentialNamespace!, user.Id, keys, allowSystem, ct).ConfigureAwait(false);
                var present = new HashSet<string>(users, StringComparer.Ordinal);
                var systemKeys = new HashSet<string>(systems, StringComparer.Ordinal);
                RemoteCredentialPresence[] status = [.. (server.Credentials ?? []).Select(c =>
                {
                    string? key = effectiveEndpoint is null || server.CredentialNamespace is null ? null : ConnectorCredentialKeys.BuildRemoteCredentialKey(server.CredentialNamespace, effectiveEndpoint, c.Key);
                    bool hasUser = key is not null && present.Contains(key);
                    bool hasSystem = key is not null && systemKeys.Contains(key);
                    return new RemoteCredentialPresence(c.Key, hasUser, hasSystem, hasUser ? "user" : hasSystem ? "system" : "none");
                })];
                return Results.Json(new ConnectorCredentialStatusResponse(connectorId,
                    [.. status.Where(c => c.EffectiveScope != "none").Select(c => c.Key)],
                    [.. status.Where(c => c.EffectiveScope == "none").Select(c => c.Key)], overrideUrl, status), KautukaJsonContext.Default.ConnectorCredentialStatusResponse);
            }

            if (server.EnvKeys is not { Count: > 0 })
            {
                string httpUrlOverrideKey = ConnectorCredentialKeys.BuildHttpUrlOverrideKey(connectorId, server.CredentialNamespace);
                IReadOnlyDictionary<string, string> overrideResolved = await credentialResolver
                    .GetResolvedAsync(user.Id, [httpUrlOverrideKey], ct)
                    .ConfigureAwait(false);
                _ = overrideResolved.TryGetValue(httpUrlOverrideKey, out string? resolvedOverrideUrl);

                return Results.Json(new ConnectorCredentialStatusResponse(connectorId, [], [], resolvedOverrideUrl), KautukaJsonContext.Default.ConnectorCredentialStatusResponse);
            }

            IReadOnlyList<string> userKeys = await credentialResolver.ListUserKeysAsync(user.Id, ct).ConfigureAwait(false);
            var userKeySet = new HashSet<string>(userKeys, StringComparer.Ordinal);
            string? httpUrlOverride = null;
            string overrideKey = ConnectorCredentialKeys.BuildHttpUrlOverrideKey(connectorId, server.CredentialNamespace);
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
            RemoteMcpCredentialRevisionTracker revisions,
            CredentialResolverService credentialResolver,
            IGatewayCatalogStore catalog,
            IGatewayUserStore users,
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

            bool hasRemovals = body?.RemoveKeys is { Length: > 0 };
            bool isRemote = string.Equals(server.Transport, "http", StringComparison.OrdinalIgnoreCase);
            if (!isRemote && hasRemovals)
                return Results.Json(new CredentialUpdateResponse("Removal is not supported by this endpoint for container credentials."), KautukaJsonContext.Default.CredentialUpdateResponse, statusCode: StatusCodes.Status400BadRequest);
            if (isRemote && body is not null && !hasHttpUrlOverride && !hasRemovals
                && (body.Entries is null || body.Entries.Values.All(string.IsNullOrWhiteSpace)))
                return Results.Json(new CredentialUpdateResponse("Credentials unchanged."), KautukaJsonContext.Default.CredentialUpdateResponse);
            if (!hasEntries && !hasHttpUrlOverride && !hasRemovals)
                return Results.Json(new CredentialUpdateResponse("No entries provided."), KautukaJsonContext.Default.CredentialUpdateResponse, statusCode: StatusCodes.Status400BadRequest);

            if (isRemote)
            {
                HashSet<string> declared = (server.Credentials ?? []).Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
                if ((body?.Entries?.Any(pair => !string.IsNullOrWhiteSpace(pair.Value) && !declared.Contains(pair.Key)) ?? false)
                    || (body?.RemoveKeys?.Any(key => !declared.Contains(key) || (body.Entries?.TryGetValue(key, out string? supplied) == true && !string.IsNullOrWhiteSpace(supplied))) ?? false))
                    return Results.Json(new CredentialUpdateResponse("Invalid or conflicting credential mutation."), KautukaJsonContext.Default.CredentialUpdateResponse, statusCode: StatusCodes.Status400BadRequest);
            }

            Uri? effectiveRemoteEndpoint = null;
            if (isRemote)
            {
                string? rawEndpoint = body?.HttpUrl;
                if (string.IsNullOrWhiteSpace(rawEndpoint))
                {
                    string overrideKey = ConnectorCredentialKeys.BuildHttpUrlOverrideKey(connectorId, server.CredentialNamespace);
                    IReadOnlyDictionary<string, string> overrides = await credentialResolver.ResolveRemoteAsync(server.CredentialAssociationId, connectorId, server.CredentialNamespace, user.Id, [overrideKey], false, ct).ConfigureAwait(false);
                    rawEndpoint = overrides.TryGetValue(overrideKey, out string? stored) && !string.IsNullOrWhiteSpace(stored) ? stored : server.HttpUrl;
                }
                if (Uri.TryCreate(rawEndpoint, UriKind.Absolute, out Uri? parsed)) effectiveRemoteEndpoint = parsed;
                if ((hasEntries || hasRemovals) && (effectiveRemoteEndpoint is null || server.CredentialNamespace is null))
                    return Results.Json(new CredentialUpdateResponse("Connector destination is required."), KautukaJsonContext.Default.CredentialUpdateResponse, statusCode: StatusCodes.Status400BadRequest);
            }
            // Container credentials retain their existing env-key behavior.
            Dictionary<string, string> filtered = string.Equals(server.Transport, "http", StringComparison.OrdinalIgnoreCase)
                ? FilterAllowedEntries(body?.Entries, server.Credentials?.Select(c => c.Key).ToArray())
                    .ToDictionary(pair => ConnectorCredentialKeys.BuildRemoteCredentialKey(server.CredentialNamespace!, effectiveRemoteEndpoint!, pair.Key), pair => pair.Value, StringComparer.Ordinal)
                : FilterAllowedEntries(body?.Entries, server.EnvKeys).Where(pair => !ConnectorCredentialKeys.IsReservedConnectorKey(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

            if (string.Equals(server.Transport, "http", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(body?.HttpUrl))
            {
                try
                {
                    _ = Uri.TryCreate(body.HttpUrl, UriKind.Absolute, out Uri? endpoint);
                    RemoteMcpConfigurationValidator.ValidateEndpoint(endpoint);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.Json(new GatewayErrorResponse(ex.Message), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
                }
                if (!GatewayOwnershipAccess.IsAdmin(user) && ProbeNetworkPolicy.IsConfirmedPrivate(body.HttpUrl))
                {
                    return Results.Json(
                        new GatewayErrorResponse("Connector destination is not allowed."),
                        KautukaJsonContext.Default.GatewayErrorResponse,
                        statusCode: StatusCodes.Status400BadRequest);
                }

                filtered[ConnectorCredentialKeys.BuildHttpUrlOverrideKey(connectorId, server.CredentialNamespace)] = body.HttpUrl.Trim();
            }

            if (filtered.Count == 0 && !hasRemovals)
                return Results.Json(new CredentialUpdateResponse("No valid entries to save."), KautukaJsonContext.Default.CredentialUpdateResponse, statusCode: StatusCodes.Status400BadRequest);

            bool remote = string.Equals(server.Transport, "http", StringComparison.OrdinalIgnoreCase);
            if (remote)
            {
                string[] removeKeys = [.. (body?.RemoveKeys ?? []).Distinct(StringComparer.Ordinal).Select(key => ConnectorCredentialKeys.BuildRemoteCredentialKey(server.CredentialNamespace!, effectiveRemoteEndpoint!, key))];
                string[] lookup = [.. filtered.Keys.Concat(removeKeys).Distinct(StringComparer.Ordinal)];
                (IReadOnlyList<string> prior, _) = server.CredentialAssociationId is null ? (Array.Empty<string>(), Array.Empty<string>())
                    : await credentialResolver.GetAssociationPresenceAsync(server.CredentialAssociationId, connectorId, server.CredentialNamespace!, user.Id, lookup, false, ct).ConfigureAwait(false);
                var previouslyPresent = new HashSet<string>(prior, StringComparer.Ordinal);
                string operation = Guid.NewGuid().ToString("N");
                try
                {
                    using (revisions.BeginExclusiveChange(connectorId, "user:" + user.Id))
                    {
                        await catalog.MutateMcpCredentialAssociationAsync(connectorId, async (current, token) =>
                        {
                            GatewayUser? active = ActiveUser(users, user.Id);
                            if (active is null || !GatewayOwnershipAccess.CanAccessConnector(active, current)
                                || current.CredentialNamespace != server.CredentialNamespace || current.UpdatedAt != server.UpdatedAt
                                || current.CredentialAssociationId != server.CredentialAssociationId
                                || (hasHttpUrlOverride && !GatewayOwnershipAccess.IsAdmin(active) && ProbeNetworkPolicy.IsConfirmedPrivate(body!.HttpUrl!)))
                                throw new InvalidOperationException("Connector or authorization changed. Retry the update.");
                            string staged = await credentialResolver.StageMutationAsync(operation, user.Id, "user", connectorId, current.CredentialNamespace!, current.CredentialAssociationId, filtered, removeKeys, token).ConfigureAwait(false);
                            GatewayUser? after = ActiveUser(users, user.Id);
                            if (after is null || !GatewayOwnershipAccess.CanAccessConnector(after, current)
                                || (hasHttpUrlOverride && !GatewayOwnershipAccess.IsAdmin(after) && ProbeNetworkPolicy.IsConfirmedPrivate(body!.HttpUrl!)))
                                throw new InvalidOperationException("Connector authorization changed. Retry the update.");
                            return (staged, operation);
                        }, ct).ConfigureAwait(false);
                    }
                    foreach (RemoteMcpCredential declaration in server.Credentials ?? [])
                    {
                        if (effectiveRemoteEndpoint is null) continue;
                        string storageKey = ConnectorCredentialKeys.BuildRemoteCredentialKey(server.CredentialNamespace!, effectiveRemoteEndpoint, declaration.Key);
                        if (filtered.ContainsKey(storageKey)) RemoteMcpAudit.Write(context, previouslyPresent.Contains(storageKey) ? "credential.replaced" : "credential.added", user.Id, connectorId, "user", "succeeded", declaration.Key);
                        if (removeKeys.Contains(storageKey, StringComparer.Ordinal)) RemoteMcpAudit.Write(context, "credential.removed", user.Id, connectorId, "user", previouslyPresent.Contains(storageKey) ? "succeeded" : "unchanged", declaration.Key);
                    }
                    return Results.Json(new CredentialUpdateResponse($"{filtered.Count + removeKeys.Count(previouslyPresent.Contains)} credential(s) updated."), KautukaJsonContext.Default.CredentialUpdateResponse);
                }
                catch (InvalidOperationException ex) { return Results.Json(new CredentialUpdateResponse(ex.Message), KautukaJsonContext.Default.CredentialUpdateResponse, statusCode: StatusCodes.Status409Conflict); }
            }
            int count = await credentialResolver.SetUserAsync(user.Id, filtered, ct).ConfigureAwait(false);
            return Results.Json(new CredentialUpdateResponse($"{count} credential(s) updated."), KautukaJsonContext.Default.CredentialUpdateResponse);
        });

        app.MapPut("/api/credentials/connector/{connectorId}/system", async (
            string connectorId, RemoteMcpCredentialRevisionTracker revisions, CredentialResolverService resolver,
            IGatewayCatalogStore catalog, IGatewayUserStore users, IGatewayAuthGuard auth, HttpContext context, CancellationToken ct) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out GatewayUser? admin);
            if (unauthorized is not null) return unauthorized;
            if (!catalog.TryGetMcpServer(connectorId, out GatewayMcpServer? server) || server is null
                || !string.Equals(server.Transport, "http", StringComparison.OrdinalIgnoreCase)
                || string.Equals(server.Scope, GatewayOwnershipScopes.User, StringComparison.OrdinalIgnoreCase))
                return Results.NotFound();
            string overrideKey = ConnectorCredentialKeys.BuildHttpUrlOverrideKey(connectorId, server.CredentialNamespace);
            IReadOnlyDictionary<string, string> overrides = await resolver.ResolveRemoteAsync(server.CredentialAssociationId, connectorId, server.CredentialNamespace, admin!.Id, [overrideKey], false, ct).ConfigureAwait(false);
            if (overrides.TryGetValue(overrideKey, out string? personalUrl) && Uri.TryCreate(personalUrl, UriKind.Absolute, out Uri? personal)
                && personal.Scheme is "http" or "https" && string.IsNullOrEmpty(personal.UserInfo)
                && !string.Equals(personal.AbsoluteUri, server.HttpUrl, StringComparison.Ordinal))
                return Results.Json(new CredentialUpdateResponse("System credentials are unavailable with a personal endpoint override."), KautukaJsonContext.Default.CredentialUpdateResponse, statusCode: StatusCodes.Status400BadRequest);
            if (!Uri.TryCreate(server.HttpUrl, UriKind.Absolute, out Uri? systemEndpoint) || server.CredentialNamespace is null)
                return Results.Json(new CredentialUpdateResponse("Connector destination is required."), KautukaJsonContext.Default.CredentialUpdateResponse, statusCode: StatusCodes.Status400BadRequest);
            SetCredentialsRequest? body = await context.Request.ReadFromJsonAsync(KautukaJsonContext.Default.SetCredentialsRequest, ct).ConfigureAwait(false);
            var declared = (server.Credentials ?? []).Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
            string[] removals = body?.RemoveKeys ?? [];
            if ((body?.Entries?.Any(pair => !string.IsNullOrWhiteSpace(pair.Value) && !declared.Contains(pair.Key)) ?? false)
                || removals.Any(key => !declared.Contains(key) || (body?.Entries?.TryGetValue(key, out string? supplied) == true && !string.IsNullOrWhiteSpace(supplied))))
                return Results.Json(new CredentialUpdateResponse("Invalid or conflicting credential removal."), KautukaJsonContext.Default.CredentialUpdateResponse, statusCode: StatusCodes.Status400BadRequest);
            Dictionary<string, string> updates = FilterAllowedEntries(body?.Entries, [.. declared])
                .ToDictionary(pair => ConnectorCredentialKeys.BuildRemoteCredentialKey(server.CredentialNamespace, systemEndpoint, pair.Key), pair => pair.Value, StringComparer.Ordinal);
            if (updates.Count == 0 && removals.Length == 0)
            {
                if (body is not null && (body.Entries is null || body.Entries.Values.All(string.IsNullOrWhiteSpace)))
                    return Results.Json(new CredentialUpdateResponse("Credentials unchanged."), KautukaJsonContext.Default.CredentialUpdateResponse);
                return Results.Json(new CredentialUpdateResponse("No valid entries to save."), KautukaJsonContext.Default.CredentialUpdateResponse, statusCode: StatusCodes.Status400BadRequest);
            }
            string[] removeKeys = [.. removals.Distinct(StringComparer.Ordinal).Select(key => ConnectorCredentialKeys.BuildRemoteCredentialKey(server.CredentialNamespace, systemEndpoint, key))];
            string[] lookup = [.. updates.Keys.Concat(removeKeys).Distinct(StringComparer.Ordinal)];
            (_, IReadOnlyList<string> previous) = server.CredentialAssociationId is null ? (Array.Empty<string>(), Array.Empty<string>())
                : await resolver.GetAssociationPresenceAsync(server.CredentialAssociationId, connectorId, server.CredentialNamespace, admin.Id, lookup, true, ct).ConfigureAwait(false);
            var previouslyConfigured = new HashSet<string>(previous, StringComparer.Ordinal);
            string operation = Guid.NewGuid().ToString("N");
            try
            {
                using (revisions.BeginExclusiveChange(connectorId, "system"))
                {
                    await catalog.MutateMcpCredentialAssociationAsync(connectorId, async (current, token) =>
                    {
                        GatewayUser? active = ActiveUser(users, admin.Id);
                        if (active is null || !GatewayOwnershipAccess.IsAdmin(active) || current.Scope != GatewayOwnershipScopes.System
                            || current.CredentialNamespace != server.CredentialNamespace || current.UpdatedAt != server.UpdatedAt || current.CredentialAssociationId != server.CredentialAssociationId)
                            throw new InvalidOperationException("Connector or authorization changed. Retry the update.");
                        string staged = await resolver.StageMutationAsync(operation, admin.Id, "system", connectorId, current.CredentialNamespace!, current.CredentialAssociationId, updates, removeKeys, token).ConfigureAwait(false);
                        if (ActiveUser(users, admin.Id) is not { } after || !GatewayOwnershipAccess.IsAdmin(after))
                            throw new InvalidOperationException("Connector authorization changed. Retry the update.");
                        return (staged, operation);
                    }, ct).ConfigureAwait(false);
                }
                foreach (RemoteMcpCredential declaration in server.Credentials ?? [])
                {
                    string storageKey = ConnectorCredentialKeys.BuildRemoteCredentialKey(server.CredentialNamespace, systemEndpoint, declaration.Key);
                    if (updates.ContainsKey(storageKey))
                        RemoteMcpAudit.Write(context, previouslyConfigured.Contains(storageKey) ? "credential.replaced" : "credential.added",
                            admin.Id, connectorId, "system", "succeeded", declaration.Key);
                }
                foreach (string key in removals.Distinct(StringComparer.Ordinal))
                {
                    bool removed = previouslyConfigured.Contains(ConnectorCredentialKeys.BuildRemoteCredentialKey(server.CredentialNamespace, systemEndpoint, key));
                    RemoteMcpAudit.Write(context, "credential.removed", admin.Id, connectorId, "system", removed ? "succeeded" : "unchanged", key);
                }
                return Results.Json(new CredentialUpdateResponse($"{updates.Count + removeKeys.Count(previouslyConfigured.Contains)} credential(s) updated."), KautukaJsonContext.Default.CredentialUpdateResponse);
            }
            catch (InvalidOperationException ex) { return Results.Json(new CredentialUpdateResponse(ex.Message), KautukaJsonContext.Default.CredentialUpdateResponse, statusCode: StatusCodes.Status409Conflict); }
        });

        return app;
    }

    private static GatewayUser? ActiveUser(IGatewayUserStore users, string id)
        => users.TryGetUserById(id, out GatewayUser? user) && user is { IsActive: true } ? user : null;

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
