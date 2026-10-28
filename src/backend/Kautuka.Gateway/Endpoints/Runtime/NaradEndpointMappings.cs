using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.RuntimePlane.Projection;

using Microsoft.AspNetCore.Mvc;

using Narad.Contracts;

namespace Kautuka.Gateway.Endpoints.Runtime;

internal static class NaradEndpointMappings
{
    public static WebApplication MapNaradEndpoints(this WebApplication app)
    {
        app.MapGet("/api/narad/v1/capabilities", (
            string? sessionId,
            string? agentId,
            IGatewayAuthGuard auth,
            IGatewayCatalogStore catalog,
            IGatewaySessionStore sessions,
            IAgentDefinitionStore definitionStore,
            HttpContext context) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(agentId))
            {
                return Results.Json(
                    NaradProfileDiscovery.BuildCapabilities(0, null),
                    KautukaJsonContext.Default.JsonObject);
            }

            GatewayAgent? agent = NaradSessionMappings.ResolveAgent(catalog, user, agentId);
            if (agent is null)
            {
                return Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }

            IResult? accessError = NaradSessionMappings.ValidateCapabilitiesSessionAccess(sessionId, agent, user, sessions);
            if (accessError is not null)
            {
                return accessError;
            }

            string storageLocatorKey = definitionStore.GetStorageLocatorKey(agent.Id);
            long currentSessionSequence = definitionStore.ReadNaradSessionProjectionRevision(storageLocatorKey, sessionId);
            IReadOnlyList<string> rootRunIds = definitionStore.ListNaradRootRunIdsForSession(storageLocatorKey, sessionId);
            string? activeRootRunId = rootRunIds.LastOrDefault(rootRunId =>
                !definitionStore.IsNaradRootRunAdmissionClosed(storageLocatorKey, rootRunId));

            return Results.Json(
                NaradProfileDiscovery.BuildCapabilities(currentSessionSequence, activeRootRunId),
                KautukaJsonContext.Default.JsonObject);
        });

        app.MapGet("/api/narad/v1/sessions/{sessionId}/events", (
            string sessionId,
            string agentId,
            long? afterSessionSequence,
            int? limit,
            IGatewayAuthGuard auth,
            IGatewayCatalogStore catalog,
            IGatewaySessionStore sessions,
            IAgentDefinitionStore definitionStore,
            HttpContext context) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            GatewayAgent? agent = NaradSessionMappings.ResolveAgent(catalog, user, agentId);
            if (agent is null)
            {
                return Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }

            IResult? accessError = NaradSessionMappings.ValidateSessionAccess(sessionId, agent, user, sessions, out _);
            if (accessError is not null)
            {
                return accessError;
            }

            long cursor = Math.Max(0, afterSessionSequence ?? 0);
            int boundedLimit = Math.Clamp(limit ?? NaradProfileDiscovery.DefaultHistoryLimit, 1, NaradProfileDiscovery.MaxHistoryLimit);
            string storageLocatorKey = definitionStore.GetStorageLocatorKey(agent.Id);
            IReadOnlyList<NaradPersistedEvent> fetched = definitionStore.ListNaradSessionHistory(storageLocatorKey, sessionId, cursor, boundedLimit + 1);
            bool hasMore = fetched.Count > boundedLimit;
            NaradPersistedEvent[] events = hasMore ? fetched.Take(boundedLimit).ToArray() : fetched.ToArray();
            long? nextSessionSequence = hasMore && events.Length > 0 ? events[^1].SessionSequence : null;

            JsonArray payload = new([.. events.Select(static evt =>
            {
                if (!NaradPublicAuthorityFilter.TryBuildPublicPayload(evt, out JsonObject publicPayload))
                {
                    return (JsonNode?)null;
                }

                return publicPayload;
            }).Where(static node => node is not null)]);

            return Results.Json(new JsonObject
            {
                ["events"] = payload,
                ["hasMore"] = hasMore,
                ["nextSessionSequence"] = nextSessionSequence,
            }, KautukaJsonContext.Default.JsonObject);
        });

        app.MapGet("/api/narad/v1/sessions/{sessionId}/stream", async (
            string sessionId,
            string agentId,
            long? afterSessionSequence,
            bool? hydrate,
            IGatewayAuthGuard auth,
            IGatewayCatalogStore catalog,
            IGatewaySessionStore sessions,
            IAgentDefinitionStore definitionStore,
            IRuntimeEventStreamHub streamHub,
            IRootRunAdmissionGate admissionGate,
            HttpContext context,
            CancellationToken ct) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            GatewayAgent? agent = NaradSessionMappings.ResolveAgent(catalog, user, agentId);
            if (agent is null)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            if (NaradSessionMappings.TrySetStreamAccessFailure(context, sessionId, agent, user, sessions, out GatewaySession? session))
            {
                return;
            }

            try
            {
                await NaradStreamDelivery.StreamSessionAsync(
                    context,
                    session!,
                    agent,
                    definitionStore,
                    streamHub,
                    admissionGate,
                    Math.Max(0, afterSessionSequence ?? 0),
                    hydrate ?? true,
                    ct).ConfigureAwait(false);
            }
            catch (NaradProjectionException)
            {
                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                }
            }
        });

        app.MapGet("/api/narad/v1/sessions/{sessionId}/watch", async (
            string sessionId,
            string agentId,
            long? afterSessionSequence,
            IGatewayAuthGuard auth,
            IGatewayCatalogStore catalog,
            IGatewaySessionStore sessions,
            IAgentDefinitionStore definitionStore,
            IRuntimeEventStreamHub streamHub,
            HttpContext context,
            CancellationToken ct) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            GatewayAgent? agent = NaradSessionMappings.ResolveAgent(catalog, user, agentId);
            if (agent is null)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            if (NaradSessionMappings.TrySetStreamAccessFailure(context, sessionId, agent, user, sessions, out _))
            {
                return;
            }

            await NaradSessionWatchDelivery.StreamSessionWatchAsync(
                context,
                sessionId,
                agent,
                definitionStore,
                streamHub,
                Math.Max(0, afterSessionSequence ?? 0),
                ct).ConfigureAwait(false);
        });

        app.MapGet("/api/narad/v1/sessions/{sessionId}/trace", (
            string sessionId,
            string agentId,
            long? afterSessionSequence,
            int? limit,
            IGatewayAuthGuard auth,
            IGatewayCatalogStore catalog,
            IGatewaySessionStore sessions,
            IAgentDefinitionStore definitionStore,
            HttpContext context) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            if (!GatewayOwnershipAccess.IsAdmin(user))
            {
                return Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }

            GatewayAgent? agent = NaradSessionMappings.ResolveAgent(catalog, user, agentId);
            if (agent is null)
            {
                return Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }

            IResult? accessError = NaradSessionMappings.ValidateSessionAccess(sessionId, agent, user, sessions, out _, adminFallback: true);
            if (accessError is not null)
            {
                return accessError;
            }

            long cursor = Math.Max(0, afterSessionSequence ?? 0);
            int boundedLimit = Math.Clamp(limit ?? NaradProfileDiscovery.DefaultHistoryLimit, 1, NaradProfileDiscovery.MaxHistoryLimit);
            string storageLocatorKey = definitionStore.GetStorageLocatorKey(agent.Id);
            IReadOnlyList<NaradPersistedEvent> fetched = definitionStore.ListNaradSessionHistory(storageLocatorKey, sessionId, cursor, boundedLimit + 1);
            bool hasMore = fetched.Count > boundedLimit;
            NaradPersistedEvent[] events = hasMore ? fetched.Take(boundedLimit).ToArray() : fetched.ToArray();

            JsonArray payload = new([.. events.Select(static evt => JsonNode.Parse(evt.EventJson)!.AsObject())]);
            return Results.Json(new JsonObject
            {
                ["events"] = payload,
                ["hasMore"] = hasMore,
                ["nextSessionSequence"] = hasMore && events.Length > 0 ? events[^1].SessionSequence : null,
            }, KautukaJsonContext.Default.JsonObject);
        }).RequireAuthorization("AdminDiagnostics");

        app.MapGet("/api/narad/v1/workspace/watch", async (
            long? afterRevision,
            IGatewayAuthGuard auth,
            [FromServices] INaradWorkspaceWatchHub workspaceWatch,
            HttpContext context,
            CancellationToken ct) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            NaradSseWriter.ConfigureResponse(context);
            NaradSseWriteContext writer = new(context, NaradProfileDiscovery.DefaultHeartbeatIntervalMs);
            await writer.StartAsync(ct).ConfigureAwait(false);
            try
            {
                NaradWorkspaceWatchSubscription subscription = workspaceWatch.Subscribe(user.Id, Math.Max(0, afterRevision ?? 0));
                try
                {
                    await foreach (JsonObject payload in subscription.Events.WithCancellation(ct).ConfigureAwait(false))
                    {
                        string? eventId = payload["eventId"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(eventId))
                        {
                            await writer.WriteWatchAsync(payload, eventId, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            await writer.WriteControlAsync(payload, ct).ConfigureAwait(false);
                        }
                    }
                }
                finally
                {
                    subscription.Dispose();
                }
            }
            finally
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }
        });

        app.MapPost("/api/narad/v1/client/attach", (
            JsonElement body,
            IGatewayAuthGuard auth,
            INaradClientLeaseService leaseService,
            HttpContext context) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            if (!leaseService.IsClientExecutorSupported)
            {
                return NaradIntentResponses.Rejected("Client executor is not supported.", StatusCodes.Status409Conflict);
            }

            if (body.GetRawText().Length > NaradRequestParsing.MaxBodyLength)
            {
                return NaradIntentResponses.Rejected("Request body exceeds maximum size.");
            }

            string? clientExecutorId = body.TryGetStringProperty("clientExecutorId");
            if (string.IsNullOrWhiteSpace(clientExecutorId))
            {
                return NaradIntentResponses.Rejected("clientExecutorId is required.");
            }

            NaradClientLease lease = leaseService.Attach(
                user.Id,
                clientExecutorId,
                TimeSpan.FromMilliseconds(NaradProfileDiscovery.DefaultLeaseDurationMs));
            return Results.Json(new JsonObject
            {
                ["type"] = "client.attached",
                ["protocolVersion"] = NaradProtocolVersions.V1,
                ["clientExecutorId"] = lease.ClientExecutorId,
                ["leaseId"] = lease.LeaseId,
                ["heartbeatIntervalMs"] = NaradProfileDiscovery.DefaultHeartbeatIntervalMs,
                ["leaseExpiresAt"] = lease.ExpiresAt.ToString("O"),
            }, KautukaJsonContext.Default.JsonObject);
        });

        app.MapPost("/api/narad/v1/client/heartbeat", (
            JsonElement body,
            IGatewayAuthGuard auth,
            INaradClientLeaseService leaseService,
            HttpContext context) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            if (!leaseService.IsClientExecutorSupported)
            {
                return NaradIntentResponses.Rejected("Client executor is not supported.", StatusCodes.Status409Conflict);
            }

            if (body.GetRawText().Length > NaradRequestParsing.MaxBodyLength)
            {
                return NaradIntentResponses.Rejected("Request body exceeds maximum size.");
            }

            string? clientExecutorId = body.TryGetStringProperty("clientExecutorId");
            string? leaseId = body.TryGetStringProperty("leaseId");
            if (string.IsNullOrWhiteSpace(clientExecutorId) || string.IsNullOrWhiteSpace(leaseId))
            {
                return NaradIntentResponses.Rejected("clientExecutorId and leaseId are required.");
            }

            return leaseService.TryRenew(user.Id, clientExecutorId, leaseId, out NaradClientLease? lease)
                ? Results.Json(new JsonObject
                {
                    ["type"] = "client.heartbeat",
                    ["protocolVersion"] = NaradProtocolVersions.V1,
                    ["clientExecutorId"] = lease!.ClientExecutorId,
                    ["leaseId"] = lease.LeaseId,
                    ["leaseExpiresAt"] = lease.ExpiresAt.ToString("O"),
                }, KautukaJsonContext.Default.JsonObject)
                : NaradIntentResponses.Rejected("Lease is invalid or expired.", StatusCodes.Status409Conflict);
        });

        app.MapPost("/api/narad/v1/agents/{agentId}/sessions/{sessionId}/messages", async (
            string agentId,
            string sessionId,
            JsonElement body,
            IGatewayAuthGuard auth,
            INaradIntentRequestHandler intents,
            HttpContext context,
            CancellationToken ct) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            return await intents.SendMessageAsync(agentId, sessionId, body, user, ct).ConfigureAwait(false);
        });

        return app.MapSessionQueueEndpoints().MapNaradThreadCommandEndpoints();
    }
}
