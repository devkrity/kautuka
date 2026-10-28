using Kautuka.Gateway.Domain.Channels;

namespace Kautuka.Gateway.Endpoints.Channels;

internal static class GatewayChannelEndpointMappings
{
    public static WebApplication MapGatewayChannelEndpoints(this WebApplication app)
    {
        MapGenericChannelIngressEndpoint(app);
        MapGenericChannelOutboxLeaseEndpoint(app);
        MapGenericChannelOutboxAckEndpoint(app);
        MapGenericChannelOutboxEnqueueEndpoint(app);
        MapChannelInteractionTransportEndpoint(app);

        return app;
    }

    private static void MapGenericChannelIngressEndpoint(WebApplication app)
    {
        app.MapPost("/api/internal/channels/ingress", async (
            ChannelInboundEvent evt,
            IChannelIngressService ingress,
            CancellationToken cancellationToken) =>
        {
            ChannelIngressResult result = await ingress.AdmitInboundEventAsync(evt, cancellationToken).ConfigureAwait(false);

            if (!result.Success)
            {
                int status = result.Status switch
                {
                    "registration_not_found" => StatusCodes.Status404NotFound,
                    "unsupported_channel" => StatusCodes.Status400BadRequest,
                    "unsupported_event" => StatusCodes.Status400BadRequest,
                    "unauthorized" => StatusCodes.Status401Unauthorized,
                    "identity_unmapped" => StatusCodes.Status403Forbidden,
                    "target_missing" => StatusCodes.Status400BadRequest,
                    "delivery_failed" => StatusCodes.Status502BadGateway,
                    _ => StatusCodes.Status200OK,
                };

                return Results.Json(
                    new ChannelIngressAcceptedResponse(false, result.Status, result.Detail),
                    ChannelContractsJsonContext.Default.ChannelIngressAcceptedResponse,
                    statusCode: status);
            }

            return Results.Json(
                new ChannelIngressAcceptedResponse(
                    true,
                    result.Status,
                    result.Detail,
                    result.SessionId,
                    result.TargetType,
                    result.TargetId,
                    result.InteractionId,
                    result.RunId),
                ChannelContractsJsonContext.Default.ChannelIngressAcceptedResponse);
        });
    }

    private static void MapGenericChannelOutboxLeaseEndpoint(WebApplication app)
    {
        app.MapPost("/api/internal/channels/outbox/lease", (
            ChannelOutboxLeaseRequest request,
            IChannelOutboxStore outbox) =>
        {
            if (string.IsNullOrWhiteSpace(request.WorkerId))
            {
                return Results.Json(
                    new ChannelOutboxLeaseResponse([]),
                    ChannelContractsJsonContext.Default.ChannelOutboxLeaseResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            IReadOnlyList<ChannelOutboxEntry> leased = outbox.LeasePending(
                request.WorkerId.Trim(),
                request.MaxItems,
                TimeSpan.FromSeconds(Math.Clamp(request.LeaseSeconds, 5, 300)),
                DateTimeOffset.UtcNow);

            ChannelOutboxAction[] actions = [.. leased.Select(item => new ChannelOutboxAction(
                item.Id,
                item.ChannelType,
                item.RegistrationId,
                item.ActionType,
                item.PayloadJson,
                item.Status,
                item.AttemptCount,
                item.CreatedAt,
                item.UpdatedAt,
                item.AvailableAt,
                item.LeaseId,
                item.LeaseOwner,
                item.LeasedUntil,
                item.LastError))];

            return Results.Json(new ChannelOutboxLeaseResponse(actions), ChannelContractsJsonContext.Default.ChannelOutboxLeaseResponse);
        });
    }

    private static void MapGenericChannelOutboxAckEndpoint(WebApplication app)
    {
        app.MapPost("/api/internal/channels/outbox/ack", (
            ChannelOutboxAckRequest request,
            IChannelOutboxStore outbox) =>
        {
            if (string.IsNullOrWhiteSpace(request.WorkerId)
                || string.IsNullOrWhiteSpace(request.LeaseId)
                || request.Items.Count == 0)
            {
                return Results.Json(
                    new GatewayBooleanSuccessResponse(false),
                    KautukaJsonContext.Default.GatewayBooleanSuccessResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach (ChannelOutboxAckItem item in request.Items)
            {
                DateTimeOffset? retryAt = item.Success
                    ? null
                    : now.AddSeconds(Math.Clamp(item.RetryAfterSeconds ?? 5, 1, 3600));

                outbox.Acknowledge(
                    item.ActionId,
                    request.WorkerId,
                    request.LeaseId,
                    item.Success,
                    item.Error,
                    now,
                    retryAt);
            }

            return Results.Json(new GatewayBooleanSuccessResponse(true), KautukaJsonContext.Default.GatewayBooleanSuccessResponse);
        });
    }

    private static void MapGenericChannelOutboxEnqueueEndpoint(WebApplication app)
    {
        app.MapPost("/api/internal/channels/outbox/enqueue", (
            ChannelOutboxEnqueueRequest request,
            IChannelOutboxStore outbox) =>
        {
            if (string.IsNullOrWhiteSpace(request.ChannelType)
                || string.IsNullOrWhiteSpace(request.RegistrationId)
                || string.IsNullOrWhiteSpace(request.ActionType))
            {
                return Results.Json(
                    new GatewayBooleanSuccessResponse(false),
                    KautukaJsonContext.Default.GatewayBooleanSuccessResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            DateTimeOffset? availableAt = request.DelaySeconds is int delay
                ? DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(delay, 1, 3600))
                : null;

            _ = outbox.Enqueue(
                request.ChannelType.Trim(),
                request.RegistrationId.Trim(),
                request.ActionType.Trim(),
                request.Payload.GetRawText(),
                availableAt);

            return Results.Json(new GatewayBooleanSuccessResponse(true), KautukaJsonContext.Default.GatewayBooleanSuccessResponse);
        });
    }

    private static void MapChannelInteractionTransportEndpoint(WebApplication app)
    {
        app.MapPost("/api/internal/channels/interactions/{interactionId}/transport", (
            string interactionId,
            ChannelInteractionTransportUpdateRequest request,
            IChannelInteractionTransportUpdater updater) =>
        {
            if (!updater.TryUpdate(interactionId, request.SourceMessageId))
            {
                return Results.Json(
                    new GatewayBooleanSuccessResponse(false),
                    KautukaJsonContext.Default.GatewayBooleanSuccessResponse,
                    statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Json(new GatewayBooleanSuccessResponse(true), KautukaJsonContext.Default.GatewayBooleanSuccessResponse);
        });
    }

}
