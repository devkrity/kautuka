using Grpc.Core;

using Kautuka.Protos;

namespace Kautuka.ChannelHost.Grpc;

/// <summary>
/// gRPC server implementation of <see cref="ChannelHostService.ChannelHostServiceBase"/>.
/// Gateway calls into this service to stream ingress events and manage attachment state.
/// </summary>
internal sealed partial class ChannelHostGrpcService(
    ChannelAttachmentStore attachmentStore,
    ChannelProviderRegistry providerRegistry,
    ChannelRuntimeCredentialStore runtimeCredentials,
    ChannelIngressStore ingressStore,
    ChannelRequestAuthorizer authorizer,
    ChannelResourceGovernor resourceGovernor,
    ChannelOperationalHealthReader operationalHealth,
    ChannelOutboundAssetStore outboundAssets,
    ChannelSessionFactsOutboundExecutor sessionFactsExecutor,
    ILogger<ChannelHostGrpcService> logger) : ChannelHostService.ChannelHostServiceBase
{
    public override async Task<ChannelHostPingResponse> Ping(
        ChannelHostPingRequest request,
        ServerCallContext context)
    {
        ChannelAuthorizationResult authorization = authorizer.Authorize(
            request, request.Context, nameof(Ping), ChannelRequestAuthorizer.Read, context);
        ChannelHostPingResponse response = new() { OperationalHealth = operationalHealth.Read(authorization) };
        foreach (IChannelAdapter adapter in providerRegistry.GetAll())
        {
            response.Providers.Add(await adapter.GetHealthAsync(context.CancellationToken).ConfigureAwait(false));
        }

        return response;
    }

    public override Task<ListChannelProvidersResponse> ListProviders(
        ListChannelProvidersRequest request,
        ServerCallContext context)
    {
        authorizer.Authorize(request, request.Context, nameof(ListProviders), ChannelRequestAuthorizer.Read, context);
        return Task.FromResult(providerRegistry.BuildProviderCatalog());
    }

    public override async Task StreamIngress(
        ChannelIngressSubscribeRequest request,
        IServerStreamWriter<ChannelInboundEventMessage> responseStream,
        ServerCallContext context)
    {
        using IDisposable scope = BeginGrpcScope(context, "StreamIngress",
            ("SubscriberId", request.SubscriberId));
        TagCurrentActivity(("rpc.method", "StreamIngress"), ("channel.subscriber_id", request.SubscriberId));
        ChannelAuthorizationResult authorization = authorizer.Authorize(request, request.Context, nameof(StreamIngress), ChannelRequestAuthorizer.Ingress, context);
        authorizer.RequireResourceScope(authorization, request.Context, nameof(StreamIngress),
            string.Equals(request.SubscriberId, ChannelIngressStore.GatewaySubscriberId, StringComparison.Ordinal),
            StatusCode.PermissionDenied, "canonical_gateway_subscriber_required");
        using IDisposable streamLease = resourceGovernor.AcquireExclusiveIngressStream(request.SubscriberId);

        LogIngressStreamStarted(logger, request.SubscriberId);
        try
        {
            await foreach (ChannelInboundEventMessage msg in ingressStore.StreamAsync(request.SubscriberId, context.CancellationToken).ConfigureAwait(false))
            {
                await responseStream.WriteAsync(msg, context.CancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // Subscriber disconnected or host is shutting down; treat as a clean stream end.
        }
    }

    public override Task<AcknowledgeIngressResponse> AcknowledgeIngress(AcknowledgeIngressRequest request, ServerCallContext context)
    {
        ChannelAuthorizationResult authorization = authorizer.Authorize(request, request.Context, nameof(AcknowledgeIngress), ChannelRequestAuthorizer.Ingress, context);
        authorizer.RequireResourceScope(authorization, request.Context, nameof(AcknowledgeIngress),
            string.Equals(request.SubscriberId, ChannelIngressStore.GatewaySubscriberId, StringComparison.Ordinal),
            StatusCode.PermissionDenied, "canonical_gateway_subscriber_required");
        if (string.IsNullOrWhiteSpace(request.EventId) || string.IsNullOrWhiteSpace(request.DeliveryLeaseId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "ingress_ack_binding_required"));
        authorization = authorizer.BeginOperation(nameof(AcknowledgeIngress), authorization);
        if (authorization.IsIdempotentRetry)
        {
            if (authorizer.TryGetCachedResponse(nameof(AcknowledgeIngress), authorization, out byte[] cached))
                return Task.FromResult(AcknowledgeIngressResponse.Parser.ParseFrom(cached));
            throw new RpcException(new Status(StatusCode.Aborted, "idempotent_operation_in_progress"));
        }
        var response = new AcknowledgeIngressResponse
        {
            Acknowledged = ingressStore.Acknowledge(request.SubscriberId, request.EventId, request.DeliveryLeaseId),
        };
        authorizer.StoreResponse(nameof(AcknowledgeIngress), authorization, response);
        return Task.FromResult(response);
    }

    public override Task<SetRuntimeCredentialsResponse> SetRuntimeCredentials(SetRuntimeCredentialsRequest request, ServerCallContext context)
    {
        ChannelAuthorizationResult authorization = authorizer.Authorize(request, request.Context, nameof(SetRuntimeCredentials), ChannelRequestAuthorizer.Runtime, context);
        RequireAttachmentScope(authorization, request.Context, nameof(SetRuntimeCredentials), request.AttachmentId, requireActive: true);
        authorization = authorizer.BeginOperation(nameof(SetRuntimeCredentials), authorization);
        if (authorization.IsIdempotentRetry)
        {
            if (authorizer.TryGetCachedResponse(nameof(SetRuntimeCredentials), authorization, out byte[] cached))
                return Task.FromResult(SetRuntimeCredentialsResponse.Parser.ParseFrom(cached));
            throw new RpcException(new Status(StatusCode.Aborted, "idempotent_operation_in_progress"));
        }
        runtimeCredentials.Replace(request.AttachmentId, request.Credentials);
        var response = new SetRuntimeCredentialsResponse { Accepted = true };
        authorizer.StoreResponse(nameof(SetRuntimeCredentials), authorization, response);
        return Task.FromResult(response);
    }

    public override async Task ReadInboundMedia(
        ReadInboundMediaRequest request,
        IServerStreamWriter<ReadInboundMediaChunk> responseStream,
        ServerCallContext context)
    {
        ChannelAuthorizationResult authorization = authorizer.Authorize(request, request.Context, nameof(ReadInboundMedia), ChannelRequestAuthorizer.Runtime, context);
        RequireAttachmentScope(authorization, request.Context, nameof(ReadInboundMedia), request.RegistrationId, requireActive: true);
        authorizer.RequireResourceScope(authorization, request.Context, nameof(ReadInboundMedia),
            ingressStore.ContainsAdmittedMedia(request.RegistrationId, request.EventId, request.MediaId),
            StatusCode.PermissionDenied, "inbound_media_descriptor_not_admitted");
        using IDisposable operationLease = resourceGovernor.AcquireRegistrationOperation(
            request.RegistrationId, nameof(ReadInboundMedia), limit: 4);
        using IDisposable scope = BeginGrpcScope(context, "ReadInboundMedia",
            ("RegistrationId", request.RegistrationId),
            ("EventId", request.EventId),
            ("MediaId", request.MediaId),
            ("CorrelationId", request.CorrelationId));
        TagCurrentActivity(
            ("rpc.method", "ReadInboundMedia"),
            ("channel.registration_id", request.RegistrationId),
            ("channel.event_id", request.EventId),
            ("channel.media_id", request.MediaId),
            ("channel.correlation_id", request.CorrelationId));

        IChannelAdapter adapter = ResolveAdapterForAttachment(request.RegistrationId);
        await foreach (ReadInboundMediaChunk chunk in adapter.ReadInboundMediaAsync(request.RegistrationId, request, context.CancellationToken).ConfigureAwait(false))
        {
            await responseStream.WriteAsync(chunk, context.CancellationToken).ConfigureAwait(false);
        }
    }

    public override Task<GetAttachmentsResponse> GetAttachments(
        GetAttachmentsRequest request,
        ServerCallContext context)
    {
        ChannelAuthorizationResult authorization = authorizer.Authorize(request, request.Context, nameof(GetAttachments), ChannelRequestAuthorizer.Read, context);
        authorizer.RequireOwner(authorization, request.Context, nameof(GetAttachments), request.OwnerUserId);
        using IDisposable scope = BeginGrpcScope(context, "GetAttachments",
            ("OwnerUserId", request.OwnerUserId),
            ("IncludeInactive", request.IncludeInactive));
        TagCurrentActivity(("rpc.method", "GetAttachments"), ("channel.owner_user_id", request.OwnerUserId));
        return Task.FromResult(attachmentStore.GetAttachments(request.OwnerUserId, request.IncludeInactive));
    }

    public override Task<GetAttachmentResponse> GetAttachment(
        GetAttachmentRequest request,
        ServerCallContext context)
    {
        ChannelAuthorizationResult authorization = authorizer.Authorize(request, request.Context, nameof(GetAttachment), ChannelRequestAuthorizer.Read, context);
        RequireAttachmentScope(authorization, request.Context, nameof(GetAttachment), request.AttachmentId);
        using IDisposable scope = BeginGrpcScope(context, "GetAttachment",
            ("AttachmentId", request.AttachmentId));
        TagCurrentActivity(("rpc.method", "GetAttachment"), ("channel.attachment_id", request.AttachmentId));

        GetAttachmentResponse? attachment = attachmentStore.GetAttachment(request.AttachmentId);
        return attachment is null
            ? ThrowNotFound<GetAttachmentResponse>("attachment_not_found")
            : Task.FromResult(attachment);
    }

    public override Task<GetAttachmentSchemaResponse> GetAttachmentSchema(
        GetAttachmentSchemaRequest request,
        ServerCallContext context)
    {
        ChannelAuthorizationResult authorization = authorizer.Authorize(request, request.Context, nameof(GetAttachmentSchema), ChannelRequestAuthorizer.Read, context);
        RequireAttachmentScope(authorization, request.Context, nameof(GetAttachmentSchema), request.AttachmentId);
        using IDisposable scope = BeginGrpcScope(context, "GetAttachmentSchema",
            ("AttachmentId", request.AttachmentId));
        TagCurrentActivity(("rpc.method", "GetAttachmentSchema"), ("channel.attachment_id", request.AttachmentId));

        AttachmentSummary? attachment = attachmentStore.GetAttachmentSummary(request.AttachmentId);
        if (attachment is null)
        {
            return ThrowNotFound<GetAttachmentSchemaResponse>("attachment_not_found");
        }

        if (!providerRegistry.TryGet(attachment.ProviderKey, out IChannelAdapter? adapter) || adapter is null)
        {
            return ThrowNotFound<GetAttachmentSchemaResponse>("provider_not_found");
        }

        GetAttachmentSchemaResponse response = new()
        {
            AttachmentId = request.AttachmentId,
            ProviderKey = adapter.ProviderKey,
            SchemaVersion = adapter.SchemaVersion,
            CapabilityVersion = adapter.CapabilityVersion,
        };
        response.Fields.AddRange(adapter.GetSchema());
        return Task.FromResult(response);
    }

    public override Task<UpsertAttachmentResponse> UpsertAttachment(
        UpsertAttachmentRequest request,
        ServerCallContext context)
    {
        ChannelAuthorizationResult authorization = authorizer.Authorize(request, request.Context, nameof(UpsertAttachment), ChannelRequestAuthorizer.Write, context);
        AttachmentSettingValue? ownerSetting = request.Settings.FirstOrDefault(static item => string.Equals(item.FieldKey, "owner_user_id", StringComparison.Ordinal));
        if (ownerSetting is null || string.IsNullOrWhiteSpace(ownerSetting.StringValue))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "owner_user_id_required"));
        authorizer.RequireOwner(authorization, request.Context, nameof(UpsertAttachment), ownerSetting.StringValue);
        AttachmentSummary? existingAttachment = attachmentStore.GetAttachmentSummary(request.AttachmentId);
        if (existingAttachment is not null)
        {
            RequireAttachmentScope(authorization, request.Context, nameof(UpsertAttachment), request.AttachmentId);
            authorizer.RequireResourceScope(authorization, request.Context, nameof(UpsertAttachment),
                string.Equals(existingAttachment.ProviderKey, request.ProviderKey, StringComparison.OrdinalIgnoreCase),
                StatusCode.PermissionDenied, "attachment_provider_immutable");
            if (existingAttachment.Status is AttachmentStatus.Suspended or AttachmentStatus.Deleted or AttachmentStatus.RecoveryRequired)
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "attachment_requires_explicit_recovery"));
        }
        using IDisposable scope = BeginGrpcScope(context, "UpsertAttachment",
            ("AttachmentId", request.AttachmentId),
            ("ProviderKey", request.ProviderKey));
        TagCurrentActivity(("rpc.method", "UpsertAttachment"), ("channel.attachment_id", request.AttachmentId), ("channel.provider_key", request.ProviderKey));
        if (!providerRegistry.TryGet(request.ProviderKey, out IChannelAdapter? adapter) || adapter is null)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "provider_not_found"));
        HashSet<string> secretKeys = [.. adapter.GetSchema()
            .Where(static field => field.Secret || field.FieldType == AttachmentSchemaFieldType.SecretText)
            .Select(static field => field.FieldKey)];
        AttachmentSettingValue[] plaintextSecrets = [.. request.Settings.Where(item => secretKeys.Contains(item.FieldKey))];
        if (plaintextSecrets.Length > 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "secret_settings_must_use_credential_references"));
        try
        {
            ChannelAttachmentStore.ValidateUpsert(request);
        }
        catch (InvalidOperationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        authorization = authorizer.BeginOperation(nameof(UpsertAttachment), authorization);
        if (authorization.IsIdempotentRetry)
        {
            if (authorizer.TryGetCachedResponse(nameof(UpsertAttachment), authorization, out byte[] cached))
                return Task.FromResult(UpsertAttachmentResponse.Parser.ParseFrom(cached));
            throw OperationPending(authorization);
        }
        UpsertAttachmentResponse response = attachmentStore.UpsertAttachment(request);
        authorizer.StoreResponse(nameof(UpsertAttachment), authorization, response);
        return Task.FromResult(response);
    }

    public override async Task<SyncOwnerTargetsResponse> SyncOwnerTargets(
        SyncOwnerTargetsRequest request,
        ServerCallContext context)
    {
        ChannelAuthorizationResult authorization = authorizer.Authorize(
            request, request.Context, nameof(SyncOwnerTargets), ChannelRequestAuthorizer.Write, context);
        authorizer.RequireOwner(authorization, request.Context, nameof(SyncOwnerTargets), request.OwnerUserId);
        authorization = authorizer.BeginOperation(nameof(SyncOwnerTargets), authorization);
        if (authorization.IsIdempotentRetry)
        {
            if (authorizer.TryGetCachedResponse(nameof(SyncOwnerTargets), authorization, out byte[] cached))
                return SyncOwnerTargetsResponse.Parser.ParseFrom(cached);
            throw OperationPending(authorization);
        }
        using IDisposable scope = BeginGrpcScope(context, "SyncOwnerTargets",
            ("OwnerUserId", request.OwnerUserId),
            ("Reason", request.Reason),
            ("TargetCount", request.Targets.Count));
        TagCurrentActivity(("rpc.method", "SyncOwnerTargets"), ("channel.owner_user_id", request.OwnerUserId), ("channel.reason", request.Reason));
        int considered = 0;
        int synced = 0;
        authorizer.MarkEffectStarted(nameof(SyncOwnerTargets), authorization);
        foreach (IChannelAdapter adapter in providerRegistry.GetAll())
        {
            SyncOwnerTargetsResponse response = await adapter.SyncOwnerTargetsAsync(request, context.CancellationToken).ConfigureAwait(false);
            considered += response.RegistrationsConsidered;
            synced += response.RegistrationsSynced;
        }

        var result = new SyncOwnerTargetsResponse
        {
            Accepted = true,
            Detail = "owner_targets_synced",
            RegistrationsConsidered = considered,
            RegistrationsSynced = synced,
        };
        authorizer.StoreResponse(nameof(SyncOwnerTargets), authorization, result);
        return result;
    }

    public override async Task<ProvisionAttachmentResponse> ProvisionAttachment(
        ProvisionAttachmentRequest request,
        ServerCallContext context)
    {
        ChannelAuthorizationResult authorization = authorizer.Authorize(
            request, request.Context, nameof(ProvisionAttachment), ChannelRequestAuthorizer.Write, context);
        RequireAttachmentScope(authorization, request.Context, nameof(ProvisionAttachment), request.AttachmentId);
        using IDisposable operationLease = resourceGovernor.AcquireRegistrationOperation(
            request.AttachmentId, nameof(ProvisionAttachment), limit: 1);
        IChannelAdapter adapter = ResolveAdapterForAttachment(request.AttachmentId);
        authorization = authorizer.BeginOperation(nameof(ProvisionAttachment), authorization);
        if (authorization.IsIdempotentRetry)
        {
            if (authorizer.TryGetCachedResponse(nameof(ProvisionAttachment), authorization, out byte[] cached))
                return ProvisionAttachmentResponse.Parser.ParseFrom(cached);
            throw OperationPending(authorization);
        }
        using IDisposable scope = BeginGrpcScope(context, "ProvisionAttachment",
            ("AttachmentId", request.AttachmentId),
            ("BotName", request.BotName));
        TagCurrentActivity(("rpc.method", "ProvisionAttachment"), ("channel.attachment_id", request.AttachmentId));
        string operationId = request.Context?.RequestId ?? Guid.NewGuid().ToString("N");
        _ = attachmentStore.MarkProvisioningStarted(request.AttachmentId, operationId + ":start");
        ProvisionAttachmentResponse response;
        try
        {
            authorizer.MarkEffectStarted(nameof(ProvisionAttachment), authorization);
            response = await adapter.ProvisionAttachmentAsync(
                request.AttachmentId, request, context.CancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _ = attachmentStore.MarkProvisioningFailed(request.AttachmentId, "provider_exception", operationId + ":failed");
            runtimeCredentials.Remove(request.AttachmentId);
            throw;
        }
        if (response.Status == ProvisioningStatus.Failed)
        {
            _ = attachmentStore.MarkProvisioningFailed(request.AttachmentId, response.Detail, operationId + ":failed");
            runtimeCredentials.Remove(request.AttachmentId);
        }
        authorizer.StoreResponse(nameof(ProvisionAttachment), authorization, response);
        return response;
    }

    public override Task<DeactivateAttachmentResponse> DeactivateAttachment(
        DeactivateAttachmentRequest request,
        ServerCallContext context)
    {
        ChannelAuthorizationResult authorization = authorizer.Authorize(request, request.Context, nameof(DeactivateAttachment), ChannelRequestAuthorizer.Write, context);
        RequireAttachmentScope(authorization, request.Context, nameof(DeactivateAttachment), request.AttachmentId);
        authorization = authorizer.BeginOperation(nameof(DeactivateAttachment), authorization);
        if (authorization.IsIdempotentRetry)
        {
            if (authorizer.TryGetCachedResponse(nameof(DeactivateAttachment), authorization, out byte[] cached))
                return Task.FromResult(DeactivateAttachmentResponse.Parser.ParseFrom(cached));
            throw OperationPending(authorization);
        }
        using IDisposable scope = BeginGrpcScope(context, "DeactivateAttachment",
            ("AttachmentId", request.AttachmentId));
        TagCurrentActivity(("rpc.method", "DeactivateAttachment"), ("channel.attachment_id", request.AttachmentId));

        bool deactivated = attachmentStore.DeactivateAttachment(request.AttachmentId, request.Context?.RequestId);
        if (deactivated)
        {
            runtimeCredentials.Remove(request.AttachmentId);
            outboundAssets.DeleteForAttachment(request.AttachmentId);
        }
        var response = new DeactivateAttachmentResponse
        {
            AttachmentId = request.AttachmentId,
            Status = deactivated ? AttachmentStatus.Inactive : AttachmentStatus.Unspecified,
            Detail = deactivated ? "deactivated" : "attachment_not_found",
        };
        authorizer.StoreResponse(nameof(DeactivateAttachment), authorization, response);
        return Task.FromResult(response);
    }

    public override Task<TransitionAttachmentLifecycleResponse> TransitionAttachmentLifecycle(
        TransitionAttachmentLifecycleRequest request,
        ServerCallContext context)
    {
        ChannelAuthorizationResult authorization = authorizer.Authorize(request, request.Context, nameof(TransitionAttachmentLifecycle), ChannelRequestAuthorizer.Write, context);
        RequireAttachmentScope(authorization, request.Context, nameof(TransitionAttachmentLifecycle), request.AttachmentId);
        if (string.IsNullOrWhiteSpace(request.OperationId) || request.OperationId.Length > 128 || request.Reason.Length > 256)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "lifecycle_operation_invalid"));
        authorization = authorizer.BeginOperation(nameof(TransitionAttachmentLifecycle), authorization);
        if (authorization.IsIdempotentRetry)
        {
            if (authorizer.TryGetCachedResponse(nameof(TransitionAttachmentLifecycle), authorization, out byte[] cached))
                return Task.FromResult(TransitionAttachmentLifecycleResponse.Parser.ParseFrom(cached));
            throw OperationPending(authorization);
        }

        bool accepted = request.Transition.ToUpperInvariant() switch
        {
            "SUSPEND" => attachmentStore.SuspendAttachment(request.AttachmentId, request.Reason, request.OperationId),
            "RECOVER" => attachmentStore.RecoverAttachment(request.AttachmentId, request.OperationId),
            "DELETE" => attachmentStore.DeleteAttachment(request.AttachmentId, request.OperationId),
            _ => throw new RpcException(new Status(StatusCode.InvalidArgument, "lifecycle_transition_not_supported")),
        };
        if (accepted && request.Transition.ToUpperInvariant() is "SUSPEND" or "DELETE")
            outboundAssets.DeleteForAttachment(request.AttachmentId);
        AttachmentStatus status = attachmentStore.GetAttachmentSummary(request.AttachmentId)?.Status ?? AttachmentStatus.Unspecified;
        var response = new TransitionAttachmentLifecycleResponse
        {
            Accepted = accepted,
            Status = status,
            Detail = accepted ? "transition_applied_or_already_complete" : "transition_rejected",
        };
        authorizer.StoreResponse(nameof(TransitionAttachmentLifecycle), authorization, response);
        return Task.FromResult(response);
    }

    public override async Task<UploadOutboundAssetResponse> UploadOutboundAsset(
        IAsyncStreamReader<UploadOutboundAssetRequest> requestStream,
        ServerCallContext context)
    {
        if (!await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "outbound_asset_metadata_required"));
        UploadOutboundAssetRequest first = requestStream.Current;
        if (first.FrameCase != UploadOutboundAssetRequest.FrameOneofCase.Metadata || first.Metadata.Context is null)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "outbound_asset_metadata_must_be_first"));

        ChannelAuthorizationResult authorization = authorizer.Authorize(first, first.Metadata.Context, nameof(UploadOutboundAsset), ChannelRequestAuthorizer.Runtime, context);
        RequireAttachmentScope(authorization, first.Metadata.Context, nameof(UploadOutboundAsset), first.Metadata.AttachmentId, requireActive: true);
        authorizer.RequireResourceScope(authorization, first.Metadata.Context, nameof(UploadOutboundAsset),
            attachmentStore.IsConversationBound(first.Metadata.AttachmentId, first.Metadata.ConversationId),
            StatusCode.PermissionDenied, "conversation_not_bound_to_attachment");
        try
        {
            ChannelOutboundAssetUploadResult result = await outboundAssets.ReceiveAsync(
                first.Metadata,
                ReadAssetChunks(requestStream, context.CancellationToken),
                context.CancellationToken).ConfigureAwait(false);
            return new UploadOutboundAssetResponse
            {
                Accepted = result.Accepted,
                UploadId = result.UploadId,
                Status = result.Status,
                Detail = result.Detail,
            };
        }
        catch (InvalidOperationException ex)
        {
            throw new RpcException(new Status(
                ex.Message.Contains("idempotency", StringComparison.Ordinal) ? StatusCode.AlreadyExists : StatusCode.InvalidArgument,
                ex.Message));
        }
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAssetChunks(
        IAsyncStreamReader<UploadOutboundAssetRequest> requestStream,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (await requestStream.MoveNext(cancellationToken).ConfigureAwait(false))
        {
            UploadOutboundAssetRequest frame = requestStream.Current;
            if (frame.FrameCase != UploadOutboundAssetRequest.FrameOneofCase.Content)
                throw new InvalidOperationException("outbound_asset_content_frame_invalid");
            if (frame.Content.Length > ChannelOutboundAssetStore.MaximumChunkBytes)
                throw new InvalidOperationException("outbound_asset_chunk_too_large");
            yield return frame.Content.Memory;
        }
    }

    public override async Task<SendOutboundMessageResponse> SendOutboundMessage(
        SendOutboundMessageRequest request,
        ServerCallContext context)
    {
        ChannelAuthorizationResult authorization = authorizer.Authorize(
            request, request.Context, nameof(SendOutboundMessage), ChannelRequestAuthorizer.Runtime, context);
        RequireAttachmentScope(authorization, request.Context, nameof(SendOutboundMessage), request.AttachmentId, requireActive: true);
        authorizer.RequireResourceScope(authorization, request.Context, nameof(SendOutboundMessage),
            attachmentStore.IsConversationBound(request.AttachmentId, request.ConversationId),
            StatusCode.PermissionDenied, "conversation_not_bound_to_attachment");
        using IDisposable operationLease = resourceGovernor.AcquireRegistrationOperation(
            request.AttachmentId, nameof(SendOutboundMessage), limit: 8);
        IChannelAdapter adapter = ResolveAdapterForAttachment(request.AttachmentId);
        authorization = authorizer.BeginOperation(nameof(SendOutboundMessage), authorization);
        if (authorization.IsIdempotentRetry)
        {
            if (authorizer.TryGetCachedResponse(nameof(SendOutboundMessage), authorization, out byte[] cached))
                return SendOutboundMessageResponse.Parser.ParseFrom(cached);
            throw OperationPending(authorization);
        }
        using IDisposable scope = BeginGrpcScope(context, "SendOutboundMessage",
            ("AttachmentId", request.AttachmentId),
            ("MessageId", request.MessageId),
            ("CorrelationId", request.CorrelationId),
            ("ConversationId", request.ConversationId),
            ("ConversationThreadId", request.ConversationThreadId),
            ("ActionType", request.Content?.ActionType));
        TagCurrentActivity(
            ("rpc.method", "SendOutboundMessage"),
            ("channel.attachment_id", request.AttachmentId),
            ("channel.message_id", request.MessageId),
            ("channel.correlation_id", request.CorrelationId),
            ("channel.conversation_id", request.ConversationId),
            ("channel.conversation_thread_id", request.ConversationThreadId),
            ("channel.action_type", request.Content?.ActionType));
        authorizer.MarkEffectStarted(nameof(SendOutboundMessage), authorization);
        SendOutboundMessageResponse response = request.Content?.ActionType switch
        {
            "session_facts" => await sessionFactsExecutor.ExecuteAsync(request.AttachmentId, adapter, request, context.CancellationToken).ConfigureAwait(false),
            "queue_view" or "session_view" or "session_picker_view" => await sessionFactsExecutor.ExecuteLegacyViewAsync(
                request.AttachmentId, adapter, request, context.CancellationToken).ConfigureAwait(false),
            ChannelOutboundActionTypes.SendSemanticMessage or ChannelOutboundActionTypes.EditSemanticMessage => await ChannelSemanticMessageOutboundExecutor.ExecuteAsync(
                request.AttachmentId, adapter, request, context.CancellationToken).ConfigureAwait(false),
            _ => await adapter.SendOutboundMessageAsync(request.AttachmentId, request, context.CancellationToken).ConfigureAwait(false),
        };
        authorizer.StoreResponse(nameof(SendOutboundMessage), authorization, response);
        return response;
    }

    private IChannelAdapter ResolveAdapterForAttachment(string attachmentId)
    {
        if (!providerRegistry.TryGetForAttachment(attachmentStore, attachmentId, out IChannelAdapter? adapter)
            || adapter is null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "attachment_not_found"));
        }

        return adapter;
    }

    private static RpcException OperationPending(ChannelAuthorizationResult authorization)
        => new(new Status(StatusCode.Aborted,
            authorization.RequiresReconciliation ? "idempotent_operation_reconciliation_required" : "idempotent_operation_in_progress"));

    private void RequireAttachmentScope(
        ChannelAuthorizationResult authorization,
        ScopedRequestContext? requestContext,
        string method,
        string attachmentId,
        bool requireActive = false)
    {
        string? ownerUserId = attachmentStore.GetAttachmentOwner(attachmentId);
        authorizer.RequireResourceScope(authorization, requestContext, method, ownerUserId is not null,
            StatusCode.NotFound, "attachment_not_found");
        authorizer.RequireOwner(authorization, requestContext, method, ownerUserId!);
        string? tenantId = attachmentStore.GetAttachmentTenant(attachmentId);
        authorizer.RequireResourceScope(authorization, requestContext, method,
            requestContext is not null && !string.IsNullOrWhiteSpace(requestContext.TenantId)
                && string.Equals(requestContext.TenantId, tenantId, StringComparison.Ordinal),
            StatusCode.PermissionDenied, "channel_tenant_scope_mismatch");
        authorizer.RequireResourceScope(authorization, requestContext, method,
            !requireActive || attachmentStore.GetAttachmentSummary(attachmentId)?.Status == AttachmentStatus.Active,
            StatusCode.FailedPrecondition, "attachment_not_active");
    }

    private IDisposable BeginGrpcScope(ServerCallContext context, string methodName, params (string Key, object? Value)[] properties)
    {
        string? externalCorrelationId = KautukaGrpcCorrelationMetadata.GetCorrelationId(context.RequestHeaders);
        List<(string Key, object? Value)> values = [("GrpcMethod", methodName)];
        if (!string.IsNullOrWhiteSpace(externalCorrelationId))
        {
            values.Add(("CorrelationId", externalCorrelationId));
        }

        values.AddRange(properties);
        return logger.BeginKautukaCorrelationScope([.. values]);
    }

    private static void TagCurrentActivity(params (string Key, object? Value)[] tags)
    {
        _ = tags;
    }

    private static Task<TResponse> ThrowNotFound<TResponse>(string detail) =>
        throw new RpcException(new Status(StatusCode.NotFound, detail));

    [LoggerMessage(EventId = 102, Level = LogLevel.Information,
        Message = "Ingress stream started. subscriberId={SubscriberId}")]
    private static partial void LogIngressStreamStarted(ILogger logger, string subscriberId);
}
