using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kautuka.ChannelHost.Providers.Telegram;

internal sealed partial class TelegramLongPollingWorker
{
    private async Task<OutboxExecutionResult> ExecuteProvisionRegistrationAsync(PollingRegistration registration, string payloadJson, CancellationToken cancellationToken)
    {
        SemaphoreSlim provisioningGate = provisioningGates[(int)((uint)StringComparer.Ordinal.GetHashCode(registration.RegistrationId) % (uint)provisioningGates.Length)];
        await provisioningGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (attachmentStore.TryGetTelegramRegistrationForProvisioning(registration.RegistrationId, out TelegramAttachmentRegistration? latest) && latest is not null)
                registration = new PollingRegistration(latest.AttachmentId, latest.OwnerUserId, latest.BotToken, latest.PollTimeoutSeconds, latest.Settings);
            ChannelRegistrationProvisioningAction? payload = JsonSerializer.Deserialize(payloadJson, ChannelContractsJsonContext.Default.ChannelRegistrationProvisioningAction);
            if (payload is null)
            {
                return new OutboxExecutionResult(false, "invalid_provisioning_payload", null);
            }

            if (string.IsNullOrWhiteSpace(payload.ConversationId))
            {
                return new OutboxExecutionResult(false, "telegram_channel_device_id_required", null);
            }

            JsonObject commandScope = new()
            {
                ["type"] = "chat",
                ["chat_id"] = payload.ConversationId,
            };

            OutboxExecutionResult commandsResult = await SendTelegramRequestAsync(registration, "setMyCommands", new JsonObject
            {
                ["scope"] = commandScope,
                ["commands"] = new JsonArray([.. payload.Commands.Select(command => (JsonNode?)new JsonObject
            {
                ["command"] = command.Command,
                ["description"] = command.Description,
            })]),
            }, cancellationToken).ConfigureAwait(false);
            if (!commandsResult.Success)
            {
                return commandsResult;
            }

            // setMyDescription, setMyShortDescription, and setMyName share Telegram's undocumented
            // bot-profile mutation rate limit (separate from the send-message limit). Treat all three
            // as best-effort so a 429 on any of them never blocks bot provisioning.
            OutboxExecutionResult descriptionResult = await SendTelegramRequestAsync(registration, "setMyDescription", new JsonObject
            {
                ["description"] = payload.Description,
            }, cancellationToken, treatUnrecoverableAsSuccess: true, ignoreRateLimit: true).ConfigureAwait(false);
            if (!descriptionResult.Success)
            {
                LogProvisioningStepSkipped(logger, registration.RegistrationId, "setMyDescription", descriptionResult.Error ?? "rate_limited");
            }

            OutboxExecutionResult shortDescriptionResult = await SendTelegramRequestAsync(registration, "setMyShortDescription", new JsonObject
            {
                ["short_description"] = payload.ShortDescription,
            }, cancellationToken, treatUnrecoverableAsSuccess: true, ignoreRateLimit: true).ConfigureAwait(false);
            if (!shortDescriptionResult.Success)
            {
                LogProvisioningStepSkipped(logger, registration.RegistrationId, "setMyShortDescription", shortDescriptionResult.Error ?? "rate_limited");
            }

            OutboxExecutionResult nameResult = await SendTelegramRequestAsync(registration, "setMyName", new JsonObject
            {
                ["name"] = payload.BotName,
            }, cancellationToken, treatUnrecoverableAsSuccess: true, ignoreRateLimit: true).ConfigureAwait(false);
            if (!nameResult.Success)
            {
                LogProvisioningStepSkipped(logger, registration.RegistrationId, "setMyName", nameResult.Error ?? "rate_limited");
            }

            (JsonObject threadMap, JsonObject threadLabelMap, JsonObject retiredTopicMap, OutboxExecutionResult topicResult) =
                await RetireManagedTopicsAsync(registration, payload.ConversationId, cancellationToken).ConfigureAwait(false);
            if (!topicResult.Success)
            {
                LogTopicProvisioningFailed(logger, registration.RegistrationId, topicResult.Error ?? "unknown_error");
                return topicResult;
            }

            JsonObject patch = new()
            {
                ["target_threads"] = threadMap,
                ["target_thread_labels"] = threadLabelMap,
                ["retired_target_threads"] = retiredTopicMap,
            };

            attachmentStore.ApplySettingsPatch(registration.RegistrationId, patch);
            return new OutboxExecutionResult(true, null, null);
        }
        finally
        {
            provisioningGate.Release();
        }
    }

    private async Task<(JsonObject ThreadMap, JsonObject ThreadLabelMap, JsonObject RetiredTopicMap, OutboxExecutionResult Result)> RetireManagedTopicsAsync(
        PollingRegistration registration,
        string conversationId,
        CancellationToken cancellationToken)
    {
        JsonObject threadMap = CloneSettingsObject(registration.Settings, "target_threads");
        JsonObject labels = CloneSettingsObject(registration.Settings, "target_thread_labels");
        JsonObject retired = CloneSettingsObject(registration.Settings, "retired_target_threads");
        KeyValuePair<string, string>[] knownTopics = threadMap
            .Concat(retired)
            .Select(entry => new KeyValuePair<string, string>(entry.Key, entry.Value?.ToString() ?? string.Empty))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Value))
            .DistinctBy(entry => entry.Value, StringComparer.Ordinal)
            .ToArray();

        JsonObject retryRetired = new();
        foreach (KeyValuePair<string, string> topic in knownTopics)
        {
            OutboxExecutionResult result = await DeleteForumTopicAsync(registration, conversationId, topic.Value, cancellationToken).ConfigureAwait(false);
            if (result.Success) continue;

            LogTopicProvisioningFailed(logger, registration.RegistrationId, result.Error ?? "legacy_topic_cleanup_failed");
            retryRetired[topic.Key] = topic.Value;
        }

        threadMap.Clear();
        labels.Clear();
        // Keep failed deletions as cleanup-only IDs so a later provisioning pass can retry.
        // They are never treated as active agent destinations by Gateway.
        retired = retryRetired;
        return (threadMap, labels, retired, new OutboxExecutionResult(true, null, null));
    }

    [LoggerMessage(
        EventId = 18,
        Level = LogLevel.Warning,
        Message = "Telegram topic provisioning failed for registration {RegistrationId}. error={Error}")]
    private static partial void LogTopicProvisioningFailed(ILogger logger, string registrationId, string error);
}
