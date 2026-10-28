using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Domain.Workflows;
using Kautuka.Gateway.RuntimePlane.Projection;

using Narad.Contracts;

namespace Kautuka.Gateway.Endpoints.Runtime;

internal static class NaradWorkflowEndpointMappings
{
    public static WebApplication MapNaradWorkflowEndpoints(this WebApplication app)
    {
        app.MapGet("/api/narad/v1/workflows/{workflowId}/instances/{instanceId}/capabilities", (
            string workflowId,
            string instanceId,
            IGatewayAuthGuard auth,
            IGatewayWorkflowStore workflowStore,
            HttpContext context) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            if (!TryGetOwnedInstance(workflowStore, workflowId, instanceId, user, out _, out IResult? error))
            {
                return error!;
            }

            long current = workflowStore.ReadNaradSessionSequence(instanceId);
            JsonObject capabilities = NaradProfileDiscovery.BuildWorkflowCapabilities(current);
            capabilities["activeWorkflowId"] = instanceId;
            return Results.Json(capabilities, KautukaJsonContext.Default.JsonObject);
        });

        app.MapGet("/api/narad/v1/workflows/{workflowId}/instances/{instanceId}/events", (
            string workflowId,
            string instanceId,
            long? afterSessionSequence,
            int? limit,
            IGatewayAuthGuard auth,
            IGatewayWorkflowStore workflowStore,
            HttpContext context) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            if (!TryGetOwnedInstance(workflowStore, workflowId, instanceId, user, out _, out IResult? error))
            {
                return error!;
            }

            int boundedLimit = Math.Clamp(limit ?? NaradProfileDiscovery.DefaultHistoryLimit, 1, NaradProfileDiscovery.MaxHistoryLimit);
            IReadOnlyList<NaradPersistedEvent> fetched = workflowStore.ListNaradEvents(
                instanceId,
                Math.Max(0, afterSessionSequence ?? 0),
                boundedLimit + 1);
            bool hasMore = fetched.Count > boundedLimit;
            NaradPersistedEvent[] events = hasMore ? [.. fetched.Take(boundedLimit)] : [.. fetched];
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
                ["nextSessionSequence"] = hasMore && events.Length > 0 ? events[^1].SessionSequence : null,
            }, KautukaJsonContext.Default.JsonObject);
        });

        app.MapGet("/api/narad/v1/workflows/{workflowId}/instances/{instanceId}/stream", async (
            string workflowId,
            string instanceId,
            long? afterSessionSequence,
            IGatewayAuthGuard auth,
            IGatewayWorkflowStore workflowStore,
            IWorkflowNaradStreamHub streamHub,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            if (!TryGetOwnedInstance(workflowStore, workflowId, instanceId, user, out _, out _))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            NaradSseWriter.ConfigureResponse(context);
            RuntimeEventStreamSubscription subscription = streamHub.Subscribe(instanceId);
            try
            {
                NaradSseWriteContext writer = new(context, NaradProfileDiscovery.DefaultHeartbeatIntervalMs);
                await using (writer.ConfigureAwait(false))
                {
                    await writer.StartAsync(cancellationToken).ConfigureAwait(false);
                    long current = workflowStore.ReadNaradSessionSequence(instanceId);
                    JsonObject capabilities = NaradProfileDiscovery.BuildWorkflowCapabilities(current);
                    capabilities["activeWorkflowId"] = instanceId;
                    await writer.WriteControlAsync(capabilities, cancellationToken).ConfigureAwait(false);
                    await writer.WriteCommentAsync("subscribed", cancellationToken).ConfigureAwait(false);

                    long cursor = Math.Max(0, afterSessionSequence ?? 0);
                    foreach (NaradPersistedEvent persisted in workflowStore.ListNaradEvents(instanceId, cursor, NaradProfileDiscovery.MaxHistoryLimit))
                    {
                        if (!NaradPublicAuthorityFilter.TryBuildPublicPayload(persisted, out JsonObject publicPayload))
                        {
                            continue;
                        }

                        await writer.WriteAuthorityAsync(publicPayload, persisted.EventId, cancellationToken).ConfigureAwait(false);
                        cursor = Math.Max(cursor, persisted.SessionSequence);
                    }

                    await foreach (NaradPersistedEvent live in subscription.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                    {
                        if (live.SessionSequence <= cursor)
                        {
                            continue;
                        }

                        if (!NaradPublicAuthorityFilter.TryBuildPublicPayload(live, out JsonObject publicPayload))
                        {
                            continue;
                        }

                        await writer.WriteAuthorityAsync(publicPayload, live.EventId, cancellationToken).ConfigureAwait(false);
                        cursor = live.SessionSequence;
                    }
                }
            }
            finally
            {
                subscription.Dispose();
            }
        });

        return app;
    }

    private static bool TryGetOwnedInstance(
        IGatewayWorkflowStore workflowStore,
        string workflowId,
        string instanceId,
        GatewayUser user,
        out GatewayWorkflowInstance? instance,
        out IResult? error)
    {
        instance = workflowStore.GetInstance(instanceId);
        if (instance is null || !string.Equals(instance.WorkflowId, workflowId, StringComparison.Ordinal))
        {
            error = Results.Json(new GatewayErrorResponse("Instance not found."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
            instance = null;
            return false;
        }

        if (!GatewayOwnershipAccess.IsSelf(user, instance.OwnerUserId))
        {
            error = Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            instance = null;
            return false;
        }

        error = null;
        return true;
    }
}
