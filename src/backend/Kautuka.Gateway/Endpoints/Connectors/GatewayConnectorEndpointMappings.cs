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
        app.MapPost("/api/connectors/test", async (IMcpConnectorTestService testService, CredentialResolverService credentialResolver, ConnectorOAuthService connectorOAuth, IGatewayCatalogStore catalog, KautukaDatabase database, IGatewayAuthGuard auth, HttpContext context, CancellationToken ct) =>
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
                && !ProbeNetworkPolicy.IsAllowed(request.HttpUrl, GatewayOwnershipAccess.IsAdmin(user)))
            {
                return Results.Json(new GatewayErrorResponse("Connector test destination is not allowed."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
            }
            else if (IsUntrustedBundlePath(user, database, request.BundlePath))
            {
                return Results.Json(new GatewayErrorResponse("Connector bundle path is not allowed."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
            }

            request = await MergeResolvedCredentialsAsync(request, user, savedConnector ? saved : null, credentialResolver, connectorOAuth, ct).ConfigureAwait(false);

            McpConnectorTestResult result = await testService.TestAsync(request, ct).ConfigureAwait(false);
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
            catch (InvalidOperationException ex)
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
                .TestAsync(BuildImportValidationRequest(inspection.ServerId, inspection.Connector), ct)
                .ConfigureAwait(false);

            if (!validation.Success)
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

            return Results.Json(
                new GatewayConnectorMcpbImportResponse(
                    hasExisting ? "updated" : "created",
                    hasExisting ? "Connector updated from MCPB bundle." : "Connector created from MCPB bundle.",
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

        app.MapGet("/api/connectors", (IGatewayCatalogStore catalog, IGatewayAuthGuard auth, HttpContext context) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
                return Results.Unauthorized();

            return Results.Json(
                new GatewayConnectorsResponse([.. catalog.ListMcpServers().Where(server => GatewayOwnershipAccess.CanAccessConnector(user, server)).Select(ToConnectorPayload)]),
                KautukaJsonContext.Default.GatewayConnectorsResponse);
        });

        app.MapGet("/api/connectors/{serverId}", (string serverId, IGatewayCatalogStore catalog, IGatewayAuthGuard auth, HttpContext context) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
                return Results.Unauthorized();

            return catalog.TryGetMcpServer(serverId, out GatewayMcpServer? server) && GatewayOwnershipAccess.CanAccessConnector(user, server)
                ? Results.Json(ToConnectorPayload(server!), KautukaJsonContext.Default.GatewayConnectorListItem)
                : Results.Json(new GatewayErrorResponse("Connector not found"), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
        });

        app.MapPost("/api/connectors/{serverId}", async (string serverId, IGatewayCatalogStore catalog, ConnectorAuthCatalog connectorAuth, KautukaDatabase database, IGatewayAuthGuard auth, HttpContext context, CancellationToken ct) =>
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

            GatewayMcpServer saved;
            try
            {
                saved = await catalog.SaveMcpServerAsync(serverId, request, scope, ownerUserId, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(
                    new GatewayErrorResponse(ex.Message),
                    KautukaJsonContext.Default.GatewayErrorResponse,
                    statusCode: StatusCodes.Status400BadRequest);
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
            return removed
                ? Results.Json(new GatewayDeletedResponse(true), KautukaJsonContext.Default.GatewayDeletedResponse)
                : Results.Json(new GatewayErrorResponse("Connector not found"), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
        });

        return app;
    }

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
        bool personalHttpDestination = false;

        if (storedDestination && server is not null)
        {
            if (string.Equals(server.Transport, "container", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(request.Id)
                && !request.Id.Contains("::", StringComparison.Ordinal))
            {
                effectiveId = WorkspaceMcpConnectorIdentity.BuildScopedConnectorId(user.Id, server.Id);
            }

            if (string.IsNullOrWhiteSpace(effectiveHttpUrl)
                && string.Equals(server.Transport, "http", StringComparison.OrdinalIgnoreCase))
            {
                string overrideKey = ConnectorCredentialKeys.BuildHttpUrlOverrideKey(server.Id);
                IReadOnlyDictionary<string, string> userOverride = await credentialResolver
                    .GetResolvedAsync(user.Id, [overrideKey], includeSystemScope: false, includeHeldSecrets: false, ct)
                    .ConfigureAwait(false);
                if (userOverride.TryGetValue(overrideKey, out string? userOverrideUrl)
                    && !string.IsNullOrWhiteSpace(userOverrideUrl))
                {
                    effectiveHttpUrl = userOverrideUrl.Trim();
                    personalHttpDestination = true;
                }
                else
                {
                    IReadOnlyDictionary<string, string> systemOverride = await credentialResolver
                        .GetResolvedAsync(user.Id, [overrideKey], includeSystemScope: true, includeHeldSecrets: false, ct)
                        .ConfigureAwait(false);
                    if (systemOverride.TryGetValue(overrideKey, out string? systemOverrideUrl)
                        && !string.IsNullOrWhiteSpace(systemOverrideUrl))
                    {
                        effectiveHttpUrl = systemOverrideUrl.Trim();
                    }
                }
            }

            string[] requiredKeys = server.EnvKeys is { Count: > 0 } ? [.. server.EnvKeys] : [];
            if (requiredKeys.Length > 0)
            {
                IReadOnlyDictionary<string, string> resolved = await credentialResolver
                    .GetResolvedAsync(
                        user.Id,
                        requiredKeys,
                        includeSystemScope: !personalHttpDestination
                            && !string.Equals(server.Scope, GatewayOwnershipScopes.User, StringComparison.OrdinalIgnoreCase),
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
                    && !personalHttpDestination
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

    private static GatewayConnectorListItem ToConnectorPayload(GatewayMcpServer server) => new(
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
        server.ToolCount,
        [.. server.ToolNames?.ToArray() ?? []],
        [.. server.EnvKeys?.ToArray() ?? []],
        server.Scope,
        server.OwnerUserId,
        server.CreatedAt,
        server.UpdatedAt,
        server.McpProtocolVersion);

    private static GatewayConnectorTestRequest BuildImportValidationRequest(string serverId, GatewayConnectorUpsertRequest connector)
        => new()
        {
            Id = serverId,
            Transport = connector.Transport ?? "container",
            ContainerImage = connector.ContainerImage,
            ContainerCommand = connector.ContainerCommand,
            ContainerArgs = connector.ContainerArgs,
            BundlePath = connector.BundlePath,
            ServerType = connector.ServerType,
            ReadinessTimeoutSeconds = connector.ReadinessTimeoutSeconds,
            JitCooldownSeconds = connector.JitCooldownSeconds,
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
            RuntimeArtifactSourceDigest = connector.RuntimeArtifactSourceDigest,
            RuntimeArtifactBuildStatus = "ready",
            ReadinessTimeoutSeconds = connector.ReadinessTimeoutSeconds,
            JitCooldownSeconds = connector.JitCooldownSeconds,
            HealthcheckUrl = connector.HealthcheckUrl,
            HealthcheckIntervalMilliseconds = connector.HealthcheckIntervalMilliseconds,
            EnvKeys = connector.EnvKeys,
            Enabled = connector.Enabled,
            ToolCount = validation.ToolCount,
            ToolNames = validation.ToolNames?.ToArray(),
        };

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
