using Grpc.Core;

using Kautuka.Gateway.Domain.Stores;
using Kautuka.Manager.Protos;

using Microsoft.AspNetCore.Mvc;

namespace Kautuka.Gateway.Endpoints.Runtime;

internal static partial class GatewayRuntimeEndpointMappings
{
    public static void MapGatewayNetworkAccessEndpoints(WebApplication app)
    {
        // 1. GET /api/network-access/requests?sinceRevision=
        app.MapGet("/api/network-access/requests", async Task<IResult> (
            HttpContext context,
            [FromServices] IManagerGrpcService managerService,
            [FromServices] IGatewayUserStore userStore,
            [FromServices] IGatewayCatalogStore catalogStore,
            [FromServices] IGatewayAuthGuard auth,
            CancellationToken cancellationToken) =>
        {
            IResult? unauthorized = auth.RequireAuthenticated(context, out GatewayUser? user);
            if (unauthorized is not null || user is null)
            {
                return unauthorized ?? Results.Unauthorized();
            }

            long? sinceRevision = null;
            if (context.Request.Query.TryGetValue("sinceRevision", out var sinceRevVal) && long.TryParse(sinceRevVal, out long parsedRev))
            {
                sinceRevision = parsedRev;
            }

            bool isAdmin = GatewayOwnershipAccess.IsAdmin(user);
            var actor = new NetworkAccessActorMessage { UserId = user.Id, IsAdmin = isAdmin };

            var request = new NetworkAccessListRequestsRequest
            {
                Actor = actor,
                SinceRevision = sinceRevision ?? 0,
                HasSinceRevision = sinceRevision.HasValue,
            };

            NetworkAccessListRequestsResponse response = await managerService.Client.ListRequestsAsync(
                request,
                cancellationToken: cancellationToken);

            var privCache = new Dictionary<string, UserPrivilegesSummaryDto>(StringComparer.Ordinal)
            {
                [user.Id] = new UserPrivilegesSummaryDto(
                    response.Privileges.CanApproveOwn,
                    response.Privileges.CanAllowAllAgents),
            };

            var dtos = new List<NetworkAccessRequestDto>(response.Requests.Count);
            foreach (NetworkAccessRequestMessage req in response.Requests)
            {
                if (!isAdmin && !string.Equals(req.UserId, user.Id, StringComparison.Ordinal))
                {
                    continue;
                }

                dtos.Add(await BuildRequestDtoAsync(
                    req,
                    managerService,
                    actor,
                    userStore,
                    catalogStore,
                    privCache,
                    cancellationToken).ConfigureAwait(false));
            }

            var summaryPrivs = new UserPrivilegesSummaryDto(
                response.Privileges.CanApproveOwn,
                response.Privileges.CanAllowAllAgents);

            NetworkAccessUserBrakeMessage? userBrake = response.Brake.Users.FirstOrDefault(u => string.Equals(u.UserId, user.Id, StringComparison.Ordinal));
            var brakeDto = new NetworkAccessBrakeDto(
                UserPaused: userBrake is not null,
                UserPausedBy: userBrake?.SetBy,
                UserLockedByAdmin: userBrake?.LockedByAdmin ?? false,
                SystemPaused: response.Brake.System,
                SystemPausedBy: string.IsNullOrWhiteSpace(response.Brake.SystemSetBy) ? null : response.Brake.SystemSetBy);

            var result = new NetworkRequestsResponseDto(
                response.Revision,
                dtos.ToArray(),
                summaryPrivs,
                brakeDto);

            return Results.Json(result, KautukaJsonContext.Default.NetworkRequestsResponseDto);
        });

        // 2. POST /api/network-access/requests/{id}/decision
        app.MapPost("/api/network-access/requests/{id}/decision", async Task<IResult> (
            string id,
            HttpContext context,
            [FromServices] IManagerGrpcService managerService,
            [FromServices] IGatewayUserStore userStore,
            [FromServices] IGatewayCatalogStore catalogStore,
            [FromServices] IGatewayAuthGuard auth,
            CancellationToken cancellationToken) =>
        {
            IResult? unauthorized = auth.RequireAuthenticated(context, out GatewayUser? user);
            if (unauthorized is not null || user is null)
            {
                return unauthorized ?? Results.Unauthorized();
            }

            RequestDecisionDto? decision = await context.Request.ReadFromJsonAsync(
                KautukaJsonContext.Default.RequestDecisionDto,
                cancellationToken).ConfigureAwait(false);

            if (decision is null)
            {
                return Results.BadRequest(new GatewayErrorResponse("Invalid decision body."));
            }

            bool isAdmin = GatewayOwnershipAccess.IsAdmin(user);

            if (!isAdmin)
            {
                if (string.Equals(decision.Scope, "connector-system", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Json(new GatewayErrorResponse("connector-system scope requires administrator privileges."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
                }

                if (decision.AllowLan == true)
                {
                    return Results.Json(new GatewayErrorResponse("allowLan requires administrator privileges."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
                }
            }

            var actor = new NetworkAccessActorMessage { UserId = user.Id, IsAdmin = isAdmin };
            var decideRequest = new NetworkAccessDecideRequest
            {
                Actor = actor,
                RequestId = id,
                Allow = decision.Allow,
                Scope = decision.Scope ?? "agent",
                AllowLan = decision.AllowLan ?? false,
                ExpectedRevision = decision.ExpectedRevision,
            };

            try
            {
                NetworkAccessDecideResponse response = await managerService.Client.DecideRequestAsync(
                    decideRequest,
                    cancellationToken: cancellationToken);

                NetworkAccessRuleDto? ruleDto = response.Rule is not null && !string.IsNullOrWhiteSpace(response.Rule.Id)
                    ? MapRuleToDto(response.Rule, userStore, catalogStore)
                    : null;

                NetworkAccessRequestDto? requestDto = response.Request is not null && !string.IsNullOrWhiteSpace(response.Request.Id)
                    ? await BuildRequestDtoAsync(
                        response.Request,
                        managerService,
                        actor,
                        userStore,
                        catalogStore,
                        privCache: null,
                        cancellationToken).ConfigureAwait(false)
                    : null;

                var result = new RequestDecisionResponseDto(response.Revision, ruleDto, requestDto);
                return Results.Json(result, KautukaJsonContext.Default.RequestDecisionResponseDto);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Aborted || ex.StatusCode == StatusCode.FailedPrecondition)
            {
                return Results.Json(new GatewayErrorResponse(ex.Status.Detail), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status409Conflict);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied)
            {
                return Results.Json(new GatewayErrorResponse(ex.Status.Detail), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.InvalidArgument)
            {
                return Results.Json(new GatewayErrorResponse(ex.Status.Detail), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
            {
                return Results.Json(new GatewayErrorResponse(ex.Status.Detail), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
            }
        });

        // 3. GET /api/network-access/rules?userId=
        app.MapGet("/api/network-access/rules", async Task<IResult> (
            HttpContext context,
            [FromServices] IManagerGrpcService managerService,
            [FromServices] IGatewayUserStore userStore,
            [FromServices] IGatewayCatalogStore catalogStore,
            [FromServices] IGatewayAuthGuard auth,
            CancellationToken cancellationToken) =>
        {
            IResult? unauthorized = auth.RequireAuthenticated(context, out GatewayUser? user);
            if (unauthorized is not null || user is null)
            {
                return unauthorized ?? Results.Unauthorized();
            }

            string? userId = context.Request.Query.TryGetValue("userId", out var uVal) ? (string?)uVal : null;

            bool isAdmin = GatewayOwnershipAccess.IsAdmin(user);
            if (!isAdmin && !string.IsNullOrWhiteSpace(userId) && !string.Equals(userId, user.Id, StringComparison.Ordinal))
            {
                return Results.Json(new GatewayErrorResponse("Cannot view another user's rules."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }

            string filterUserId = !isAdmin ? user.Id : (userId ?? string.Empty);

            var request = new NetworkAccessListRulesRequest
            {
                Actor = new NetworkAccessActorMessage { UserId = user.Id, IsAdmin = isAdmin },
                FilterUserId = filterUserId,
            };

            NetworkAccessListRulesResponse response = await managerService.Client.ListRulesAsync(request, cancellationToken: cancellationToken);

            var ruleDtos = response.Rules
                .Where(r => isAdmin || string.Equals(r.UserId, user.Id, StringComparison.Ordinal))
                .Select(r => MapRuleToDto(r, userStore, catalogStore))
                .ToArray();

            var result = new NetworkRulesResponseDto(response.Revision, ruleDtos);
            return Results.Json(result, KautukaJsonContext.Default.NetworkRulesResponseDto);
        });

        // 4. PUT /api/network-access/rules
        app.MapPut("/api/network-access/rules", async Task<IResult> (
            HttpContext context,
            [FromServices] IManagerGrpcService managerService,
            [FromServices] IGatewayUserStore userStore,
            [FromServices] IGatewayCatalogStore catalogStore,
            [FromServices] IGatewayAuthGuard auth,
            CancellationToken cancellationToken) =>
        {
            IResult? unauthorized = auth.RequireAuthenticated(context, out GatewayUser? user);
            if (unauthorized is not null || user is null)
            {
                return unauthorized ?? Results.Unauthorized();
            }

            CreateOrUpdateRuleDto? dto = await context.Request.ReadFromJsonAsync(
                KautukaJsonContext.Default.CreateOrUpdateRuleDto,
                cancellationToken).ConfigureAwait(false);

            if (dto is null)
            {
                return Results.BadRequest(new GatewayErrorResponse("Invalid rule payload."));
            }

            bool isAdmin = GatewayOwnershipAccess.IsAdmin(user);
            bool isSystemScope = string.Equals(dto.ScopeKind, "system", StringComparison.OrdinalIgnoreCase);
            bool isConnectorSystemScope = string.Equals(dto.ScopeKind, "connector-system", StringComparison.OrdinalIgnoreCase);

            if (!isAdmin)
            {
                if (isSystemScope || isConnectorSystemScope)
                {
                    return Results.Json(new GatewayErrorResponse($"{dto.ScopeKind} scope requires administrator privileges."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
                }

                if (string.Equals(dto.DestKind, "any-public", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Json(new GatewayErrorResponse("any-public destination requires administrator privileges."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
                }

                if (dto.AllowLan == true)
                {
                    return Results.Json(new GatewayErrorResponse("allowLan requires administrator privileges."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
                }

                if (!string.IsNullOrWhiteSpace(dto.UserId) && !string.Equals(dto.UserId, user.Id, StringComparison.Ordinal))
                {
                    return Results.Json(new GatewayErrorResponse("Cannot manage rules for another user."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
                }
            }

            string targetUserId = (isSystemScope || isConnectorSystemScope)
                ? string.Empty
                : (isAdmin ? (dto.UserId ?? user.Id) : user.Id);
            var setRuleRequest = new NetworkAccessSetRuleRequest
            {
                Actor = new NetworkAccessActorMessage { UserId = user.Id, IsAdmin = isAdmin },
                ScopeKind = dto.ScopeKind,
                UserId = targetUserId,
                AgentId = dto.AgentId ?? string.Empty,
                ConnectorId = dto.ConnectorId ?? string.Empty,
                Destination = dto.Destination,
                DestKind = dto.DestKind,
                Port = dto.Port,
                Allowed = dto.Allowed,
                AllowLan = dto.AllowLan ?? false,
                ExpectedRevision = dto.ExpectedRevision,
            };

            try
            {
                NetworkAccessSetRuleResponse response = await managerService.Client.SetRuleAsync(setRuleRequest, cancellationToken: cancellationToken);
                NetworkAccessRuleDto result = MapRuleToDto(response.Rule, userStore, catalogStore);
                return Results.Json(result, KautukaJsonContext.Default.NetworkAccessRuleDto);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Aborted || ex.StatusCode == StatusCode.FailedPrecondition)
            {
                return Results.Json(new GatewayErrorResponse(ex.Status.Detail), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status409Conflict);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied)
            {
                return Results.Json(new GatewayErrorResponse(ex.Status.Detail), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.InvalidArgument)
            {
                return Results.Json(new GatewayErrorResponse(ex.Status.Detail), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        // 5. DELETE /api/network-access/rules/{id}?expectedRevision=
        app.MapDelete("/api/network-access/rules/{id}", async Task<IResult> (
            string id,
            HttpContext context,
            [FromServices] IManagerGrpcService managerService,
            [FromServices] IGatewayAuthGuard auth,
            CancellationToken cancellationToken) =>
        {
            IResult? unauthorized = auth.RequireAuthenticated(context, out GatewayUser? user);
            if (unauthorized is not null || user is null)
            {
                return unauthorized ?? Results.Unauthorized();
            }

            long expectedRevision = 0;
            if (context.Request.Query.TryGetValue("expectedRevision", out var revVal))
            {
                _ = long.TryParse(revVal, out expectedRevision);
            }

            bool isAdmin = GatewayOwnershipAccess.IsAdmin(user);

            var deleteRequest = new NetworkAccessDeleteRuleRequest
            {
                Actor = new NetworkAccessActorMessage { UserId = user.Id, IsAdmin = isAdmin },
                RuleId = id,
                ExpectedRevision = expectedRevision,
            };

            try
            {
                await managerService.Client.DeleteRuleAsync(deleteRequest, cancellationToken: cancellationToken);
                return Results.NoContent();
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Aborted || ex.StatusCode == StatusCode.FailedPrecondition)
            {
                return Results.Json(new GatewayErrorResponse(ex.Status.Detail), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status409Conflict);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied)
            {
                return Results.Json(new GatewayErrorResponse(ex.Status.Detail), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
            {
                return Results.Json(new GatewayErrorResponse(ex.Status.Detail), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
            }
        });

        // 6. GET /api/network-access/activity
        app.MapGet("/api/network-access/activity", async Task<IResult> (
            HttpContext context,
            [FromServices] IManagerGrpcService managerService,
            [FromServices] IGatewayUserStore userStore,
            [FromServices] IGatewayCatalogStore catalogStore,
            [FromServices] IGatewayAuthGuard auth,
            CancellationToken cancellationToken) =>
        {
            IResult? unauthorized = auth.RequireAuthenticated(context, out GatewayUser? user);
            if (unauthorized is not null || user is null)
            {
                return unauthorized ?? Results.Unauthorized();
            }

            bool isAdmin = GatewayOwnershipAccess.IsAdmin(user);

            var request = new NetworkAccessListActivityRequest
            {
                Actor = new NetworkAccessActorMessage { UserId = user.Id, IsAdmin = isAdmin },
            };

            NetworkAccessListActivityResponse response = await managerService.Client.ListActivityAsync(request, cancellationToken: cancellationToken);

            var entries = response.Entries
                .Where(e => isAdmin || string.Equals(e.UserId, user.Id, StringComparison.Ordinal))
                .Select(e =>
                {
                    string? userDisplayName = null;
                    if (!string.IsNullOrWhiteSpace(e.UserId))
                    {
                        userDisplayName = userStore.TryGetUserById(e.UserId, out GatewayUser? u) && u is not null
                            ? (!string.IsNullOrWhiteSpace(u.DisplayName) ? u.DisplayName : u.Username)
                            : e.UserId;
                    }

                    string? agentDisplayName = null;
                    if (!string.IsNullOrWhiteSpace(e.AgentId) && catalogStore.TryGetAgent(e.AgentId, out GatewayAgent? a) && a is not null)
                    {
                        agentDisplayName = a.Name;
                    }

                    string? connectorDisplayName = null;
                    if (!string.IsNullOrWhiteSpace(e.ConnectorId) && catalogStore.TryGetMcpServer(e.ConnectorId, out GatewayMcpServer? c) && c is not null)
                    {
                        connectorDisplayName = c.Name;
                    }

                    return new NetworkAccessActivityDto(
                        TimestampUtc: e.TimestampUtc,
                        UserId: string.IsNullOrWhiteSpace(e.UserId) ? null : e.UserId,
                        UserDisplayName: userDisplayName,
                        AgentId: string.IsNullOrWhiteSpace(e.AgentId) ? null : e.AgentId,
                        AgentDisplayName: agentDisplayName,
                        ConnectorId: string.IsNullOrWhiteSpace(e.ConnectorId) ? null : e.ConnectorId,
                        ConnectorDisplayName: connectorDisplayName,
                        Destination: e.Destination,
                        Port: e.Port,
                        Decision: e.Decision,
                        Reason: e.Reason,
                        OpenInternetBadge: e.OpenInternetBadge,
                        CompatibilityMode: e.CompatibilityMode);
                })
                .ToArray();

            var result = new NetworkActivityResponseDto(entries);
            return Results.Json(result, KautukaJsonContext.Default.NetworkActivityResponseDto);
        });

        // 7. GET /api/network-access/users/{userId}/privileges
        app.MapGet("/api/network-access/users/{userId}/privileges", async Task<IResult> (
            string userId,
            HttpContext context,
            [FromServices] IManagerGrpcService managerService,
            [FromServices] IGatewayUserStore userStore,
            [FromServices] IGatewayAuthGuard auth,
            CancellationToken cancellationToken) =>
        {
            IResult? unauthorized = auth.RequireAuthenticated(context, out GatewayUser? user);
            if (unauthorized is not null || user is null)
            {
                return unauthorized ?? Results.Unauthorized();
            }

            bool isAdmin = GatewayOwnershipAccess.IsAdmin(user);
            if (!isAdmin && !string.Equals(userId, user.Id, StringComparison.Ordinal))
            {
                return Results.Json(new GatewayErrorResponse("Cannot view another user's privileges."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }

            bool targetIsAdmin = userStore.TryGetUserById(userId, out GatewayUser? targetUser) && targetUser is not null && GatewayOwnershipAccess.IsAdmin(targetUser);

            var request = new NetworkAccessGetUserPrivilegesRequest
            {
                Actor = new NetworkAccessActorMessage { UserId = user.Id, IsAdmin = isAdmin },
                UserId = userId,
                TargetIsAdmin = targetIsAdmin,
            };

            NetworkAccessUserPrivilegesMessage response = await managerService.GetUserPrivilegesAsync(request, cancellationToken).ConfigureAwait(false);

            var result = new UserNetworkPrivilegesDto(
                UserId: response.UserId,
                CanApproveOwn: response.CanApproveOwn,
                CanAllowAllAgents: response.CanAllowAllAgents,
                SetBy: string.IsNullOrWhiteSpace(response.SetBy) ? null : response.SetBy,
                SetAtUtc: string.IsNullOrWhiteSpace(response.SetAtUtc) ? null : response.SetAtUtc);

            return Results.Json(result, KautukaJsonContext.Default.UserNetworkPrivilegesDto);
        });

        // 8. PUT /api/network-access/users/{userId}/privileges
        app.MapPut("/api/network-access/users/{userId}/privileges", async Task<IResult> (
            string userId,
            HttpContext context,
            [FromServices] IManagerGrpcService managerService,
            [FromServices] IGatewayUserStore userStore,
            [FromServices] IGatewayAuthGuard auth,
            CancellationToken cancellationToken) =>
        {
            IResult? forbidden = auth.RequireAdmin(context, out GatewayUser? user);
            if (forbidden is not null || user is null)
            {
                return forbidden ?? Results.Json(new GatewayErrorResponse("Administrator privileges required."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }

            UpdateUserNetworkPrivilegesDto? dto = await context.Request.ReadFromJsonAsync(
                KautukaJsonContext.Default.UpdateUserNetworkPrivilegesDto,
                cancellationToken).ConfigureAwait(false);

            if (dto is null)
            {
                return Results.BadRequest(new GatewayErrorResponse("Invalid privileges body."));
            }

            bool targetIsAdmin = userStore.TryGetUserById(userId, out GatewayUser? targetUser) && targetUser is not null && GatewayOwnershipAccess.IsAdmin(targetUser);

            var request = new NetworkAccessSetUserPrivilegesRequest
            {
                Actor = new NetworkAccessActorMessage { UserId = user.Id, IsAdmin = true },
                UserId = userId,
                CanApproveOwn = dto.CanApproveOwn,
                CanAllowAllAgents = dto.CanAllowAllAgents,
                TargetIsAdmin = targetIsAdmin,
            };

            NetworkAccessUserPrivilegesMessage response = await managerService.SetUserPrivilegesAsync(request, cancellationToken).ConfigureAwait(false);

            var result = new UserNetworkPrivilegesDto(
                UserId: response.UserId,
                CanApproveOwn: response.CanApproveOwn,
                CanAllowAllAgents: response.CanAllowAllAgents,
                SetBy: string.IsNullOrWhiteSpace(response.SetBy) ? null : response.SetBy,
                SetAtUtc: string.IsNullOrWhiteSpace(response.SetAtUtc) ? null : response.SetAtUtc);

            return Results.Json(result, KautukaJsonContext.Default.UserNetworkPrivilegesDto);
        });

        // 9. GET /api/network-access/brake
        app.MapGet("/api/network-access/brake", async Task<IResult> (
            HttpContext context,
            [FromServices] IManagerGrpcService managerService,
            [FromServices] IGatewayAuthGuard auth,
            CancellationToken cancellationToken) =>
        {
            IResult? unauthorized = auth.RequireAuthenticated(context, out GatewayUser? user);
            if (unauthorized is not null || user is null)
            {
                return unauthorized ?? Results.Unauthorized();
            }

            bool isAdmin = GatewayOwnershipAccess.IsAdmin(user);
            var request = new NetworkAccessGetBrakeRequest
            {
                Actor = new NetworkAccessActorMessage { UserId = user.Id, IsAdmin = isAdmin },
            };

            NetworkAccessBrakeMessage response = await managerService.Client.GetBrakeAsync(request, cancellationToken: cancellationToken);

            NetworkAccessUserBrakeMessage? userBrake = response.Users.FirstOrDefault(u => string.Equals(u.UserId, user.Id, StringComparison.Ordinal));
            var result = new NetworkAccessBrakeDto(
                UserPaused: userBrake is not null,
                UserPausedBy: userBrake?.SetBy,
                UserLockedByAdmin: userBrake?.LockedByAdmin ?? false,
                SystemPaused: response.System,
                SystemPausedBy: string.IsNullOrWhiteSpace(response.SystemSetBy) ? null : response.SystemSetBy);

            return Results.Json(result, KautukaJsonContext.Default.NetworkAccessBrakeDto);
        });

        // 10. PUT /api/network-access/brake
        app.MapPut("/api/network-access/brake", async Task<IResult> (
            HttpContext context,
            [FromServices] IManagerGrpcService managerService,
            [FromServices] IGatewayAuthGuard auth,
            CancellationToken cancellationToken) =>
        {
            IResult? unauthorized = auth.RequireAuthenticated(context, out GatewayUser? user);
            if (unauthorized is not null || user is null)
            {
                return unauthorized ?? Results.Unauthorized();
            }

            UpdateUserBrakeDto? dto = await context.Request.ReadFromJsonAsync(
                KautukaJsonContext.Default.UpdateUserBrakeDto,
                cancellationToken).ConfigureAwait(false);

            if (dto is null)
            {
                return Results.BadRequest(new GatewayErrorResponse("Invalid brake payload."));
            }

            bool isAdmin = GatewayOwnershipAccess.IsAdmin(user);
            if (!isAdmin && !string.IsNullOrWhiteSpace(dto.UserId) && !string.Equals(dto.UserId, user.Id, StringComparison.Ordinal))
            {
                return Results.Json(new GatewayErrorResponse("Cannot pause or resume network access for another user."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }

            string targetUserId = isAdmin ? (dto.UserId ?? user.Id) : user.Id;

            var request = new NetworkAccessSetBrakeRequest
            {
                Actor = new NetworkAccessActorMessage { UserId = user.Id, IsAdmin = isAdmin },
                Scope = "user",
                UserId = targetUserId,
                On = dto.On,
            };

            try
            {
                NetworkAccessBrakeMessage response = await managerService.Client.SetBrakeAsync(request, cancellationToken: cancellationToken);
                NetworkAccessUserBrakeMessage? userBrake = response.Users.FirstOrDefault(u => string.Equals(u.UserId, user.Id, StringComparison.Ordinal));
                var result = new NetworkAccessBrakeDto(
                    UserPaused: userBrake is not null,
                    UserPausedBy: userBrake?.SetBy,
                    UserLockedByAdmin: userBrake?.LockedByAdmin ?? false,
                    SystemPaused: response.System,
                    SystemPausedBy: string.IsNullOrWhiteSpace(response.SystemSetBy) ? null : response.SystemSetBy);

                return Results.Json(result, KautukaJsonContext.Default.NetworkAccessBrakeDto);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied)
            {
                return Results.Json(new GatewayErrorResponse(ex.Status.Detail), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }
        });

        // 11. PUT /api/network-access/brake/system
        app.MapPut("/api/network-access/brake/system", async Task<IResult> (
            HttpContext context,
            [FromServices] IManagerGrpcService managerService,
            [FromServices] IGatewayAuthGuard auth,
            CancellationToken cancellationToken) =>
        {
            IResult? forbidden = auth.RequireAdmin(context, out GatewayUser? user);
            if (forbidden is not null || user is null)
            {
                return forbidden ?? Results.Json(new GatewayErrorResponse("Administrator privileges required."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }

            UpdateSystemBrakeDto? dto = await context.Request.ReadFromJsonAsync(
                KautukaJsonContext.Default.UpdateSystemBrakeDto,
                cancellationToken).ConfigureAwait(false);

            if (dto is null)
            {
                return Results.BadRequest(new GatewayErrorResponse("Invalid system brake payload."));
            }

            var request = new NetworkAccessSetBrakeRequest
            {
                Actor = new NetworkAccessActorMessage { UserId = user.Id, IsAdmin = true },
                Scope = "system",
                On = dto.On,
            };

            NetworkAccessBrakeMessage response = await managerService.Client.SetBrakeAsync(request, cancellationToken: cancellationToken);
            NetworkAccessUserBrakeMessage? userBrake = response.Users.FirstOrDefault(u => string.Equals(u.UserId, user.Id, StringComparison.Ordinal));
            var result = new NetworkAccessBrakeDto(
                UserPaused: userBrake is not null,
                UserPausedBy: userBrake?.SetBy,
                UserLockedByAdmin: userBrake?.LockedByAdmin ?? false,
                SystemPaused: response.System,
                SystemPausedBy: string.IsNullOrWhiteSpace(response.SystemSetBy) ? null : response.SystemSetBy);

            return Results.Json(result, KautukaJsonContext.Default.NetworkAccessBrakeDto);
        });

        // 12. GET /api/network-access/compat
        app.MapGet("/api/network-access/compat", async Task<IResult> (
            HttpContext context,
            [FromServices] IManagerGrpcService managerService,
            [FromServices] IGatewayAuthGuard auth,
            CancellationToken cancellationToken) =>
        {
            IResult? forbidden = auth.RequireAdmin(context, out GatewayUser? user);
            if (forbidden is not null || user is null)
            {
                return forbidden ?? Results.Json(new GatewayErrorResponse("Administrator privileges required."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }

            var request = new NetworkAccessActorMessage { UserId = user.Id, IsAdmin = true };
            ImageCompatStateResponse response = await managerService.Client.GetImageCompatibilityAsync(request, cancellationToken: cancellationToken);

            var pendingDtos = response.PendingRequests.Select(p => new ImageCompatRequestDto(
                ImageDigest: p.ImageDigest,
                ConnectorId: string.IsNullOrWhiteSpace(p.ConnectorId) ? null : p.ConnectorId,
                ConnectorDisplayName: string.IsNullOrWhiteSpace(p.ConnectorDisplayName) ? null : p.ConnectorDisplayName,
                FirstSeenUtc: p.FirstSeenUtc,
                Reason: string.IsNullOrWhiteSpace(p.FailureReason) ? null : p.FailureReason)).ToArray();

            var profileDtos = response.Profiles.Select(p => new ImageCompatProfileDto(
                ImageDigest: p.ImageDigest,
                ConnectorId: string.IsNullOrWhiteSpace(p.ConnectorId) ? null : p.ConnectorId,
                ConnectorDisplayName: string.IsNullOrWhiteSpace(p.ConnectorDisplayName) ? null : p.ConnectorDisplayName,
                Relaxations: p.Relaxations.ToArray(),
                GrantedBy: p.GrantedBy,
                GrantedAtUtc: p.GrantedAtUtc,
                Reason: string.IsNullOrWhiteSpace(p.Reason) ? null : p.Reason)).ToArray();

            var result = new ImageCompatResponseDto(pendingDtos, profileDtos);
            return Results.Json(result, KautukaJsonContext.Default.ImageCompatResponseDto);
        });

        // 13. POST /api/network-access/compat/{imageDigest}
        app.MapPost("/api/network-access/compat/{imageDigest}", async Task<IResult> (
            string imageDigest,
            HttpContext context,
            [FromServices] IManagerGrpcService managerService,
            [FromServices] IGatewayAuthGuard auth,
            CancellationToken cancellationToken) =>
        {
            IResult? forbidden = auth.RequireAdmin(context, out GatewayUser? user);
            if (forbidden is not null || user is null)
            {
                return forbidden ?? Results.Json(new GatewayErrorResponse("Administrator privileges required."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }

            CreateCompatProfileDto? dto = await context.Request.ReadFromJsonAsync(
                KautukaJsonContext.Default.CreateCompatProfileDto,
                cancellationToken).ConfigureAwait(false);

            if (dto is null)
            {
                return Results.BadRequest(new GatewayErrorResponse("Invalid compat profile payload."));
            }

            var request = new ImageCompatSetProfileRequest
            {
                Actor = new NetworkAccessActorMessage { UserId = user.Id, IsAdmin = true },
                ImageDigest = imageDigest,
                ConnectorId = dto.ConnectorId ?? string.Empty,
                ConnectorDisplayName = dto.ConnectorDisplayName ?? string.Empty,
                Reason = dto.Reason ?? string.Empty,
            };
            request.Relaxations.AddRange(dto.Relaxations);

            ImageCompatProfileMessage response = await managerService.Client.SetImageCompatibilityAsync(request, cancellationToken: cancellationToken);

            var result = new ImageCompatProfileDto(
                ImageDigest: response.ImageDigest,
                ConnectorId: string.IsNullOrWhiteSpace(response.ConnectorId) ? null : response.ConnectorId,
                ConnectorDisplayName: string.IsNullOrWhiteSpace(response.ConnectorDisplayName) ? null : response.ConnectorDisplayName,
                Relaxations: response.Relaxations.ToArray(),
                GrantedBy: response.GrantedBy,
                GrantedAtUtc: response.GrantedAtUtc,
                Reason: string.IsNullOrWhiteSpace(response.Reason) ? null : response.Reason);

            return Results.Json(result, KautukaJsonContext.Default.ImageCompatProfileDto);
        });

        // 14. DELETE /api/network-access/compat/{imageDigest}
        app.MapDelete("/api/network-access/compat/{imageDigest}", async Task<IResult> (
            string imageDigest,
            HttpContext context,
            [FromServices] IManagerGrpcService managerService,
            [FromServices] IGatewayAuthGuard auth,
            CancellationToken cancellationToken) =>
        {
            IResult? forbidden = auth.RequireAdmin(context, out GatewayUser? user);
            if (forbidden is not null || user is null)
            {
                return forbidden ?? Results.Json(new GatewayErrorResponse("Administrator privileges required."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }

            var request = new ImageCompatDeleteProfileRequest
            {
                Actor = new NetworkAccessActorMessage { UserId = user.Id, IsAdmin = true },
                ImageDigest = imageDigest,
            };

            await managerService.Client.DeleteImageCompatibilityAsync(request, cancellationToken: cancellationToken);
            return Results.NoContent();
        });
    }

    private static NetworkAccessRuleDto MapRuleToDto(
        NetworkAccessRuleMessage rule,
        IGatewayUserStore userStore,
        IGatewayCatalogStore catalogStore)
    {
        string? userDisplayName = null;
        if (!string.IsNullOrWhiteSpace(rule.UserId))
        {
            userDisplayName = userStore.TryGetUserById(rule.UserId, out GatewayUser? u) && u is not null
                ? (!string.IsNullOrWhiteSpace(u.DisplayName) ? u.DisplayName : u.Username)
                : rule.UserId;
        }

        string? agentDisplayName = null;
        if (!string.IsNullOrWhiteSpace(rule.AgentId) && catalogStore.TryGetAgent(rule.AgentId, out GatewayAgent? a) && a is not null)
        {
            agentDisplayName = a.Name;
        }

        string? connectorDisplayName = null;
        if (!string.IsNullOrWhiteSpace(rule.ConnectorId) && catalogStore.TryGetMcpServer(rule.ConnectorId, out GatewayMcpServer? c) && c is not null)
        {
            connectorDisplayName = c.Name;
        }

        string summaryText = FormatRuleSummaryText(rule, userDisplayName, agentDisplayName, connectorDisplayName);

        return new NetworkAccessRuleDto(
            Id: rule.Id,
            ScopeKind: rule.ScopeKind,
            UserId: string.IsNullOrWhiteSpace(rule.UserId) ? null : rule.UserId,
            UserDisplayName: userDisplayName,
            AgentId: string.IsNullOrWhiteSpace(rule.AgentId) ? null : rule.AgentId,
            AgentDisplayName: agentDisplayName,
            ConnectorId: string.IsNullOrWhiteSpace(rule.ConnectorId) ? null : rule.ConnectorId,
            ConnectorDisplayName: connectorDisplayName,
            Destination: rule.Destination,
            DestKind: rule.DestKind,
            Port: rule.Port,
            Allowed: rule.Allowed,
            AllowLan: rule.AllowLan,
            Source: rule.Source,
            Paused: rule.Paused,
            SummaryText: summaryText,
            DecidedBy: rule.DecidedBy,
            DecidedAtUtc: rule.DecidedAtUtc,
            OpenInternetBadge: rule.OpenInternetBadge);
    }

    private static async Task<NetworkAccessRequestDto> BuildRequestDtoAsync(
        NetworkAccessRequestMessage req,
        IManagerGrpcService managerService,
        NetworkAccessActorMessage actor,
        IGatewayUserStore userStore,
        IGatewayCatalogStore catalogStore,
        Dictionary<string, UserPrivilegesSummaryDto>? privCache,
        CancellationToken cancellationToken)
    {
        string? userDisplayName = null;
        bool ownerIsAdmin = false;

        if (!string.IsNullOrWhiteSpace(req.UserId))
        {
            if (userStore.TryGetUserById(req.UserId, out GatewayUser? ownerUser) && ownerUser is not null)
            {
                userDisplayName = !string.IsNullOrWhiteSpace(ownerUser.DisplayName) ? ownerUser.DisplayName : ownerUser.Username;
                ownerIsAdmin = GatewayOwnershipAccess.IsAdmin(ownerUser);
            }
            else
            {
                userDisplayName = req.UserId;
            }
        }

        string? agentDisplayName = null;
        if (!string.IsNullOrWhiteSpace(req.AgentId) && catalogStore.TryGetAgent(req.AgentId, out GatewayAgent? agent) && agent is not null)
        {
            agentDisplayName = agent.Name;
        }

        string? connectorDisplayName = null;
        if (!string.IsNullOrWhiteSpace(req.ConnectorId) && catalogStore.TryGetMcpServer(req.ConnectorId, out GatewayMcpServer? connector) && connector is not null)
        {
            connectorDisplayName = connector.Name;
        }

        UserPrivilegesSummaryDto? ownerPrivs = null;
        if (!string.IsNullOrWhiteSpace(req.UserId) && !string.Equals(req.UserId, "system", StringComparison.Ordinal))
        {
            if (privCache is null || !privCache.TryGetValue(req.UserId, out ownerPrivs))
            {
                try
                {
                    NetworkAccessUserPrivilegesMessage p = await managerService.GetUserPrivilegesAsync(
                        new NetworkAccessGetUserPrivilegesRequest
                        {
                            Actor = actor,
                            UserId = req.UserId,
                            TargetIsAdmin = ownerIsAdmin,
                        },
                        cancellationToken).ConfigureAwait(false);
                    ownerPrivs = new UserPrivilegesSummaryDto(p.CanApproveOwn, p.CanAllowAllAgents);
                }
                catch (Exception ex) when (ex is RpcException or NotSupportedException or InvalidOperationException)
                {
                    ownerPrivs = new UserPrivilegesSummaryDto(ownerIsAdmin, ownerIsAdmin);
                }

                privCache?.TryAdd(req.UserId, ownerPrivs);
            }
        }

        bool canApproveOwn = ownerPrivs?.CanApproveOwn ?? false;
        bool canAllowAllAgents = ownerPrivs?.CanAllowAllAgents ?? false;
        bool isSystemwide = req.Systemwide || req.Attribution is "none" || req.ActorKind is "first-party";
        bool waitingForAdmin = req.WaitingForAdmin || isSystemwide || !canApproveOwn;

        return new NetworkAccessRequestDto(
            Id: req.Id,
            UserId: string.IsNullOrWhiteSpace(req.UserId) ? null : req.UserId,
            UserDisplayName: userDisplayName,
            UserPronoun: null,
            AgentId: string.IsNullOrWhiteSpace(req.AgentId) ? null : req.AgentId,
            AgentDisplayName: agentDisplayName,
            ConnectorId: string.IsNullOrWhiteSpace(req.ConnectorId) ? null : req.ConnectorId,
            ConnectorDisplayName: connectorDisplayName,
            Systemwide: req.Systemwide,
            Attribution: req.Attribution,
            Destination: req.Destination,
            DestKind: req.DestKind,
            Port: req.Port,
            ActorKind: req.ActorKind,
            FirstSeenUtc: req.FirstSeenUtc,
            LastSeenUtc: req.LastSeenUtc,
            Attempts: req.Attempts,
            Status: req.Status,
            Origin: req.Origin,
            Purpose: string.IsNullOrWhiteSpace(req.Purpose) ? null : req.Purpose,
            CompatibilityMode: req.CompatibilityMode,
            WaitingForAdmin: waitingForAdmin,
            OwnerCanApproveOwn: canApproveOwn,
            OwnerCanAllowAllAgents: canAllowAllAgents);
    }

    private static string FormatRuleSummaryText(
        NetworkAccessRuleMessage rule,
        string? userDisplayName,
        string? agentDisplayName,
        string? connectorDisplayName)
    {
        string user = !string.IsNullOrWhiteSpace(userDisplayName) ? userDisplayName : (rule.UserId ?? "User");
        string agent = !string.IsNullOrWhiteSpace(agentDisplayName) ? agentDisplayName : (rule.AgentId ?? "Agent");
        string connector = !string.IsNullOrWhiteSpace(connectorDisplayName) ? connectorDisplayName : (rule.ConnectorId ?? "Connector");

        if (string.Equals(rule.DestKind, "any-public", StringComparison.OrdinalIgnoreCase))
        {
            return $"{user}'s {connector} can reach any public website";
        }

        string dest = rule.Port is 0 or 80 or 443 ? rule.Destination : $"{rule.Destination}:{rule.Port}";
        string action = rule.Allowed ? "can reach" : "cannot reach";

        return rule.ScopeKind switch
        {
            "agent" => $"{user}'s {agent} {action} {dest}",
            "user" => !string.IsNullOrWhiteSpace(rule.ConnectorId)
                ? $"Only {user}, using {connector} {action} {dest}"
                : $"All of {user}'s agents, including new ones, {action} {dest}",
            "connector-system" => $"Everyone using {connector} {action} {dest}",
            "system" => $"Everyone (systemwide) {action} {dest}",
            _ => $"{dest} ({rule.ScopeKind})"
        };
    }
}
