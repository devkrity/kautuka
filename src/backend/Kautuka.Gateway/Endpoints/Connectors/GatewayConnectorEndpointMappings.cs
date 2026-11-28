using Kautuka.Gateway.Domain.AgentFiles;
using Kautuka.Gateway.Domain.Connectors;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Infrastructure.Connectors;
using Kautuka.Gateway.Infrastructure.Credentials;

namespace Kautuka.Gateway.Endpoints.Connectors;

internal static class GatewayConnectorEndpointMappings
{
    public static WebApplication MapGatewayConnectorEndpoints(this WebApplication app)
    {
        app.MapPost("/api/connectors/test", async (IMcpConnectorTestService testService, RemoteMcpCredentialRevisionTracker revisions, IGatewayUserStore users, CredentialResolverService credentialResolver, ConnectorOAuthService connectorOAuth, IGatewayCatalogStore catalog, KautukaDatabase database, IGatewayAuthGuard auth, HttpContext context, CancellationToken ct) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
                return Results.Unauthorized();

            GatewayConnectorTestRequest? request = await context.Request.ReadFromJsonAsync(
                KautukaJsonContext.Default.GatewayConnectorTestRequest,
                ct).ConfigureAwait(false);

            if (request is null)
            {
                return Results.Json(
                    new GatewayErrorResponse("Invalid connector test payload."),
                    KautukaJsonContext.Default.GatewayErrorResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            bool hasCallerEnvironment = request.TestEnv is { Count: > 0 };
            if (hasCallerEnvironment && !ConnectorEnvironmentKeyPolicy.TryValidate(request.TestEnv!.Keys, out string testEnvError))
            {
                return Results.Json(
                    new GatewayErrorResponse(testEnvError),
                    KautukaJsonContext.Default.GatewayErrorResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            string revisionKey = request.Id ?? string.Empty;
            string initialUserRevision = revisions.Get(revisionKey, user.Id, includeSystem: false);
            string initialCombinedRevision = revisions.Get(revisionKey, user.Id, includeSystem: true);
            bool savedConnector = TryGetAccessibleConnector(request, user, catalog, out GatewayMcpServer? saved);
            if (savedConnector && saved is not null)
            {
                if (!UsesStoredDestination(request, saved))
                {
                    return Results.Json(new GatewayErrorResponse("Connector test must use the saved destination."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
                }

                request = StoredDefinition(saved, request);
            }
            else if (!CanImportConnectors(user))
            {
                return Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }
            else if (string.Equals(request.Transport, "http", StringComparison.OrdinalIgnoreCase)
                && ProbeNetworkPolicy.GetDestinationFailure(request.HttpUrl, GatewayOwnershipAccess.IsAdmin(user)) is string destinationFailure)
            {
                return Results.Json(new GatewayErrorResponse(destinationFailure), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
            }
            else if (IsUntrustedBundlePath(user, database, request.BundlePath))
            {
                return Results.Json(new GatewayErrorResponse("Connector bundle path is not allowed."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
            }

            if (string.Equals(request.Transport, "http", StringComparison.OrdinalIgnoreCase))
            {
                if (request.TestEnv is { Count: > 0 })
                    return Results.Json(new GatewayErrorResponse("Remote MCP tests require stored credential references."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
                // Saved connector definitions are trusted only after the access and destination checks above.
                string? effectiveUrl = request.HttpUrl;
                bool personalDestination = false;
                if (saved is not null)
                {
                    string overrideKey = ConnectorCredentialKeys.BuildHttpUrlOverrideKey(saved.Id, saved.CredentialNamespace);
                    IReadOnlyDictionary<string, string> overrides = await credentialResolver.ResolveRemoteAsync(saved.CredentialAssociationId, saved.Id, saved.CredentialNamespace, user.Id, [overrideKey], false, ct).ConfigureAwait(false);
                    if (overrides.TryGetValue(overrideKey, out string? candidate)
                        && Uri.TryCreate(candidate, UriKind.Absolute, out Uri? overrideUri)
                        && overrideUri.Scheme is "http" or "https"
                        && string.IsNullOrEmpty(overrideUri.UserInfo)
                        && (!ProbeNetworkPolicy.IsConfirmedPrivate(candidate) || GatewayOwnershipAccess.IsAdmin(user)))
                    {
                        effectiveUrl = overrideUri.AbsoluteUri;
                        personalDestination = !string.Equals(effectiveUrl, saved.HttpUrl, StringComparison.Ordinal);
                    }
                }
                if (!Uri.TryCreate(effectiveUrl, UriKind.Absolute, out Uri? endpoint) || endpoint.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(endpoint.UserInfo))
                    return Results.Json(new GatewayErrorResponse("Connector destination is invalid."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
                bool includeSystem = saved is not null && !personalDestination && !string.Equals(saved.Scope, GatewayOwnershipScopes.User, StringComparison.OrdinalIgnoreCase);
                string revision = includeSystem ? initialCombinedRevision : initialUserRevision;
                if (!string.Equals(revision, revisions.Get(saved?.Id ?? revisionKey, user.Id, includeSystem), StringComparison.Ordinal)
                    || (saved is not null && (!catalog.TryGetMcpServer(saved.Id, out GatewayMcpServer? latestSnapshot) || latestSnapshot is null
                        || latestSnapshot.UpdatedAt != saved.UpdatedAt || latestSnapshot.CredentialNamespace != saved.CredentialNamespace)))
                    return Results.Json(new GatewayErrorResponse("Remote MCP configuration changed. Retry the operation."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status409Conflict);
                string? savedUpdatedAt = saved?.UpdatedAt;
                bool allowPrivate = GatewayOwnershipAccess.IsAdmin(user) || (saved is not null && !personalDestination && !string.Equals(saved.Scope, GatewayOwnershipScopes.User, StringComparison.OrdinalIgnoreCase));
                async Task RevalidateAsync(CancellationToken token)
                {
                    GatewayUser? currentUser = ActiveUser(users, user.Id);
                    if (currentUser is null)
                        throw new HttpRequestException("Caller is no longer active.");
                    bool currentPrivate = GatewayOwnershipAccess.IsAdmin(currentUser) || (saved is not null && !personalDestination && !string.Equals(saved.Scope, GatewayOwnershipScopes.User, StringComparison.OrdinalIgnoreCase));
                    if (currentPrivate != allowPrivate) throw new HttpRequestException("Remote MCP destination permission changed.");
                    if (!string.Equals(revision, revisions.Get(saved?.Id ?? revisionKey, user.Id, includeSystem), StringComparison.Ordinal))
                        throw new HttpRequestException("Remote MCP credentials changed. Retry the operation.");
                    if (saved is not null)
                    {
                        if (!catalog.TryGetMcpServer(saved.Id, out GatewayMcpServer? current) || current is null
                            || !GatewayOwnershipAccess.CanAccessConnector(currentUser, current) || !string.Equals(savedUpdatedAt, current.UpdatedAt, StringComparison.Ordinal))
                            throw new HttpRequestException("Remote MCP configuration changed. Retry the operation.");
                        string key = ConnectorCredentialKeys.BuildHttpUrlOverrideKey(saved.Id, saved.CredentialNamespace);
                        IReadOnlyDictionary<string, string> latest = await credentialResolver.ResolveRemoteAsync(current.CredentialAssociationId, current.Id, current.CredentialNamespace, user.Id, [key], false, token).ConfigureAwait(false);
                        string? candidate = latest.TryGetValue(key, out string? value) && Uri.TryCreate(value, UriKind.Absolute, out Uri? parsed)
                            && parsed.Scheme is "http" or "https" && string.IsNullOrEmpty(parsed.UserInfo)
                            && (!ProbeNetworkPolicy.IsConfirmedPrivate(value) || GatewayOwnershipAccess.IsAdmin(currentUser))
                            ? parsed.AbsoluteUri : current.HttpUrl;
                        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? latestEndpoint) || latestEndpoint != endpoint)
                            throw new HttpRequestException("Remote MCP destination changed. Retry the operation.");
                    }
                }
                request = new GatewayConnectorTestRequest
                {
                    Id = request.Id,
                    Transport = "http",
                    HttpUrl = endpoint.AbsoluteUri,
                    McpProtocolVersion = request.McpProtocolVersion,
                    AllowPrivateDestination = allowPrivate,
                    RemoteContext = new RemoteMcpRequestContext(endpoint, saved?.Id ?? string.Empty, user.Id,
                        saved is not null && !personalDestination && !string.Equals(saved.Scope, GatewayOwnershipScopes.User, StringComparison.OrdinalIgnoreCase),
                        [.. saved?.Credentials ?? []], [.. saved?.HttpHeaders ?? []],
                        (keys, token) => credentialResolver.ResolveRemoteAsync(saved?.CredentialAssociationId, saved?.Id ?? string.Empty, saved?.CredentialNamespace, user.Id, keys, includeSystem, token), RevalidateAsync,
                        () => revisions.Acquire(saved?.Id ?? revisionKey, user.Id, agentId: null, includeSystem), saved?.CredentialNamespace, request.McpProtocolVersion,
                        PreferIpv4: endpoint.IdnHost.Equals("mcp.exa.ai", StringComparison.OrdinalIgnoreCase),
                        RequiresCredentialReentry: saved?.RequiresCredentialReentry == true),
                };
            }
            else
            {
                request = await MergeResolvedCredentialsAsync(request, user, savedConnector ? saved : null, credentialResolver, connectorOAuth, ct).ConfigureAwait(false);
            }

            request.UserId ??= user.Id;
            bool mayManage = saved is not null && GatewayOwnershipAccess.CanManageConnector(user, saved);
            bool mayRefreshWebTools = !mayManage && saved is not null
                && saved.Id == "kautuka-web-tools"
                && saved.Scope == GatewayOwnershipScopes.System
                && saved.Transport == "container"
                && !string.IsNullOrWhiteSpace(saved.BundlePath)
                && saved.EnvKeys is not { Count: > 0 }
                && request.TestEnv is not { Count: > 0 }
                && !HasCurrentLocalTools(saved);
            bool maySaveDiscovery = !hasCallerEnvironment && (mayManage || mayRefreshWebTools);
            request.SaveDiscovery = maySaveDiscovery && !string.Equals(request.Transport, "http", StringComparison.OrdinalIgnoreCase);
            McpConnectorTestResult result = await testService.TestAsync(request, ct).ConfigureAwait(false);
            if (maySaveDiscovery && result.Success && result.ToolDefinitions is not null && saved is not null
                && string.Equals(saved.Transport, "container", StringComparison.OrdinalIgnoreCase))
            {
                string? digest = string.IsNullOrWhiteSpace(saved.BundlePath) ? null : result.SourceDigest;
                if (!IsCanonicalImageDigest(result.ImageDigest) || (!string.IsNullOrWhiteSpace(saved.BundlePath) && !IsCanonicalSourceDigest(digest)))
                    return Results.Json(new GatewayErrorResponse("Connector tools could not be saved without an immutable artifact digest."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
                if (mayRefreshWebTools && !string.Equals(digest, saved.RuntimeArtifactSourceDigest, StringComparison.OrdinalIgnoreCase))
                    return ConnectorEnablementConflict("The Web Tools bundle changed during discovery. Test it again.");
                try
                {
                    await catalog.SaveVerifiedLocalToolsAsync(saved.Id, saved.UpdatedAt, result.ToolDefinitions, digest, result.ImageDigest!, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
                {
                    return Results.Json(new GatewayErrorResponse(ex.Message), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status409Conflict);
                }
            }
            if (string.Equals(request.Transport, "http", StringComparison.OrdinalIgnoreCase))
                RemoteMcpAudit.Write(context, "connection.tested", user.Id,
                    request.Id ?? "unsaved", saved?.Scope ?? GatewayOwnershipScopes.User, result.Success ? "succeeded" : "failed");
            return Results.Json(
                new GatewayConnectorTestResponse(
                    result.Success,
                    result.Message,
                    result.ToolCount,
                    result.PackageChecked,
                    [.. result.ToolNames ?? []],
                    result.ToolDescriptions is null
                        ? new Dictionary<string, string?>(StringComparer.Ordinal)
                        : new Dictionary<string, string?>(result.ToolDescriptions, StringComparer.Ordinal)),
                KautukaJsonContext.Default.GatewayConnectorTestResponse);
        });

        app.MapPost("/api/connectors/import/mcpb", async (
            HttpRequest request,
            IGatewayCatalogStore catalog,
            ConnectorAuthCatalog connectorAuth,
            IMcpbPackageService mcpbPackageService,
            IMcpConnectorTestService mcpConnectorTestService,
            IGatewayAuthGuard auth,
            HttpContext context,
            CancellationToken ct) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            if (!CanImportConnectors(user))
            {
                return Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }

            IFormFile? archive = await GetArchiveAsync(request, ct).ConfigureAwait(false);
            if (archive is null)
            {
                return Results.Json(
                    new GatewayConnectorMcpbImportResponse(
                        "failed",
                        "MCPB bundle file is required.",
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        null),
                    KautukaJsonContext.Default.GatewayConnectorMcpbImportResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            IFormCollection form = await request.ReadFormAsync(ct).ConfigureAwait(false);

#pragma warning disable CA2007 // await using on Stream — no SynchronizationContext in ASP.NET Core
            await using Stream stream = archive.OpenReadStream();
#pragma warning restore CA2007
            McpbConnectorInspection inspection;
            try
            {
                inspection = await mcpbPackageService.InspectAsync(stream, archive.FileName, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                return Results.Json(
                    new GatewayConnectorMcpbImportResponse(
                        "failed",
                        ex.Message,
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        null),
                    KautukaJsonContext.Default.GatewayConnectorMcpbImportResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (connectorAuth.Find(inspection.ServerId) is not null)
            {
                return Results.Json(
                    new GatewayErrorResponse("This connector is installed with Kautuka. Turn it on and sign in."),
                    KautukaJsonContext.Default.GatewayErrorResponse,
                    statusCode: StatusCodes.Status409Conflict);
            }

            bool hasExisting = catalog.TryGetMcpServer(inspection.ServerId, out GatewayMcpServer? existing) && existing is not null;
            if (hasExisting && !GatewayOwnershipAccess.CanManageConnector(user, existing))
            {
                return Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }

            string scope = GatewayOwnershipAccess.ResolveCatalogScope(user, GetOptionalFormValue(form, "scope"));
            string? ownerUserId = GatewayOwnershipAccess.ResolveCatalogOwnerUserId(scope, user);

            McpConnectorTestResult validation = await mcpConnectorTestService
                .TestAsync(BuildImportValidationRequest(inspection.ServerId, inspection.Connector, user.Id), ct)
                .ConfigureAwait(false);

            if (validation.Success && validation.ToolDefinitions is not null && (!IsCanonicalImageDigest(validation.ImageDigest)
                || (!string.IsNullOrWhiteSpace(inspection.Connector.BundlePath) && !IsCanonicalSourceDigest(validation.SourceDigest))))
                return Results.Json(new GatewayErrorResponse("Connector tools could not be saved without an immutable image digest."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);

            if (!validation.Success && !validation.NetworkApprovalRequired)
            {
                return Results.Json(
                    new GatewayConnectorMcpbImportResponse(
                        "failed",
                        validation.Message,
                        inspection.ServerId,
                        inspection.ManifestVersion,
                        inspection.ServerType,
                        null),
                    KautukaJsonContext.Default.GatewayConnectorMcpbImportResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            GatewayMcpServer saved = await catalog.SaveMcpServerAsync(
                inspection.ServerId,
                BuildActivatedImportRequest(inspection.Connector, validation),
                scope,
                ownerUserId,
                ct).ConfigureAwait(false);
            if (validation.NetworkApprovalRequired)
                saved = await catalog.SaveMcpServerDiscoveryAsync(inspection.ServerId, null, null, ct).ConfigureAwait(false);

            return Results.Json(
                new GatewayConnectorMcpbImportResponse(
                    hasExisting ? "updated" : "created",
                    validation.NetworkApprovalRequired
                        ? "Connector saved. Tool discovery needs network approval; approve the request and test the connection again."
                        : hasExisting ? "Connector updated from MCPB bundle." : "Connector created from MCPB bundle.",
                    inspection.ServerId,
                    inspection.ManifestVersion,
                    inspection.ServerType,
                    ToConnectorPayload(saved)),
                KautukaJsonContext.Default.GatewayConnectorMcpbImportResponse);
        });

        app.MapPut("/api/connectors/{serverId}/discovery", async (
            string serverId,
            IGatewayCatalogStore catalog,
            IGatewayAuthGuard auth,
            HttpContext context,
            CancellationToken ct) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
                return Results.Unauthorized();

            if (!catalog.TryGetMcpServer(serverId, out GatewayMcpServer? existing) || existing is null)
            {
                return Results.Json(new GatewayErrorResponse("Connector not found"), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
            }

            if (!GatewayOwnershipAccess.CanManageConnector(user, existing))
            {
                return Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }

            GatewayConnectorUpsertRequest? request = await context.Request.ReadFromJsonAsync(
                KautukaJsonContext.Default.GatewayConnectorUpsertRequest,
                ct).ConfigureAwait(false);

            if (request is null || (request.ToolCount is null && request.ToolNames is null))
            {
                return Results.Json(
                    new GatewayErrorResponse("Invalid connector discovery payload."),
                    KautukaJsonContext.Default.GatewayErrorResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            GatewayMcpServer saved = await catalog.SaveMcpServerDiscoveryAsync(serverId, request.ToolCount, request.ToolNames, ct).ConfigureAwait(false);
            return Results.Json(ToConnectorPayload(saved), KautukaJsonContext.Default.GatewayConnectorListItem);
        });

        app.MapGet("/api/connectors", (IGatewayCatalogStore catalog, ConnectorUserEnablement enablement, ConnectorAuthCatalog connectorAuth, IGatewayAuthGuard auth, HttpContext context) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
                return Results.Unauthorized();

            return Results.Json(
                new GatewayConnectorsResponse([.. catalog.ListMcpServers().Where(server => GatewayOwnershipAccess.CanAccessConnector(user, server))
                    .Select(server => ToConnectorPayload(server, connectorAuth) with { Enabled = enablement.IsEnabled(user.Id, server.Id, server.Enabled) })]),
                KautukaJsonContext.Default.GatewayConnectorsResponse);
        });

        app.MapGet("/api/connectors/{serverId}", (string serverId, IGatewayCatalogStore catalog, ConnectorUserEnablement enablement, ConnectorAuthCatalog connectorAuth, IGatewayAuthGuard auth, HttpContext context) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
                return Results.Unauthorized();

            return catalog.TryGetMcpServer(serverId, out GatewayMcpServer? server) && server is not null && GatewayOwnershipAccess.CanAccessConnector(user, server)
                ? Results.Json(ToConnectorPayload(server, connectorAuth) with { Enabled = enablement.IsEnabled(user.Id, serverId, server.Enabled) }, KautukaJsonContext.Default.GatewayConnectorListItem)
                : Results.Json(new GatewayErrorResponse("Connector not found"), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
        });

        app.MapPut("/api/connectors/{serverId}/enabled", async (string serverId, IGatewayCatalogStore catalog,
            ConnectorUserEnablement enablement, RemoteMcpCredentialRevisionTracker revisions, IWorkspaceDesiredStatePublisher desiredState,
            IMcpConnectorTestService testService, IGatewayAuthGuard auth, HttpContext context, CancellationToken ct) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null) return Results.Unauthorized();
            GatewayConnectorUpsertRequest? request = await context.Request.ReadFromJsonAsync(
                KautukaJsonContext.Default.GatewayConnectorUpsertRequest, ct).ConfigureAwait(false);
            if (request?.Enabled is not bool enabled) return Results.BadRequest();
            if (!catalog.TryGetMcpServer(serverId, out GatewayMcpServer? visible) || visible is null
                || !GatewayOwnershipAccess.CanAccessConnector(user, visible)) return Results.NotFound();

            if (enabled && string.Equals(visible.Transport, "container", StringComparison.OrdinalIgnoreCase)
                && visible.EnvKeys is not { Count: > 0 } && !HasCurrentLocalTools(visible))
            {
                GatewayConnectorTestRequest probe = StoredDefinition(visible, new GatewayConnectorTestRequest { Id = serverId });
                probe.UserId = user.Id;
                probe.SaveDiscovery = true;
                McpConnectorTestResult result = await testService.TestAsync(probe, ct).ConfigureAwait(false);
                string? sourceDigest = string.IsNullOrWhiteSpace(visible.BundlePath) ? null : result.SourceDigest;
                if (!result.Success) return ConnectorEnablementConflict(result.Message);
                if (result.ToolDefinitions is null || !IsCanonicalImageDigest(result.ImageDigest)
                    || (!string.IsNullOrWhiteSpace(visible.BundlePath) && !IsCanonicalSourceDigest(sourceDigest)))
                    return ConnectorEnablementConflict("Connector tools could not be verified against an immutable image.");
                try
                {
                    visible = await catalog.SaveVerifiedLocalToolsAsync(serverId, visible.UpdatedAt, result.ToolDefinitions,
                        sourceDigest, result.ImageDigest!, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { return ConnectorEnablementConflict(ex.Message); }
            }

            bool saved = false;
            try
            {
                using IDisposable change = revisions.BeginExclusiveChange(serverId, "user:" + user.Id);
                await catalog.WithMcpServerLockAsync(serverId, _ =>
                {
                    if (catalog.TryGetMcpServer(serverId, out GatewayMcpServer? server)
                        && ReferenceEquals(server, visible) && GatewayOwnershipAccess.CanAccessConnector(user, server))
                    {
                        enablement.SetEnabled(user.Id, serverId, enabled);
                        saved = true;
                    }
                    return Task.CompletedTask;
                }, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { return ConnectorEnablementConflict(ex.Message); }
            if (!saved) return ConnectorEnablementConflict("Connector settings changed. Try again.");

            if (string.Equals(visible.Transport, "container", StringComparison.OrdinalIgnoreCase))
            {
                DesiredStatePublishResult published = await desiredState.TryPublishCurrentAsync("connector-user-enablement", ct).ConfigureAwait(false);
                if (!published.Accepted)
                    return ConnectorEnablementConflict("Your setting was saved, but Manager could not apply it yet. Retry the change.");
            }
            return Results.NoContent();
        });

        app.MapPost("/api/connectors/{serverId}", async (string serverId, IGatewayCatalogStore catalog, ConnectorAuthCatalog connectorAuth, IMcpConnectorTestService testService, CredentialResolverService credentialResolver, ConnectorOAuthService connectorOAuth, KautukaDatabase database, IGatewayAuthGuard auth, HttpContext context, CancellationToken ct) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
                return Results.Unauthorized();

            GatewayConnectorUpsertRequest? request = await context.Request.ReadFromJsonAsync(
                KautukaJsonContext.Default.GatewayConnectorUpsertRequest,
                ct).ConfigureAwait(false);

            if (request is null)
            {
                return Results.Json(
                    new GatewayErrorResponse("Invalid connector payload."),
                    KautukaJsonContext.Default.GatewayErrorResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            bool hasExisting = catalog.TryGetMcpServer(serverId, out GatewayMcpServer? existing) && existing is not null;
            if (!hasExisting && !CanImportConnectors(user))
            {
                return Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }

            if (hasExisting && !GatewayOwnershipAccess.CanManageConnector(user, existing))
            {
                return Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }

            if (connectorAuth.Find(serverId) is not null)
            {
                if (!hasExisting || existing is null)
                {
                    return Results.Json(
                        new GatewayErrorResponse("This connector is installed with Kautuka. Turn it on and sign in."),
                        KautukaJsonContext.Default.GatewayErrorResponse,
                        statusCode: StatusCodes.Status409Conflict);
                }

                request = ManagedConnectorSettings.WithEnabled(existing, request.Enabled);
            }

            if (IsUntrustedBundlePath(user, database, request.BundlePath))
            {
                return Results.Json(
                    new GatewayErrorResponse("Connector bundle path is not allowed."),
                    KautukaJsonContext.Default.GatewayErrorResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (IsBlockedPrivateConnectorDestination(user, request.Transport, request.HttpUrl))
            {
                return Results.Json(
                    new GatewayErrorResponse("Connector destination is not allowed."),
                    KautukaJsonContext.Default.GatewayErrorResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            string scope = GatewayOwnershipAccess.ResolveCatalogScope(user, request.Scope);
            string? ownerUserId = GatewayOwnershipAccess.ResolveCatalogOwnerUserId(scope, user);

            if (request.EnvKeys is { Length: > 0 }
                && !ConnectorEnvironmentKeyPolicy.TryValidate(request.EnvKeys.Where(static key => !string.IsNullOrWhiteSpace(key)).Select(static key => key.Trim()), out string envKeyError))
            {
                return Results.Json(
                    new GatewayErrorResponse(envKeyError),
                    KautukaJsonContext.Default.GatewayErrorResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            McpConnectorTestResult? discovery = null;
            bool disablingExisting = hasExisting && existing!.Enabled && request.Enabled == false;
            if (!disablingExisting && !string.Equals(request.Transport, "http", StringComparison.OrdinalIgnoreCase))
            {
                GatewayConnectorTestRequest candidate = BuildImportValidationRequest(serverId, request, user.Id);
                candidate = await MergeResolvedCredentialsAsync(candidate, user, existing, credentialResolver, connectorOAuth, ct).ConfigureAwait(false);
                candidate.UserId = user.Id;
                candidate.SaveDiscovery = true;
                discovery = await testService.TestAsync(candidate, ct).ConfigureAwait(false);
                if (discovery.Success && discovery.ToolDefinitions is not null && (!IsCanonicalImageDigest(discovery.ImageDigest)
                    || (!string.IsNullOrWhiteSpace(request.BundlePath) && !IsCanonicalSourceDigest(discovery.SourceDigest))))
                    return Results.Json(new GatewayErrorResponse("Connector tools could not be saved without an immutable image digest."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
                if (!discovery.Success && !discovery.NetworkApprovalRequired)
                    return Results.Json(new GatewayErrorResponse(discovery.Message), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
                request = BuildActivatedImportRequest(request, discovery);
            }

            GatewayMcpServer saved;
            try
            {
                saved = await catalog.SaveMcpServerAsync(serverId, request, scope, ownerUserId, ct).ConfigureAwait(false);
                if (discovery?.NetworkApprovalRequired == true)
                    saved = await catalog.SaveMcpServerDiscoveryAsync(serverId, null, null, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                if (string.Equals(request.Transport, "http", StringComparison.OrdinalIgnoreCase))
                    RemoteMcpAudit.Write(context, hasExisting ? "connector.updated" : "connector.created",
                        user.Id, serverId, scope, "failed");
                return Results.Json(
                    new GatewayErrorResponse(ex.Message),
                    KautukaJsonContext.Default.GatewayErrorResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (string.Equals(saved.Transport, "http", StringComparison.OrdinalIgnoreCase))
            {
                RemoteMcpAudit.Write(context, hasExisting ? "connector.updated" : "connector.created", user.Id, serverId, saved.Scope, "succeeded");
                if (existing is not null && existing.Enabled != saved.Enabled)
                    RemoteMcpAudit.Write(context, saved.Enabled ? "connector.enabled" : "connector.disabled", user.Id, serverId, saved.Scope, "succeeded");
            }
            return Results.Json(ToConnectorPayload(saved), KautukaJsonContext.Default.GatewayConnectorListItem);
        });

        app.MapDelete("/api/connectors/{serverId}", async (string serverId, IGatewayCatalogStore catalog, IGatewayAuthGuard auth, HttpContext context, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
                return Results.Unauthorized();

            ILogger logger = loggerFactory.CreateLogger("Kautuka.Gateway.Connectors");

            if (!catalog.TryGetMcpServer(serverId, out GatewayMcpServer? server) || server is null)
            {
                return Results.Json(new GatewayErrorResponse("Connector not found"), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
            }

            if (!GatewayOwnershipAccess.CanManageConnector(user, server))
            {
                return Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }

            if (server.IsInternalSystemConnector)
            {
                GatewayConnectorEndpointMappingsLog.InternalConnectorDeleteRejected(logger, serverId);
                return Results.Json(new GatewayErrorResponse("Internal system connectors cannot be deleted."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status409Conflict);
            }

            bool removed = await catalog.DeleteMcpServerAsync(serverId, ct).ConfigureAwait(false);
            if (string.Equals(server.Transport, "http", StringComparison.OrdinalIgnoreCase))
                RemoteMcpAudit.Write(context, "connector.deleted", user.Id, serverId, server.Scope, removed ? "succeeded" : "unchanged");
            return removed
                ? Results.Json(new GatewayDeletedResponse(true), KautukaJsonContext.Default.GatewayDeletedResponse)
                : Results.Json(new GatewayErrorResponse("Connector not found"), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
        });

        return app;
    }

    private static IResult ConnectorEnablementConflict(string message)
        => Results.Json(new GatewayErrorResponse(message), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status409Conflict);

    private static GatewayUser? ActiveUser(IGatewayUserStore users, string id)
        => users.TryGetUserById(id, out GatewayUser? user) && user is { IsActive: true } ? user : null;

    private static bool TryGetAccessibleConnector(
        GatewayConnectorTestRequest request,
        GatewayUser user,
        IGatewayCatalogStore catalog,
        out GatewayMcpServer? server)
    {
        server = null;
        return !string.IsNullOrWhiteSpace(request.Id)
            && catalog.TryGetMcpServer(request.Id, out server)
            && server is not null
            && GatewayOwnershipAccess.CanAccessConnector(user, server);
    }

    private static bool UsesStoredDestination(GatewayConnectorTestRequest request, GatewayMcpServer server)
    {
        if (!string.IsNullOrWhiteSpace(request.Transport)
            && !string.Equals(request.Transport, server.Transport, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(request.HttpUrl)
            && !string.Equals(request.HttpUrl.Trim(), server.HttpUrl?.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(request.LocalCommand))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(request.ContainerImage)
            && !string.Equals(request.ContainerImage, server.ContainerImage, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(request.ContainerCommand)
            && !string.Equals(request.ContainerCommand, server.ContainerCommand, StringComparison.Ordinal))
        {
            return false;
        }

        if (request.ContainerArgs is { Length: > 0 }
            && !server.ContainerArgs.SequenceEqual(request.ContainerArgs, StringComparer.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static async Task<GatewayConnectorTestRequest> MergeResolvedCredentialsAsync(
        GatewayConnectorTestRequest request,
        GatewayUser user,
        GatewayMcpServer? server,
        CredentialResolverService credentialResolver,
        ConnectorOAuthService connectorOAuth,
        CancellationToken ct)
    {
        var merged = request.TestEnv is { Count: > 0 }
            ? new Dictionary<string, string>(request.TestEnv, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        string? effectiveHttpUrl = request.HttpUrl;
        string? effectiveId = request.Id;
        bool storedDestination = server is not null && UsesStoredDestination(request, server);

        if (storedDestination && server is not null)
        {
            if (string.Equals(server.Transport, "container", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(request.Id)
                && !request.Id.Contains("::", StringComparison.Ordinal))
            {
                effectiveId = WorkspaceMcpConnectorIdentity.BuildScopedConnectorId(user.Id, server.Id);
            }

            string[] requiredKeys = server.EnvKeys is { Count: > 0 } ? [.. server.EnvKeys] : [];
            if (requiredKeys.Length > 0)
            {
                IReadOnlyDictionary<string, string> resolved = await credentialResolver
                    .GetResolvedAsync(
                        user.Id,
                        requiredKeys,
                        includeSystemScope: !string.Equals(server.Scope, GatewayOwnershipScopes.User, StringComparison.OrdinalIgnoreCase),
                        includeHeldSecrets: false,
                        ct)
                    .ConfigureAwait(false);

                foreach ((string key, string value) in resolved)
                {
                    if (!merged.ContainsKey(key) && !string.IsNullOrWhiteSpace(value))
                    {
                        merged[key] = value;
                    }
                }
            }
        }

        connectorOAuth.RemoveRefreshTokens(effectiveId ?? request.Id, merged);

        return new GatewayConnectorTestRequest
        {
            Id = effectiveId,
            Transport = request.Transport,
            HttpUrl = effectiveHttpUrl,
            LocalCommand = request.LocalCommand,
            LocalArgs = request.LocalArgs,
            ContainerImage = request.ContainerImage,
            ContainerCommand = request.ContainerCommand,
            ContainerArgs = request.ContainerArgs,
            ContainerSidecarName = request.ContainerSidecarName,
            BundlePath = request.BundlePath,
            ServerType = request.ServerType,
            ReadinessTimeoutSeconds = request.ReadinessTimeoutSeconds,
            JitCooldownSeconds = request.JitCooldownSeconds,
            HealthcheckUrl = request.HealthcheckUrl,
            HealthcheckIntervalMilliseconds = request.HealthcheckIntervalMilliseconds,
            McpProtocolVersion = request.McpProtocolVersion,
            EnvKeys = request.EnvKeys,
            TestEnv = merged,
            AllowPrivateDestination = GatewayOwnershipAccess.IsAdmin(user)
                || (server is not null
                    && !string.Equals(server.Scope, GatewayOwnershipScopes.User, StringComparison.OrdinalIgnoreCase)),
        };
    }

    private static bool IsUntrustedBundlePath(GatewayUser user, KautukaDatabase database, string? bundlePath)
    {
        if (GatewayOwnershipAccess.IsAdmin(user) || string.IsNullOrWhiteSpace(bundlePath))
        {
            return false;
        }

        string root = Path.GetFullPath(Path.Combine(database.DataDirectory, "mcpb-bundles"));
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(bundlePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }

        string comparisonRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!fullPath.StartsWith(comparisonRoot + Path.DirectorySeparatorChar, comparison))
        {
            return true;
        }

        return WorkspaceReparseGuard.ContainsReparsePoint(root, fullPath);
    }

    private static bool IsBlockedPrivateConnectorDestination(GatewayUser user, string? transport, string? httpUrl)
    {
        if (GatewayOwnershipAccess.IsAdmin(user)
            || (!string.IsNullOrWhiteSpace(transport)
                && !string.Equals(transport, "http", StringComparison.OrdinalIgnoreCase))
            || string.IsNullOrWhiteSpace(httpUrl))
        {
            return false;
        }

        return ProbeNetworkPolicy.IsConfirmedPrivate(httpUrl);
    }

    private static GatewayConnectorTestRequest StoredDefinition(GatewayMcpServer server, GatewayConnectorTestRequest request)
        => new()
        {
            Id = request.Id,
            Transport = server.Transport,
            HttpUrl = string.IsNullOrWhiteSpace(request.HttpUrl) ? server.HttpUrl : request.HttpUrl,
            ContainerImage = server.ContainerImage,
            ContainerCommand = server.ContainerCommand,
            ContainerArgs = server.ContainerArgs,
            ContainerSidecarName = server.ContainerSidecarName,
            BundlePath = server.BundlePath,
            ServerType = server.ServerType,
            ReadinessTimeoutSeconds = server.ReadinessTimeoutSeconds,
            JitCooldownSeconds = server.JitCooldownSeconds,
            HealthcheckUrl = server.HealthcheckUrl,
            HealthcheckIntervalMilliseconds = server.HealthcheckIntervalMilliseconds,
            McpProtocolVersion = server.McpProtocolVersion,
            EnvKeys = server.EnvKeys?.ToArray(),
            TestEnv = request.TestEnv,
            AllowPrivateDestination = true,
        };

    internal static GatewayConnectorListItem ToConnectorPayload(GatewayMcpServer server, ConnectorAuthCatalog? connectorAuth = null) => new(
        server.Id,
        server.Name,
        server.Description,
        server.IconUrl,
        server.PresetId,
        server.Transport,
        server.HttpUrl,
        server.ContainerImage,
        server.ContainerCommand,
        [.. server.ContainerArgs],
        server.ContainerSidecarName,
        server.BundlePath,
        server.ServerType,
        server.RuntimeArtifactType,
        server.RuntimeArtifactSourceDigest,
        server.RuntimeArtifactBuildStatus,
        server.ReadinessTimeoutSeconds,
        server.JitCooldownSeconds,
        server.HealthcheckUrl,
        server.HealthcheckIntervalMilliseconds,
        server.Enabled,
        server.IsInternalSystemConnector,
        !HasCurrentLocalTools(server) ? null : server.ToolCount,
        !HasCurrentLocalTools(server) ? [] : [.. server.ToolNames?.ToArray() ?? []],
        [.. server.EnvKeys?.ToArray() ?? []],
        server.Scope,
        server.OwnerUserId,
        server.CreatedAt,
        server.UpdatedAt,
        server.McpProtocolVersion,
        [.. server.Credentials ?? []],
        [.. server.HttpHeaders ?? []],
        server.RequiresCredentialReentry,
        server.Enabled,
        GatewayCatalogStore.ToDefinition(server).RequiresIsolation(connectorAuth),
        [.. (server.EnvKeys ?? []).Where(key => !ConnectorEnvironmentKeyPolicy.IsSupported(key, out _))
            .Select(key => { _ = ConnectorEnvironmentKeyPolicy.IsSupported(key, out string message); return message + " Edit this connector to choose a supported setting."; })]);

    private static bool HasCurrentLocalTools(GatewayMcpServer server)
        => !string.Equals(server.Transport, "container", StringComparison.OrdinalIgnoreCase)
            || (server.LocalToolDefinitions is not null && IsCanonicalImageDigest(server.LocalImageDigest)
                && (string.IsNullOrWhiteSpace(server.BundlePath)
                    || (!string.IsNullOrWhiteSpace(server.LocalArtifactDigest)
                        && string.Equals(server.LocalArtifactDigest, server.RuntimeArtifactSourceDigest, StringComparison.OrdinalIgnoreCase))));

    private static GatewayConnectorTestRequest BuildImportValidationRequest(string serverId, GatewayConnectorUpsertRequest connector, string userId)
        => new()
        {
            Id = serverId,
            UserId = userId,
            SaveDiscovery = true,
            Transport = connector.Transport ?? "container",
            LocalCommand = connector.LocalCommand,
            LocalArgs = connector.LocalArgs,
            ContainerImage = connector.ContainerImage,
            ContainerCommand = connector.ContainerCommand,
            ContainerArgs = connector.ContainerArgs,
            ContainerSidecarName = connector.ContainerSidecarName,
            BundlePath = connector.BundlePath,
            ServerType = connector.ServerType,
            ReadinessTimeoutSeconds = connector.ReadinessTimeoutSeconds,
            JitCooldownSeconds = connector.JitCooldownSeconds,
            McpProtocolVersion = connector.McpProtocolVersion,
            EnvKeys = connector.EnvKeys,
            TestEnv = connector.DiscoveryEnv is null ? null : connector.DiscoveryEnv
                .Where(pair => connector.EnvKeys?.Contains(pair.Key, StringComparer.Ordinal) == true)
                .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal),
        };

    private static GatewayConnectorUpsertRequest BuildActivatedImportRequest(
        GatewayConnectorUpsertRequest connector,
        McpConnectorTestResult validation)
        => new()
        {
            Scope = connector.Scope,
            Name = connector.Name,
            Description = connector.Description,
            IconUrl = connector.IconUrl,
            PresetId = connector.PresetId,
            Transport = connector.Transport,
            HttpUrl = connector.HttpUrl,
            LocalCommand = connector.LocalCommand,
            LocalArgs = connector.LocalArgs,
            ContainerImage = connector.ContainerImage,
            ContainerCommand = connector.ContainerCommand,
            ContainerArgs = connector.ContainerArgs,
            ContainerSidecarName = connector.ContainerSidecarName,
            BundlePath = connector.BundlePath,
            ServerType = connector.ServerType,
            RuntimeArtifactType = connector.RuntimeArtifactType,
            RuntimeArtifactSourceDigest = validation.Success && !string.IsNullOrWhiteSpace(connector.BundlePath) ? validation.SourceDigest : connector.RuntimeArtifactSourceDigest,
            RuntimeArtifactBuildStatus = connector.RuntimeArtifactType is null ? connector.RuntimeArtifactBuildStatus : "ready",
            ReadinessTimeoutSeconds = connector.ReadinessTimeoutSeconds,
            JitCooldownSeconds = connector.JitCooldownSeconds,
            HealthcheckUrl = connector.HealthcheckUrl,
            HealthcheckIntervalMilliseconds = connector.HealthcheckIntervalMilliseconds,
            McpProtocolVersion = connector.McpProtocolVersion,
            EnvKeys = connector.EnvKeys,
            Credentials = connector.Credentials,
            HttpHeaders = connector.HttpHeaders,
            ConfirmLegacyAuthReplacement = connector.ConfirmLegacyAuthReplacement,
            Enabled = connector.Enabled,
            ToolCount = validation.ToolCount,
            ToolNames = validation.ToolNames?.ToArray(),
            LocalToolDefinitions = validation.ToolDefinitions,
            LocalArtifactDigest = validation.Success && !string.IsNullOrWhiteSpace(connector.BundlePath) ? validation.SourceDigest : null,
            LocalImageDigest = validation.ImageDigest,
            LocalDiscoveryAttempted = true,
        };

    private static bool IsCanonicalImageDigest(string? digest)
        => digest is { Length: 71 } && digest.StartsWith("sha256:", StringComparison.Ordinal)
            && digest.AsSpan(7).ToString().All(Uri.IsHexDigit);

    private static bool IsCanonicalSourceDigest(string? digest)
        => digest is { Length: 64 } && digest.All(Uri.IsHexDigit);

    private static async Task<IFormFile?> GetArchiveAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        IFormCollection form = await request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        return form.Files.GetFile("file") ?? (form.Files.Count > 0 ? form.Files[0] : null);
    }

    private static string? GetOptionalFormValue(IFormCollection form, string key)
    {
        if (!form.TryGetValue(key, out Microsoft.Extensions.Primitives.StringValues values))
        {
            return null;
        }

        string? value = values.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool CanImportConnectors(GatewayUser user)
        => string.Equals(user.Role, "admin", StringComparison.OrdinalIgnoreCase)
            || user.CanImportConnectors;
}

internal static partial class GatewayConnectorEndpointMappingsLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Rejecting delete for internal system connector '{ConnectorId}'.")]
    public static partial void InternalConnectorDeleteRejected(ILogger logger, string connectorId);
}
