using Grpc.Core;

using Kautuka.Manager.Protos;

using Microsoft.AspNetCore.Mvc;

namespace Kautuka.Gateway.Endpoints.Runtime;

internal sealed record NetworkAuditEventDto(long Sequence, string Id, string StartedAtUtc, string ObservedAtUtc,
    NetworkAccessIdentityDto? Identity, string? ContainerId, string SourceAddress, string Destination, int Port,
    string Outcome, bool Connected, bool Closed, long BytesSent, long BytesReceived, long DurationMilliseconds);
internal sealed record NetworkAuditPageDto(IReadOnlyList<NetworkAuditEventDto> Events, long NextBefore, long CoverageGaps,
    long UnattributedConnections, string? LastError);

internal static partial class GatewayNetworkAccessEndpointMappings
{
    private static void MapNetworkAudit(WebApplication app)
    {
        app.MapGet("/api/network-access/audit", async (HttpContext context, IGatewayAuthGuard auth,
            [FromServices] IManagerGrpcService manager, long? before, int? limit, CancellationToken ct) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null) return Results.Unauthorized();
            if (before < 0 || limit is < 1 or > 100) return Results.BadRequest();
            try
            {
                NetworkAuditResponse page = await manager.GetNetworkAuditAsync(new NetworkAuditReadRequest
                {
                    Actor = Actor(user),
                    Before = before ?? 0,
                    Limit = limit ?? 50,
                }, ct).ConfigureAwait(false);
                var response = new NetworkAuditPageDto([.. page.Events.Select(item => new NetworkAuditEventDto(item.Sequence, item.Id,
                    item.StartedAtUtc, item.ObservedAtUtc, item.Identity is null ? null : Identity(item.Identity),
                    string.IsNullOrEmpty(item.ContainerId) ? null : item.ContainerId, item.SourceAddress, item.Destination, item.Port,
                    item.Outcome, item.Connected, item.Closed, item.BytesSent, item.BytesReceived, item.DurationMilliseconds))],
                    page.NextBefore, page.CoverageGaps, page.UnattributedConnections, string.IsNullOrEmpty(page.LastError) ? null : page.LastError);
                return Results.Json(response, KautukaJsonContext.Default.NetworkAuditPageDto);
            }
            catch (RpcException ex)
            {
                return Results.Problem(statusCode: ex.StatusCode == StatusCode.InvalidArgument ? 400 : 503,
                    detail: "Network audit could not be read.");
            }
            catch (NotSupportedException) { return Results.Problem(statusCode: 503, detail: "Manager does not support network audit."); }
        });
    }
}
