using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Google.Protobuf;

using Grpc.Core;

using Kautuka.ChannelHost.Grpc;
using Kautuka.Protos;

namespace Kautuka.ChannelHost.Providers.Slack;

internal static class SlackFileDownloadPolicy
{
    public static bool IsSlackOwnedHttpsUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        return IsSlackOwnedHost(uri.IdnHost);
    }

    public static bool IsSlackOwnedWebSocketUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return uri.IsAbsoluteUri
            && string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase)
            && IsSlackOwnedHost(uri.IdnHost);
    }

    private static bool IsSlackOwnedHost(string host)
        => string.Equals(host, "slack.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".slack.com", StringComparison.OrdinalIgnoreCase);
}

internal sealed class SlackSocketModeWorker(
    ChannelAttachmentStore attachmentStore,
    ChannelRuntimeCredentialStore runtimeCredentials,
    ChannelIngressStore ingressStore,
    ChannelOutboundAssetStore outboundAssets,
    IHttpClientFactory httpClientFactory,
    ILogger<SlackSocketModeWorker> logger,
    ChannelResourceGovernor? resourceGovernor = null,
    ChannelProviderTransportProfile? transportProfile = null,
    Func<Uri, CancellationToken, Task<WebSocket>>? socketConnector = null,
    Func<int, TimeSpan>? connectionBackoffDelay = null,
    Func<string, string, string, CancellationToken, Task>? connectionLoopRunner = null,
    TimeSpan? supervisorPollInterval = null,
    Func<int, TimeSpan>? identitySyncRetryDelay = null,
    TimeProvider? connectionTimeProvider = null,
    TimeSpan? connectionStabilityThreshold = null) : IChannelAdapter
{
    private static readonly ConcurrentDictionary<string, DateTimeOffset> nonOwnerNoticeTimes = new(StringComparer.Ordinal);
    private const string eventsApiEnvelopeType = "events_api";
    private const string interactiveEnvelopeType = "interactive";
    private const string slashCommandsEnvelopeType = "slash_commands";
    private const int maximumWebSocketMessageBytes = 1_048_576;

    private readonly ConcurrentDictionary<string, ActiveSlackConnection> activeConnections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IdentitySyncFailure> identitySyncFailures = new(StringComparer.Ordinal);
    private readonly Func<Uri, CancellationToken, Task<WebSocket>> connectSocket = socketConnector ?? ConnectSocketAsync;
    private readonly Func<int, TimeSpan> getConnectionBackoffDelay = connectionBackoffDelay ?? GetConnectionBackoffDelay;
    private readonly Func<int, TimeSpan> getIdentitySyncRetryDelay = identitySyncRetryDelay ?? GetIdentitySyncRetryDelay;
    private readonly Func<string, string, string, CancellationToken, Task>? testConnectionLoopRunner = connectionLoopRunner;
    private readonly TimeSpan supervisorPollDelay = supervisorPollInterval ?? TimeSpan.FromSeconds(5);
    private readonly TimeProvider connectionClock = connectionTimeProvider ?? TimeProvider.System;
    private readonly TimeSpan stableConnectionThreshold = connectionStabilityThreshold ?? TimeSpan.FromSeconds(10);

    public string ProviderKey => "slack";
    public string DisplayName => "Slack";
    public string SchemaVersion => "slack/v1";
    public string CapabilityVersion => "slack/v1";
    public ChannelProviderCapabilities Capabilities => ChannelProviderCapabilityCatalog.Slack;

    public IReadOnlyList<AttachmentSchemaField> GetSchema()
        =>
        [
            new AttachmentSchemaField { FieldKey = "bot_token", Label = "Bot token", Description = "Slack Bot User OAuth Token, usually starts with xoxb-.", FieldType = AttachmentSchemaFieldType.SecretText, Required = true, Secret = true },
            new AttachmentSchemaField { FieldKey = "app_token", Label = "App token", Description = "Slack App-Level Token for Socket Mode, usually starts with xapp-.", FieldType = AttachmentSchemaFieldType.SecretText, Required = true, Secret = true },
        ];

    public async Task<ChannelProviderHealth> GetHealthAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ChannelAttachmentRecord> attachments = attachmentStore.ListActiveAttachments(ProviderKey);
        ChannelAttachmentRecord? attachment = attachments.Count > 0 ? attachments[0] : null;
        string botToken = attachment is null ? string.Empty : runtimeCredentials.Get(attachment.AttachmentId, "bot_token");
        if (string.IsNullOrWhiteSpace(botToken))
        {
            return new ChannelProviderHealth { ProviderKey = ProviderKey, DisplayName = DisplayName, Status = "not_configured", Detail = "missing_bot_token" };
        }

        try
        {
            if (attachment is not null)
            {
                if (NeedsSlackIdentitySync(attachment.AttachmentId, attachment.Settings, botToken)
                    && !ShouldDeferIdentitySync(attachment.AttachmentId, botToken, DateTimeOffset.UtcNow))
                {
                    string? syncFailure;
                    try
                    {
                        syncFailure = await SyncSlackIdentityAsync(attachment.AttachmentId, botToken, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        RecordIdentitySyncFailure(attachment.AttachmentId, botToken, null);
                        throw;
                    }
                    if (syncFailure is not null) RecordIdentitySyncFailure(attachment.AttachmentId, botToken, syncFailure);
                    else identitySyncFailures.TryRemove(attachment.AttachmentId, out _);
                }
            }

            using HttpRequestMessage request = new(HttpMethod.Post, "https://slack.com/api/auth.test");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", botToken);
            using JsonDocument document = await SendSlackApiJsonAsync(request, cancellationToken).ConfigureAwait(false);
            bool ok = document.RootElement.TryGetProperty("ok", out JsonElement okElement) && okElement.GetBoolean();
            string detail = ok
                ? "reachable"
                : document.RootElement.TryGetProperty("error", out JsonElement errorElement)
                    ? errorElement.GetString() ?? "unknown_error"
                    : "unknown_error";

            return new ChannelProviderHealth
            {
                ProviderKey = ProviderKey,
                DisplayName = DisplayName,
                Status = ok ? "healthy" : "degraded",
                Detail = detail,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or TaskCanceledException or JsonException)
        {
            return new ChannelProviderHealth { ProviderKey = ProviderKey, DisplayName = DisplayName, Status = "degraded", Detail = ex.GetType().Name };
        }
    }

    private static readonly HashSet<string> knownOutboundActions = new(StringComparer.Ordinal)
    {
        "send_text", "send_chat_action", "send_inline_keyboard", "send_interaction_prompt", "send_semantic_message", "edit_semantic_message",
        "acknowledge_callback", "edit_message_text", "delete_message", "add_reaction", "stream_message", "send_file",
        "send_ephemeral_message", "open_modal", ChannelOutboundActionTypes.RuntimeEvent,
    };

    public async IAsyncEnumerable<ChannelInboundEventMessage> StreamIngressAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (ChannelInboundEventMessage message in ingressStore.StreamAsync("adapter-slack", cancellationToken).ConfigureAwait(false))
        {
            yield return message;
        }
    }

    public Task<SyncOwnerTargetsResponse> SyncOwnerTargetsAsync(SyncOwnerTargetsRequest request, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Task.FromResult(new SyncOwnerTargetsResponse
        {
            Accepted = true,
            Detail = "provider_sync_not_required",
            RegistrationsConsidered = attachmentStore.ListActiveAttachments(ProviderKey, request.OwnerUserId).Count,
            RegistrationsSynced = 0,
        });
    }

    public Task<ProvisionAttachmentResponse> ProvisionAttachmentAsync(string attachmentId, ProvisionAttachmentRequest request, CancellationToken cancellationToken)
        => ProvisionAttachmentCoreAsync(attachmentId, request, cancellationToken);

    private async Task<ProvisionAttachmentResponse> ProvisionAttachmentCoreAsync(string attachmentId, ProvisionAttachmentRequest request, CancellationToken cancellationToken)
    {
        ChannelAttachmentRecord? attachment = attachmentStore.GetAttachmentForProvisioning(attachmentId, ProviderKey);
        string botToken = attachment is null ? string.Empty : runtimeCredentials.Get(attachment.AttachmentId, "bot_token");
        string appToken = attachment is null ? string.Empty : runtimeCredentials.Get(attachment.AttachmentId, "app_token");
        async Task<string?> SyncIdentityForProvisioningAsync(string id, string token, CancellationToken cancellation)
        {
            string? failure;
            try { failure = await SyncSlackIdentityAsync(id, token, cancellation).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellation.IsCancellationRequested)
            {
                RecordIdentitySyncFailure(id, token, null);
                throw;
            }
            if (failure is not null) RecordIdentitySyncFailure(id, token, failure);
            return failure;
        }
        SlackSetupValidator.Result validation = await SlackSetupValidator.ValidateAsync(
            httpClientFactory,
            SyncIdentityForProvisioningAsync,
            attachmentId,
            botToken,
            appToken,
            cancellationToken).ConfigureAwait(false);
        if (!validation.Success)
        {
            return new ProvisionAttachmentResponse
            {
                AttachmentId = attachmentId,
                Status = ProvisioningStatus.Failed,
                Detail = validation.Detail,
                OperationId = request.Context?.RequestId ?? string.Empty,
            };
        }

        attachmentStore.MarkProvisioned(attachmentId, "provisioned", request.Context?.RequestId ?? Guid.NewGuid().ToString("N"));
        identitySyncFailures.TryRemove(attachmentId, out _);
        return new ProvisionAttachmentResponse
        {
            AttachmentId = attachmentId,
            Status = ProvisioningStatus.Succeeded,
            Detail = validation.Detail,
            OperationId = request.Context?.RequestId ?? string.Empty,
        };
    }

    public async Task<SendOutboundMessageResponse> SendOutboundMessageAsync(string attachmentId, SendOutboundMessageRequest request, CancellationToken cancellationToken)
    {
        ChannelAttachmentRecord? attachment = attachmentStore.ListActiveAttachments(ProviderKey).FirstOrDefault(item => string.Equals(item.AttachmentId, attachmentId, StringComparison.Ordinal));
        if (attachment is null)
        {
            return new SendOutboundMessageResponse { Accepted = false, DeliveryStatus = DeliveryStatus.Rejected, Detail = "attachment_not_found" };
        }

        string botToken = runtimeCredentials.Get(attachment.AttachmentId, "bot_token");
        if (string.IsNullOrWhiteSpace(botToken))
        {
            return new SendOutboundMessageResponse { Accepted = false, DeliveryStatus = DeliveryStatus.Rejected, Detail = "missing_bot_token" };
        }

        string actionType = request.Content?.ActionType ?? string.Empty;
        if (!knownOutboundActions.Contains(actionType))
            return new SendOutboundMessageResponse { Accepted = false, DeliveryStatus = DeliveryStatus.Rejected, Detail = $"unsupported_action:{actionType}" };

        if (actionType == "acknowledge_callback") return BuildNoOpResponse("callback_already_acked");
        if (actionType == "send_chat_action") return BuildNoOpResponse("typing_not_supported");

        if (!IsSlackDeliveryAllowed(attachment.Settings, request))
        {
            SlackSocketModeWorkerLog.OutboundDestinationRejected(logger, attachmentId);
            return new SendOutboundMessageResponse { Accepted = false, DeliveryStatus = DeliveryStatus.Rejected, Detail = "slack_private_dm_only" };
        }

        try
        {
            if (ShouldDeferIdentitySync(attachment.AttachmentId, botToken, DateTimeOffset.UtcNow))
            {
                bool hasFailure = identitySyncFailures.TryGetValue(attachment.AttachmentId, out IdentitySyncFailure? failure);
                return new SendOutboundMessageResponse
                {
                    Accepted = false,
                    DeliveryStatus = hasFailure && failure!.Permanent ? DeliveryStatus.Rejected : DeliveryStatus.RetryableFailure,
                    Detail = hasFailure ? failure!.Detail ?? "slack_identity_sync_deferred" : "slack_identity_sync_deferred",
                };
            }

            if (NeedsSlackIdentitySync(attachment.AttachmentId, attachment.Settings, botToken))
            {
                string? syncFailure;
                try { syncFailure = await SyncSlackIdentityAsync(attachment.AttachmentId, botToken, cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    RecordIdentitySyncFailure(attachment.AttachmentId, botToken, null);
                    throw;
                }
                if (syncFailure is not null)
                {
                    RecordIdentitySyncFailure(attachment.AttachmentId, botToken, syncFailure);
                    return new SendOutboundMessageResponse { Accepted = false, DeliveryStatus = DeliveryStatus.Rejected, Detail = syncFailure };
                }
                identitySyncFailures.TryRemove(attachment.AttachmentId, out _);
            }

            return request.Content?.ActionType switch
            {
                "send_text" => await ExecuteSendTextAsync(botToken, request, cancellationToken).ConfigureAwait(false),
                "send_chat_action" => BuildNoOpResponse("typing_not_supported"),
                "send_inline_keyboard" => await ExecuteSendInlineKeyboardAsync(botToken, request, cancellationToken).ConfigureAwait(false),
                "send_interaction_prompt" => await ExecuteSendInteractionPromptAsync(botToken, request, cancellationToken).ConfigureAwait(false),
                "send_semantic_message" => await ExecuteSendSemanticMessageAsync(botToken, request, cancellationToken).ConfigureAwait(false),
                "edit_semantic_message" => await ExecuteEditSemanticMessageAsync(botToken, request, cancellationToken).ConfigureAwait(false),
                "acknowledge_callback" => BuildNoOpResponse("callback_already_acked"),
                "edit_message_text" => await ExecuteEditMessageAsync(botToken, request, cancellationToken).ConfigureAwait(false),
                "delete_message" => await ExecuteDeleteMessageAsync(botToken, request, cancellationToken).ConfigureAwait(false),
                "add_reaction" => await ExecuteAddReactionAsync(botToken, request, cancellationToken).ConfigureAwait(false),
                "stream_message" => await ExecuteStreamMessageAsync(attachmentId, botToken, request, cancellationToken).ConfigureAwait(false),
                "send_file" => await ExecuteSendFileAsync(attachmentId, botToken, request, cancellationToken).ConfigureAwait(false),
                "send_ephemeral_message" => await ExecuteSendEphemeralMessageAsync(botToken, request, cancellationToken).ConfigureAwait(false),
                "open_modal" => await ExecuteOpenModalAsync(botToken, request, cancellationToken).ConfigureAwait(false),
                ChannelOutboundActionTypes.RuntimeEvent => await ChannelRuntimeEventOutboundExecutor.ExecuteGrpcAsync(
                    attachment.Settings,
                    request,
                    (child, ct) => SendOutboundMessageAsync(attachmentId, child, ct),
                    cancellationToken,
                    richContext: true).ConfigureAwait(false),
                _ => new SendOutboundMessageResponse { Accepted = false, DeliveryStatus = DeliveryStatus.Rejected, Detail = $"unsupported_action:{request.Content?.ActionType}" },
            };
        }
        catch (HttpRequestException ex)
        {
            SlackSocketModeWorkerLog.SendOutboundMessageError(logger, ex, attachmentId);
            return new SendOutboundMessageResponse { Accepted = false, DeliveryStatus = DeliveryStatus.RetryableFailure, Detail = "provider_unavailable" };
        }
        catch (JsonException ex)
        {
            SlackSocketModeWorkerLog.SendOutboundMessageError(logger, ex, attachmentId);
            return new SendOutboundMessageResponse { Accepted = false, DeliveryStatus = DeliveryStatus.Rejected, Detail = "invalid_payload" };
        }
        catch (InvalidOperationException ex)
        {
            SlackSocketModeWorkerLog.SendOutboundMessageError(logger, ex, attachmentId);
            return new SendOutboundMessageResponse { Accepted = false, DeliveryStatus = DeliveryStatus.Rejected, Detail = "invalid_payload" };
        }
    }

    public async IAsyncEnumerable<ReadInboundMediaChunk> ReadInboundMediaAsync(string attachmentId, ReadInboundMediaRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string botToken = runtimeCredentials.Get(attachmentId, "bot_token");
        if (string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(request.MediaId)) yield break;
        using HttpRequestMessage infoRequest = new(HttpMethod.Get, $"https://slack.com/api/files.info?file={Uri.EscapeDataString(request.MediaId)}");
        infoRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", botToken);
        using JsonDocument info = await SendSlackApiJsonAsync(infoRequest, cancellationToken).ConfigureAwait(false);
        if (!info.RootElement.TryGetProperty("ok", out JsonElement ok) || !ok.GetBoolean() || !info.RootElement.TryGetProperty("file", out JsonElement file)) yield break;
        string downloadUrl = file.TryGetProperty("url_private_download", out JsonElement download) ? download.GetString() ?? string.Empty : string.Empty;
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out Uri? uri) || !SlackFileDownloadPolicy.IsSlackOwnedHttpsUri(uri)) yield break;
        using HttpRequestMessage downloadRequest = new(HttpMethod.Get, uri); downloadRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", botToken);
        using HttpResponseMessage response = await httpClientFactory.CreateClient("slack-api").SendAsync(downloadRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode(); using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        byte[] buffer = new byte[64 * 1024]; int read; bool first = true;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            yield return new ReadInboundMediaChunk { FileName = first && file.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? string.Empty : string.Empty, ContentType = first && file.TryGetProperty("mimetype", out JsonElement mime) ? mime.GetString() ?? string.Empty : string.Empty, Content = ByteString.CopyFrom(buffer, 0, read), IsFinal = false };
            first = false;
        }
        yield return new ReadInboundMediaChunk { IsFinal = true };
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The supervisor isolates attachment failures so other Slack connections continue.")]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                IReadOnlyList<ChannelAttachmentRecord> attachments = attachmentStore.ListActiveAttachments(ProviderKey);
                HashSet<string> currentIds = [.. attachments.Select(item => item.AttachmentId)];
                foreach (string failedAttachmentId in identitySyncFailures.Keys)
                {
                    if (!currentIds.Contains(failedAttachmentId)) identitySyncFailures.TryRemove(failedAttachmentId, out _);
                }

                foreach ((string attachmentId, ActiveSlackConnection connection) in activeConnections.ToArray())
                {
                    if (currentIds.Contains(attachmentId) && connection.Task is { IsCompleted: false })
                    {
                        continue;
                    }

                    if (activeConnections.TryGetValue(attachmentId, out ActiveSlackConnection? current)
                        && ReferenceEquals(current, connection)
                        && activeConnections.TryRemove(attachmentId, out ActiveSlackConnection? removed))
                    {
                        await removed.Cancellation.CancelAsync().ConfigureAwait(false);
                        if (removed.Task is not null)
                        {
                            try { await removed.Task.ConfigureAwait(false); }
                            catch (OperationCanceledException) when (removed.Cancellation.IsCancellationRequested) { }
                            catch (Exception ex) { SlackSocketModeWorkerLog.ConnectionLoopError(logger, ex, attachmentId); }
                        }
                        removed.Cancellation.Dispose();
                    }
                }

                foreach (ChannelAttachmentRecord attachment in attachments)
                {
                    if (activeConnections.ContainsKey(attachment.AttachmentId))
                    {
                        continue;
                    }

                    string appToken = runtimeCredentials.Get(attachment.AttachmentId, "app_token");
                    if (string.IsNullOrWhiteSpace(appToken))
                    {
                        continue;
                    }

                    string botToken = runtimeCredentials.Get(attachment.AttachmentId, "bot_token");
                    if (!string.IsNullOrWhiteSpace(botToken)
                        && ShouldDeferIdentitySync(attachment.AttachmentId, botToken, DateTimeOffset.UtcNow))
                    {
                        continue;
                    }
                    if (!string.IsNullOrWhiteSpace(botToken)
                        && NeedsSlackIdentitySync(attachment.AttachmentId, attachment.Settings, botToken))
                    {
                        string? syncFailure;
                        try { syncFailure = await SyncSlackIdentityAsync(attachment.AttachmentId, botToken, cancellationToken).ConfigureAwait(false); }
                        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                        {
                            RecordIdentitySyncFailure(attachment.AttachmentId, botToken, null);
                            SlackSocketModeWorkerLog.ConnectionLoopError(logger, ex, attachment.AttachmentId);
                            continue;
                        }
                        if (syncFailure is not null)
                        {
                            RecordIdentitySyncFailure(attachment.AttachmentId, botToken, syncFailure);
                            continue;
                        }
                        identitySyncFailures.TryRemove(attachment.AttachmentId, out _);
                    }

                    CancellationTokenSource? linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    try
                    {
                        ActiveSlackConnection connection = new(linkedCts);
                        if (activeConnections.TryAdd(attachment.AttachmentId, connection))
                        {
                            CancellationTokenSource connectionCts = linkedCts;
                            linkedCts = null;
                            connection.Task = Task.Run(() => RunConnectionLoopForAttachmentAsync(attachment.AttachmentId, attachment.OwnerUserId, appToken, connectionCts.Token), connectionCts.Token);
                        }
                    }
                    finally { linkedCts?.Dispose(); }
                }

                await Task.Delay(supervisorPollDelay, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (ActiveSlackConnection connection in activeConnections.Values)
            {
                await connection.Cancellation.CancelAsync().ConfigureAwait(false);
                if (connection.Task is not null)
                {
                    try { await connection.Task.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (connection.Cancellation.IsCancellationRequested) { }
                    catch (Exception ex) { SlackSocketModeWorkerLog.ConnectionLoopError(logger, ex, "shutdown"); }
                }
                connection.Cancellation.Dispose();
            }

            activeConnections.Clear();
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Provider failures must be logged and retried without terminating Socket Mode supervision.")]
    private Task RunConnectionLoopForAttachmentAsync(string attachmentId, string ownerUserId, string appToken, CancellationToken cancellationToken)
        => testConnectionLoopRunner is null
            ? RunConnectionLoopSafeAsync(attachmentId, ownerUserId, appToken, cancellationToken)
            : testConnectionLoopRunner(attachmentId, ownerUserId, appToken, cancellationToken);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Provider failures must be logged and retried without terminating Socket Mode supervision.")]
    internal async Task RunConnectionLoopSafeAsync(string attachmentId, string ownerUserId, string appToken, CancellationToken cancellationToken)
    {
        int failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                bool connectionWasStable = await RunConnectionLoopAsync(attachmentId, ownerUserId, appToken, cancellationToken).ConfigureAwait(false);
                if (connectionWasStable)
                {
                    failures = 0;
                    continue;
                }
                failures++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                SlackSocketModeWorkerLog.ConnectionLoopError(logger, ex, attachmentId);
                failures++;
            }
            try { await Task.Delay(getConnectionBackoffDelay(failures), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
    }

    internal static TimeSpan GetConnectionBackoffDelay(int attempt)
    {
        double baseSeconds = Math.Min(60, Math.Pow(2, Math.Clamp(attempt - 1, 0, 6)));
        int maximumJitterMilliseconds = (int)(Math.Min(5, baseSeconds * 0.2) * 1000);
        double jitterSeconds = maximumJitterMilliseconds > 0
            ? System.Security.Cryptography.RandomNumberGenerator.GetInt32(maximumJitterMilliseconds) / 1000d
            : 0;
        return TimeSpan.FromSeconds(Math.Min(60, baseSeconds + jitterSeconds));
    }

    private async Task<bool> RunConnectionLoopAsync(string attachmentId, string ownerUserId, string appToken, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "https://slack.com/api/apps.connections.open");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", appToken);

        using JsonDocument openResponse = await SendSlackApiJsonAsync(request, cancellationToken).ConfigureAwait(false);
        if (!openResponse.RootElement.TryGetProperty("ok", out JsonElement okElement)
            || !okElement.GetBoolean()
            || !openResponse.RootElement.TryGetProperty("url", out JsonElement urlElement)
            || string.IsNullOrWhiteSpace(urlElement.GetString()))
        {
            SlackSocketModeWorkerLog.ConnectionOpenFailed(logger, attachmentId, openResponse.RootElement.GetRawText());
            return false;
        }

        if (!Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out Uri? socketUri)
            || (transportProfile?.Mode == ChannelProviderTransportMode.Simulator
                ? !transportProfile.IsSimulatorSlackSocketUri(socketUri)
                : !SlackFileDownloadPolicy.IsSlackOwnedWebSocketUri(socketUri)))
        {
            SlackSocketModeWorkerLog.ConnectionOpenFailed(logger, attachmentId, "slack_socket_url_rejected");
            return false;
        }

        using WebSocket webSocket = await connectSocket(socketUri, cancellationToken).ConfigureAwait(false);
        if (webSocket.State != WebSocketState.Open)
        {
            SlackSocketModeWorkerLog.ConnectionOpenFailed(logger, attachmentId, "slack_socket_not_open_after_connect");
            return false;
        }

        long connectedAt = connectionClock.GetTimestamp();
        SlackSocketModeWorkerLog.SocketModeConnected(logger, attachmentId);

        byte[] buffer = new byte[8192];
        while (!cancellationToken.IsCancellationRequested && webSocket.State == WebSocketState.Open)
        {
            using MemoryStream payloadStream = new();
            WebSocketReceiveResult result;
            bool frameTooLarge = false;
            do
            {
                result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (payloadStream.Length + result.Count > maximumWebSocketMessageBytes) { frameTooLarge = true; break; }
                await payloadStream.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken).ConfigureAwait(false);
            }
            while (!result.EndOfMessage);

            if (frameTooLarge)
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "frame_limit_exceeded", cancellationToken).ConfigureAwait(false);
                break;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            payloadStream.Position = 0;
            await HandleWebSocketMessageAsync(attachmentId, ownerUserId, payloadStream, webSocket, cancellationToken).ConfigureAwait(false);
        }
        return connectionClock.GetElapsedTime(connectedAt) >= stableConnectionThreshold;
    }

    internal static bool IsFrameWithinLimitForTest(long accumulatedBytes, int nextFrameBytes) =>
        accumulatedBytes >= 0 && nextFrameBytes >= 0 && accumulatedBytes + nextFrameBytes <= maximumWebSocketMessageBytes;

    internal async Task HandleWebSocketMessageAsync(string attachmentId, string ownerUserId, MemoryStream payloadStream, WebSocket webSocket, CancellationToken cancellationToken)
    {
        try
        {
            using JsonDocument document = await JsonDocument.ParseAsync(payloadStream, cancellationToken: cancellationToken).ConfigureAwait(false);
            JsonElement root = document.RootElement;
            string envelopeType = root.TryGetProperty("type", out JsonElement typeElement)
                ? typeElement.GetString() ?? string.Empty
                : string.Empty;

            if (string.Equals(envelopeType, "disconnect", StringComparison.OrdinalIgnoreCase))
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "server disconnect", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!string.Equals(envelopeType, eventsApiEnvelopeType, StringComparison.Ordinal)
                && !string.Equals(envelopeType, interactiveEnvelopeType, StringComparison.Ordinal)
                && !string.Equals(envelopeType, slashCommandsEnvelopeType, StringComparison.Ordinal))
            {
                return;
            }

            string envelopeId = root.TryGetProperty("envelope_id", out JsonElement envelopeIdElement)
                ? envelopeIdElement.GetString() ?? string.Empty
                : string.Empty;

            if (!root.TryGetProperty("payload", out JsonElement payloadElement) || payloadElement.ValueKind != JsonValueKind.Object)
            {
                await AcknowledgeEnvelopeAsync(webSocket, envelopeId, cancellationToken).ConfigureAwait(false);
                return;
            }

            JsonElement? attachmentSettings = attachmentStore.TryGetActiveAttachment(attachmentId, out ChannelAttachmentRecord? activeAttachment)
                ? activeAttachment?.Settings
                : null;

            ChannelInboundEventMessage? inboundMessage = string.Equals(envelopeType, eventsApiEnvelopeType, StringComparison.Ordinal)
                ? TryCreateEventsApiMessage(attachmentId, ownerUserId, payloadElement, attachmentSettings)
                : string.Equals(envelopeType, slashCommandsEnvelopeType, StringComparison.Ordinal)
                    ? TryCreateSlashCommandMessage(attachmentId, ownerUserId, payloadElement, envelopeId)
                    : TryCreateInteractiveMessage(attachmentId, ownerUserId, payloadElement, envelopeId);

            if (inboundMessage is null)
            {
                await AcknowledgeEnvelopeAsync(webSocket, envelopeId, cancellationToken).ConfigureAwait(false);
                return;
            }

            string ownerSlackId = GetStringSetting(attachmentSettings, "slack_owner_user_id");
            string ownerDm = GetStringSetting(attachmentSettings, "slack_dm_channel_id");
            bool isSlash = string.Equals(envelopeType, slashCommandsEnvelopeType, StringComparison.Ordinal);
            bool isBlockAction = string.Equals(envelopeType, interactiveEnvelopeType, StringComparison.Ordinal)
                && payloadElement.TryGetProperty("type", out JsonElement actionType)
                && string.Equals(actionType.GetString(), "block_actions", StringComparison.Ordinal);
            bool isPrivateDm = inboundMessage.ConversationId.StartsWith('D');

            if (string.IsNullOrWhiteSpace(ownerSlackId))
            {
                string pairingText = inboundMessage.Text.Trim();
                if (!string.Equals(envelopeType, eventsApiEnvelopeType, StringComparison.Ordinal)
                    || !isPrivateDm
                    || pairingText.Length != 6
                    || pairingText.Any(static character => !char.IsAsciiDigit(character)))
                {
                    await AcknowledgeEnvelopeAsync(webSocket, envelopeId, cancellationToken).ConfigureAwait(false);
                    return;
                }
                inboundMessage.Text = pairingText;
            }
            else
            {
                bool isOwnerUser = string.Equals(inboundMessage.ExternalUserId, ownerSlackId, StringComparison.Ordinal);
                bool isOwnerDm = isOwnerUser && string.Equals(inboundMessage.ConversationId, ownerDm, StringComparison.Ordinal);
                if ((isSlash || isBlockAction) && !isPrivateDm)
                {
                    const string refusal = "Use Kautuka in your direct message with this app.";
                    if (isBlockAction)
                    {
                        await AcknowledgeEnvelopeAsync(webSocket, envelopeId, cancellationToken).ConfigureAwait(false);
                        await SendSlackResponseUrlEphemeralAsync(payloadElement, refusal, cancellationToken).ConfigureAwait(false);
                    }
                    else
                        await AcknowledgeEnvelopeAsync(webSocket, envelopeId, cancellationToken,
                            new JsonObject { ["response_type"] = "ephemeral", ["text"] = refusal }).ConfigureAwait(false);
                    return;
                }

                if (!isOwnerDm)
                {
                    if (isBlockAction)
                    {
                        await AcknowledgeEnvelopeAsync(webSocket, envelopeId, cancellationToken).ConfigureAwait(false);
                        await SendSlackResponseUrlEphemeralAsync(payloadElement, "This Kautuka assistant is private.", cancellationToken).ConfigureAwait(false);
                    }
                    else if (isSlash)
                    {
                        await AcknowledgeEnvelopeAsync(webSocket, envelopeId, cancellationToken,
                            new JsonObject { ["response_type"] = "ephemeral", ["text"] = "This Kautuka assistant is private." }).ConfigureAwait(false);
                        return;
                    }
                    else if (string.Equals(envelopeType, eventsApiEnvelopeType, StringComparison.Ordinal) && !isOwnerUser && isPrivateDm)
                        await SendNonOwnerDirectNoticeOnceAsync(attachmentId, inboundMessage.ConversationId, inboundMessage.ExternalUserId, ownerSlackId, cancellationToken).ConfigureAwait(false);
                    if (!isBlockAction)
                        await AcknowledgeEnvelopeAsync(webSocket, envelopeId, cancellationToken).ConfigureAwait(false);
                    return;
                }
            }

            await EnrichSlackInboundContextAsync(attachmentId, inboundMessage, cancellationToken).ConfigureAwait(false);
            resourceGovernor?.EnforceProviderIngress(ProviderKey, attachmentId);
            _ = ingressStore.Enqueue(inboundMessage, inboundMessage.EventId);

            // Slack must not observe the envelope acknowledgement before a normalized
            // inbound event is durably committed.
            await AcknowledgeEnvelopeAsync(webSocket, envelopeId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or WebSocketException or InvalidOperationException or RpcException or HttpRequestException)
        {
            SlackSocketModeWorkerLog.MessageProcessingError(logger, ex, attachmentId);
            try
            {
                payloadStream.Position = 0;
                using JsonDocument envelope = await JsonDocument.ParseAsync(payloadStream, cancellationToken: cancellationToken).ConfigureAwait(false);
                string envelopeId = envelope.RootElement.TryGetProperty("envelope_id", out JsonElement id) ? id.GetString() ?? string.Empty : string.Empty;
                if (!string.IsNullOrWhiteSpace(envelopeId)) await AcknowledgeEnvelopeAsync(webSocket, envelopeId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ackException) when (ackException is JsonException or WebSocketException or OperationCanceledException)
            {
                if (ackException is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    SlackSocketModeWorkerLog.MessageProcessingError(logger, ackException, attachmentId);
            }
        }
    }

    private static async Task<WebSocket> ConnectSocketAsync(Uri uri, CancellationToken cancellationToken)
    {
        ClientWebSocket socket = new();
        try
        {
            await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task SendSlackResponseUrlEphemeralAsync(JsonElement payload, string text, CancellationToken cancellationToken)
    {
        if (!payload.TryGetProperty("response_url", out JsonElement responseUrlElement)
            || !Uri.TryCreate(responseUrlElement.GetString(), UriKind.Absolute, out Uri? responseUri)
            || !SlackFileDownloadPolicy.IsSlackOwnedHttpsUri(responseUri)) return;
        JsonObject body = new() { ["response_type"] = "ephemeral", ["text"] = text };
        using HttpRequestMessage request = new(HttpMethod.Post, responseUri)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using HttpResponseMessage response = await httpClientFactory.CreateClient("slack-api").SendAsync(request, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            SlackSocketModeWorkerLog.ResponseUrlEphemeralFailed(logger, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            SlackSocketModeWorkerLog.ResponseUrlEphemeralFailed(logger, ex);
        }
    }

    private async Task SendNonOwnerDirectNoticeOnceAsync(string attachmentId, string channelId, string userId, string ownerId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(channelId) || !channelId.StartsWith('D')) return;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string key = $"{attachmentId}:{userId}";
        while (true)
        {
            if (!nonOwnerNoticeTimes.TryGetValue(key, out DateTimeOffset previous))
            {
                if (nonOwnerNoticeTimes.TryAdd(key, now)) break;
                continue;
            }
            if (now - previous < TimeSpan.FromHours(24)) return;
            if (nonOwnerNoticeTimes.TryUpdate(key, now, previous)) break;
        }
        if (nonOwnerNoticeTimes.Count > 1_000)
        {
            foreach ((string staleKey, DateTimeOffset time) in nonOwnerNoticeTimes.OrderBy(static pair => pair.Value).Take(nonOwnerNoticeTimes.Count - 1_000))
                nonOwnerNoticeTimes.TryRemove(staleKey, out _);
        }
        string token = runtimeCredentials.Get(attachmentId, "bot_token");
        if (string.IsNullOrWhiteSpace(token)) return;
        string ownerName = "the workspace owner";
        try
        {
            using HttpRequestMessage ownerRequest = new(HttpMethod.Get, $"https://slack.com/api/users.info?user={Uri.EscapeDataString(ownerId)}");
            ownerRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using JsonDocument owner = await SendSlackApiJsonAsync(ownerRequest, cancellationToken).ConfigureAwait(false);
            if (owner.RootElement.TryGetProperty("user", out JsonElement user)
                && user.TryGetProperty("profile", out JsonElement profile)
                && profile.TryGetProperty("display_name", out JsonElement display)
                && !string.IsNullOrWhiteSpace(display.GetString())) ownerName = display.GetString()!;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException) { }
        JsonObject body = new() { ["channel"] = channelId, ["text"] = $"This is {ownerName}'s private Kautuka assistant. It only responds to its owner." };
        _ = await SendSlackMessageAsync(token, "https://slack.com/api/chat.postMessage", body, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnrichSlackInboundContextAsync(string attachmentId, ChannelInboundEventMessage message, CancellationToken cancellationToken)
    {
        string botToken = runtimeCredentials.Get(attachmentId, "bot_token");
        if (string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(message.ExternalUserId)) return;
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(500));
        try
        {
            using HttpRequestMessage userRequest = new(HttpMethod.Get, $"https://slack.com/api/users.info?user={Uri.EscapeDataString(message.ExternalUserId)}");
            userRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", botToken);
            using JsonDocument user = await SendSlackApiJsonAsync(userRequest, timeout.Token).ConfigureAwait(false);
            if (user.RootElement.TryGetProperty("ok", out JsonElement ok) && ok.GetBoolean()
                && user.RootElement.TryGetProperty("user", out JsonElement userObject))
            {
                message.ExternalUserDisplayName = userObject.TryGetProperty("profile", out JsonElement profile)
                    && profile.TryGetProperty("display_name", out JsonElement display) && !string.IsNullOrWhiteSpace(display.GetString())
                        ? display.GetString() : userObject.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? string.Empty : string.Empty;
            }
            if (!message.ConversationId.StartsWith('D') || string.IsNullOrWhiteSpace(message.ConversationThreadId)) return;
            using HttpRequestMessage historyRequest = new(HttpMethod.Get, $"https://slack.com/api/conversations.replies?channel={Uri.EscapeDataString(message.ConversationId)}&ts={Uri.EscapeDataString(message.ConversationThreadId)}&limit=20");
            historyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", botToken);
            using JsonDocument history = await SendSlackApiJsonAsync(historyRequest, timeout.Token).ConfigureAwait(false);
            if (history.RootElement.TryGetProperty("ok", out JsonElement historyOk) && historyOk.GetBoolean()
                && history.RootElement.TryGetProperty("messages", out JsonElement messages) && messages.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in messages.EnumerateArray().TakeLast(20))
                    message.BoundedThreadHistory.Add(new ChannelInboundHistoryMessage
                    {
                        ProviderMessageId = item.TryGetProperty("ts", out JsonElement ts) ? ts.GetString() ?? string.Empty : string.Empty,
                        ExternalUserId = item.TryGetProperty("user", out JsonElement author) ? author.GetString() ?? string.Empty : string.Empty,
                        Text = item.TryGetProperty("text", out JsonElement text) ? (text.GetString() ?? string.Empty)[..Math.Min(text.GetString()?.Length ?? 0, 4_000)] : string.Empty,
                    });
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException) { }
    }

    internal Task EnrichSlackInboundContextForTestAsync(string attachmentId, ChannelInboundEventMessage message, CancellationToken cancellationToken)
        => EnrichSlackInboundContextAsync(attachmentId, message, cancellationToken);

    private static async Task AcknowledgeEnvelopeAsync(WebSocket webSocket, string envelopeId, CancellationToken cancellationToken, JsonObject? payload = null)
    {
        if (string.IsNullOrWhiteSpace(envelopeId)) return;
        JsonObject ack = new() { ["envelope_id"] = envelopeId };
        if (payload is not null) ack["payload"] = payload.DeepClone();
        byte[] ackBytes = Encoding.UTF8.GetBytes(ack.ToJsonString());
        await webSocket.SendAsync(ackBytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }

    internal static ChannelInboundEventMessage? TryCreateEventsApiMessageForTest(string attachmentId, string ownerUserId, JsonElement payload, JsonElement? settings = null)
        => TryCreateEventsApiMessage(attachmentId, ownerUserId, payload, settings);

    internal static ChannelInboundEventMessage? TryCreateSlashCommandMessageForTest(string attachmentId, string ownerUserId, JsonElement payload, string envelopeId)
        => TryCreateSlashCommandMessage(attachmentId, ownerUserId, payload, envelopeId);

    private static ChannelInboundEventMessage? TryCreateSlashCommandMessage(string attachmentId, string ownerUserId, JsonElement payload, string envelopeId)
    {
        string userId = payload.TryGetProperty("user_id", out JsonElement user) ? user.GetString() ?? string.Empty : string.Empty;
        string channelId = payload.TryGetProperty("channel_id", out JsonElement channel) ? channel.GetString() ?? string.Empty : string.Empty;
        string command = payload.TryGetProperty("command", out JsonElement commandElement) ? commandElement.GetString() ?? string.Empty : string.Empty;
        if (SlackSetupManifest.TryMapSlashCommand(command, out string normalizedCommand))
        {
            command = normalizedCommand;
        }
        string arguments = payload.TryGetProperty("text", out JsonElement textElement) ? textElement.GetString() ?? string.Empty : string.Empty;
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(command)) return null;
        ChannelInboundEventMessage result = new()
        {
            EventId = string.IsNullOrWhiteSpace(envelopeId) ? Guid.NewGuid().ToString("N") : envelopeId,
            ChannelType = "slack",
            RegistrationId = attachmentId,
            OwnerUserId = ownerUserId,
            ExternalUserId = userId,
            ConversationId = channelId,
            ConversationThreadId = string.Empty,
            EventType = "command",
            Text = string.IsNullOrWhiteSpace(arguments) ? command : $"{command} {arguments}",
            AcknowledgeToken = envelopeId,
            PayloadJson = "{}",
        };
        return result;
    }

    private static ChannelInboundEventMessage? TryCreateEventsApiMessage(string attachmentId, string ownerUserId, JsonElement payload, JsonElement? settings)
    {
        if (!payload.TryGetProperty("event", out JsonElement eventElement) || eventElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string eventType = eventElement.TryGetProperty("type", out JsonElement eventTypeElement)
            ? eventTypeElement.GetString() ?? string.Empty
            : string.Empty;
        if (!string.Equals(eventType, "message", StringComparison.Ordinal)
            || !eventElement.TryGetProperty("channel_type", out JsonElement channelType)
            || !string.Equals(channelType.GetString(), "im", StringComparison.Ordinal))
        {
            return null;
        }

        if (eventElement.TryGetProperty("subtype", out JsonElement subtypeElement)
            && !string.IsNullOrWhiteSpace(subtypeElement.GetString()))
        {
            return null;
        }

        if (IsBotAuthoredSlackMessage(eventElement, settings))
        {
            return null;
        }

        string externalUserId = eventElement.TryGetProperty("user", out JsonElement userElement)
            ? userElement.GetString() ?? string.Empty
            : string.Empty;
        string conversationId = eventElement.TryGetProperty("channel", out JsonElement channelElement)
            ? channelElement.GetString() ?? string.Empty
            : string.Empty;
        if (string.IsNullOrWhiteSpace(externalUserId) || string.IsNullOrWhiteSpace(conversationId))
        {
            return null;
        }

        string eventId = payload.TryGetProperty("event_id", out JsonElement eventIdElement)
            ? eventIdElement.GetString() ?? Guid.NewGuid().ToString("N")
            : Guid.NewGuid().ToString("N");
        string text = eventElement.TryGetProperty("text", out JsonElement textElement)
            ? textElement.GetString() ?? string.Empty
            : string.Empty;
        string threadId = eventElement.TryGetProperty("thread_ts", out JsonElement threadElement)
            ? threadElement.GetString() ?? string.Empty
            : string.Empty;

        ChannelInboundEventMessage result = new()
        {
            EventId = eventId,
            ChannelType = "slack",
            RegistrationId = attachmentId,
            OwnerUserId = ownerUserId,
            ExternalUserId = externalUserId,
            ConversationId = conversationId,
            ConversationThreadId = threadId,
            EventType = "message",
            Text = text,
            PayloadJson = "{}",
        };
        if (eventElement.TryGetProperty("files", out JsonElement files) && files.ValueKind == JsonValueKind.Array)
            foreach (JsonElement file in files.EnumerateArray().Take(10))
            {
                string id = file.TryGetProperty("id", out JsonElement fileId) ? fileId.GetString() ?? string.Empty : string.Empty;
                if (!string.IsNullOrWhiteSpace(id)) result.InboundMedia.Add(new InboundMediaDescriptorMessage { MediaId = id, ProviderUniqueId = id, MediaKind = "file", FileName = file.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? string.Empty : string.Empty, ContentType = file.TryGetProperty("mimetype", out JsonElement mime) ? mime.GetString() ?? string.Empty : string.Empty, SizeBytes = file.TryGetProperty("size", out JsonElement size) && size.TryGetInt64(out long bytes) ? bytes : 0 });
            }
        return result;
    }

    private static bool IsSlackDeliveryAllowed(JsonElement settings, SendOutboundMessageRequest request)
    {
        string? configuredDm = settings.ValueKind == JsonValueKind.Object
            && settings.TryGetProperty("slack_dm_channel_id", out JsonElement dm) ? dm.GetString() : null;
        if (string.IsNullOrWhiteSpace(configuredDm) || !configuredDm.StartsWith('D')) return false;
        JsonDocument payloadDocument;
        try { payloadDocument = JsonDocument.Parse(request.Content?.PayloadJson ?? "{}"); }
        catch (JsonException) { return false; }
        using (payloadDocument)
        {
            JsonElement payload = payloadDocument.RootElement;
            string destination = payload.TryGetProperty("conversationId", out JsonElement target)
                ? target.GetString() ?? string.Empty
                : payload.TryGetProperty("channel", out target) ? target.GetString() ?? string.Empty : string.Empty;
            if (request.Content?.ActionType == "send_ephemeral_message")
            {
                string ownerUserId = GetStringSetting(settings, "slack_owner_user_id");
                string targetUserId = payload.TryGetProperty("externalUserId", out JsonElement externalUser)
                    ? externalUser.GetString() ?? string.Empty
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(ownerUserId)
                    || !string.Equals(targetUserId, ownerUserId, StringComparison.Ordinal)) return false;
            }
            return string.Equals(destination, configuredDm, StringComparison.Ordinal);
        }
    }

    private static ChannelInboundEventMessage? TryCreateInteractiveMessage(string attachmentId, string ownerUserId, JsonElement payload, string? envelopeId)
    {
        if (!payload.TryGetProperty("type", out JsonElement payloadTypeElement))
        {
            return null;
        }

        if (string.Equals(payloadTypeElement.GetString(), "view_submission", StringComparison.Ordinal))
            return TryCreateModalSubmissionMessage(attachmentId, ownerUserId, payload, envelopeId);
        if (!string.Equals(payloadTypeElement.GetString(), "block_actions", StringComparison.Ordinal)) return null;

        string externalUserId = payload.TryGetProperty("user", out JsonElement userElement)
            && userElement.ValueKind == JsonValueKind.Object
            && userElement.TryGetProperty("id", out JsonElement userIdElement)
                ? userIdElement.GetString() ?? string.Empty
                : string.Empty;
        string conversationId = payload.TryGetProperty("channel", out JsonElement channelElement)
            && channelElement.ValueKind == JsonValueKind.Object
            && channelElement.TryGetProperty("id", out JsonElement channelIdElement)
                ? channelIdElement.GetString() ?? string.Empty
                : string.Empty;
        if (string.IsNullOrWhiteSpace(externalUserId) || string.IsNullOrWhiteSpace(conversationId))
        {
            return null;
        }

        string callbackData = string.Empty;
        if (payload.TryGetProperty("actions", out JsonElement actionsElement)
            && actionsElement.ValueKind == JsonValueKind.Array)
        {
            JsonElement firstAction = actionsElement.EnumerateArray().FirstOrDefault();
            if (firstAction.ValueKind == JsonValueKind.Object)
            {
                callbackData = firstAction.TryGetProperty("selected_option", out JsonElement selectedOption)
                    && selectedOption.ValueKind == JsonValueKind.Object
                    && selectedOption.TryGetProperty("value", out JsonElement selectedValue)
                        ? selectedValue.GetString() ?? string.Empty
                    : firstAction.TryGetProperty("value", out JsonElement valueElement)
                    ? valueElement.GetString() ?? string.Empty
                    : firstAction.TryGetProperty("action_id", out JsonElement actionIdElement)
                        ? actionIdElement.GetString() ?? string.Empty
                        : string.Empty;
            }
        }
        if (!IsSafeInboundCallback(callbackData, ChannelProviderCapabilityCatalog.Slack.MaximumCallbackTokenBytes)) return null;

        string sourceMessageTs = payload.TryGetProperty("container", out JsonElement containerElement)
            && containerElement.ValueKind == JsonValueKind.Object
            && containerElement.TryGetProperty("message_ts", out JsonElement messageTsElement)
                ? messageTsElement.GetString() ?? string.Empty
                : string.Empty;
        string threadId = payload.TryGetProperty("message", out JsonElement messageElement)
            && messageElement.ValueKind == JsonValueKind.Object
            && messageElement.TryGetProperty("thread_ts", out JsonElement threadTsElement)
                ? threadTsElement.GetString() ?? string.Empty
                : string.Empty;

        return new ChannelInboundEventMessage
        {
            EventId = string.IsNullOrWhiteSpace(envelopeId) ? Guid.NewGuid().ToString("N") : envelopeId,
            ChannelType = "slack",
            RegistrationId = attachmentId,
            OwnerUserId = ownerUserId,
            ExternalUserId = externalUserId,
            ConversationId = conversationId,
            ConversationThreadId = threadId,
            EventType = "callback",
            CallbackData = callbackData,
            AcknowledgeToken = envelopeId ?? "socket_mode",
            PayloadJson = "{}",
            SourceMessageId = TryNormalizeSlackTimestamp(sourceMessageTs),
            SourceTransportMessageId = sourceMessageTs,
        };
    }

    private static ChannelInboundEventMessage? TryCreateModalSubmissionMessage(string attachmentId, string ownerUserId, JsonElement payload, string? envelopeId)
    {
        string externalUserId = payload.TryGetProperty("user", out JsonElement user)
            && user.TryGetProperty("id", out JsonElement userId) ? userId.GetString() ?? string.Empty : string.Empty;
        if (string.IsNullOrWhiteSpace(externalUserId) || !payload.TryGetProperty("view", out JsonElement view)) return null;
        string callback = view.TryGetProperty("callback_id", out JsonElement callbackId) ? callbackId.GetString() ?? string.Empty : string.Empty;
        string conversationId = string.Empty;
        string threadId = string.Empty;
        if (view.TryGetProperty("private_metadata", out JsonElement metadataElement))
        {
            try
            {
                using JsonDocument metadata = JsonDocument.Parse(metadataElement.GetString() ?? "{}");
                conversationId = metadata.RootElement.TryGetProperty("conversationId", out JsonElement conversation) ? conversation.GetString() ?? string.Empty : string.Empty;
                threadId = metadata.RootElement.TryGetProperty("threadId", out JsonElement thread) ? thread.GetString() ?? string.Empty : string.Empty;
            }
            catch (JsonException) { return null; }
        }
        if (string.IsNullOrWhiteSpace(callback) || string.IsNullOrWhiteSpace(conversationId)) return null;
        return new ChannelInboundEventMessage
        {
            EventId = view.TryGetProperty("id", out JsonElement viewId) ? viewId.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N"),
            ChannelType = "slack",
            RegistrationId = attachmentId,
            OwnerUserId = ownerUserId,
            ExternalUserId = externalUserId,
            ConversationId = conversationId,
            ConversationThreadId = threadId,
            EventType = "callback",
            CallbackData = callback,
            AcknowledgeToken = envelopeId ?? "socket_mode",
            PayloadJson = BuildSanitizedModalInputPayload(view),
        };
    }

    internal static ChannelInboundEventMessage? TryCreateInteractiveMessageForTest(string attachmentId, string ownerUserId, JsonElement payload, string? envelopeId)
        => TryCreateInteractiveMessage(attachmentId, ownerUserId, payload, envelopeId);

    private static string BuildSanitizedModalInputPayload(JsonElement view)
    {
        JsonObject values = [];
        if (view.TryGetProperty("state", out JsonElement state)
            && state.TryGetProperty("values", out JsonElement blocks)
            && blocks.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty block in blocks.EnumerateObject())
            {
                if (block.Value.ValueKind != JsonValueKind.Object) continue;
                JsonElement input = block.Value.EnumerateObject().Select(static property => property.Value).FirstOrDefault();
                if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("value", out JsonElement value) && value.ValueKind == JsonValueKind.String)
                    values[block.Name] = value.GetString();
            }
        }
        return new JsonObject { ["inputValues"] = values }.ToJsonString();
    }

    private static bool IsSafeInboundCallback(string value, int maximumBytes) =>
        !string.IsNullOrWhiteSpace(value)
        && Encoding.UTF8.GetByteCount(value) <= maximumBytes
        && value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '~' or ':' or '-');

    private async Task<string?> SyncSlackIdentityAsync(string attachmentId, string botToken, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "https://slack.com/api/auth.test");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", botToken);

        using JsonDocument document = await SendSlackApiJsonAsync(request, cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("ok", out JsonElement okElement) || !okElement.GetBoolean())
        {
            return document.RootElement.TryGetProperty("error", out JsonElement errorElement)
                ? errorElement.GetString() ?? "auth_test_failed"
                : "auth_test_failed";
        }

        JsonObject patch = [];
        AddIdentityPatchValue(document.RootElement, patch, "user_id", "bot_user_id");
        AddIdentityPatchValue(document.RootElement, patch, "bot_id", "bot_id");
        AddIdentityPatchValue(document.RootElement, patch, "team_id", "team_id");
        if (!patch.TryGetPropertyValue("bot_id", out JsonNode? botIdNode)
            || string.IsNullOrWhiteSpace(botIdNode?.GetValue<string>())) return "provider_response_invalid";
        {
            string botId = botIdNode.GetValue<string>();
            using HttpRequestMessage botInfoRequest = new(HttpMethod.Get, $"https://slack.com/api/bots.info?bot={Uri.EscapeDataString(botId)}");
            botInfoRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", botToken);
            using JsonDocument botInfo = await SendSlackApiJsonAsync(botInfoRequest, cancellationToken).ConfigureAwait(false);
            if (!botInfo.RootElement.TryGetProperty("ok", out JsonElement botInfoOk) || !botInfoOk.GetBoolean())
                return botInfo.RootElement.TryGetProperty("error", out JsonElement botInfoError)
                    ? botInfoError.GetString() ?? "provider_response_invalid"
                    : "provider_response_invalid";
            if (botInfo.RootElement.TryGetProperty("bot", out JsonElement bot)
                && bot.ValueKind == JsonValueKind.Object)
                AddIdentityPatchValue(bot, patch, "app_id", "app_id");
        }
        if (!patch.TryGetPropertyValue("team_id", out JsonNode? teamNode)
            || string.IsNullOrWhiteSpace(teamNode?.GetValue<string>())
            || !patch.TryGetPropertyValue("app_id", out JsonNode? appNode)
            || string.IsNullOrWhiteSpace(appNode?.GetValue<string>())) return "provider_response_invalid";

        return attachmentStore.TryClaimSlackIdentity(attachmentId, patch, out string? conflict)
            ? null
            : conflict ?? "slack_workspace_in_use";
    }

    private async Task<SendOutboundMessageResponse> ExecuteSendTextAsync(string botToken, SendOutboundMessageRequest request, CancellationToken cancellationToken)
    {
        ChannelTextMessageAction? payload = string.IsNullOrWhiteSpace(request.Content?.PayloadJson)
            ? null
            : JsonSerializer.Deserialize(request.Content.PayloadJson, ChannelContractsJsonContext.Default.ChannelTextMessageAction);
        if (payload is null)
        {
            return InvalidPayloadResponse();
        }

        JsonObject body = new()
        {
            ["channel"] = payload.ConversationId,
            ["text"] = ApplySenderAttribution(payload.Text, payload.SenderDisplayName),
        };
        JsonArray textBlocks = ChannelSemanticRendering.CreateSlackTextBlocks(payload.Text, payload.SenderDisplayName);
        if (textBlocks.Count > 0)
        {
            body["blocks"] = textBlocks;
        }
        if (!string.IsNullOrWhiteSpace(payload.ConversationThreadId))
        {
            body["thread_ts"] = payload.ConversationThreadId;
        }

        return await SendSlackMessageAsync(botToken, "https://slack.com/api/chat.postMessage", body, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SendOutboundMessageResponse> ExecuteSendSemanticMessageAsync(string botToken, SendOutboundMessageRequest request, CancellationToken cancellationToken)
    {
        ChannelSemanticMessageAction? payload = string.IsNullOrWhiteSpace(request.Content?.PayloadJson) ? null : JsonSerializer.Deserialize(request.Content.PayloadJson, ChannelContractsJsonContext.Default.ChannelSemanticMessageAction);
        if (payload is null) return InvalidPayloadResponse();
        JsonObject body = ChannelSemanticRendering.RenderSlack(payload);
        body["client_msg_id"] = StableSlackClientMessageId(request.IdempotencyKey);
        return await SendSlackMessageAsync(botToken, "https://slack.com/api/chat.postMessage", body, cancellationToken).ConfigureAwait(false);
    }

    private static string StableSlackClientMessageId(string idempotencyKey)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey ?? string.Empty));
        return new Guid(hash.AsSpan(0, 16)).ToString("D");
    }

    private async Task<SendOutboundMessageResponse> ExecuteEditSemanticMessageAsync(string botToken, SendOutboundMessageRequest request, CancellationToken cancellationToken)
    {
        ChannelSemanticMessageAction? payload = JsonSerializer.Deserialize(request.Content.PayloadJson, ChannelContractsJsonContext.Default.ChannelSemanticMessageAction);
        if (payload is null || string.IsNullOrWhiteSpace(payload.TransportMessageId)) return InvalidPayloadResponse();
        JsonObject body = ChannelSemanticRendering.RenderSlack(payload);
        body.Remove("thread_ts");
        body["ts"] = payload.TransportMessageId;
        SendOutboundMessageResponse result = await SendSlackMessageAsync(botToken, "https://slack.com/api/chat.update", body, cancellationToken).ConfigureAwait(false);
        if (result.Detail == "slack_error:message_not_found") result.Detail = "message_not_found";
        if (result.Accepted) result.TransportMessageId = payload.TransportMessageId;
        return result;
    }

    private async Task<SendOutboundMessageResponse> ExecuteSendEphemeralMessageAsync(string botToken, SendOutboundMessageRequest request, CancellationToken cancellationToken)
    {
        ChannelEphemeralMessageAction? payload = string.IsNullOrWhiteSpace(request.Content?.PayloadJson)
            ? null
            : JsonSerializer.Deserialize(request.Content.PayloadJson, ChannelContractsJsonContext.Default.ChannelEphemeralMessageAction);
        if (payload is null || string.IsNullOrWhiteSpace(payload.ExternalUserId)) return InvalidPayloadResponse();
        ChannelSemanticMessageAction semantic = new(payload.ConversationId, payload.ConversationThreadId, payload.Document);
        JsonObject body = ChannelSemanticRendering.RenderSlack(semantic);
        body["user"] = payload.ExternalUserId;
        return await SendSlackMessageAsync(botToken, "https://slack.com/api/chat.postEphemeral", body, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SendOutboundMessageResponse> ExecuteOpenModalAsync(string botToken, SendOutboundMessageRequest request, CancellationToken cancellationToken)
    {
        ChannelModalAction? payload = string.IsNullOrWhiteSpace(request.Content?.PayloadJson)
            ? null
            : JsonSerializer.Deserialize(request.Content.PayloadJson, ChannelContractsJsonContext.Default.ChannelModalAction);
        if (payload is null || string.IsNullOrWhiteSpace(payload.TriggerId) || payload.Inputs.Count == 0 || payload.Inputs.Count > 10
            || payload.Inputs.Any(static input => input.Type != ChannelSemanticActionTypes.Input)) return InvalidPayloadResponse();
        JsonArray blocks = [];
        foreach (ChannelSemanticAction input in payload.Inputs)
        {
            blocks.Add((JsonNode?)new JsonObject
            {
                ["type"] = "input",
                ["block_id"] = input.ActionId,
                ["optional"] = !input.Required,
                ["label"] = new JsonObject { ["type"] = "plain_text", ["text"] = input.Label },
                ["element"] = new JsonObject { ["type"] = "plain_text_input", ["action_id"] = input.ActionId },
            });
        }
        string metadata = new JsonObject { ["conversationId"] = payload.ConversationId, ["threadId"] = payload.ConversationThreadId }.ToJsonString();
        JsonObject body = new()
        {
            ["trigger_id"] = payload.TriggerId,
            ["view"] = new JsonObject
            {
                ["type"] = "modal",
                ["callback_id"] = payload.CallbackToken,
                ["private_metadata"] = metadata,
                ["title"] = new JsonObject { ["type"] = "plain_text", ["text"] = payload.Title },
                ["submit"] = new JsonObject { ["type"] = "plain_text", ["text"] = payload.SubmitLabel },
                ["close"] = new JsonObject { ["type"] = "plain_text", ["text"] = payload.CloseLabel },
                ["blocks"] = blocks,
            },
        };
        return await SendSlackMessageAsync(botToken, "https://slack.com/api/views.open", body, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SendOutboundMessageResponse> ExecuteSendInlineKeyboardAsync(string botToken, SendOutboundMessageRequest request, CancellationToken cancellationToken)
    {
        ChannelInlineKeyboardAction? payload = string.IsNullOrWhiteSpace(request.Content?.PayloadJson)
            ? null
            : JsonSerializer.Deserialize(request.Content.PayloadJson, ChannelContractsJsonContext.Default.ChannelInlineKeyboardAction);
        if (payload is null)
        {
            return InvalidPayloadResponse();
        }

        JsonObject body = new()
        {
            ["channel"] = payload.ConversationId,
            ["text"] = payload.Text,
            ["blocks"] = BuildButtonBlocks(payload.Text, payload.Rows, null),
        };
        if (!string.IsNullOrWhiteSpace(payload.ConversationThreadId))
        {
            body["thread_ts"] = payload.ConversationThreadId;
        }

        return await SendSlackMessageAsync(botToken, "https://slack.com/api/chat.postMessage", body, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SendOutboundMessageResponse> ExecuteSendInteractionPromptAsync(string botToken, SendOutboundMessageRequest request, CancellationToken cancellationToken)
    {
        ChannelInteractionPromptAction? payload = string.IsNullOrWhiteSpace(request.Content?.PayloadJson)
            ? null
            : JsonSerializer.Deserialize(request.Content.PayloadJson, ChannelContractsJsonContext.Default.ChannelInteractionPromptAction);
        if (payload is null)
        {
            return InvalidPayloadResponse();
        }

        JsonObject body = new()
        {
            ["channel"] = payload.ConversationId,
            ["text"] = payload.Prompt,
            ["blocks"] = BuildButtonBlocks(payload.Prompt, payload.Rows, payload.SenderDisplayName),
        };
        if (!string.IsNullOrWhiteSpace(payload.ConversationThreadId))
        {
            body["thread_ts"] = payload.ConversationThreadId;
        }

        return await SendSlackMessageAsync(botToken, "https://slack.com/api/chat.postMessage", body, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SendOutboundMessageResponse> ExecuteEditMessageAsync(string botToken, SendOutboundMessageRequest request, CancellationToken cancellationToken)
    {
        ChannelMessageEditAction? payload = string.IsNullOrWhiteSpace(request.Content?.PayloadJson)
            ? null
            : JsonSerializer.Deserialize(request.Content.PayloadJson, ChannelContractsJsonContext.Default.ChannelMessageEditAction);
        if (payload is null)
        {
            return InvalidPayloadResponse();
        }

        JsonObject body = new()
        {
            ["channel"] = payload.ConversationId,
            ["ts"] = payload.MessageId,
            ["text"] = ApplySenderAttribution(payload.Text, payload.SenderDisplayName),
            ["blocks"] = payload.ClearInlineKeyboard ? new JsonArray() : null,
        };

        return await SendSlackMessageAsync(botToken, "https://slack.com/api/chat.update", body, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SendOutboundMessageResponse> ExecuteDeleteMessageAsync(string botToken, SendOutboundMessageRequest request, CancellationToken cancellationToken)
    {
        ChannelMessageDeleteAction? payload = string.IsNullOrWhiteSpace(request.Content?.PayloadJson)
            ? null
            : JsonSerializer.Deserialize(request.Content.PayloadJson, ChannelContractsJsonContext.Default.ChannelMessageDeleteAction);
        if (payload is null)
        {
            return InvalidPayloadResponse();
        }

        JsonObject body = new()
        {
            ["channel"] = payload.ConversationId,
            ["ts"] = payload.MessageId,
        };

        return await SendSlackMessageAsync(botToken, "https://slack.com/api/chat.delete", body, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SendOutboundMessageResponse> ExecuteAddReactionAsync(string botToken, SendOutboundMessageRequest request, CancellationToken cancellationToken)
    {
        ChannelReactionAction? payload = string.IsNullOrWhiteSpace(request.Content?.PayloadJson) ? null : JsonSerializer.Deserialize(request.Content.PayloadJson, ChannelContractsJsonContext.Default.ChannelReactionAction);
        if (payload is null) return InvalidPayloadResponse();
        JsonObject body = new() { ["channel"] = payload.ConversationId, ["timestamp"] = payload.MessageId, ["name"] = payload.Reaction.Trim(':') };
        return await SendSlackMessageAsync(botToken, "https://slack.com/api/reactions.add", body, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SendOutboundMessageResponse> ExecuteStreamMessageAsync(string attachmentId, string botToken, SendOutboundMessageRequest request, CancellationToken cancellationToken)
    {
        ChannelStreamMessageAction? payload = string.IsNullOrWhiteSpace(request.Content?.PayloadJson) ? null : JsonSerializer.Deserialize(request.Content.PayloadJson, ChannelContractsJsonContext.Default.ChannelStreamMessageAction);
        if (payload is null || payload.Sequence < 0) return InvalidPayloadResponse();
        ChannelStreamDelivery? existing = attachmentStore.GetStreamDelivery(attachmentId, payload.StreamId);
        if (existing is not null && payload.Sequence <= existing.LastSequence) return BuildNoOpResponse("stream_update_already_applied");
        string? transportMessageId = payload.TransportMessageId ?? existing?.TransportMessageId;
        JsonObject body = new() { ["channel"] = payload.ConversationId, ["text"] = ApplySenderAttribution(payload.AccumulatedText, payload.SenderDisplayName) };
        SendOutboundMessageResponse result;
        if (string.IsNullOrWhiteSpace(transportMessageId))
        {
            if (!string.IsNullOrWhiteSpace(payload.ConversationThreadId)) body["thread_ts"] = payload.ConversationThreadId;
            result = await SendSlackMessageAsync(botToken, "https://slack.com/api/chat.postMessage", body, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            body["ts"] = transportMessageId;
            result = await SendSlackMessageAsync(botToken, "https://slack.com/api/chat.update", body, cancellationToken).ConfigureAwait(false);
        }
        string resolvedMessageId = string.IsNullOrWhiteSpace(result.TransportMessageId) ? transportMessageId ?? string.Empty : result.TransportMessageId;
        if (result.Accepted && !string.IsNullOrWhiteSpace(resolvedMessageId)) attachmentStore.RecordStreamDelivery(attachmentId, payload.StreamId, resolvedMessageId, payload.Sequence, payload.IsFinal);
        return result;
    }

    private async Task<SendOutboundMessageResponse> ExecuteSendFileAsync(string attachmentId, string botToken, SendOutboundMessageRequest request, CancellationToken cancellationToken)
    {
        ChannelOutboundFileAction? payload = string.IsNullOrWhiteSpace(request.Content?.PayloadJson) ? null : JsonSerializer.Deserialize(request.Content.PayloadJson, ChannelContractsJsonContext.Default.ChannelOutboundFileAction);
        if (payload is null) return InvalidPayloadResponse();
        using ChannelOutboundAssetLease? lease = outboundAssets.OpenAvailable(payload.UploadId, attachmentId, payload.ConversationId);
        if (lease is null || lease.SizeBytes != payload.DeclaredSizeBytes || !string.Equals(lease.SafeFileName, payload.SafeFileName, StringComparison.Ordinal) || !string.Equals(lease.ContentType, payload.ContentType, StringComparison.OrdinalIgnoreCase)) return new SendOutboundMessageResponse { Accepted = false, DeliveryStatus = DeliveryStatus.Rejected, Detail = "outbound_asset_unavailable_or_mismatched" };
        using HttpRequestMessage getUrl = new(HttpMethod.Get, $"https://slack.com/api/files.getUploadURLExternal?filename={Uri.EscapeDataString(lease.SafeFileName)}&length={lease.SizeBytes}");
        getUrl.Headers.Authorization = new AuthenticationHeaderValue("Bearer", botToken);
        using JsonDocument uploadInfo = await SendSlackApiJsonAsync(getUrl, cancellationToken).ConfigureAwait(false);
        if (!uploadInfo.RootElement.TryGetProperty("ok", out JsonElement ok) || !ok.GetBoolean()) return new SendOutboundMessageResponse { Accepted = false, DeliveryStatus = DeliveryStatus.RetryableFailure, Detail = "slack_upload_url_failed" };
        string uploadUrl = uploadInfo.RootElement.GetProperty("upload_url").GetString() ?? throw new InvalidOperationException("slack_upload_url_missing");
        string fileId = uploadInfo.RootElement.GetProperty("file_id").GetString() ?? throw new InvalidOperationException("slack_file_id_missing");
        if (!Uri.TryCreate(uploadUrl, UriKind.Absolute, out Uri? uploadUri) || !SlackFileDownloadPolicy.IsSlackOwnedHttpsUri(uploadUri))
        {
            return new SendOutboundMessageResponse { Accepted = false, DeliveryStatus = DeliveryStatus.Rejected, Detail = "slack_upload_url_rejected" };
        }

        using MultipartFormDataContent upload = new();
        using StreamContent fileContent = new(lease.Content); fileContent.Headers.ContentType = new MediaTypeHeaderValue(lease.ContentType);
        upload.Add(fileContent, "file", lease.SafeFileName);
        using HttpResponseMessage uploadResponse = await httpClientFactory.CreateClient("slack-api").PostAsync(uploadUri, upload, cancellationToken).ConfigureAwait(false);
        uploadResponse.EnsureSuccessStatusCode();
        JsonObject completeBody = new() { ["files"] = new JsonArray((JsonNode?)new JsonObject { ["id"] = fileId, ["title"] = lease.SafeFileName }), ["channel_id"] = payload.ConversationId };
        if (!string.IsNullOrWhiteSpace(payload.Caption)) completeBody["initial_comment"] = payload.Caption;
        if (!string.IsNullOrWhiteSpace(payload.ConversationThreadId)) completeBody["thread_ts"] = payload.ConversationThreadId;
        using HttpRequestMessage complete = new(HttpMethod.Post, "https://slack.com/api/files.completeUploadExternal") { Content = new StringContent(completeBody.ToJsonString(), Encoding.UTF8, "application/json") };
        complete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", botToken);
        using JsonDocument completed = await SendSlackApiJsonAsync(complete, cancellationToken).ConfigureAwait(false);
        if (!completed.RootElement.TryGetProperty("ok", out JsonElement completedOk) || !completedOk.GetBoolean()) return new SendOutboundMessageResponse { Accepted = false, DeliveryStatus = DeliveryStatus.RetryableFailure, Detail = "slack_upload_complete_failed" };
        lease.Dispose();
        outboundAssets.MarkConsumed(payload.UploadId);
        return new SendOutboundMessageResponse { Accepted = true, DeliveryStatus = DeliveryStatus.Delivered, Detail = "file_delivered", TransportMessageId = fileId };
    }

    private async Task<SendOutboundMessageResponse> SendSlackMessageAsync(string botToken, string url, JsonObject body, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", botToken);
        request.Content = new StringContent(body.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json");

        using JsonDocument responseDocument = await SendSlackApiJsonAsync(request, cancellationToken).ConfigureAwait(false);
        if (!responseDocument.RootElement.TryGetProperty("ok", out JsonElement okElement) || !okElement.GetBoolean())
        {
            string error = responseDocument.RootElement.TryGetProperty("error", out JsonElement errorElement)
                ? errorElement.GetString() ?? "unknown_error"
                : "unknown_error";
            return new SendOutboundMessageResponse { Accepted = false, DeliveryStatus = DeliveryStatus.RetryableFailure, Detail = $"slack_error:{error}" };
        }

        string transportMessageId = responseDocument.RootElement.TryGetProperty("ts", out JsonElement tsElement)
            ? tsElement.GetString() ?? string.Empty
            : string.Empty;
        return new SendOutboundMessageResponse
        {
            Accepted = true,
            DeliveryStatus = DeliveryStatus.Delivered,
            Detail = "delivered",
            TransportMessageId = transportMessageId,
        };
    }

    private async Task<JsonDocument> SendSlackApiJsonAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpClient client = httpClientFactory.CreateClient("slack-api");
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(content);
    }

    private static JsonArray BuildButtonBlocks(string text, IReadOnlyList<ChannelInlineKeyboardRow> rows, string? senderDisplayName)
    {
        JsonArray blocks = [];
        blocks.Add((JsonNode?)new JsonObject
        {
            ["type"] = "section",
            ["text"] = new JsonObject
            {
                ["type"] = "mrkdwn",
                ["text"] = ApplySenderAttribution(text, senderDisplayName),
            },
        });

        foreach (ChannelInlineKeyboardRow row in rows)
        {
            JsonArray elements = [];
            foreach (ChannelInlineKeyboardButton button in row.Buttons)
            {
                elements.Add((JsonNode?)new JsonObject
                {
                    ["type"] = "button",
                    ["text"] = new JsonObject
                    {
                        ["type"] = "plain_text",
                        ["text"] = button.Label,
                        ["emoji"] = true,
                    },
                    ["action_id"] = $"btn_{Math.Abs(button.CallbackData.GetHashCode(StringComparison.Ordinal))}",
                    ["value"] = button.CallbackData,
                });
            }

            blocks.Add((JsonNode?)new JsonObject
            {
                ["type"] = "actions",
                ["elements"] = elements,
            });
        }

        return blocks;
    }

    private static string ApplySenderAttribution(string text, string? senderDisplayName)
        => string.IsNullOrWhiteSpace(senderDisplayName)
            ? text
            : $"*{senderDisplayName}*\n{text}";

    private static bool IsBotAuthoredSlackMessage(JsonElement eventElement, JsonElement? settings)
    {
        if (eventElement.TryGetProperty("bot_id", out JsonElement botIdElement)
            && !string.IsNullOrWhiteSpace(botIdElement.GetString()))
        {
            return true;
        }

        if (eventElement.TryGetProperty("bot_profile", out JsonElement botProfileElement)
            && botProfileElement.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        string configuredAppId = GetStringSetting(settings, "app_id");
        if (!string.IsNullOrWhiteSpace(configuredAppId)
            && eventElement.TryGetProperty("app_id", out JsonElement appIdElement)
            && string.Equals(appIdElement.GetString(), configuredAppId, StringComparison.Ordinal))
        {
            return true;
        }

        string messageUserId = eventElement.TryGetProperty("user", out JsonElement userElement)
            ? userElement.GetString() ?? string.Empty
            : string.Empty;
        string configuredBotUserId = GetStringSetting(settings, "bot_user_id");
        if (!string.IsNullOrWhiteSpace(configuredBotUserId)
            && string.Equals(messageUserId, configuredBotUserId, StringComparison.Ordinal))
        {
            return true;
        }

        string configuredBotId = GetStringSetting(settings, "bot_id");
        if (!string.IsNullOrWhiteSpace(configuredBotId)
            && eventElement.TryGetProperty("bot_id", out JsonElement configuredBotIdElement)
            && string.Equals(configuredBotIdElement.GetString(), configuredBotId, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private bool NeedsSlackIdentitySync(string attachmentId, JsonElement? settings, string botToken)
    {
        if (identitySyncFailures.TryGetValue(attachmentId, out IdentitySyncFailure? failure)
            && !string.Equals(failure.TokenFingerprint, TokenFingerprint(botToken), StringComparison.Ordinal))
            identitySyncFailures.TryRemove(new KeyValuePair<string, IdentitySyncFailure>(attachmentId, failure));
        return string.IsNullOrWhiteSpace(GetStringSetting(settings, "bot_user_id"));
    }

    private bool ShouldDeferIdentitySync(string attachmentId, string botToken, DateTimeOffset now)
    {
        if (!identitySyncFailures.TryGetValue(attachmentId, out IdentitySyncFailure? failure)) return false;
        if (!string.Equals(failure.TokenFingerprint, TokenFingerprint(botToken), StringComparison.Ordinal))
        {
            identitySyncFailures.TryRemove(new KeyValuePair<string, IdentitySyncFailure>(attachmentId, failure));
            return false;
        }
        return failure.Permanent || failure.RetryAfter > now;
    }

    private void RecordIdentitySyncFailure(string attachmentId, string botToken, string? detail)
        => RecordIdentitySyncFailure(attachmentId, botToken, detail, DateTimeOffset.UtcNow);

    private void RecordIdentitySyncFailure(string attachmentId, string botToken, string? detail, DateTimeOffset now)
    {
        string fingerprint = TokenFingerprint(botToken);
        bool permanent = detail is "slack_workspace_in_use" or "invalid_auth" or "token_revoked";
        identitySyncFailures.AddOrUpdate(
            attachmentId,
            _ => CreateIdentitySyncFailure(fingerprint, detail, permanent, 1, now),
            (_, previous) =>
            {
                bool sameToken = string.Equals(previous.TokenFingerprint, fingerprint, StringComparison.Ordinal);
                int attempts = sameToken ? previous.Attempts + 1 : 1;
                bool sticky = permanent || sameToken && previous.Permanent;
                string? stickyDetail = sameToken && previous.Permanent ? previous.Detail : detail;
                return CreateIdentitySyncFailure(fingerprint, stickyDetail, sticky, attempts, now);
            });
    }

    private IdentitySyncFailure CreateIdentitySyncFailure(string fingerprint, string? detail, bool permanent, int attempts, DateTimeOffset now)
    {
        TimeSpan retryDelay = getIdentitySyncRetryDelay(attempts);
        return new IdentitySyncFailure(fingerprint, detail, permanent, now + retryDelay, attempts);
    }

    internal static TimeSpan GetIdentitySyncRetryDelay(int attempts)
        => TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Clamp(attempts - 1, 0, 6))));

    internal void RecordIdentitySyncFailureForTest(string attachmentId, string token, string? detail)
        => RecordIdentitySyncFailure(attachmentId, token, detail);

    internal void RecordIdentitySyncFailureForTest(string attachmentId, string token, string? detail, DateTimeOffset now)
        => RecordIdentitySyncFailure(attachmentId, token, detail, now);

    internal bool ShouldDeferIdentitySyncForTest(string attachmentId, string token, DateTimeOffset now)
        => ShouldDeferIdentitySync(attachmentId, token, now);

    internal DateTimeOffset? GetIdentitySyncRetryAfterForTest(string attachmentId)
        => identitySyncFailures.TryGetValue(attachmentId, out IdentitySyncFailure? failure) ? failure.RetryAfter : null;

    private static string TokenFingerprint(string token)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private sealed class ActiveSlackConnection(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task? Task { get; set; }
    }

    private sealed record IdentitySyncFailure(string TokenFingerprint, string? Detail, bool Permanent, DateTimeOffset RetryAfter, int Attempts);

    private static void AddIdentityPatchValue(JsonElement root, JsonObject patch, string sourceProperty, string targetProperty)
    {
        if (root.TryGetProperty(sourceProperty, out JsonElement element)
            && element.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(element.GetString()))
        {
            patch[targetProperty] = element.GetString()!;
        }
    }

    private static SendOutboundMessageResponse InvalidPayloadResponse()
        => new() { Accepted = false, DeliveryStatus = DeliveryStatus.Rejected, Detail = "invalid_payload" };

    private static SendOutboundMessageResponse BuildNoOpResponse(string detail)
        => new() { Accepted = true, DeliveryStatus = DeliveryStatus.Delivered, Detail = detail };

    private static long TryNormalizeSlackTimestamp(string timestamp)
    {
        if (string.IsNullOrWhiteSpace(timestamp))
        {
            return 0;
        }

        string digits = timestamp.Replace(".", string.Empty, StringComparison.Ordinal);
        return long.TryParse(digits, out long parsed) ? parsed : 0;
    }

    private static string GetStringSetting(JsonElement? settings, string propertyName)
        => settings.HasValue
            && settings.Value.ValueKind == JsonValueKind.Object
            && settings.Value.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()?.Trim() ?? string.Empty
                : string.Empty;
}
