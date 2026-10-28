using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Grpc.Core;

using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Domain.Workflows;
using Kautuka.Gateway.Infrastructure.Channels;
using Kautuka.Gateway.Infrastructure.Credentials;
using Kautuka.Protos;

namespace Kautuka.Gateway.Domain.Channels;

internal interface IChannelAttachmentRegistry
{
    Task RefreshAttachmentAsync(string attachmentId, string ownerUserId, CancellationToken cancellationToken);
    Task RefreshUserAsync(string userId, CancellationToken cancellationToken);
    Task RefreshUsersAsync(IEnumerable<GatewayUser> users, CancellationToken cancellationToken);
    bool TryGetRegistration(string attachmentId, out GatewayChannelRegistration? registration);
    bool TryResolveIdentity(string attachmentId, string externalUserId, string chatId, out GatewayChannelIdentityLink? identity);
    bool TryResolveActiveRegistration(string userId, out string? registrationId, out string? externalUserId);
    GatewayChannelRegistration[] GetActiveChatRegistrations(string userId)
        => TryResolveActiveRegistration(userId, out string? id, out _)
            && id is not null && TryGetRegistration(id, out GatewayChannelRegistration? registration)
            && registration is not null ? [registration] : [];


    bool TryResolveAgentBot(string userId, string agentId, out string? registrationId, out string? externalUserId)
    {
        registrationId = null;
        externalUserId = null;
        return false;
    }

    bool TryResolveWorkflowReviewBot(string userId, string? workflowId, out string? registrationId, out string? externalUserId)
    {
        registrationId = null;
        externalUserId = null;
        return false;
    }

    GatewayChannelTargetBinding[] GetBindings(string attachmentId);
    GatewayChannelTargetBinding? GetDefaultBinding(string attachmentId);
    GatewayUserListItem BuildUserPayload(GatewayUser user);
    Task<GatewayUserChannelsResponse> BuildUserChannelCatalogAsync(GatewayUser user, GatewayChannelSetupPayload? setup, CancellationToken cancellationToken);
    Task<ChannelSettingsUpdateResult?> UpdateUserChannelSettingsAsync(GatewayUser user, string providerKey, JsonElement body, CancellationToken cancellationToken);
    Task<ChannelLifecycleTransitionResult> TransitionLifecycleAsync(
        GatewayUser user,
        string providerKey,
        string transition,
        string reason,
        CancellationToken cancellationToken)
        => Task.FromResult(new ChannelLifecycleTransitionResult(false, "UNSUPPORTED", "Channel lifecycle transitions are not available."));
    Task SetActiveTargetForUserAsync(string attachmentId, string externalUserId, string targetType, string targetId, CancellationToken cancellationToken);
    Task<bool> BindExternalIdentityAsync(string attachmentId, string ownerUserId, string externalUserId, string conversationId, CancellationToken cancellationToken);
    Task<string?> StartSlackPairingAsync(string ownerUserId, CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);
    Task<bool> CompleteSlackPairingAsync(string attachmentId, string ownerUserId, string externalUserId, string conversationId, string code, CancellationToken cancellationToken)
        => Task.FromResult(false);
    Task<bool> IsSlackPairedAsync(string ownerUserId, CancellationToken cancellationToken)
        => Task.FromResult(false);
    Task<string?> GetSlackPairingFailureReasonAsync(string ownerUserId, CancellationToken cancellationToken);
    ChannelHitlTetherMode GetChannelHitlTetherMode(string userId, string? sessionId);
}

internal sealed class ChannelAttachmentRegistry(
    GatewayState state,
    IChannelHostGrpcService channelHost,
    CredentialResolverService credentialResolver,
    ILogger<ChannelAttachmentRegistry> logger,
    IGatewayWorkflowStore? workflowStore = null,
    IChannelConversationMapStore? conversationMapStore = null) : IChannelAttachmentRegistry
{
    private const string agentTargetType = "agent";
    private const string botTokenKey = "bot_token";
    private const string channelDeviceIdKey = "channel_device_id";
    private const string hitlTetherModeKey = "hitl_tether_mode";
    private const string defaultTargetTypeKey = "default_target_type";
    private const string defaultTargetIdKey = "default_target_id";
    private readonly Lock sync = new();
    private readonly Dictionary<string, CachedAttachment> attachmentsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> activeAttachmentByUserId = new(StringComparer.Ordinal);

    public async Task RefreshAttachmentAsync(string attachmentId, string ownerUserId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(attachmentId) || string.IsNullOrWhiteSpace(ownerUserId))
        {
            return;
        }

        GetAttachmentResponse response;
        try
        {
            response = await channelHost.GetAttachmentAsync(new GetAttachmentRequest
            {
                Context = BuildContext(ownerUserId, "channels.read"),
                AttachmentId = attachmentId,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            RemoveCachedAttachment(attachmentId);
            return;
        }
        catch (InvalidOperationException ex)
        {
            LogAttachmentRefreshFailure(logger, attachmentId, "read", ex);
            RemoveCachedAttachment(attachmentId);
            return;
        }

        try
        {
            CacheAttachment(response);
        }
        catch (Exception ex) when (IsProjectionFailure(ex))
        {
            LogAttachmentRefreshFailure(logger, attachmentId, "projection", ex);
            RemoveCachedAttachment(attachmentId);
            return;
        }

        if (response.Attachment?.Status != AttachmentStatus.Active)
        {
            return;
        }

        try
        {
            await HydrateRuntimeCredentialsAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            // Credential hydration is transient runtime state. Never erase a valid,
            // durable attachment merely because that transient hand-off failed.
            LogAttachmentRefreshFailure(logger, attachmentId, "credential_hydration", ex);
        }
    }

    private static readonly Action<ILogger, string, string, string, Exception?> logAttachmentRefreshFailureCore =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Warning,
            new EventId(7310, "ChannelAttachmentRefreshFailed"),
            "Channel attachment refresh failed. AttachmentId={AttachmentId}; Stage={Stage}; FailureType={FailureType}");

    private static void LogAttachmentRefreshFailure(
        ILogger logger,
        string attachmentId,
        string stage,
        Exception exception)
        => logAttachmentRefreshFailureCore(logger, attachmentId, stage, exception.GetType().Name, null);

    private static bool IsProjectionFailure(Exception exception)
        => exception is InvalidOperationException or JsonException or ArgumentException;

    public async Task RefreshUserAsync(string userId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        GetAttachmentsResponse response;
        try
        {
            response = await channelHost.GetAttachmentsAsync(new GetAttachmentsRequest
            {
                Context = BuildContext(userId, "channels.read"),
                OwnerUserId = userId,
                IncludeInactive = true,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        List<string> attachmentIds = [.. response.Attachments.Select(static item => item.AttachmentId)];
        foreach (string attachmentId in attachmentIds)
        {
            await RefreshAttachmentAsync(attachmentId, userId, cancellationToken).ConfigureAwait(false);
        }

        GatewayChannelRegistration[] accountRegistrations;
        lock (sync)
        {
            string[] stale = [.. attachmentsById.Values
                .Where(item => string.Equals(item.OwnerUserId, userId, StringComparison.Ordinal)
                    && !attachmentIds.Contains(item.Registration.Id, StringComparer.Ordinal))
                .Select(item => item.Registration.Id)];

            foreach (string attachmentId in stale)
            {
                attachmentsById.Remove(attachmentId);
            }

            CachedAttachment? active = attachmentsById.Values
                .Where(item => string.Equals(item.OwnerUserId, userId, StringComparison.Ordinal)
                    && item.Registration.IsActive
                    && ChannelAttachmentIds.IsChat(item.Registration.Id))
                .OrderByDescending(item => item.Registration.UpdatedAt, StringComparer.Ordinal)
                .FirstOrDefault();

            if (active is null)
            {
                activeAttachmentByUserId.Remove(userId);
            }
            else
            {
                activeAttachmentByUserId[userId] = active.Registration.Id;
            }

            accountRegistrations = [.. attachmentsById.Values
                .Where(item => string.Equals(item.OwnerUserId, userId, StringComparison.Ordinal)
                    && ChannelAttachmentIds.IsChat(item.Registration.Id))
                .Select(static item => item.Registration)];
        }

        // The attachment response is cached one registration at a time. Rebuild the
        // account-bot bindings after all agent bots are loaded so a separate-bot agent
        // cannot leak into the account bot just because it appeared later in the list.
        foreach (GatewayChannelRegistration registration in accountRegistrations)
        {
            GatewayChannelTargetBinding[] bindings = BuildBindings(registration);
            lock (sync)
            {
                if (attachmentsById.TryGetValue(registration.Id, out CachedAttachment? cached))
                {
                    attachmentsById[registration.Id] = cached with { Bindings = bindings };
                }
            }
        }
    }

    public async Task RefreshUsersAsync(IEnumerable<GatewayUser> users, CancellationToken cancellationToken)
    {
        foreach (GatewayUser user in users)
        {
            await RefreshUserAsync(user.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    public bool TryGetRegistration(string attachmentId, out GatewayChannelRegistration? registration)
    {
        lock (sync)
        {
            if (attachmentsById.TryGetValue(attachmentId, out CachedAttachment? cached))
            {
                registration = cached.Registration;
                return true;
            }
        }

        registration = null;
        return false;
    }

    public bool TryResolveIdentity(string attachmentId, string externalUserId, string chatId, out GatewayChannelIdentityLink? identity)
    {
        lock (sync)
        {
            if (!attachmentsById.TryGetValue(attachmentId, out CachedAttachment? cached))
            {
                identity = null;
                return false;
            }

            bool linkedSlackOwner = string.Equals(cached.Registration.ChannelType, "slack", StringComparison.OrdinalIgnoreCase)
                && string.Equals(cached.Registration.Settings.GetStringNodeValue("slack_owner_user_id"), externalUserId, StringComparison.Ordinal);
            if (linkedSlackOwner
                || string.Equals(cached.ExternalUserId, externalUserId, StringComparison.Ordinal)
                || (!string.IsNullOrWhiteSpace(chatId) && string.Equals(cached.ExternalUserId, chatId, StringComparison.Ordinal)))
            {
                identity = new GatewayChannelIdentityLink(
                    $"{attachmentId}:user:{cached.ExternalUserId}",
                    attachmentId,
                    cached.OwnerUserId,
                    cached.ExternalUserId,
                    cached.Registration.CreatedAt);
                return true;
            }
        }

        identity = null;
        return false;
    }

    public bool TryResolveActiveRegistration(string userId, out string? registrationId, out string? externalUserId)
    {
        lock (sync)
        {
            if (activeAttachmentByUserId.TryGetValue(userId, out string? attachmentId)
                && attachmentsById.TryGetValue(attachmentId, out CachedAttachment? cached)
                && cached.Registration.IsActive)
            {
                registrationId = attachmentId;
                externalUserId = cached.ExternalUserId;
                return true;
            }
        }

        registrationId = null;
        externalUserId = null;
        return false;
    }

    public GatewayChannelRegistration[] GetActiveChatRegistrations(string userId)
    {
        lock (sync)
        {
            return [.. attachmentsById.Values
                .Where(item => item.OwnerUserId == userId && item.Registration.IsActive
                    && ChannelAttachmentIds.IsChat(item.Registration.Id)
                    && !string.IsNullOrWhiteSpace(item.ExternalUserId))
                .Select(static item => item.Registration)];
        }
    }

    public bool TryResolveAgentBot(string userId, string agentId, out string? registrationId, out string? externalUserId)
        => TryResolveActiveCached(ChannelAttachmentIds.Agent("telegram", userId, agentId), out registrationId, out externalUserId);

    public bool TryResolveWorkflowReviewBot(string userId, string? workflowId, out string? registrationId, out string? externalUserId)
    {
        if (!string.IsNullOrWhiteSpace(workflowId)
            && TryResolveActiveCached(ChannelAttachmentIds.Workflow("telegram", userId, workflowId), out registrationId, out externalUserId))
        {
            return true;
        }

        return TryResolveActiveCached(ChannelAttachmentIds.Workflows("telegram", userId), out registrationId, out externalUserId);
    }

    public GatewayChannelTargetBinding[] GetBindings(string attachmentId)
    {
        GatewayChannelRegistration? registration;
        lock (sync)
        {
            registration = attachmentsById.TryGetValue(attachmentId, out CachedAttachment? cached) ? cached.Registration : null;
        }
        return registration is null ? [] : BuildBindings(registration);
    }

    public GatewayChannelTargetBinding? GetDefaultBinding(string attachmentId)
        => GetBindings(attachmentId).FirstOrDefault(static item => item.IsDefault);

    public GatewayUserListItem BuildUserPayload(GatewayUser user)
    {
        CachedAttachment? attachment;
        GatewayChannelTetherModePayload[] tetherModes;
        lock (sync)
        {
            attachment = activeAttachmentByUserId.TryGetValue(user.Id, out string? attachmentId)
                && attachmentsById.TryGetValue(attachmentId, out CachedAttachment? cached)
                ? cached
                : null;
            tetherModes = [.. attachmentsById.Values
                .Where(item => item.Registration.IsActive && string.Equals(item.OwnerUserId, user.Id, StringComparison.Ordinal))
                .Select(static item => new GatewayChannelTetherModePayload(
                    item.Registration.Id,
                    item.Registration.ChannelType,
                    ChannelHitlTetherModeExtensions.Parse(item.Registration.Settings.GetStringNodeValue(hitlTetherModeKey)).ToStorageValue()))];
        }

        GatewayChannelIdentityLinkPayload[] identityLinks = attachment is null || string.IsNullOrWhiteSpace(attachment.ExternalUserId)
            ? []
            : [new GatewayChannelIdentityLinkPayload(
                $"{attachment.Registration.Id}:user:{attachment.ExternalUserId}",
                attachment.Registration.Id,
                attachment.Registration.ChannelType,
                attachment.ExternalUserId,
                attachment.Registration.CreatedAt)];

        GatewayChannelTargetBinding? defaultBinding = attachment?.Bindings.FirstOrDefault(static item => item.IsDefault);
        bool channelBotTokenSet = attachment is not null && attachment.CredentialReferences.ContainsKey(botTokenKey);

        return new GatewayUserListItem(
            user.Id,
            user.Username,
            user.Email,
            user.DisplayName,
            user.Role,
            user.IsActive,
            user.CanImportConnectors,
            user.CanImportSkills,
            attachment?.ExternalUserId,
            channelBotTokenSet,
            defaultBinding?.TargetType,
            defaultBinding?.TargetId,
            attachment is null ? [] : [attachment.Registration.Id],
            tetherModes,
            identityLinks,
            []);
    }

    public async Task<GatewayUserChannelsResponse> BuildUserChannelCatalogAsync(
        GatewayUser user,
        GatewayChannelSetupPayload? setup,
        CancellationToken cancellationToken)
    {
        await RefreshUserAsync(user.Id, cancellationToken).ConfigureAwait(false);
        ListChannelProvidersResponse providersResponse = await channelHost.ListProvidersAsync(
            new ListChannelProvidersRequest { Context = BuildContext(user.Id, "channels.read") },
            cancellationToken).ConfigureAwait(false);

        GatewayChannelProviderPayload[] providers = [.. providersResponse.Providers.Select(provider => new GatewayChannelProviderPayload(
            provider.ProviderKey,
            provider.DisplayName,
            provider.SchemaVersion,
            provider.CapabilityVersion,
            [.. provider.Fields.Select(field => new GatewayChannelProviderFieldPayload(
                field.FieldKey,
                field.Label,
                string.IsNullOrWhiteSpace(field.Description) ? null : field.Description,
                field.FieldType.ToString(),
                field.Required,
                IsSecretField(field),
                string.IsNullOrWhiteSpace(field.DefaultValue) ? null : field.DefaultValue,
                string.IsNullOrWhiteSpace(field.ValidationHint) ? null : field.ValidationHint,
                string.IsNullOrWhiteSpace(field.Group) ? null : field.Group,
                [.. field.EnumValues]))]))];

        Dictionary<string, GatewayChannelProviderPayload> providersByKey = providers.ToDictionary(static item => item.ProviderKey, StringComparer.OrdinalIgnoreCase);
        GatewayUserChannelPayload[] channels;
        lock (sync)
        {
            channels = [.. attachmentsById.Values
                .Where(item => string.Equals(item.OwnerUserId, user.Id, StringComparison.Ordinal))
                .OrderBy(item => ChannelAttachmentIds.IsChat(item.Registration.Id) ? 0 : 1)
                .ThenBy(item => item.Registration.Id, StringComparer.Ordinal)
                .Select(item =>
                {
                    providersByKey.TryGetValue(item.Registration.ChannelType, out GatewayChannelProviderPayload? provider);
                    GatewayChannelIdentityLinkPayload[] identityLinks = string.IsNullOrWhiteSpace(item.ExternalUserId)
                        ? []
                        : [new GatewayChannelIdentityLinkPayload(
                            $"{item.Registration.Id}:user:{item.ExternalUserId}",
                            item.Registration.Id,
                            item.Registration.ChannelType,
                            item.ExternalUserId,
                            item.Registration.CreatedAt)];

                    JsonObject safeSettings = SanitizeSettings(item.Registration.Settings, provider);
                    string[] configuredSecretFieldKeys = provider is null
                        ? []
                        : [.. provider.Fields
                            .Where(static field => field.Secret)
                            .Select(static field => field.FieldKey)
                            .Where(item.CredentialReferences.ContainsKey)];

                    GatewayChannelTargetBinding? defaultBinding = item.Bindings.FirstOrDefault(static binding => binding.IsDefault);
                    return new GatewayUserChannelPayload(
                        item.Registration.ChannelType,
                        provider?.DisplayName ?? item.Registration.ChannelType,
                        item.Registration.Id,
                        item.ExternalUserId,
                        defaultBinding?.TargetType,
                        defaultBinding?.TargetId,
                        ChannelHitlTetherModeExtensions.Parse(item.Registration.Settings.GetStringNodeValue(hitlTetherModeKey)).ToStorageValue(),
                        safeSettings,
                        configuredSecretFieldKeys,
                        identityLinks,
                        item.LifecycleStatus);
                })];
        }

        return new GatewayUserChannelsResponse(BuildUserPayload(user), providers, channels, setup);
    }

    public async Task<ChannelSettingsUpdateResult?> UpdateUserChannelSettingsAsync(GatewayUser user, string providerKey, JsonElement body, CancellationToken cancellationToken)
    {
        await RefreshUserAsync(user.Id, cancellationToken).ConfigureAwait(false);

        ListChannelProvidersResponse providersResponse = await channelHost.ListProvidersAsync(
            new ListChannelProvidersRequest { Context = BuildContext(user.Id, "channels.read") },
            cancellationToken).ConfigureAwait(false);
        ChannelProviderDefinition? provider = providersResponse.Providers.FirstOrDefault(item => string.Equals(item.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase));
        if (provider is null || !TryParseRequestedRole(body, out ChannelAttachmentRole role))
        {
            return null;
        }

        string? subjectId = body.GetNullableStringProperty("attachmentSubject")?.Trim();
        string? registrationId = ChannelAttachmentIds.For(provider.ProviderKey, user.Id, role, subjectId);
        if (registrationId is null || !OwnsRequestedSubject(user, role, subjectId))
        {
            return null;
        }

        if (body.GetBoolProperty("clear", false) && role != ChannelAttachmentRole.Chat)
        {
            return await DeactivateScopedAttachmentAsync(user, registrationId, cancellationToken).ConfigureAwait(false);
        }

        TryGetRegistration(registrationId, out GatewayChannelRegistration? existing);
        JsonObject settings = existing?.Settings.DeepClone() as JsonObject ?? [];
        if (string.Equals(provider.ProviderKey, "slack", StringComparison.OrdinalIgnoreCase))
        {
            settings.Remove(channelDeviceIdKey);
            settings.Remove("workspace_id");
        }
        JsonObject requestedSettings = body.TryGetProperty("settings", out JsonElement settingsElement) && settingsElement.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(settingsElement.GetRawText()) as JsonObject ?? []
            : [];
        if (string.Equals(provider.ProviderKey, "slack", StringComparison.OrdinalIgnoreCase))
        {
            requestedSettings.Remove(channelDeviceIdKey);
            requestedSettings.Remove("workspace_id");
        }
        string accountDeviceId = string.Empty;
        if (role != ChannelAttachmentRole.Chat)
        {
            requestedSettings.Remove(channelDeviceIdKey);
            if (!TryGetAccountDeviceId(provider.ProviderKey, user.Id, out accountDeviceId))
            {
                return null;
            }
        }

        Dictionary<string, string> credentialReferences = existing is null
            ? new(StringComparer.Ordinal)
            : GetCredentialReferences(registrationId);
        foreach (AttachmentSchemaField secretField in provider.Fields.Where(IsSecretField))
        {
            if (requestedSettings[secretField.FieldKey] is JsonValue secretValue
                && secretValue.TryGetValue<string>(out string? plaintext)
                && !string.IsNullOrWhiteSpace(plaintext))
            {
                string credentialKey = $"channel.{registrationId}.{secretField.FieldKey}";
                if (string.Equals(secretField.FieldKey, botTokenKey, StringComparison.Ordinal))
                {
                    await RejectDuplicateBotTokenAsync(user.Id, registrationId, plaintext, cancellationToken).ConfigureAwait(false);
                }

                _ = await credentialResolver.SetUserAsync(user.Id, new Dictionary<string, string> { [credentialKey] = plaintext }, cancellationToken).ConfigureAwait(false);
                credentialReferences[secretField.FieldKey] = credentialKey;
            }
            requestedSettings.Remove(secretField.FieldKey);
            settings.Remove(secretField.FieldKey);
        }
        string? requestedTetherModeValue = body.GetNullableStringProperty("hitlTetherMode")?.Trim();
        string? defaultTargetType = body.GetNullableStringProperty("defaultTargetType")?.Trim();
        string? defaultTargetId = body.GetNullableStringProperty("defaultTargetId")?.Trim();

        if (role == ChannelAttachmentRole.Chat)
        {
            if (!string.Equals(defaultTargetType, agentTargetType, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(defaultTargetId))
            {
                defaultTargetType = settings.GetStringNodeValue(defaultTargetTypeKey)?.Trim();
                defaultTargetId = settings.GetStringNodeValue(defaultTargetIdKey)?.Trim();
            }

            string[] effectiveAgentIds = string.Equals(provider.ProviderKey, "telegram", StringComparison.OrdinalIgnoreCase)
                ? GetAccountBotAgentIds(user)
                : GetEffectiveAgentIds(user);
            if (string.IsNullOrWhiteSpace(defaultTargetId) || !effectiveAgentIds.Contains(defaultTargetId, StringComparer.Ordinal))
            {
                defaultTargetType = effectiveAgentIds.Length == 0 ? null : agentTargetType;
                defaultTargetId = effectiveAgentIds.FirstOrDefault();
            }
        }
        else if (role == ChannelAttachmentRole.Agent)
        {
            defaultTargetType = agentTargetType;
            defaultTargetId = subjectId;
        }
        else
        {
            defaultTargetType = null;
            defaultTargetId = null;
        }

        ChannelHitlTetherMode tetherMode = ChannelHitlTetherModeExtensions.Parse(
            requestedTetherModeValue ?? settings.GetStringNodeValue(hitlTetherModeKey));
        string previousDeviceId = settings.GetStringNodeValue(channelDeviceIdKey) ?? string.Empty;
        foreach ((string key, JsonNode? value) in requestedSettings)
        {
            settings[key] = value?.DeepClone();
        }

        RemoveInvalidOptionalString(settings, channelDeviceIdKey);
        if (role != ChannelAttachmentRole.Chat)
        {
            settings[channelDeviceIdKey] = accountDeviceId;
        }

        if (role != ChannelAttachmentRole.Chat
            && (string.IsNullOrWhiteSpace(settings.GetStringNodeValue(channelDeviceIdKey))
                || !credentialReferences.ContainsKey(botTokenKey)))
        {
            return null;
        }

        settings["owner_user_id"] = user.Id;
        settings["display_name"] = provider.DisplayName + " " + user.Id;
        settings[hitlTetherModeKey] = tetherMode.ToStorageValue();
        if (role is ChannelAttachmentRole.Workflows or ChannelAttachmentRole.Workflow)
        {
            settings.Remove(defaultTargetTypeKey);
            settings.Remove(defaultTargetIdKey);
        }
        else
        {
            settings[defaultTargetTypeKey] = defaultTargetType;
            settings[defaultTargetIdKey] = defaultTargetId;
        }

        UpsertAttachmentRequest request = new()
        {
            Context = BuildContext(user.Id, "channels.write"),
            AttachmentId = registrationId,
            ProviderKey = provider.ProviderKey,
            ExpectedSchemaVersion = provider.SchemaVersion,
        };
        request.Settings.AddRange(ToSettingValues(settings));
        request.CredentialReferences.AddRange(credentialReferences.Select(static item => new AttachmentCredentialReference
        {
            FieldKey = item.Key,
            CredentialKey = item.Value,
        }));
        UpsertAttachmentResponse saved = await channelHost.UpsertAttachmentAsync(request, cancellationToken).ConfigureAwait(false);
        if (saved.ValidationStatus == ValidationStatus.Rejected)
        {
            ValidationIssue? issue = saved.ValidationIssues.FirstOrDefault();
            throw new ChannelSetupRejectedException(
                issue?.Code ?? "channel_setup_rejected",
                issue?.Message ?? $"{provider.DisplayName} settings could not be saved.");
        }
        if (role == ChannelAttachmentRole.Agent)
        {
            await RefreshUserAsync(user.Id, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await RefreshAttachmentAsync(registrationId, user.Id, cancellationToken).ConfigureAwait(false);
        }

        return new ChannelSettingsUpdateResult(
            BuildUserPayload(user),
            !string.Equals(previousDeviceId, settings.GetStringNodeValue(channelDeviceIdKey) ?? string.Empty, StringComparison.Ordinal),
            registrationId,
            settings.GetStringNodeValue(channelDeviceIdKey),
            defaultTargetType,
            defaultTargetId);
    }

    public async Task<ChannelLifecycleTransitionResult> TransitionLifecycleAsync(
        GatewayUser user,
        string providerKey,
        string transition,
        string reason,
        CancellationToken cancellationToken)
    {
        await RefreshUserAsync(user.Id, cancellationToken).ConfigureAwait(false);
        CachedAttachment? attachment;
        lock (sync)
        {
            attachment = attachmentsById.Values.FirstOrDefault(item =>
                string.Equals(item.OwnerUserId, user.Id, StringComparison.Ordinal)
                && string.Equals(item.Registration.ChannelType, providerKey, StringComparison.OrdinalIgnoreCase)
                && ChannelAttachmentIds.IsChat(item.Registration.Id));
        }
        if (attachment is null)
        {
            return new(false, "not_found", "Channel registration was not found.");
        }

        string normalized = transition.Trim().ToUpperInvariant();
        if (normalized is "REMOVE")
        {
            normalized = "DELETE";
        }

        // Soft-delete is permanent retirement; ACTIVE must suspend first (ChannelHost allow-list).
        if (normalized is "DELETE"
            && string.Equals(attachment.LifecycleStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase))
        {
            TransitionAttachmentLifecycleResponse suspend = await channelHost.TransitionAttachmentLifecycleAsync(
                new TransitionAttachmentLifecycleRequest
                {
                    Context = BuildContext(user.Id, "channels.write"),
                    AttachmentId = attachment.Registration.Id,
                    Transition = "SUSPEND",
                    Reason = reason,
                    OperationId = Guid.NewGuid().ToString("N"),
                },
                cancellationToken).ConfigureAwait(false);
            if (!suspend.Accepted)
            {
                await RefreshAttachmentAsync(attachment.Registration.Id, user.Id, cancellationToken).ConfigureAwait(false);
                return new(false, suspend.Status.ToString().ToUpperInvariant(), suspend.Detail);
            }
        }

        IReadOnlyDictionary<string, string> credentialKeys = GetCredentialReferences(attachment.Registration.Id);
        TransitionAttachmentLifecycleResponse response = await channelHost.TransitionAttachmentLifecycleAsync(
            new TransitionAttachmentLifecycleRequest
            {
                Context = BuildContext(user.Id, "channels.write"),
                AttachmentId = attachment.Registration.Id,
                Transition = normalized,
                Reason = reason,
                OperationId = Guid.NewGuid().ToString("N"),
            },
            cancellationToken).ConfigureAwait(false);

        if (response.Accepted && normalized is "DELETE")
        {
            foreach (string credentialKey in credentialKeys.Values.Distinct(StringComparer.Ordinal))
            {
                _ = await credentialResolver.DeleteUserAsync(user.Id, credentialKey, cancellationToken).ConfigureAwait(false);
            }
        }

        await RefreshUserAsync(user.Id, cancellationToken).ConfigureAwait(false);
        return new(response.Accepted, response.Status.ToString().ToUpperInvariant(), response.Detail);
    }

    public async Task SetActiveTargetForUserAsync(string attachmentId, string externalUserId, string targetType, string targetId, CancellationToken cancellationToken)
    {
        if (!TryGetRegistration(attachmentId, out GatewayChannelRegistration? registration) || registration is null)
        {
            throw new InvalidOperationException("Channel registration is unavailable.");
        }
        await RefreshAttachmentAsync(attachmentId, registration.OwnerUserId, cancellationToken).ConfigureAwait(false);
        if (!TryGetRegistration(attachmentId, out registration) || registration is null)
        {
            return;
        }

        if (!GetBindings(attachmentId).Any(binding => string.Equals(binding.TargetType, targetType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(binding.TargetId, targetId, StringComparison.Ordinal))) throw new InvalidOperationException("Selected channel target is no longer available.");

        JsonObject settings = registration.Settings.DeepClone() as JsonObject ?? [];
        JsonObject activeByUser = settings["active_target_by_external_user"] as JsonObject ?? [];
        activeByUser[externalUserId] = $"{targetType}:{targetId}";
        settings["active_target_by_external_user"] = activeByUser;

        UpsertAttachmentRequest request = new()
        {
            Context = BuildContext(registration.OwnerUserId, "channels.write"),
            AttachmentId = attachmentId,
            ProviderKey = registration.ChannelType,
            ExpectedSchemaVersion = registration.Settings.GetStringNodeValue("schema_version") ?? registration.ChannelType + "/v1",
        };
        request.Settings.AddRange(ToSettingValues(settings));
        request.CredentialReferences.AddRange(GetCredentialReferences(attachmentId).Select(static item => new AttachmentCredentialReference
        {
            FieldKey = item.Key,
            CredentialKey = item.Value,
        }));
        UpsertAttachmentResponse response = await channelHost.UpsertAttachmentAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.ValidationStatus != ValidationStatus.Accepted) throw new InvalidOperationException("The target selection could not be saved.");
        await RefreshAttachmentAsync(attachmentId, registration.OwnerUserId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> BindExternalIdentityAsync(
        string attachmentId,
        string ownerUserId,
        string externalUserId,
        string conversationId,
        CancellationToken cancellationToken)
    {
        await RefreshAttachmentAsync(attachmentId, ownerUserId, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(externalUserId, conversationId, StringComparison.Ordinal)
            || !TryGetRegistration(attachmentId, out GatewayChannelRegistration? registration)
            || registration is null
            || !registration.IsActive
            || !string.Equals(registration.OwnerUserId, ownerUserId, StringComparison.Ordinal))
        {
            return false;
        }

        string? existingIdentity = registration.Settings.GetStringNodeValue(channelDeviceIdKey);
        if (!string.IsNullOrWhiteSpace(existingIdentity)
            && !string.Equals(existingIdentity, externalUserId, StringComparison.Ordinal))
        {
            return false;
        }

        JsonObject settings = registration.Settings.DeepClone() as JsonObject ?? [];
        settings[channelDeviceIdKey] = externalUserId;
        UpsertAttachmentRequest request = new()
        {
            Context = BuildContext(ownerUserId, "channels.write"),
            AttachmentId = attachmentId,
            ProviderKey = registration.ChannelType,
            ExpectedSchemaVersion = registration.Settings.GetStringNodeValue("schema_version") ?? registration.ChannelType + "/v1",
        };
        request.Settings.AddRange(ToSettingValues(settings));
        request.CredentialReferences.AddRange(GetCredentialReferences(attachmentId).Select(static item => new AttachmentCredentialReference
        {
            FieldKey = item.Key,
            CredentialKey = item.Value,
        }));
        _ = await channelHost.UpsertAttachmentAsync(request, cancellationToken).ConfigureAwait(false);
        await RefreshAttachmentAsync(attachmentId, ownerUserId, cancellationToken).ConfigureAwait(false);
        return TryResolveIdentity(attachmentId, externalUserId, conversationId, out _);
    }

    public async Task<string?> StartSlackPairingAsync(string ownerUserId, CancellationToken cancellationToken)
    {
        await RefreshUserAsync(ownerUserId, cancellationToken).ConfigureAwait(false);
        string? registrationId = ChannelAttachmentIds.Chat("slack", ownerUserId);
        if (registrationId is null || !TryGetRegistration(registrationId, out GatewayChannelRegistration? registration)
            || registration is null || !registration.IsActive)
        {
            return null;
        }

        string code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        JsonObject settings = registration.Settings.DeepClone() as JsonObject ?? [];
        settings["slack_pairing_code_hash"] = HashSlackPairingCode(registrationId, ownerUserId, code);
        settings["slack_pairing_expires_at"] = DateTimeOffset.UtcNow.AddMinutes(5).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        settings["slack_pairing_failed_attempts"] = 0;
        settings.Remove("slack_pairing_failure_reason");
        settings.Remove("slack_owner_user_id");
        settings.Remove("slack_dm_channel_id");
        settings.Remove(channelDeviceIdKey);
        await UpsertSettingsAsync(registration, ownerUserId, settings, cancellationToken).ConfigureAwait(false);
        await RefreshAttachmentAsync(registrationId, ownerUserId, cancellationToken).ConfigureAwait(false);
        return code;
    }

    public async Task<bool> CompleteSlackPairingAsync(
        string attachmentId,
        string ownerUserId,
        string externalUserId,
        string conversationId,
        string code,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(externalUserId)
            || code.Length != 6
            || !code.All(char.IsAsciiDigit)
            || !conversationId.StartsWith('D'))
        {
            return false;
        }

        if (!TryGetRegistration(attachmentId, out GatewayChannelRegistration? registration)
            || registration is null || !registration.IsActive
            || !string.Equals(registration.ChannelType, "slack", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(registration.OwnerUserId, ownerUserId, StringComparison.Ordinal))
        {
            return false;
        }

        string? savedHash = registration.Settings.GetStringNodeValue("slack_pairing_code_hash");
        string? expiresAt = registration.Settings.GetStringNodeValue("slack_pairing_expires_at");
        string? pairedOwner = registration.Settings.GetStringNodeValue("slack_owner_user_id");
        if (!string.IsNullOrWhiteSpace(pairedOwner)) return false;
        if (string.IsNullOrWhiteSpace(savedHash)
            || !DateTimeOffset.TryParse(expiresAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out DateTimeOffset expiry)
            || expiry <= DateTimeOffset.UtcNow)
        {
            return false;
        }

        bool matches = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(savedHash),
            Encoding.UTF8.GetBytes(HashSlackPairingCode(attachmentId, ownerUserId, code)));
        if (!matches)
        {
            JsonObject failedSettings = registration.Settings.DeepClone() as JsonObject ?? [];
            int failures = int.TryParse(failedSettings["slack_pairing_failed_attempts"]?.ToString(), out int count) ? count : 0;
            failures++;
            failedSettings["slack_pairing_failed_attempts"] = failures;
            if (failures >= 5)
            {
                failedSettings.Remove("slack_pairing_code_hash");
                failedSettings["slack_pairing_failure_reason"] = "Too many incorrect codes. Generate a new pairing code to try again.";
            }
            await UpsertSettingsAsync(registration, ownerUserId, failedSettings, cancellationToken).ConfigureAwait(false);
            await RefreshAttachmentAsync(attachmentId, ownerUserId, cancellationToken).ConfigureAwait(false);
            return false;
        }

        JsonObject settings = registration.Settings.DeepClone() as JsonObject ?? [];
        settings["slack_owner_user_id"] = externalUserId;
        settings["slack_dm_channel_id"] = conversationId;
        settings.Remove(channelDeviceIdKey);
        settings.Remove("slack_pairing_code_hash");
        settings.Remove("slack_pairing_expires_at");
        settings.Remove("slack_pairing_failed_attempts");
        await UpsertSettingsAsync(registration, ownerUserId, settings, cancellationToken).ConfigureAwait(false);
        await RefreshAttachmentAsync(attachmentId, ownerUserId, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public Task<bool> IsSlackPairedAsync(string ownerUserId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? registrationId = ChannelAttachmentIds.Chat("slack", ownerUserId);
        bool isPaired = registrationId is not null
            && TryGetRegistration(registrationId, out GatewayChannelRegistration? registration)
            && registration is not null
            && !string.IsNullOrWhiteSpace(registration.Settings.GetStringNodeValue("slack_owner_user_id"))
            && registration.Settings.GetStringNodeValue("slack_dm_channel_id")?.StartsWith('D') == true;
        return Task.FromResult(isPaired);
    }

    public Task<string?> GetSlackPairingFailureReasonAsync(string ownerUserId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? registrationId = ChannelAttachmentIds.Chat("slack", ownerUserId);
        string? reason = registrationId is not null
            && TryGetRegistration(registrationId, out GatewayChannelRegistration? registration)
            && registration is not null
            ? registration.Settings.GetStringNodeValue("slack_pairing_failure_reason")
            : null;
        return Task.FromResult(reason);
    }

    private async Task UpsertSettingsAsync(GatewayChannelRegistration registration, string ownerUserId, JsonObject settings, CancellationToken cancellationToken)
    {
        UpsertAttachmentRequest request = new()
        {
            Context = BuildContext(ownerUserId, "channels.write"),
            AttachmentId = registration.Id,
            ProviderKey = registration.ChannelType,
            ExpectedSchemaVersion = registration.Settings.GetStringNodeValue("schema_version") ?? registration.ChannelType + "/v1",
        };
        request.Settings.AddRange(ToSettingValues(settings));
        request.CredentialReferences.AddRange(GetCredentialReferences(registration.Id).Select(static item => new AttachmentCredentialReference
        {
            FieldKey = item.Key,
            CredentialKey = item.Value,
        }));
        UpsertAttachmentResponse result = await channelHost.UpsertAttachmentAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.ValidationStatus != ValidationStatus.Accepted)
        {
            throw new InvalidOperationException("Slack setup could not be saved.");
        }
    }

    private static string HashSlackPairingCode(string registrationId, string ownerUserId, string code)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{registrationId}\0{ownerUserId}\0{code}")));

    public ChannelHitlTetherMode GetChannelHitlTetherMode(string userId, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || conversationMapStore is null) return ChannelHitlTetherMode.Mirror;
        lock (sync)
        {
            foreach (CachedAttachment cached in attachmentsById.Values)
            {
                if (!cached.Registration.IsActive || !string.Equals(cached.OwnerUserId, userId, StringComparison.Ordinal)) continue;
                ChannelConversationMapEntry? map = conversationMapStore.TryResolveBySession(sessionId, cached.Registration.ChannelType);
                if (map is not null && string.Equals(map.RegistrationId, cached.Registration.Id, StringComparison.Ordinal))
                    return ChannelHitlTetherModeExtensions.Parse(cached.Registration.Settings.GetStringNodeValue(hitlTetherModeKey));
            }
            return ChannelHitlTetherMode.Mirror;
        }
    }

    private bool TryResolveActiveCached(string attachmentId, out string? registrationId, out string? externalUserId)
    {
        lock (sync)
        {
            if (attachmentsById.TryGetValue(attachmentId, out CachedAttachment? cached)
                && cached.Registration.IsActive
                && !string.IsNullOrWhiteSpace(cached.ExternalUserId))
            {
                registrationId = attachmentId;
                externalUserId = cached.ExternalUserId;
                return true;
            }
        }

        registrationId = null;
        externalUserId = null;
        return false;
    }

    private bool OwnsRequestedSubject(GatewayUser user, ChannelAttachmentRole role, string? subjectId)
    {
        if (role == ChannelAttachmentRole.Agent)
        {
            return state.Agents.TryGetValue(subjectId ?? string.Empty, out GatewayAgent? agent)
                && GatewayOwnershipAccess.CanAccessAgent(user, agent);
        }

        if (role != ChannelAttachmentRole.Workflow)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(subjectId))
        {
            return false;
        }

        if (workflowStore is null)
        {
            return true;
        }

        GatewayWorkflowDefinition? definition = workflowStore.GetDefinition(subjectId);
        return definition is not null
            && (string.Equals(definition.OwnerUserId, user.Id, StringComparison.Ordinal)
                || GatewayOwnershipAccess.IsAdmin(user));
    }

    private async Task RejectDuplicateBotTokenAsync(
        string userId,
        string registrationId,
        string plaintext,
        CancellationToken cancellationToken)
    {
        string ownKey = $"channel.{registrationId}.{botTokenKey}";
        IReadOnlyList<string> keys = await credentialResolver.ListUserKeysAsync(userId, cancellationToken).ConfigureAwait(false);
        string[] otherKeys = [.. keys.Where(key =>
            key.StartsWith("channel.", StringComparison.Ordinal)
            && key.EndsWith("." + botTokenKey, StringComparison.Ordinal)
            && !string.Equals(key, ownKey, StringComparison.Ordinal))];
        if (otherKeys.Length == 0)
        {
            return;
        }

        IReadOnlyDictionary<string, string> resolved = await credentialResolver.GetResolvedAsync(userId, otherKeys, cancellationToken).ConfigureAwait(false);
        foreach (string existing in resolved.Values)
        {
            if (SecretsEqual(existing, plaintext))
            {
                throw new DuplicateChannelBotTokenException();
            }
        }
    }

    private async Task<ChannelSettingsUpdateResult> DeactivateScopedAttachmentAsync(
        GatewayUser user,
        string registrationId,
        CancellationToken cancellationToken)
    {
        if (!TryGetRegistration(registrationId, out GatewayChannelRegistration? registration) || registration is null)
        {
            return new ChannelSettingsUpdateResult(BuildUserPayload(user), false, null, null, null, null);
        }

        IReadOnlyDictionary<string, string> credentialKeys = GetCredentialReferences(registrationId);
        if (string.Equals(GetLifecycleStatus(registrationId), "ACTIVE", StringComparison.OrdinalIgnoreCase))
        {
            TransitionAttachmentLifecycleResponse suspend = await channelHost.TransitionAttachmentLifecycleAsync(
                new TransitionAttachmentLifecycleRequest
                {
                    Context = BuildContext(user.Id, "channels.write"),
                    AttachmentId = registrationId,
                    Transition = "SUSPEND",
                    Reason = "scoped_bot_removed",
                    OperationId = Guid.NewGuid().ToString("N"),
                },
                cancellationToken).ConfigureAwait(false);
            if (!suspend.Accepted)
            {
                await RefreshAttachmentAsync(registrationId, user.Id, cancellationToken).ConfigureAwait(false);
                return new ChannelSettingsUpdateResult(BuildUserPayload(user), false, registrationId, null, null, null);
            }
        }

        _ = await channelHost.TransitionAttachmentLifecycleAsync(
            new TransitionAttachmentLifecycleRequest
            {
                Context = BuildContext(user.Id, "channels.write"),
                AttachmentId = registrationId,
                Transition = "DELETE",
                Reason = "scoped_bot_removed",
                OperationId = Guid.NewGuid().ToString("N"),
            },
            cancellationToken).ConfigureAwait(false);
        foreach (string credentialKey in credentialKeys.Values.Distinct(StringComparer.Ordinal))
        {
            _ = await credentialResolver.DeleteUserAsync(user.Id, credentialKey, cancellationToken).ConfigureAwait(false);
        }

        await RefreshUserAsync(user.Id, cancellationToken).ConfigureAwait(false);
        return new ChannelSettingsUpdateResult(BuildUserPayload(user), false, null, null, null, null);
    }

    private string GetLifecycleStatus(string registrationId)
    {
        lock (sync)
        {
            return attachmentsById.TryGetValue(registrationId, out CachedAttachment? cached)
                ? cached.LifecycleStatus
                : string.Empty;
        }
    }

    private static bool TryParseRequestedRole(JsonElement body, out ChannelAttachmentRole role)
    {
        string? name = body.GetNullableStringProperty("attachmentRole");
        if (name is null || name is "default" or "chat")
        {
            role = ChannelAttachmentRole.Chat;
            return true;
        }

        switch (name)
        {
            case "agent":
                role = ChannelAttachmentRole.Agent;
                return true;
            case "workflows":
                role = ChannelAttachmentRole.Workflows;
                return true;
            case "workflow":
                role = ChannelAttachmentRole.Workflow;
                return true;
            default:
                role = default;
                return false;
        }
    }

    private bool TryGetAccountDeviceId(string providerKey, string userId, out string deviceId)
    {
        deviceId = string.Empty;
        string chatRegistrationId = ChannelAttachmentIds.Chat(providerKey, userId);
        if (!TryGetRegistration(chatRegistrationId, out GatewayChannelRegistration? registration) || registration is null)
        {
            return false;
        }

        string? stored = registration.Settings.GetStringNodeValue(channelDeviceIdKey)?.Trim();
        if (string.IsNullOrWhiteSpace(stored))
        {
            return false;
        }

        deviceId = stored;
        return true;
    }

    private static bool SecretsEqual(string left, string right)
    {
        byte[] leftBytes = Encoding.UTF8.GetBytes(left);
        byte[] rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private void CacheAttachment(GetAttachmentResponse response)
    {
        JsonObject settings = ToSettingsObject(response.Settings);
        string ownerUserId = settings.GetStringNodeValue("owner_user_id") ?? string.Empty;
        string updatedAt = response.Attachment.LastProvisionedAtUtc;
        GatewayChannelRegistration registration = new(
            response.Attachment.AttachmentId,
            response.Attachment.ProviderKey,
            ownerUserId,
            $"user:{ownerUserId}",
            response.Attachment.Status == AttachmentStatus.Active,
            settings,
            string.IsNullOrWhiteSpace(updatedAt) ? DateTimeOffset.UtcNow.ToString("O") : updatedAt,
            string.IsNullOrWhiteSpace(updatedAt) ? DateTimeOffset.UtcNow.ToString("O") : updatedAt);

        GatewayChannelTargetBinding[] bindings = BuildBindings(registration);
        string externalUserId = string.Equals(registration.ChannelType, "slack", StringComparison.OrdinalIgnoreCase)
            ? settings.GetStringNodeValue("slack_dm_channel_id") ?? string.Empty
            : settings.GetStringNodeValue(channelDeviceIdKey) ?? string.Empty;
        CachedAttachment cached = new(
        registration,
        ownerUserId,
        externalUserId,
        bindings,
        response.CredentialReferences.ToDictionary(static item => item.FieldKey, static item => item.CredentialKey, StringComparer.Ordinal),
            ToLifecycleStatus(response.Attachment.Status));

        lock (sync)
        {
            attachmentsById[registration.Id] = cached;
            if (!string.IsNullOrWhiteSpace(ownerUserId) && ChannelAttachmentIds.IsChat(registration.Id))
            {
                if (registration.IsActive)
                {
                    activeAttachmentByUserId[ownerUserId] = registration.Id;
                }
                else if (activeAttachmentByUserId.TryGetValue(ownerUserId, out string? current)
                    && string.Equals(current, registration.Id, StringComparison.Ordinal))
                {
                    activeAttachmentByUserId.Remove(ownerUserId);
                }
            }
        }
    }

    private static string ToLifecycleStatus(AttachmentStatus status) => status switch
    {
        AttachmentStatus.Active => "ACTIVE",
        AttachmentStatus.Inactive => "INACTIVE",
        AttachmentStatus.Provisioning => "PROVISIONING",
        AttachmentStatus.Error => "ERROR",
        AttachmentStatus.Suspended => "SUSPENDED",
        AttachmentStatus.RecoveryRequired => "RECOVERY_REQUIRED",
        AttachmentStatus.Deleted => "DELETED",
        _ => "UNSPECIFIED",
    };

    private GatewayChannelTargetBinding[] BuildBindings(GatewayChannelRegistration registration)
    {
        GatewayUser? user = state.Users.Values.FirstOrDefault(item => string.Equals(item.Id, registration.OwnerUserId, StringComparison.Ordinal));
        if (user is null)
        {
            return [];
        }

        if (ChannelAttachmentIds.TryParse(registration.Id, out ChannelAttachmentIdentity identity))
        {
            if (identity.Role is ChannelAttachmentRole.Workflows or ChannelAttachmentRole.Workflow)
            {
                return [];
            }

            if (identity.Role == ChannelAttachmentRole.Agent)
            {
                if (string.IsNullOrWhiteSpace(identity.SubjectId)
                    || !GetEffectiveAgentIds(user).Contains(identity.SubjectId, StringComparer.Ordinal))
                {
                    return [];
                }

                return
                [
                    new GatewayChannelTargetBinding(
                        $"{registration.Id}:agent:{identity.SubjectId}",
                        registration.Id,
                        agentTargetType,
                        identity.SubjectId,
                        true,
                        registration.UpdatedAt),
                ];
            }
        }

        string[] agentIds = identity.Role == ChannelAttachmentRole.Chat
            && string.Equals(registration.ChannelType, "telegram", StringComparison.OrdinalIgnoreCase)
            ? GetAccountBotAgentIds(user)
            : GetEffectiveAgentIds(user);

        string? defaultTargetType = registration.Settings.GetStringNodeValue(defaultTargetTypeKey)?.Trim();
        string? defaultTargetId = registration.Settings.GetStringNodeValue(defaultTargetIdKey)?.Trim();
        bool defaultAssigned = false;
        List<GatewayChannelTargetBinding> bindings = [];

        foreach (string agentId in agentIds)
        {
            bool isDefault = !defaultAssigned
                && string.Equals(defaultTargetType, agentTargetType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(defaultTargetId, agentId, StringComparison.Ordinal);

            bindings.Add(new GatewayChannelTargetBinding(
                $"{registration.Id}:agent:{agentId}",
                registration.Id,
                agentTargetType,
                agentId,
                isDefault,
                registration.UpdatedAt));
            defaultAssigned = defaultAssigned || isDefault;
        }

        if (!defaultAssigned && bindings.Count > 0)
        {
            bindings[0] = bindings[0] with { IsDefault = true };
        }

        return [.. bindings];
    }

    private string[] GetEffectiveAgentIds(GatewayUser user)
        => [.. state.Agents.Values
            .Where(agent => GatewayOwnershipAccess.CanAccessAgent(user, agent))
            .Select(agent => agent.Id)
            .OrderBy(static item => item, StringComparer.Ordinal)];

    private string[] GetAccountBotAgentIds(GatewayUser user)
        => [.. GetEffectiveAgentIds(user)
            .Where(agentId => !TryResolveActiveCached(ChannelAttachmentIds.Agent("telegram", user.Id, agentId), out _, out _))];

    private void RemoveCachedAttachment(string attachmentId)
    {
        lock (sync)
        {
            if (attachmentsById.Remove(attachmentId, out CachedAttachment? removed)
                && !string.IsNullOrWhiteSpace(removed.OwnerUserId)
                && activeAttachmentByUserId.TryGetValue(removed.OwnerUserId, out string? current)
                && string.Equals(current, attachmentId, StringComparison.Ordinal))
            {
                activeAttachmentByUserId.Remove(removed.OwnerUserId);
            }
        }
    }

    private static JsonObject ToSettingsObject(IEnumerable<AttachmentSettingValue> settings)
    {
        JsonObject result = [];
        foreach (AttachmentSettingValue setting in settings)
        {
            if (!string.IsNullOrWhiteSpace(setting.JsonValue))
            {
                result[setting.FieldKey] = JsonNode.Parse(setting.JsonValue);
            }
            else if (setting.MultiValues.Count > 0)
            {
                result[setting.FieldKey] = new JsonArray([.. setting.MultiValues.Select(static item => (JsonNode?)item)]);
            }
            else if (!string.IsNullOrWhiteSpace(setting.StringValue))
            {
                result[setting.FieldKey] = setting.StringValue;
            }
            else if (Math.Abs(setting.NumberValue) > double.Epsilon)
            {
                result[setting.FieldKey] = setting.NumberValue;
            }
            else
            {
                result[setting.FieldKey] = setting.BoolValue;
            }
        }

        return result;
    }

    private static JsonObject SanitizeSettings(JsonObject source, GatewayChannelProviderPayload? provider)
    {
        JsonObject copy = JsonNode.Parse(source.ToJsonString()) as JsonObject ?? [];
        if (provider is null)
        {
            return copy;
        }

        foreach (GatewayChannelProviderFieldPayload field in provider.Fields.Where(static field => field.Secret))
        {
            copy.Remove(field.FieldKey);
        }

        return copy;
    }

    private static bool IsSecretField(AttachmentSchemaField field)
        => field.Secret || field.FieldType == AttachmentSchemaFieldType.SecretText;

    private static void RemoveInvalidOptionalString(JsonObject settings, string fieldKey)
    {
        if (settings[fieldKey] is not JsonValue value
            || !value.TryGetValue<string>(out string? text)
            || string.IsNullOrWhiteSpace(text))
        {
            settings.Remove(fieldKey);
        }
    }

    private static IEnumerable<AttachmentSettingValue> ToSettingValues(JsonObject settings)
    {
        foreach ((string key, JsonNode? valueNode) in settings)
        {
            if (valueNode is null)
            {
                continue;
            }

            AttachmentSettingValue value = new() { FieldKey = key };
            switch (valueNode)
            {
                case JsonArray array:
                    foreach (JsonNode? item in array)
                    {
                        value.MultiValues.Add(item?.ToString() ?? string.Empty);
                    }
                    break;
                case JsonValue jsonValue when jsonValue.TryGetValue<string>(out string? stringValue):
                    if (string.IsNullOrWhiteSpace(stringValue))
                    {
                        continue;
                    }
                    value.StringValue = stringValue;
                    break;
                case JsonValue jsonValue when jsonValue.TryGetValue<bool>(out bool boolValue):
                    value.BoolValue = boolValue;
                    break;
                case JsonValue jsonValue when jsonValue.TryGetValue<int>(out int intValue):
                    value.NumberValue = intValue;
                    break;
                case JsonValue jsonValue when jsonValue.TryGetValue<long>(out long longValue):
                    value.NumberValue = longValue;
                    break;
                case JsonValue jsonValue when jsonValue.TryGetValue<double>(out double doubleValue):
                    value.NumberValue = doubleValue;
                    break;
                default:
                    value.JsonValue = valueNode.ToJsonString();
                    break;
            }

            yield return value;
        }
    }

    private static ScopedRequestContext BuildContext(string actorUserId, params string[] capabilities)
    {
        ScopedRequestContext context = new()
        {
            RequestId = Guid.NewGuid().ToString("N"),
            ActorUserId = actorUserId,
            SubmittedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            GatewaySessionId = "gateway-channel-attachments",
        };
        context.ActorCapabilities.Add(capabilities);
        return context;
    }

    private Dictionary<string, string> GetCredentialReferences(string attachmentId)
    {
        lock (sync)
        {
            return attachmentsById.TryGetValue(attachmentId, out CachedAttachment? cached)
                ? new Dictionary<string, string>(cached.CredentialReferences, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private async Task HydrateRuntimeCredentialsAsync(GetAttachmentResponse response, CancellationToken cancellationToken)
    {
        if (response.CredentialReferences.Count == 0) return;
        string ownerUserId = ToSettingsObject(response.Settings).GetStringNodeValue("owner_user_id") ?? string.Empty;
        string[] keys = [.. response.CredentialReferences.Select(static item => item.CredentialKey).Distinct(StringComparer.Ordinal)];
        IReadOnlyDictionary<string, string> resolved = await credentialResolver.GetResolvedAsync(ownerUserId, keys, cancellationToken).ConfigureAwait(false);
        SetRuntimeCredentialsRequest request = new()
        {
            AttachmentId = response.Attachment.AttachmentId,
            Context = BuildContext(ownerUserId, "channels.runtime"),
        };
        foreach (AttachmentCredentialReference reference in response.CredentialReferences)
        {
            if (resolved.TryGetValue(reference.CredentialKey, out string? value))
                request.Credentials.Add(new RuntimeCredentialValue { FieldKey = reference.FieldKey, SecretValue = value });
        }
        _ = await channelHost.SetRuntimeCredentialsAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static string[] NormalizeStringArray(string[]? values)
        => values is null ? [] : [.. values.Where(static value => !string.IsNullOrWhiteSpace(value))];

    private static Dictionary<string, string[]> NormalizePermissions(Dictionary<string, string[]>? source)
    {
        if (source is null)
        {
            return new Dictionary<string, string[]>(StringComparer.Ordinal);
        }

        Dictionary<string, string[]> normalized = new(StringComparer.Ordinal);
        foreach ((string key, string[] value) in source)
        {
            normalized[key] = NormalizeStringArray(value);
        }

        return normalized;
    }

    private sealed record CachedAttachment(
        GatewayChannelRegistration Registration,
        string OwnerUserId,
        string ExternalUserId,
        GatewayChannelTargetBinding[] Bindings,
        IReadOnlyDictionary<string, string> CredentialReferences,
        string LifecycleStatus);
}

internal sealed record ChannelLifecycleTransitionResult(bool Accepted, string Status, string Detail);
