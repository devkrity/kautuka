using System.Text.Json;

using Grpc.Core;

using Kautuka.Gateway.Domain.Connectors;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Infrastructure.Credentials;
using Kautuka.Protos;

namespace Kautuka.Gateway.Endpoints.Connectors;

internal static class ConnectorDraftEndpointMappings
{
    public static WebApplication MapConnectorDraftEndpoints(this WebApplication app)
    {
        app.MapGet("/api/credentials/connector-drafts", async (HttpContext context, IGatewayAuthGuard auth, CredentialResolverService resolver, CancellationToken ct) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null) return Results.Unauthorized();
            try
            {
                IReadOnlyList<ConnectorDraftMetadata> drafts = await resolver.ListDraftsAsync(user.Id, ct).ConfigureAwait(false);
                ConnectorDraftPendingItem[] items = [.. drafts.Select(draft => new ConnectorDraftPendingItem(draft.DraftId, draft.ConnectorId,
                    DateTimeOffset.FromUnixTimeSeconds(draft.ExpiresAtUnixSeconds), draft.CredentialScope, Deserialize(draft), draft.EffectiveEndpoint))];
                return Results.Json(new ConnectorDraftListPayload(items), KautukaJsonContext.Default.ConnectorDraftListPayload);
            }
            catch (RpcException ex) { return DraftFailure(ex); }
        });

        app.MapPost("/api/credentials/connector-drafts", async (HttpContext context, IGatewayAuthGuard auth, IGatewayCatalogStore catalog, CredentialResolverService resolver, CancellationToken ct) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null) return Results.Unauthorized();
            CreateConnectorDraftPayload? body = await context.Request.ReadFromJsonAsync(KautukaJsonContext.Default.CreateConnectorDraftPayload, ct).ConfigureAwait(false);
            if (body?.Candidate is null || string.IsNullOrWhiteSpace(body.ConnectorId) || body.ConnectorId.Length > 128 || body.CredentialScope is not ("user" or "system"))
                return BadRequest("Invalid connector draft.");
            GatewayConnectorUpsertRequest candidate = body.Candidate;
            if (!string.Equals(candidate.Transport, "http", StringComparison.OrdinalIgnoreCase) || candidate.EnvKeys is { Length: > 0 }
                || candidate.Credentials is null || candidate.HttpHeaders is null
                || !string.IsNullOrWhiteSpace(candidate.LocalCommand) || candidate.LocalArgs is { Length: > 0 }
                || !string.IsNullOrWhiteSpace(candidate.ContainerImage) || !string.IsNullOrWhiteSpace(candidate.ContainerCommand)
                || candidate.ContainerArgs is { Length: > 0 } || !string.IsNullOrWhiteSpace(candidate.ContainerSidecarName)
                || !string.IsNullOrWhiteSpace(candidate.BundlePath) || !string.IsNullOrWhiteSpace(candidate.ServerType)
                || !string.IsNullOrWhiteSpace(candidate.RuntimeArtifactType) || !string.IsNullOrWhiteSpace(candidate.RuntimeArtifactSourceDigest)
                || !string.IsNullOrWhiteSpace(candidate.RuntimeArtifactBuildStatus) || candidate.ReadinessTimeoutSeconds is not null
                || candidate.JitCooldownSeconds is not null || !string.IsNullOrWhiteSpace(candidate.HealthcheckUrl) || candidate.HealthcheckIntervalMilliseconds is not null)
                return BadRequest("Draft requires a complete remote connector configuration.");
            try
            {
                _ = Uri.TryCreate(candidate.HttpUrl, UriKind.Absolute, out Uri? endpoint);
                RemoteMcpConfigurationValidator.ValidateEndpoint(endpoint);
                RemoteMcpConfigurationValidator.Validate("http", candidate.Credentials, candidate.HttpHeaders);
            }
            catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
            candidate = NormalizeCandidate(candidate);
            catalog.TryGetMcpServer(body.ConnectorId, out GatewayMcpServer? existing);
            Uri? baseEndpoint = await ResolveEffectiveEndpointAsync(user, existing, candidate, resolver, ct).ConfigureAwait(false);
            bool canManage = existing is null || GatewayOwnershipAccess.CanManageConnector(user, existing);
            if (existing?.RequiresCredentialReentry == true && !canManage) return Forbidden();
            if (existing?.RequiresCredentialReentry == true && !candidate.ConfirmLegacyAuthReplacement)
                return BadRequest("Confirm the new authentication choice before testing or saving this connector.");
            Uri requestedEndpoint = new(candidate.HttpUrl!, UriKind.Absolute);
            bool priorPersonal = existing is not null && baseEndpoint is not null && !string.Equals(baseEndpoint.AbsoluteUri, existing.HttpUrl, StringComparison.Ordinal);
            Uri effectiveEndpoint = canManage && priorPersonal ? baseEndpoint! : requestedEndpoint;
            bool personal = existing is not null && !string.Equals(effectiveEndpoint.AbsoluteUri, existing.HttpUrl, StringComparison.Ordinal);
            bool personalCandidate = !canManage && existing is not null;
            if (!Authorized(user, existing, candidate, body.CredentialScope, personalCandidate)) return Forbidden();
            if (body.CredentialScope == "system" && personal) return Forbidden();
            string? destinationFailure = !GatewayOwnershipAccess.IsAdmin(user) && (existing is null || existing.Scope == GatewayOwnershipScopes.User || personal)
                ? ProbeNetworkPolicy.GetDestinationFailure(effectiveEndpoint.AbsoluteUri, allowPrivate: false)
                : null;
            if (destinationFailure is not null) return BadRequest(destinationFailure);
            string scope = GatewayOwnershipAccess.ResolveCatalogScope(user, candidate.Scope);
            if (body.CredentialScope == "system" && (existing is not null ? existing.Scope != GatewayOwnershipScopes.System : scope != GatewayOwnershipScopes.System)) return Forbidden();
            bool changedDestination = existing is not null && !string.Equals(baseEndpoint?.AbsoluteUri, effectiveEndpoint.AbsoluteUri, StringComparison.Ordinal);
            var declared = candidate.Credentials!.Select(credential => credential.Key).ToHashSet(StringComparer.Ordinal);
            string[] removals = body.RemoveKeys ?? [];
            if (removals.Any(key => !declared.Contains(key) || (body.Entries?.TryGetValue(key, out string? value) == true && !string.IsNullOrWhiteSpace(value)))
                || (body.Entries?.Any(entry => !declared.Contains(entry.Key)) ?? false)) return BadRequest("Invalid or conflicting credential mutation.");
            string credentialNamespace = existing?.CredentialNamespace ?? Guid.NewGuid().ToString("N");
            var storageKeys = candidate.Credentials!.Select(credential => ConnectorCredentialKeys.BuildRemoteCredentialKey(credentialNamespace, effectiveEndpoint, credential.Key)).ToList();
            var entries = (body.Entries ?? []).Where(entry => !string.IsNullOrWhiteSpace(entry.Value))
                .ToDictionary(entry => ConnectorCredentialKeys.BuildRemoteCredentialKey(credentialNamespace, effectiveEndpoint, entry.Key), entry => entry.Value, StringComparer.Ordinal);
            string[] removed = [.. removals.Distinct(StringComparer.Ordinal).Select(key => ConnectorCredentialKeys.BuildRemoteCredentialKey(credentialNamespace, effectiveEndpoint, key))];
            if (personalCandidate && changedDestination)
            {
                string key = ConnectorCredentialKeys.BuildHttpUrlOverrideKey(body.ConnectorId, credentialNamespace);
                storageKeys.Add(key); entries[key] = effectiveEndpoint.AbsoluteUri;
            }
            if (storageKeys.Any(key => key.Length > 256)) return BadRequest("Connector credential key is too long.");
            string candidateJson = JsonSerializer.Serialize(candidate, KautukaJsonContext.Default.GatewayConnectorUpsertRequest);
            try
            {
                ConnectorDraftMetadata draft = await resolver.CreateDraftAsync(Guid.NewGuid().ToString("N"), user.Id, body.ConnectorId, body.CredentialScope,
                    candidateJson, existing?.UpdatedAt ?? string.Empty, effectiveEndpoint.AbsoluteUri, baseEndpoint?.AbsoluteUri ?? string.Empty,
                    !changedDestination && existing is not null, credentialNamespace, storageKeys, entries, removed, existing?.CredentialAssociationId, ct).ConfigureAwait(false);
                RemoteMcpAudit.Write(context, "draft.created", user.Id, body.ConnectorId, body.CredentialScope, "succeeded", draft.DraftId);
                return Results.Json(new ConnectorDraftCreatedResponse(draft.DraftId, DateTimeOffset.FromUnixTimeSeconds(draft.ExpiresAtUnixSeconds)), KautukaJsonContext.Default.ConnectorDraftCreatedResponse);
            }
            catch (RpcException ex) { return DraftFailure(ex); }
        });

        app.MapDelete("/api/credentials/connector-drafts/{draftId}", async (string draftId, HttpContext context, IGatewayAuthGuard auth, IGatewayCatalogStore catalog, CredentialResolverService resolver, RemoteMcpCredentialRevisionTracker revisions, IGatewayUserStore users, CancellationToken ct) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null) return Results.Unauthorized();
            try
            {
                ConnectorDraftMetadata draft;
                try { draft = await resolver.GetDraftAsync(draftId, user.Id, "user", ct).ConfigureAwait(false); }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound) { draft = await resolver.GetStagedDraftAsync(draftId, user.Id, ct).ConfigureAwait(false); }
                using (revisions.BeginPolicyChange("draft", draft.DraftId))
                {
                    await catalog.WithMcpServerLockAsync(draft.ConnectorId, async token =>
                    {
                        if (ActiveUser(users, user.Id) is null) throw new InvalidOperationException("Caller is no longer active.");
                        _ = await resolver.CancelDraftAsync(draftId, user.Id, "user", token).ConfigureAwait(false);
                    }, ct).ConfigureAwait(false);
                    RemoteMcpAudit.Write(context, "draft.canceled", user.Id, draft.ConnectorId, draft.CredentialScope, "succeeded", draftId);
                    return Results.Ok();
                }
            }
            catch (RpcException ex) { return DraftFailure(ex); }
            catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
        });

        app.MapPost("/api/connectors/drafts/{draftId}/test", async (string draftId, HttpContext context, IGatewayAuthGuard auth, IGatewayCatalogStore catalog, CredentialResolverService resolver, RemoteMcpCredentialRevisionTracker revisions, IGatewayUserStore users, IMcpConnectorTestService testService, CancellationToken ct) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null) return Results.Unauthorized();
            try
            {
                ConnectorDraftMetadata draft = await resolver.GetDraftAsync(draftId, user.Id, "user", ct).ConfigureAwait(false);
                GatewayConnectorUpsertRequest candidate = Deserialize(draft);
                string userRevision = revisions.Get(draft.ConnectorId, user.Id, includeSystem: false);
                string combinedRevision = revisions.Get(draft.ConnectorId, user.Id, includeSystem: true);
                catalog.TryGetMcpServer(draft.ConnectorId, out GatewayMcpServer? existing);
                if (!BaseMatches(existing, draft)) return BadRequest("Connector draft is stale.");
                Uri endpoint = new(draft.EffectiveEndpoint, UriKind.Absolute);
                Uri? currentEndpoint = await ResolveEffectiveEndpointAsync(user, existing, candidate, resolver, ct).ConfigureAwait(false);
                bool personal = existing is not null && !string.Equals(endpoint.AbsoluteUri, existing.HttpUrl, StringComparison.Ordinal);
                if (!Authorized(user, existing, candidate, draft.CredentialScope, existing is not null && !GatewayOwnershipAccess.CanManageConnector(user, existing))) return Forbidden();
                if (!string.Equals(currentEndpoint?.AbsoluteUri ?? string.Empty, draft.BaseEndpoint, StringComparison.Ordinal) || (draft.CredentialScope == "system" && personal)) return BadRequest("Connector draft destination changed.");
                bool includeSystem = draft.UseDurableCredentials && !personal && (existing is not null
                    ? !string.Equals(existing.Scope, GatewayOwnershipScopes.User, StringComparison.OrdinalIgnoreCase)
                    : GatewayOwnershipAccess.ResolveCatalogScope(user, candidate.Scope) == GatewayOwnershipScopes.System);
                string revision = includeSystem ? combinedRevision : userRevision;
                if (!string.Equals(revision, revisions.Get(draft.ConnectorId, user.Id, includeSystem), StringComparison.Ordinal)
                    || (catalog.TryGetMcpServer(draft.ConnectorId, out GatewayMcpServer? latestSnapshot) ? !BaseMatches(latestSnapshot, draft) : !BaseMatches(null, draft)))
                    return BadRequest("Connector draft configuration or credentials changed.");
                bool allowPrivate = GatewayOwnershipAccess.IsAdmin(user) || (existing is not null && !personal && existing.Scope == GatewayOwnershipScopes.System);
                async Task Revalidate(CancellationToken token)
                {
                    GatewayUser? currentUser = ActiveUser(users, user.Id);
                    catalog.TryGetMcpServer(draft.ConnectorId, out GatewayMcpServer? current);
                    if (currentUser is null || !Authorized(currentUser, current, candidate, draft.CredentialScope, current is not null && !GatewayOwnershipAccess.CanManageConnector(currentUser, current)) || !BaseMatches(current, draft)
                        || (GatewayOwnershipAccess.IsAdmin(currentUser) || (current is not null && !personal && current.Scope == GatewayOwnershipScopes.System)) != allowPrivate
                        || !string.Equals(revision, revisions.Get(draft.ConnectorId, user.Id, includeSystem), StringComparison.Ordinal))
                        throw new HttpRequestException("Connector draft authorization or configuration changed.");
                    if (!string.Equals((await ResolveEffectiveEndpointAsync(currentUser, current, candidate, resolver, token).ConfigureAwait(false))?.AbsoluteUri ?? string.Empty, draft.BaseEndpoint, StringComparison.Ordinal))
                        throw new HttpRequestException("Connector draft destination changed.");
                    ConnectorDraftMetadata latest = await resolver.GetDraftAsync(draftId, user.Id, "user", token).ConfigureAwait(false);
                    if (latest.CandidateJson != draft.CandidateJson) throw new HttpRequestException("Connector draft is no longer available.");
                }
                var remote = new RemoteMcpRequestContext(endpoint, draft.ConnectorId, user.Id, includeSystem,
                    candidate.Credentials!, candidate.HttpHeaders!,
                    (keys, token) => resolver.ResolveDraftAsync(draftId, user.Id, draft.CredentialScope, keys, includeSystem, token), Revalidate,
                    () =>
                    {
                        RemoteMcpOperationLease lease = revisions.Acquire(draft.ConnectorId, user.Id, null, includeSystem, draftId);
                        lease.ScheduleExpiry(DateTimeOffset.FromUnixTimeSeconds(draft.ExpiresAtUnixSeconds));
                        return lease;
                    }, draft.CredentialNamespace, candidate.McpProtocolVersion,
                    PreferIpv4: endpoint.IdnHost.Equals("mcp.exa.ai", StringComparison.OrdinalIgnoreCase));
                McpConnectorTestResult result = await testService.TestAsync(new GatewayConnectorTestRequest
                {
                    Id = draft.ConnectorId,
                    Transport = "http",
                    HttpUrl = endpoint.AbsoluteUri,
                    McpProtocolVersion = candidate.McpProtocolVersion,
                    AllowPrivateDestination = allowPrivate,
                    RemoteContext = remote,
                }, ct).ConfigureAwait(false);
                RemoteMcpAudit.Write(context, "draft.tested", user.Id, draft.ConnectorId, draft.CredentialScope, result.Success ? "succeeded" : "failed", draftId);
                return Results.Json(new GatewayConnectorTestResponse(result.Success, result.Message, result.ToolCount, result.PackageChecked,
                    [.. result.ToolNames ?? []], result.ToolDescriptions is null ? new Dictionary<string, string?>(StringComparer.Ordinal) : new Dictionary<string, string?>(result.ToolDescriptions, StringComparer.Ordinal)), KautukaJsonContext.Default.GatewayConnectorTestResponse);
            }
            catch (RpcException ex) { return DraftFailure(ex); }
            catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
        });

        app.MapPost("/api/connectors/drafts/{draftId}/save", async (string draftId, HttpContext context, IGatewayAuthGuard auth, IGatewayCatalogStore catalog, CredentialResolverService resolver, RemoteMcpCredentialRevisionTracker revisions, IGatewayUserStore users, CancellationToken ct) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null) return Results.Unauthorized();
            try
            {
                ConnectorDraftMetadata draft;
                try { draft = await resolver.GetDraftAsync(draftId, user.Id, "user", ct).ConfigureAwait(false); }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound) { draft = await resolver.GetStagedDraftAsync(draftId, user.Id, ct).ConfigureAwait(false); }
                GatewayConnectorUpsertRequest candidate = Deserialize(draft);
                catalog.TryGetMcpServer(draft.ConnectorId, out GatewayMcpServer? current);
                Uri? currentEndpoint = await ResolveEffectiveEndpointAsync(user, current, candidate, resolver, ct).ConfigureAwait(false);
                Uri effective = new(draft.EffectiveEndpoint, UriKind.Absolute);
                bool personal = current is not null && !string.Equals(effective.AbsoluteUri, current.HttpUrl, StringComparison.Ordinal);
                bool personalCandidate = current is not null && !GatewayOwnershipAccess.CanManageConnector(user, current);
                if (!Authorized(user, current, candidate, draft.CredentialScope, personalCandidate)) return Forbidden();
                bool committed = current?.CredentialOperationId == draftId;
                if ((!committed && !string.Equals(currentEndpoint?.AbsoluteUri ?? string.Empty, draft.BaseEndpoint, StringComparison.Ordinal))
                    || (draft.CredentialScope == "system" && personal))
                    return BadRequest("Connector draft destination changed.");
                bool persistConfiguration = current is null || (GatewayOwnershipAccess.CanManageConnector(user, current) && (!GatewayCatalogStore.RemoteCandidateMatches(current, candidate) || (current.RequiresCredentialReentry && candidate.ConfirmLegacyAuthReplacement)));
                string scope = current?.Scope ?? GatewayOwnershipAccess.ResolveCatalogScope(user, candidate.Scope);
                string? owner = current?.OwnerUserId ?? GatewayOwnershipAccess.ResolveCatalogOwnerUserId(scope, user);
                string revisionScope = draft.CredentialScope == "system" ? "system" : "user:" + user.Id;
                using (revisions.BeginExclusiveChange(draft.ConnectorId, revisionScope))
                {
                    bool AuthorizedNow(GatewayMcpServer? latest) => ActiveUser(users, user.Id) is { } actor && Authorized(actor, latest, candidate, draft.CredentialScope, personalCandidate);
                    GatewayMcpServer saved;
                    if (persistConfiguration)
                    {
                        saved = await catalog.ActivateAssociationDraftAsync(draft.ConnectorId, candidate, draft.BaseUpdatedAt, draft.CredentialNamespace, draft.SourceAssociationId, draftId, scope, owner, AuthorizedNow,
                            token => resolver.StageAssociationAsync(draftId, user.Id, draft.CredentialScope, draft.SourceAssociationId, token), ct).ConfigureAwait(false);
                    }
                    else
                    {
                        saved = await catalog.MutateMcpCredentialAssociationAsync(draft.ConnectorId, async (latest, token) =>
                        {
                            if (!AuthorizedNow(latest) || latest.CredentialNamespace != draft.CredentialNamespace || !GatewayCatalogStore.RemoteCandidateMatches(latest, candidate, ignoreHttpUrl: true))
                                throw new InvalidOperationException("Connector draft is no longer authorized.");
                            if (latest.CredentialOperationId == draftId && latest.CredentialSourceId == draft.SourceAssociationId && latest.CredentialAssociationId is not null)
                                return (latest.CredentialAssociationId, draftId);
                            if (!BaseMatches(latest, draft) || (latest.CredentialAssociationId ?? string.Empty) != draft.SourceAssociationId)
                                throw new InvalidOperationException("Connector configuration or credentials changed since the draft was created.");
                            string staged = await resolver.StageAssociationAsync(draftId, user.Id, draft.CredentialScope, draft.SourceAssociationId, token).ConfigureAwait(false);
                            if (!AuthorizedNow(latest)) throw new InvalidOperationException("Connector draft is no longer authorized.");
                            return (staged, draftId);
                        }, ct).ConfigureAwait(false);
                    }
                    if (persistConfiguration && !committed)
                    {
                        RemoteMcpAudit.Write(context, current is null ? "connector.created" : "connector.updated", user.Id, draft.ConnectorId, saved.Scope, "succeeded");
                        if (current is not null && current.Enabled != saved.Enabled)
                            RemoteMcpAudit.Write(context, saved.Enabled ? "connector.enabled" : "connector.disabled", user.Id, draft.ConnectorId, saved.Scope, "succeeded");
                    }
                    RemoteMcpAudit.Write(context, "draft.saved", user.Id, draft.ConnectorId, draft.CredentialScope, "succeeded", draftId);
                    try { await resolver.DiscardDraftInputAsync(draftId, user.Id, ct).ConfigureAwait(false); }
                    catch (RpcException) { /* Temporary input expires even if this cleanup is interrupted. */ }
                    return Results.Json(GatewayConnectorEndpointMappings.ToConnectorPayload(saved), KautukaJsonContext.Default.GatewayConnectorListItem);
                }
            }
            catch (RpcException ex) { return DraftFailure(ex); }
            catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
        });
        return app;
    }

    private static GatewayUser? ActiveUser(IGatewayUserStore users, string id)
        => users.TryGetUserById(id, out GatewayUser? user) && user is { IsActive: true } ? user : null;

    private static async Task<Uri?> ResolveEffectiveEndpointAsync(GatewayUser user, GatewayMcpServer? server, GatewayConnectorUpsertRequest candidate, CredentialResolverService resolver, CancellationToken ct)
    {
        if (server is null) return new Uri(candidate.HttpUrl!, UriKind.Absolute);
        _ = Uri.TryCreate(server.HttpUrl, UriKind.Absolute, out Uri? configured);
        string overrideKey = ConnectorCredentialKeys.BuildHttpUrlOverrideKey(server.Id, server.CredentialNamespace);
        IReadOnlyDictionary<string, string> overrides = await resolver.ResolveRemoteAsync(server.CredentialAssociationId, server.Id, server.CredentialNamespace, user.Id, [overrideKey], false, ct).ConfigureAwait(false);
        if (!overrides.TryGetValue(overrideKey, out string? raw) || !Uri.TryCreate(raw, UriKind.Absolute, out Uri? endpoint)) return configured;
        try { RemoteMcpConfigurationValidator.ValidateEndpoint(endpoint); }
        catch (InvalidOperationException) { return configured; }
        return !GatewayOwnershipAccess.IsAdmin(user) && ProbeNetworkPolicy.IsConfirmedPrivate(endpoint.AbsoluteUri) ? configured : endpoint;
    }

    private static GatewayConnectorUpsertRequest NormalizeCandidate(GatewayConnectorUpsertRequest candidate) => new()
    {
        Scope = candidate.Scope,
        Name = candidate.Name,
        Description = candidate.Description,
        IconUrl = candidate.IconUrl,
        PresetId = candidate.PresetId,
        Transport = candidate.Transport,
        HttpUrl = candidate.HttpUrl,
        McpProtocolVersion = candidate.McpProtocolVersion,
        Credentials = candidate.Credentials,
        HttpHeaders = candidate.HttpHeaders,
        ConfirmLegacyAuthReplacement = candidate.ConfirmLegacyAuthReplacement,
        Enabled = candidate.Enabled,
    };

    private static GatewayConnectorUpsertRequest Deserialize(ConnectorDraftMetadata draft)
        => JsonSerializer.Deserialize(draft.CandidateJson, KautukaJsonContext.Default.GatewayConnectorUpsertRequest) ?? throw new InvalidOperationException("Connector draft is invalid.");
    private static bool BaseMatches(GatewayMcpServer? server, ConnectorDraftMetadata draft)
        => server is null ? string.IsNullOrEmpty(draft.BaseUpdatedAt) && string.IsNullOrEmpty(draft.SourceAssociationId) : string.Equals(server.UpdatedAt, draft.BaseUpdatedAt, StringComparison.Ordinal) && string.Equals(server.CredentialAssociationId ?? string.Empty, draft.SourceAssociationId, StringComparison.Ordinal) && string.Equals(server.CredentialNamespace, draft.CredentialNamespace, StringComparison.Ordinal);
    private static bool Authorized(GatewayUser user, GatewayMcpServer? server, GatewayConnectorUpsertRequest candidate, string credentialScope, bool personalCandidate = false)
    {
        if (credentialScope == "system" && !GatewayOwnershipAccess.IsAdmin(user)) return false;
        if (server is null) return (GatewayOwnershipAccess.IsAdmin(user) || user.CanImportConnectors)
            && (credentialScope != "system" || GatewayOwnershipAccess.ResolveCatalogScope(user, candidate.Scope) == GatewayOwnershipScopes.System);
        if (!GatewayOwnershipAccess.CanAccessConnector(user, server) || server.IsInternalSystemConnector) return false;
        if (server.RequiresCredentialReentry && (!GatewayOwnershipAccess.CanManageConnector(user, server) || !candidate.ConfirmLegacyAuthReplacement)) return false;
        if (credentialScope == "system" && server.Scope != GatewayOwnershipScopes.System) return false;
        return GatewayOwnershipAccess.CanManageConnector(user, server)
            || (credentialScope == "user" && GatewayCatalogStore.RemoteCandidateMatches(server, candidate, ignoreHttpUrl: personalCandidate));
    }
    private static IResult BadRequest(string message) => Results.Json(new GatewayErrorResponse(message), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
    private static IResult Forbidden() => Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
    private static IResult DraftFailure(RpcException exception) => exception.StatusCode switch
    {
        StatusCode.NotFound => Results.NotFound(),
        StatusCode.ResourceExhausted => Results.Json(new GatewayErrorResponse("Connector draft quota exceeded."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status429TooManyRequests),
        StatusCode.InvalidArgument or StatusCode.FailedPrecondition => BadRequest("Connector draft is invalid or unavailable."),
        StatusCode.PermissionDenied or StatusCode.Unauthenticated => Forbidden(),
        _ => Results.Json(new GatewayErrorResponse("Credential service is unavailable."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status503ServiceUnavailable),
    };
}
