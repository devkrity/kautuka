using Grpc.Core;

using Kautuka.Manager.Protos;

using Microsoft.AspNetCore.Mvc;

namespace Kautuka.Gateway.Endpoints.Runtime;

internal sealed record NetworkAccessIdentityDto(string ScopeType, string UserId, string AgentId, string Role, string ComponentId,
    string? DisplayName = null, string? AgentName = null, string? UserName = null);
internal sealed record NetworkAccessRuleDto(string Id, NetworkAccessIdentityDto Identity, string Destination, int Port, bool Allowed, string? UpdatedAtUtc, bool AllowPrivateNetwork = false);
internal sealed record NetworkAccessRequestDto(string Id, NetworkAccessIdentityDto Identity, string Destination, int Port,
    string CreatedAtUtc, string LastSeenAtUtc, long Attempts, string Status);
internal sealed record NetworkAccessViewDto(bool Enabled, long Revision, long ActiveRevision, string? ActiveVersion,
    string ActivationState, string? LastError, IReadOnlyList<NetworkAccessRuleDto> Rules, IReadOnlyList<NetworkAccessRequestDto> Requests,
    IReadOnlyList<NetworkAccessIdentityDto> Sources, IReadOnlyList<NetworkAccessRuleDto> Defaults, int NextOffset = 0,
    int RuleCount = 0, int RequestCount = 0, int SourceCount = 0, int DefaultCount = 0, int PendingRequestCount = 0, bool RequestCapacityReached = false,
    int MaxPrivateNetworks = 0, int ActivePrivateNetworks = -1);
internal sealed record NetworkAccessRuleChangeDto(NetworkAccessIdentityDto Identity, string Destination, int Port, bool Allowed, long ExpectedRevision, bool AllowPrivateNetwork = false);
internal sealed record NetworkAccessDecisionDto(bool Allowed, long ExpectedRevision, bool AllowPrivateNetwork = false);

internal static partial class GatewayNetworkAccessEndpointMappings
{
    public static WebApplication MapGatewayNetworkAccessEndpoints(this WebApplication app)
    {
        MapNetworkAudit(app);
        app.MapGet("/api/network-access", async (HttpContext context, IGatewayAuthGuard auth, [FromServices] IManagerGrpcService manager,
            int? offset, long? revision, bool? summary, CancellationToken ct) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null) return Results.Unauthorized();
            if (offset < 0 || revision < 0) return Results.BadRequest();
            return await NetworkResultAsync(() => manager.GetNetworkAccessPolicyAsync(new NetworkAccessReadRequest
            {
                Actor = Actor(user),
                Offset = offset ?? 0,
                SummaryOnly = summary ?? false,
                ExpectedRevision = revision ?? 0,
                EnforceRevision = revision.HasValue,
            }, ct), CreateIdentityDescriber(user, context.RequestServices)).ConfigureAwait(false);
        });
        app.MapPut("/api/network-access/rules", async (NetworkAccessRuleChangeDto request, HttpContext context,
            IGatewayAuthGuard auth, [FromServices] IManagerGrpcService manager, CancellationToken ct) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null) return Results.Unauthorized();
            if (request.Identity is null || string.IsNullOrWhiteSpace(request.Destination)) return Results.BadRequest();
            NetworkAccessActorMessage actor = Actor(user);
            if (request.AllowPrivateNetwork && !actor.IsAdmin) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!actor.IsAdmin && (request.Identity.UserId != user.Id || request.Identity.ScopeType == "core" || request.Identity.UserId == "system"))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            return await NetworkResultAsync(() => manager.SetNetworkAccessRuleAsync(new NetworkAccessRuleRequest
            {
                Actor = actor,
                ExpectedRevision = request.ExpectedRevision,
                AllowPrivateNetwork = request.AllowPrivateNetwork,
                Rule = new NetworkAccessRuleMessage
                {
                    Identity = new NetworkAccessIdentityMessage
                    {
                        ScopeType = request.Identity.ScopeType ?? string.Empty,
                        UserId = request.Identity.UserId ?? string.Empty,
                        AgentId = request.Identity.AgentId ?? string.Empty,
                        Role = request.Identity.Role ?? string.Empty,
                        ComponentId = request.Identity.ComponentId ?? string.Empty,
                    },
                    Destination = request.Destination,
                    Port = request.Port,
                    Allowed = request.Allowed,
                },
            }, ct), CreateIdentityDescriber(user, context.RequestServices)).ConfigureAwait(false);
        });
        app.MapPost("/api/network-access/requests/{requestId}/decision", async (string requestId, NetworkAccessDecisionDto request,
            HttpContext context, IGatewayAuthGuard auth, [FromServices] IManagerGrpcService manager, CancellationToken ct) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null) return Results.Unauthorized();
            if (request.AllowPrivateNetwork && !Actor(user).IsAdmin) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return await NetworkResultAsync(() => manager.DecideNetworkAccessRequestAsync(new NetworkAccessDecisionRequest
            {
                Actor = Actor(user),
                RequestId = requestId,
                Allowed = request.Allowed,
                ExpectedRevision = request.ExpectedRevision,
                AllowPrivateNetwork = request.AllowPrivateNetwork,
            }, ct), CreateIdentityDescriber(user, context.RequestServices)).ConfigureAwait(false);
        });
        return app;
    }

    private static NetworkAccessActorMessage Actor(GatewayUser user)
        => new() { UserId = user.Id, IsAdmin = string.Equals(user.Role, "admin", StringComparison.OrdinalIgnoreCase) };

    private static async Task<IResult> NetworkResultAsync(Func<Task<NetworkAccessPolicyResponse>> call, Func<NetworkAccessIdentityMessage, NetworkAccessIdentityDto> describe)
    {
        try { return Results.Json(View(await call().ConfigureAwait(false), describe), KautukaJsonContext.Default.NetworkAccessViewDto); }
        catch (RpcException ex)
        {
            int status = ex.StatusCode switch
            {
                StatusCode.PermissionDenied => StatusCodes.Status403Forbidden,
                StatusCode.NotFound => StatusCodes.Status404NotFound,
                StatusCode.InvalidArgument => StatusCodes.Status400BadRequest,
                StatusCode.Aborted => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status503ServiceUnavailable,
            };
            return Results.Problem(statusCode: status, detail: ex.Status.Detail);
        }
        catch (NotSupportedException) { return Results.Problem(statusCode: 503, detail: "Manager does not support network access control."); }
    }

    private static NetworkAccessIdentityDto Identity(NetworkAccessIdentityMessage value)
        => new(value.ScopeType, value.UserId, value.AgentId, value.Role, value.ComponentId);

    private static NetworkAccessRuleDto Rule(NetworkAccessRuleMessage value, Func<NetworkAccessIdentityMessage, NetworkAccessIdentityDto> describe)
        => new(value.Id, describe(value.Identity), value.Destination, value.Port, value.Allowed,
            string.IsNullOrEmpty(value.UpdatedAtUtc) ? null : value.UpdatedAtUtc, value.AllowPrivateNetwork);

    private static NetworkAccessViewDto View(NetworkAccessPolicyResponse value, Func<NetworkAccessIdentityMessage, NetworkAccessIdentityDto> describe)
        => new(value.Enabled, value.Revision, value.ActiveRevision, string.IsNullOrEmpty(value.ActiveVersion) ? null : value.ActiveVersion,
            value.ActivationState, string.IsNullOrEmpty(value.LastError) ? null : value.LastError,
            [.. value.Rules.Select(rule => Rule(rule, describe))],
            [.. value.Requests.Select(request => new NetworkAccessRequestDto(request.Id, describe(request.Identity), request.Destination,
                request.Port, request.CreatedAtUtc, request.LastSeenAtUtc, request.Attempts, request.Status))],
            [.. value.Sources.Select(describe)], [.. value.Defaults.Select(rule => Rule(rule, describe))], value.NextOffset,
            value.RuleCount, value.RequestCount, value.SourceCount, value.DefaultCount, value.PendingRequestCount, value.RequestCapacityReached,
            value.MaxPrivateNetworks, value.ActivePrivateNetworks);
}
