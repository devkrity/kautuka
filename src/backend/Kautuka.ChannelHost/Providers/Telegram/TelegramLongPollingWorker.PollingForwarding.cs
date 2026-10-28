using Kautuka.Protos;

namespace Kautuka.ChannelHost.Providers.Telegram;

internal sealed partial class TelegramLongPollingWorker
{
    private async Task<int> PollRegistrationAsync(PollingRegistration registration, CancellationToken cancellationToken)
    {
        HttpClient telegramClient = httpClientFactory.CreateClient("telegram-api");

        long offset = ReadPollingOffset(registration);

        var getUpdatesBody = new TelegramGetUpdatesRequest(
            registration.PollTimeoutSeconds,
            offset,
            ["message", "callback_query"]);

        long pollStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        LogPollRequestStarted(logger, registration.RegistrationId, offset, registration.PollTimeoutSeconds);

        HttpResponseMessage pollResponse;
        try
        {
            pollResponse = await telegramClient.PostAsJsonAsync(
                $"https://api.telegram.org/bot{registration.BotToken}/getUpdates",
                getUpdatesBody,
                TelegramJsonContext.Default.TelegramGetUpdatesRequest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                double elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(pollStarted).TotalMilliseconds;
                LogPollRequestCompleted(logger, registration.RegistrationId, 0, elapsedMs);
            }
            throw;
        }
        using HttpResponseMessage response = pollResponse;

        if (logger.IsEnabled(LogLevel.Information))
        {
            double elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(pollStarted).TotalMilliseconds;
            LogPollRequestCompleted(logger, registration.RegistrationId, (int)response.StatusCode, elapsedMs);
        }

        if (!response.IsSuccessStatusCode)
        {
            LogTelegramGetUpdatesFailed(logger, registration.RegistrationId, response.StatusCode);
            string body = await SafeReadResponseBodyAsync(response, cancellationToken).ConfigureAwait(false);
            if (TryGetTelegramRetryAfter(response, body, out TimeSpan retryAfter))
                return Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
            return (int)response.StatusCode is 401 or 403 or 404 ? 300 : 3;
        }

        Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using System.Text.Json.JsonDocument document = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object
                || !document.RootElement.TryGetProperty("ok", out System.Text.Json.JsonElement okElement)
                || okElement.ValueKind != System.Text.Json.JsonValueKind.True)
            {
                return 3;
            }

            if (!document.RootElement.TryGetProperty("result", out System.Text.Json.JsonElement resultElement)
                || resultElement.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return 3;
            }

            bool forwarded = await ForwardUpdatesAsync(
                registration,
                resultElement,
                offset,
                cancellationToken).ConfigureAwait(false);
            return forwarded ? 0 : 3;
        }
    }

    private long ReadPollingOffset(PollingRegistration registration)
    {
        string? cursor = ingressStore.GetCursor(ProviderKey, registration.CursorScope);
        if (cursor is null && string.Equals(GetStringSetting(registration.Settings, "telegram_bot_id"), registration.BotIdentity, StringComparison.Ordinal))
        {
            // Adopt an attachment-scoped legacy cursor only when its recorded bot identity matches.
            cursor = ingressStore.GetCursor(ProviderKey, registration.RegistrationId);
            if (cursor is not null) ingressStore.SetCursor(ProviderKey, registration.CursorScope, cursor);
        }
        return long.TryParse(cursor, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long offset) ? offset : 0;
    }

    private async Task<bool> ForwardUpdatesAsync(
        PollingRegistration registration,
        System.Text.Json.JsonElement resultElement,
        long startingOffset,
        CancellationToken cancellationToken)
    {
        long nextOffset = startingOffset;
        foreach (System.Text.Json.JsonElement updateElement in resultElement.EnumerateArray())
        {
            ForwardUpdateOutcome outcome = await ForwardUpdateAsync(
                registration,
                updateElement,
                nextOffset,
                cancellationToken).ConfigureAwait(false);

            if (outcome.ShouldAdvanceOffset && outcome.UpdateId >= 0)
            {
                nextOffset = Math.Max(nextOffset, outcome.UpdateId + 1);
                if (!outcome.CursorPersisted)
                    ingressStore.SetCursor(ProviderKey, registration.CursorScope, nextOffset.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (outcome.ShouldStopPolling)
            {
                return false;
            }
        }

        return true;
    }

    private async Task<ForwardUpdateOutcome> ForwardUpdateAsync(
        PollingRegistration registration,
        System.Text.Json.JsonElement updateElement,
        long cursorFloor,
        CancellationToken cancellationToken)
    {
        long updateId = TryGetUpdateId(updateElement);
        if (updateId < 0 || updateId == long.MaxValue)
        {
            LogForwardTelegramUpdateFailed(logger, registration.RegistrationId, "invalid_update_id", "Provider update omitted a valid update id.");
            return new ForwardUpdateOutcome(updateId, ShouldAdvanceOffset: false, ShouldStopPolling: true);
        }
        if (updateId < cursorFloor)
            return new ForwardUpdateOutcome(updateId, ShouldAdvanceOffset: false, ShouldStopPolling: false);

        // Topic-based routing has been retired. Never reinterpret a message from an old
        // topic as a prompt to whichever agent happens to be selected in the main chat.
        if (await TryHandleRetiredTopicMessageAsync(registration, updateElement, cancellationToken).ConfigureAwait(false))
        {
            return new ForwardUpdateOutcome(updateId, ShouldAdvanceOffset: true, ShouldStopPolling: false);
        }

        ChannelInboundEvent? inboundEvent = CreateInboundEvent(registration, updateElement);
        if (inboundEvent is null)
        {
            return new ForwardUpdateOutcome(updateId, ShouldAdvanceOffset: true, ShouldStopPolling: false);
        }

        if (string.Equals(inboundEvent.EventType, "callback", StringComparison.OrdinalIgnoreCase))
            LogCallbackReceived(logger, registration.RegistrationId, inboundEvent.EventId);

        if (string.Equals(inboundEvent.EventType, "message", StringComparison.OrdinalIgnoreCase)
            && (inboundEvent.InboundMedia?.Count ?? 0) > 0)
        {
            InboundMediaValidationResult validation = ValidateInboundMediaDescriptors(inboundEvent.InboundMedia);
            if (validation.HasRejections)
            {
                string rejectionText = BuildInboundMediaRejectionText(validation);
                if (!string.IsNullOrWhiteSpace(rejectionText))
                {
                    _ = await SendTelegramRequestAsync(
                        registration,
                        "sendMessage",
                        BuildTextPayload(inboundEvent.ConversationId, inboundEvent.ConversationThreadId, rejectionText),
                        cancellationToken).ConfigureAwait(false);
                }
            }

            if (validation.AcceptedMedia.Count == 0)
            {
                return new ForwardUpdateOutcome(updateId, ShouldAdvanceOffset: true, ShouldStopPolling: false);
            }

            inboundEvent = inboundEvent with { InboundMedia = validation.AcceptedMedia };
        }

        ChannelInboundEventMessage proto = new()
        {
            EventId = inboundEvent.EventId,
            ChannelType = inboundEvent.ChannelType,
            RegistrationId = inboundEvent.RegistrationId,
            OwnerUserId = inboundEvent.OwnerUserId ?? string.Empty,
            ExternalUserId = inboundEvent.ExternalUserId,
            ConversationId = inboundEvent.ConversationId,
            ConversationThreadId = inboundEvent.ConversationThreadId ?? string.Empty,
            EventType = inboundEvent.EventType,
            Text = inboundEvent.Text ?? string.Empty,
            CallbackData = inboundEvent.CallbackData ?? string.Empty,
            AcknowledgeToken = inboundEvent.AcknowledgeToken ?? string.Empty,
            SourceMessageId = inboundEvent.SourceMessageId ?? 0,
            SourceTransportMessageId = inboundEvent.SourceMessageId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            ReplySourceTransportMessageId = inboundEvent.ReplySourceTransportMessageId ?? string.Empty,
            PayloadJson = inboundEvent.Payload.GetRawText(),
        };
        proto.InboundMedia.AddRange((inboundEvent.InboundMedia ?? []).Select(static media => new InboundMediaDescriptorMessage
        {
            MediaId = media.MediaId,
            MediaKind = media.MediaKind,
            FileName = media.FileName ?? string.Empty,
            ContentType = media.ContentType ?? string.Empty,
            SizeBytes = media.SizeBytes ?? 0,
            ProviderUniqueId = media.ProviderUniqueId ?? string.Empty,
        }));

        try
        {
            // Write to the ingress channel; Gateway subscribes via StreamIngressAsync.
            resourceGovernor?.EnforceProviderIngress(ProviderKey, registration.RegistrationId);
            _ = ingressStore.Enqueue(proto, Math.Max(cursorFloor, updateId + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), registration.CursorScope);

            return new ForwardUpdateOutcome(updateId, ShouldAdvanceOffset: true, ShouldStopPolling: false, CursorPersisted: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ForwardUpdateOutcome(updateId, ShouldAdvanceOffset: false, ShouldStopPolling: true);
        }
        catch (System.Threading.Channels.ChannelClosedException ex)
        {
            LogForwardTelegramUpdateFailed(logger, registration.RegistrationId, "ingress_queue_error", ex.Message);
            return new ForwardUpdateOutcome(updateId, ShouldAdvanceOffset: false, ShouldStopPolling: true);
        }
        catch (InvalidOperationException ex)
        {
            LogForwardTelegramUpdateFailed(logger, registration.RegistrationId, "ingress_queue_error", ex.Message);
            return new ForwardUpdateOutcome(updateId, ShouldAdvanceOffset: false, ShouldStopPolling: true);
        }
    }

    private async Task<bool> TryHandleRetiredTopicMessageAsync(
        PollingRegistration registration,
        System.Text.Json.JsonElement update,
        CancellationToken cancellationToken)
    {
        if (!update.TryGetProperty("message", out System.Text.Json.JsonElement message)
            || message.ValueKind != System.Text.Json.JsonValueKind.Object)
            return false;

        if (!message.TryGetProperty("chat", out System.Text.Json.JsonElement chat)
            || !chat.TryGetProperty("id", out System.Text.Json.JsonElement chatIdElement)
            || !message.TryGetProperty("message_thread_id", out System.Text.Json.JsonElement threadIdElement))
        {
            return false;
        }

        string conversationId = chatIdElement.ToString();
        if (string.IsNullOrWhiteSpace(threadIdElement.ToString())
            || !string.Equals(GetStringSetting(registration.Settings, "channel_device_id"), conversationId, StringComparison.Ordinal))
        {
            return false;
        }

        const string notice = "Topics are no longer used for Kautuka. Return to the main chat and choose the agent and session with the keyboard before sending a prompt.";
        OutboxExecutionResult result = await SendTelegramRequestAsync(registration, "sendMessage",
            BuildTextPayload(conversationId, null, notice), cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            LogForwardTelegramUpdateFailed(logger, registration.RegistrationId, "retired_topic_notice_failed", result.Error ?? "unknown_error");
        }

        // Suppress the old topic prompt even if Telegram could not deliver the notice.
        return true;
    }

    internal async Task<bool> TryHandleRetiredTopicMessageForTestAsync(
        string registrationId,
        System.Text.Json.JsonElement update,
        CancellationToken cancellationToken)
    {
        if (!attachmentStore.TryGetTelegramRegistration(registrationId, out TelegramAttachmentRegistration? registration)
            || registration is null)
        {
            return false;
        }

        return await TryHandleRetiredTopicMessageAsync(new PollingRegistration(registration.AttachmentId, registration.OwnerUserId,
            registration.BotToken, registration.PollTimeoutSeconds, registration.Settings), update, cancellationToken).ConfigureAwait(false);
    }

    private static long TryGetUpdateId(System.Text.Json.JsonElement updateElement)
        => updateElement.TryGetProperty("update_id", out System.Text.Json.JsonElement updateIdElement)
            && updateIdElement.TryGetInt64(out long parsedId)
            ? parsedId
            : -1;

}
