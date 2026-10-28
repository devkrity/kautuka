using System.Text.Json;

using Grpc.Core;

using Kautuka.Gateway.Domain.Channels;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Endpoints.Common;

namespace Kautuka.Gateway.Endpoints.Settings;

internal static partial class GatewaySettingsEndpointMappings
{
    private static void MapUserSettingsEndpoints(WebApplication app)
    {
        app.MapGet("/api/settings/channels/slack/create-app", (IGatewayAuthGuard auth, HttpContext context) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Json(new GatewayErrorResponse("Not authenticated."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status401Unauthorized);
            }

            return Results.Text(SlackSetupManifest.CreateAppUrl(user.DisplayName).AbsoluteUri, "text/plain");
        });

        app.MapPost("/api/settings/me/channels/slack/pairing", async (
            IChannelAttachmentRegistry attachments,
            IGatewayAuthGuard auth,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Json(new GatewayErrorResponse("Not authenticated."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status401Unauthorized);
            }

            string? code = await attachments.StartSlackPairingAsync(user.Id, cancellationToken).ConfigureAwait(false);
            return code is null
                ? Results.Json(new GatewayErrorResponse("Connect Slack successfully before pairing your account."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status409Conflict)
                : Results.Content($"{{\"code\":\"{code}\"}}", "application/json");
        });

        app.MapGet("/api/settings/me/channels/slack/pairing", async (
            IChannelAttachmentRegistry attachments,
            IGatewayAuthGuard auth,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Json(new GatewayErrorResponse("Not authenticated."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status401Unauthorized);
            }

            bool paired = await attachments.IsSlackPairedAsync(user.Id, cancellationToken).ConfigureAwait(false);
            string? reason = paired ? null : await attachments.GetSlackPairingFailureReasonAsync(user.Id, cancellationToken).ConfigureAwait(false);
            GatewaySlackPairingStatusResponse response = new(paired, reason);
            return Results.Json(response, KautukaJsonContext.Default.GatewaySlackPairingStatusResponse);
        });

        app.MapPost("/api/settings/me/channels/slack/test-message", async (
            IChannelAttachmentRegistry attachments,
            IChannelOutboundMessagePublisher outboundPublisher,
            IGatewayAuthGuard auth,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Json(new GatewayErrorResponse("Not authenticated."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status401Unauthorized);
            }

            await attachments.RefreshUserAsync(user.Id, cancellationToken).ConfigureAwait(false);
            if (!attachments.TryResolveActiveRegistration(user.Id, out string? registrationId, out string? chatId)
                || string.IsNullOrWhiteSpace(registrationId) || string.IsNullOrWhiteSpace(chatId)
                || !attachments.TryGetRegistration(registrationId, out GatewayChannelRegistration? registration)
                || registration is null
                || !string.Equals(registration.ChannelType, "slack", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(registration.Settings.GetStringNodeValue("slack_owner_user_id")))
            {
                return Results.Json(new GatewayErrorResponse("Link your Slack direct message before sending a test."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status409Conflict);
            }

            await outboundPublisher.PublishTextMessageAsync(
                new ChannelOutboundTextMessage("slack", registrationId, chatId, null, "Kautuka is connected to Slack. Your setup is working."),
                cancellationToken).ConfigureAwait(false);
            return Results.Content("{\"sent\":true}", "application/json");
        });

        app.MapGet("/api/settings/users", async (IGatewayUserStore users, IChannelAttachmentRegistry attachments, IGatewayAuthGuard auth, HttpContext context) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out _);
            if (unauthorized is not null)
            {
                return unauthorized;
            }

            GatewayUser[] userRecords = [.. users.ListUsers().Select(item => users.TryGetUserById(item.Id, out GatewayUser? user) ? user : null).Where(static item => item is not null)!];
            await attachments.RefreshUsersAsync(userRecords, context.RequestAborted).ConfigureAwait(false);
            GatewayUserListItem[] items = [.. userRecords.Select(user => attachments.BuildUserPayload(user) with
            {
                ExternalProviders = users.ExternalProvidersFor(user.Id),
            })];
            return Results.Json(new GatewayUsersResponse(items), KautukaJsonContext.Default.GatewayUsersResponse);
        });

        app.MapPost("/api/settings/users/invite", (JsonElement body, IGatewayUserStore users, IGatewayAuthGuard auth, HttpContext context) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out _);
            if (unauthorized is not null)
            {
                return unauthorized;
            }

            GatewayUserListItem? user = users.InviteUser(body, out string? error);
            return user is null
                ? Results.Json(new GatewayErrorResponse(error ?? "Unable to invite user."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest)
                : Results.Json(new GatewayUserResponse(user, users.IssuedInviteCode(user.Id)), KautukaJsonContext.Default.GatewayUserResponse);
        });

        app.MapPut("/api/settings/users/{userId}/role", (string userId, JsonElement body, IGatewayUserStore users, IGatewayAuthGuard auth, HttpContext context) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out _);
            if (unauthorized is not null)
            {
                return unauthorized;
            }

            GatewayUserRoleUpdateRequest request;
            try
            {
                request = body.Deserialize(KautukaJsonContext.Default.GatewayUserRoleUpdateRequest) ?? new GatewayUserRoleUpdateRequest();
            }
            catch (JsonException)
            {
                return Results.BadRequest(new GatewayErrorResponse("Invalid user role payload."));
            }

            return users.UpdateRole(userId, request, out string? error)
                ? Results.Json(new GatewayBooleanSuccessResponse(true), KautukaJsonContext.Default.GatewayBooleanSuccessResponse)
                : Results.Json(
                    new GatewayErrorResponse(error ?? "Unable to update user role."),
                    KautukaJsonContext.Default.GatewayErrorResponse,
                    statusCode: string.Equals(error, "User not found.", StringComparison.Ordinal)
                        ? StatusCodes.Status404NotFound
                        : StatusCodes.Status400BadRequest);
        });

        app.MapPut("/api/settings/users/{userId}/active", (string userId, JsonElement body, IGatewayUserStore users, IGatewayAuthGuard auth, HttpContext context) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out _);
            if (unauthorized is not null)
            {
                return unauthorized;
            }

            GatewayUserActivationUpdateRequest request;
            try
            {
                request = body.Deserialize(KautukaJsonContext.Default.GatewayUserActivationUpdateRequest) ?? new GatewayUserActivationUpdateRequest();
            }
            catch (JsonException)
            {
                return Results.BadRequest(new GatewayErrorResponse("Invalid user activation payload."));
            }

            return users.UpdateIsActive(userId, request.IsActive, out string? error)
                ? Results.Json(new GatewayBooleanSuccessResponse(true), KautukaJsonContext.Default.GatewayBooleanSuccessResponse)
                : Results.Json(
                    new GatewayErrorResponse(error ?? "Unable to update user activation."),
                    KautukaJsonContext.Default.GatewayErrorResponse,
                    statusCode: string.Equals(error, "User not found.", StringComparison.Ordinal)
                        ? StatusCodes.Status404NotFound
                        : StatusCodes.Status400BadRequest);
        });

        app.MapPut("/api/settings/users/{userId}/import-permissions", (string userId, JsonElement body, IGatewayUserStore users, IGatewayAuthGuard auth, HttpContext context) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out _);
            if (unauthorized is not null)
            {
                return unauthorized;
            }

            GatewayUserImportPermissionsUpdateRequest request;
            try
            {
                request = body.Deserialize(KautukaJsonContext.Default.GatewayUserImportPermissionsUpdateRequest) ?? new GatewayUserImportPermissionsUpdateRequest();
            }
            catch (JsonException)
            {
                return Results.BadRequest(new GatewayErrorResponse("Invalid user import permissions payload."));
            }

            return users.UpdateImportPermissions(userId, request)
                ? Results.Json(new GatewayBooleanSuccessResponse(true), KautukaJsonContext.Default.GatewayBooleanSuccessResponse)
                : Results.Json(new GatewayErrorResponse("User not found"), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
        });

        app.MapPut("/api/settings/users/{userId}/profile", (string userId, JsonElement body, IGatewayUserStore users, IGatewayAuthGuard auth, HttpContext context) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out _);
            if (unauthorized is not null)
            {
                return unauthorized;
            }

            GatewayUserProfileUpdateRequest request;
            try
            {
                request = body.Deserialize(KautukaJsonContext.Default.GatewayUserProfileUpdateRequest) ?? new GatewayUserProfileUpdateRequest();
            }
            catch (JsonException)
            {
                return Results.BadRequest(new GatewayErrorResponse("Invalid user profile payload."));
            }

            return users.UpdateProfile(userId, request.Email, request.DisplayName)
                ? Results.Json(new GatewayBooleanSuccessResponse(true), KautukaJsonContext.Default.GatewayBooleanSuccessResponse)
                : Results.Json(new GatewayErrorResponse("User not found"), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
        });

        app.MapPut("/api/settings/me/profile", (JsonElement body, IGatewayUserStore users, IGatewayAuthGuard auth, HttpContext context) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Json(new GatewayErrorResponse("Not authenticated."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status401Unauthorized);
            }

            GatewayUserProfileUpdateRequest request;
            try
            {
                request = body.Deserialize(KautukaJsonContext.Default.GatewayUserProfileUpdateRequest) ?? new GatewayUserProfileUpdateRequest();
            }
            catch (JsonException)
            {
                return Results.BadRequest(new GatewayErrorResponse("Invalid user profile payload."));
            }

            if (string.IsNullOrWhiteSpace(request.DisplayName))
            {
                return Results.BadRequest(new GatewayErrorResponse("Display name is required."));
            }

            if (!string.IsNullOrWhiteSpace(request.Email))
            {
                if (!System.Net.Mail.MailAddress.TryCreate(request.Email.Trim(), out System.Net.Mail.MailAddress? parsedEmail)
                    || parsedEmail is null
                    || !string.Equals(parsedEmail.Address, request.Email.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return Results.BadRequest(new GatewayErrorResponse("Enter a valid email address."));
                }
            }

            if (!users.UpdateProfile(user.Id, request.Email, request.DisplayName)
                || !users.TryGetUserById(user.Id, out GatewayUser? updatedUser)
                || updatedUser is null)
            {
                return Results.Json(new GatewayErrorResponse("User not found."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Json(
                GatewayEndpointUtilities.ToUserPayload(updatedUser, users.ExternalProvidersFor(updatedUser.Id)),
                KautukaJsonContext.Default.GatewayUserPayload);
        });

        app.MapDelete("/api/settings/users/{userId}/external-logins/{provider}", ClearUserExternalLogin);
        app.MapDelete("/api/settings/me/external-logins/{provider}", ClearMyExternalLogin);

        app.MapPut("/api/settings/me/password", (JsonElement body, IGatewayUserStore users, IGatewayAuthGuard auth, HttpContext context) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Json(new GatewayErrorResponse("Not authenticated."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status401Unauthorized);
            }

            GatewayUserPasswordUpdateRequest request;
            try
            {
                request = body.Deserialize(KautukaJsonContext.Default.GatewayUserPasswordUpdateRequest) ?? new GatewayUserPasswordUpdateRequest();
            }
            catch (JsonException)
            {
                return Results.BadRequest(new GatewayErrorResponse("Invalid password update payload."));
            }

            return users.UpdatePassword(user.Id, request.CurrentPassword, request.NewPassword, out string? error)
                ? Results.Json(new GatewayBooleanSuccessResponse(true), KautukaJsonContext.Default.GatewayBooleanSuccessResponse)
                : Results.Json(new GatewayErrorResponse(error ?? "Unable to update password."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
        });

        app.MapGet("/api/settings/me/channels", async (
            IChannelAttachmentRegistry attachments,
            IGatewayAuthGuard auth,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Json(new GatewayErrorResponse("Not authenticated."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status401Unauthorized);
            }

            GatewayUserChannelsResponse response = await attachments.BuildUserChannelCatalogAsync(user, null, cancellationToken).ConfigureAwait(false);
            return Results.Json(response, KautukaJsonContext.Default.GatewayUserChannelsResponse);
        });

        app.MapPost("/api/settings/me/channels/{providerKey}/lifecycle", async (
            string providerKey,
            JsonElement body,
            IChannelAttachmentRegistry attachments,
            IGatewayAuthGuard auth,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Json(new GatewayErrorResponse("Not authenticated."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status401Unauthorized);
            }

            string transition = body.GetStringProperty("transition", string.Empty).Trim().ToUpperInvariant();
            if (transition is "REMOVE")
            {
                transition = "DELETE";
            }

            if (transition is not ("SUSPEND" or "RECOVER" or "DELETE"))
            {
                return Results.Json(new GatewayErrorResponse("Self-service channel lifecycle supports suspend, recover, and remove only."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
            }

            ChannelLifecycleTransitionResult result = await attachments.TransitionLifecycleAsync(
                user,
                providerKey,
                transition,
                "user_requested",
                cancellationToken).ConfigureAwait(false);
            return Results.Json(
                new GatewayChannelLifecyclePayload(result.Accepted, result.Status, result.Detail),
                KautukaJsonContext.Default.GatewayChannelLifecyclePayload,
                statusCode: result.Accepted ? StatusCodes.Status200OK : StatusCodes.Status409Conflict);
        });

        app.MapGet("/api/settings/users/{userId}/channels", async (
            string userId,
            IGatewayUserStore users,
            IChannelAttachmentRegistry attachments,
            IGatewayAuthGuard auth,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out _);
            if (unauthorized is not null)
            {
                return unauthorized;
            }

            if (!users.TryGetUserById(userId, out GatewayUser? user) || user is null)
            {
                return Results.Json(new GatewayErrorResponse("User not found."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
            }

            GatewayUserChannelsResponse response = await attachments.BuildUserChannelCatalogAsync(user, null, cancellationToken).ConfigureAwait(false);
            return Results.Json(response, KautukaJsonContext.Default.GatewayUserChannelsResponse);
        });

        app.MapPut("/api/settings/users/{userId}/channels/{channelType}", async (
            string userId,
            string channelType,
            JsonElement body,
            IGatewayUserStore users,
            IChannelAttachmentRegistry attachments,
            IChannelRegistrationProvisioningService provisioning,
            IGatewayAuthGuard auth,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out _);
            if (unauthorized is not null)
            {
                return unauthorized;
            }

            if (!users.TryGetUserById(userId, out GatewayUser? user) || user is null)
            {
                return Results.Json(new GatewayErrorResponse("User not found."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
            }

            ChannelSettingsUpdateResult? update;
            try
            {
                update = await attachments.UpdateUserChannelSettingsAsync(user, channelType, body, cancellationToken).ConfigureAwait(false);
            }
            catch (RpcException exception) when (exception.StatusCode == StatusCode.FailedPrecondition && exception.Status.Detail == "attachment_requires_explicit_recovery")
            {
                GatewayUserChannelsResponse recovery = await attachments.BuildUserChannelCatalogAsync(
                    user,
                    new GatewayChannelSetupPayload(false, "recovery_required", "Start recovery before replacing credentials or reconnecting."),
                    cancellationToken).ConfigureAwait(false);
                return Results.Json(recovery, KautukaJsonContext.Default.GatewayUserChannelsResponse);
            }
            catch (DuplicateChannelBotTokenException)
            {
                return DuplicateBotTokenResult(channelType);
            }
            catch (ChannelSetupRejectedException exception)
            {
                GatewayUserChannelsResponse rejected = await attachments.BuildUserChannelCatalogAsync(
                    user,
                    new GatewayChannelSetupPayload(false, exception.Code, exception.Message),
                    cancellationToken).ConfigureAwait(false);
                return Results.Json(rejected, KautukaJsonContext.Default.GatewayUserChannelsResponse);
            }
            if (update is null)
            {
                return Results.Json(new GatewayErrorResponse("Unable to update channel settings."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
            }

            ChannelRegistrationProvisioningResult? provisioningResult = null;
            if (!string.IsNullOrWhiteSpace(update.RegistrationId))
            {
                provisioningResult = await provisioning.ProvisionRegistrationAsync(
                    update.RegistrationId,
                    cancellationToken).ConfigureAwait(false);
            }

            GatewayChannelSetupPayload? setup = provisioningResult is null
                ? null
                : new GatewayChannelSetupPayload(provisioningResult.Success, provisioningResult.Status, provisioningResult.Detail);
            GatewayUserChannelsResponse response = await attachments.BuildUserChannelCatalogAsync(user, setup, cancellationToken).ConfigureAwait(false);
            return Results.Json(response, KautukaJsonContext.Default.GatewayUserChannelsResponse);
        });

        app.MapPut("/api/settings/me/channels/{channelType}", async (
            string channelType,
            JsonElement body,
            IChannelAttachmentRegistry attachments,
            IChannelRegistrationProvisioningService provisioning,
            IGatewayAuthGuard auth,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Json(new GatewayErrorResponse("Not authenticated."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status401Unauthorized);
            }

            ChannelSettingsUpdateResult? update;
            try
            {
                update = await attachments.UpdateUserChannelSettingsAsync(user, channelType, body, cancellationToken).ConfigureAwait(false);
            }
            catch (RpcException exception) when (exception.StatusCode == StatusCode.FailedPrecondition && exception.Status.Detail == "attachment_requires_explicit_recovery")
            {
                GatewayUserChannelsResponse recovery = await attachments.BuildUserChannelCatalogAsync(
                    user,
                    new GatewayChannelSetupPayload(false, "recovery_required", "Start recovery before replacing credentials or reconnecting."),
                    cancellationToken).ConfigureAwait(false);
                return Results.Json(recovery, KautukaJsonContext.Default.GatewayUserChannelsResponse);
            }
            catch (DuplicateChannelBotTokenException)
            {
                return DuplicateBotTokenResult(channelType);
            }
            catch (ChannelSetupRejectedException exception)
            {
                GatewayUserChannelsResponse rejected = await attachments.BuildUserChannelCatalogAsync(
                    user,
                    new GatewayChannelSetupPayload(false, exception.Code, exception.Message),
                    cancellationToken).ConfigureAwait(false);
                return Results.Json(rejected, KautukaJsonContext.Default.GatewayUserChannelsResponse);
            }
            if (update is null)
            {
                return Results.Json(new GatewayErrorResponse("Unable to update channel settings."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
            }

            ChannelRegistrationProvisioningResult? provisioningResult = null;
            if (!string.IsNullOrWhiteSpace(update.RegistrationId))
            {
                provisioningResult = await provisioning.ProvisionRegistrationAsync(
                    update.RegistrationId,
                    cancellationToken).ConfigureAwait(false);
            }

            GatewayChannelSetupPayload? setup = provisioningResult is null
                ? null
                : new GatewayChannelSetupPayload(provisioningResult.Success, provisioningResult.Status, provisioningResult.Detail);
            GatewayUserChannelsResponse response = await attachments.BuildUserChannelCatalogAsync(user, setup, cancellationToken).ConfigureAwait(false);
            return Results.Json(response, KautukaJsonContext.Default.GatewayUserChannelsResponse);
        });

        app.MapPost("/api/settings/users/{userId}/channels/{channelType}/hitl-test", async (
            string userId,
            string channelType,
            JsonElement body,
            IGatewayUserStore users,
            IChannelAttachmentRegistry attachments,
            IChannelIngressService ingress,
            IChannelRegistrationProvisioningService provisioning,
            IGatewayAuthGuard auth,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out _);
            if (unauthorized is not null)
            {
                return unauthorized;
            }

            if (!users.TryGetUserById(userId, out GatewayUser? user) || user is null)
            {
                return Results.Json(new GatewayErrorResponse("User not found."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
            }

            bool hasDraftChannelSettings = HasDraftChannelSettings(body);

            string? externalUserId = null;
            if (hasDraftChannelSettings)
            {
                ChannelSettingsUpdateResult? update;
                try
                {
                    update = await attachments.UpdateUserChannelSettingsAsync(user, channelType, body, cancellationToken).ConfigureAwait(false);
                }
                catch (DuplicateChannelBotTokenException)
                {
                    return DuplicateBotTokenResult(channelType);
                }
                if (update is null)
                {
                    return Results.Json(new GatewayErrorResponse("Unable to update channel settings."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
                }

                externalUserId = update.ExternalUserId;
                if (!string.IsNullOrWhiteSpace(update.RegistrationId))
                {
                    ChannelRegistrationProvisioningResult provisioningResult = await provisioning.ProvisionRegistrationAsync(
                        update.RegistrationId,
                        cancellationToken).ConfigureAwait(false);
                    if (!provisioningResult.Success)
                    {
                        return Results.Json(new GatewayErrorResponse(provisioningResult.Detail), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
                    }
                }
            }

            await attachments.RefreshUserAsync(userId, cancellationToken).ConfigureAwait(false);
            if (!attachments.TryResolveActiveRegistration(userId, out string? registrationId, out string? configuredChannelDeviceId)
                || string.IsNullOrWhiteSpace(registrationId))
            {
                return Results.Json(new GatewayErrorResponse("User does not have active channel settings configured."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
            }

            if (!hasDraftChannelSettings)
            {
                ChannelRegistrationProvisioningResult provisioningResult = await provisioning.ProvisionRegistrationAsync(registrationId, cancellationToken).ConfigureAwait(false);
                if (!provisioningResult.Success)
                {
                    return Results.Json(new GatewayErrorResponse(provisioningResult.Detail), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
                }
            }

            string conversationId = ResolveHitlConversationId(body, externalUserId, configuredChannelDeviceId);
            if (string.IsNullOrWhiteSpace(conversationId))
            {
                return Results.Json(new GatewayErrorResponse("A conversation id is required when the channel destination is not configured."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
            }

            ChannelDispatchOptionPayload[] optionsPayload = body.TryGetProperty("options", out JsonElement options) && options.ValueKind == JsonValueKind.Array
                ? [.. options.EnumerateArray().Select(option => new ChannelDispatchOptionPayload(
                    option.GetStringProperty("label", string.Empty),
                    option.GetStringProperty("value", string.Empty)))]
                :
                [
                    new ChannelDispatchOptionPayload("Approve", "approve"),
                    new ChannelDispatchOptionPayload("Deny", "deny"),
                ];

            JsonElement payload = JsonSerializer.SerializeToElement(
                new ChannelDispatchInteractionPayload(
                    conversationId,
                    body.GetStringProperty("prompt", "Approve this action?"),
                    optionsPayload,
                    body.GetIntProperty("timeoutSeconds", 900),
                    body.GetNullableStringProperty("sessionId")),
                KautukaJsonContext.Default.ChannelDispatchInteractionPayload);

            ChannelInteractionDispatchResult result = await ingress.DispatchChannelInteractionAsync(registrationId, payload, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                return Results.Json(
                    new ChannelInteractionDispatchResponse(false, result.Status, result.Detail, result.InteractionId, result.RunId),
                    KautukaJsonContext.Default.ChannelInteractionDispatchResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            return Results.Json(
                new ChannelInteractionDispatchResponse(true, result.Status, result.Detail, result.InteractionId, result.RunId),
                KautukaJsonContext.Default.ChannelInteractionDispatchResponse);
        });

        app.MapPost("/api/settings/me/channels/{channelType}/hitl-test", async (
            string channelType,
            JsonElement body,
            IChannelAttachmentRegistry attachments,
            IChannelIngressService ingress,
            IChannelRegistrationProvisioningService provisioning,
            IGatewayAuthGuard auth,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Json(new GatewayErrorResponse("Not authenticated."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status401Unauthorized);
            }

            bool hasDraftChannelSettings = HasDraftChannelSettings(body);

            string? externalUserId = null;
            if (hasDraftChannelSettings)
            {
                ChannelSettingsUpdateResult? update;
                try
                {
                    update = await attachments.UpdateUserChannelSettingsAsync(user, channelType, body, cancellationToken).ConfigureAwait(false);
                }
                catch (DuplicateChannelBotTokenException)
                {
                    return DuplicateBotTokenResult(channelType);
                }
                if (update is null)
                {
                    return Results.Json(new GatewayErrorResponse("Unable to update channel settings."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
                }

                externalUserId = update.ExternalUserId;
                if (!string.IsNullOrWhiteSpace(update.RegistrationId))
                {
                    ChannelRegistrationProvisioningResult provisioningResult = await provisioning.ProvisionRegistrationAsync(
                        update.RegistrationId,
                        cancellationToken).ConfigureAwait(false);
                    if (!provisioningResult.Success)
                    {
                        return Results.Json(new GatewayErrorResponse(provisioningResult.Detail), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
                    }
                }
            }

            await attachments.RefreshUserAsync(user.Id, cancellationToken).ConfigureAwait(false);
            if (!attachments.TryResolveActiveRegistration(user.Id, out string? registrationId, out string? configuredChannelDeviceId)
                || string.IsNullOrWhiteSpace(registrationId))
            {
                return Results.Json(new GatewayErrorResponse("User does not have active channel settings configured."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
            }

            if (!hasDraftChannelSettings)
            {
                ChannelRegistrationProvisioningResult provisioningResult = await provisioning.ProvisionRegistrationAsync(registrationId, cancellationToken).ConfigureAwait(false);
                if (!provisioningResult.Success)
                {
                    return Results.Json(new GatewayErrorResponse(provisioningResult.Detail), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
                }
            }

            string conversationId = ResolveHitlConversationId(body, externalUserId, configuredChannelDeviceId);
            if (string.IsNullOrWhiteSpace(conversationId))
            {
                return Results.Json(new GatewayErrorResponse("A conversation id is required when the channel destination is not configured."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
            }

            ChannelDispatchOptionPayload[] optionsPayload = body.TryGetProperty("options", out JsonElement options) && options.ValueKind == JsonValueKind.Array
                ? [.. options.EnumerateArray().Select(option => new ChannelDispatchOptionPayload(
                    option.GetStringProperty("label", string.Empty),
                    option.GetStringProperty("value", string.Empty)))]
                :
                [
                    new ChannelDispatchOptionPayload("Approve", "approve"),
                    new ChannelDispatchOptionPayload("Deny", "deny"),
                ];

            JsonElement payload = JsonSerializer.SerializeToElement(
                new ChannelDispatchInteractionPayload(
                    conversationId,
                    body.GetStringProperty("prompt", "Approve this action?"),
                    optionsPayload,
                    body.GetIntProperty("timeoutSeconds", 900),
                    body.GetNullableStringProperty("sessionId")),
                KautukaJsonContext.Default.ChannelDispatchInteractionPayload);

            ChannelInteractionDispatchResult result = await ingress.DispatchChannelInteractionAsync(registrationId, payload, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                return Results.Json(
                    new ChannelInteractionDispatchResponse(false, result.Status, result.Detail, result.InteractionId, result.RunId),
                    KautukaJsonContext.Default.ChannelInteractionDispatchResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            return Results.Json(
                new ChannelInteractionDispatchResponse(true, result.Status, result.Detail, result.InteractionId, result.RunId),
                KautukaJsonContext.Default.ChannelInteractionDispatchResponse);
        });
    }

    private static IResult DuplicateBotTokenResult(string channelType)
        => Results.Json(
            new GatewayErrorResponse($"That bot token is already used by another {ProviderDisplayName(channelType)} bot on this account."),
            KautukaJsonContext.Default.GatewayErrorResponse,
            statusCode: StatusCodes.Status409Conflict);

    private static string ProviderDisplayName(string providerKey) => providerKey.ToUpperInvariant() switch
    {
        "TELEGRAM" => "Telegram",
        "SLACK" => "Slack",
        _ => providerKey,
    };

    private static bool HasDraftChannelSettings(JsonElement body)
    {
        if (body.TryGetProperty("settings", out JsonElement settingsElement)
            && settingsElement.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        return body.TryGetProperty("defaultTargetType", out _)
            || body.TryGetProperty("defaultTargetId", out _)
            || body.TryGetProperty("hitlTetherMode", out _);
    }

    private static string ResolveHitlConversationId(JsonElement body, string? updatedExternalUserId, string? configuredExternalUserId)
    {
        string explicitConversationId = body.GetStringProperty("chatId", body.GetStringProperty("chat_id", string.Empty)).Trim();
        if (!string.IsNullOrWhiteSpace(explicitConversationId))
        {
            return explicitConversationId;
        }

        return updatedExternalUserId ?? configuredExternalUserId ?? string.Empty;
    }

    private static IResult ClearUserExternalLogin(
        string userId,
        string provider,
        IGatewayUserStore users,
        IGatewayTokenStore tokens,
        IGatewayAuthGuard auth,
        HttpContext context)
    {
        IResult? unauthorized = auth.RequireAdmin(context, out GatewayUser? actor);
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        if (actor is null)
        {
            return Results.Json(new GatewayErrorResponse("Not authenticated."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status401Unauthorized);
        }

        return ClearExternalLogin(users, tokens, actor.Id, userId, provider, revoke: !string.Equals(actor.Id, userId, StringComparison.Ordinal));
    }

    private static IResult ClearMyExternalLogin(
        string provider,
        IGatewayUserStore users,
        IGatewayTokenStore tokens,
        IGatewayAuthGuard auth,
        HttpContext context)
    {
        if (!auth.TryAuthorize(context, out GatewayUser? actor) || actor is null)
        {
            return Results.Json(new GatewayErrorResponse("Not authenticated."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status401Unauthorized);
        }

        return ClearExternalLogin(users, tokens, actor.Id, actor.Id, provider, revoke: false);
    }

    private static IResult ClearExternalLogin(
        IGatewayUserStore users,
        IGatewayTokenStore tokens,
        string actorId,
        string userId,
        string provider,
        bool revoke)
    {
        if (!users.TryClearExternalLogin(userId, provider, actorId, out string? error))
        {
            return Results.Json(new GatewayErrorResponse(error ?? "Sign-in link was not found."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
        }

        if (revoke)
        {
            tokens.RevokeUserSessions(userId);
        }

        return Results.Json(new GatewayBooleanSuccessResponse(true), KautukaJsonContext.Default.GatewayBooleanSuccessResponse);
    }
}
