using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kautuka.ChannelHost.Providers.Telegram;

internal sealed partial class TelegramLongPollingWorker
{
    private async Task<TelegramMessageResult> SendTelegramMessageForResultAsync(
        PollingRegistration registration,
        JsonObject payload,
        CancellationToken cancellationToken,
        string method = "sendMessage",
        string messageIdProperty = "message_id")
    {
        HttpClient telegramClient = httpClientFactory.CreateClient("telegram-api");
        try
        {
            await WaitForTelegramSendWindowAsync(payload, cancellationToken).ConfigureAwait(false);

            using HttpContent requestContent = JsonContent.Create(payload, TelegramJsonContext.Default.JsonObject);
            using HttpResponseMessage response = await telegramClient.PostAsync(
                new Uri($"https://api.telegram.org/bot{registration.BotToken}/{method}"), requestContent, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                string body = await SafeReadResponseBodyAsync(response, cancellationToken).ConfigureAwait(false);
                // Never treat BadRequest/Forbidden as delivered for sendMessage: that marks
                // the outbox complete while the chat stays empty (malformed thread id, etc.).
                return new TelegramMessageResult(null, CreateFailureResult(response, body, treatUnrecoverableAsSuccess: false));
            }

            using JsonDocument responseDocument = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!responseDocument.RootElement.TryGetProperty("ok", out JsonElement okElement) || okElement.ValueKind != JsonValueKind.True)
            {
                return new TelegramMessageResult(null, new OutboxExecutionResult(false, "telegram_not_ok", 30));
            }

            string? messageId = responseDocument.RootElement.TryGetProperty("result", out JsonElement result)
                && result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty(messageIdProperty, out JsonElement messageIdElement)
                ? messageIdElement.ToString()
                : null;
            if (string.IsNullOrWhiteSpace(messageId))
                return new TelegramMessageResult(null, new OutboxExecutionResult(false, "telegram_message_id_missing", 30));
            return new TelegramMessageResult(messageId, new OutboxExecutionResult(true, null, null));
        }
        catch (HttpRequestException ex)
        {
            LogPollingFailed(logger, ex, registration.RegistrationId);
            return new TelegramMessageResult(null, new OutboxExecutionResult(false, "http_request_failed", 30));
        }
        catch (JsonException ex)
        {
            LogPollingFailed(logger, ex, registration.RegistrationId);
            return new TelegramMessageResult(null, new OutboxExecutionResult(false, "invalid_json", 30));
        }
    }

    private async Task<OutboxExecutionResult> SendTelegramRequestAsync(
        PollingRegistration registration,
        string method,
        JsonObject payload,
        CancellationToken cancellationToken,
        bool treatUnrecoverableAsSuccess = true,
        bool ignoreRateLimit = false)
    {
        HttpClient telegramClient = httpClientFactory.CreateClient("telegram-api");
        try
        {
            await WaitForTelegramSendWindowAsync(payload, cancellationToken).ConfigureAwait(false);

            using HttpResponseMessage response = await telegramClient.PostAsJsonAsync(
                $"https://api.telegram.org/bot{registration.BotToken}/{method}",
                payload,
                TelegramJsonContext.Default.JsonObject,
                cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return new OutboxExecutionResult(true, null, null);
            }

            string body = await SafeReadResponseBodyAsync(response, cancellationToken).ConfigureAwait(false);
            return CreateFailureResult(response, body, treatUnrecoverableAsSuccess, ignoreRateLimit);
        }
        catch (HttpRequestException ex)
        {
            LogPollingFailed(logger, ex, registration.RegistrationId);
            return new OutboxExecutionResult(false, "http_request_failed", 30);
        }
        catch (JsonException ex)
        {
            LogPollingFailed(logger, ex, registration.RegistrationId);
            return new OutboxExecutionResult(false, "invalid_json", 30);
        }
    }

    private static OutboxExecutionResult CreateFailureResult(HttpResponseMessage response, string responseBody, bool treatUnrecoverableAsSuccess = true, bool ignoreRateLimit = false)
    {
        if (TryGetTelegramRetryAfter(response, responseBody, out TimeSpan retryAfter))
        {
            // When the caller opts in to ignoring rate limits (e.g. best-effort profile mutations),
            // return success so a 429 doesn't abort the rest of the provisioning flow.
            if (ignoreRateLimit)
            {
                return new OutboxExecutionResult(false, responseBody, 0);
            }
            return new OutboxExecutionResult(false, responseBody, Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)));
        }

        if (treatUnrecoverableAsSuccess && IsUnrecoverableTelegramStatus(response.StatusCode))
        {
            return new OutboxExecutionResult(true, null, null);
        }

        return new OutboxExecutionResult(false, responseBody, 30);
    }

    private static bool TryGetTelegramRetryAfter(HttpResponseMessage response, string? responseBody, out TimeSpan retryAfter)
    {
        if (response.Headers.RetryAfter?.Delta is TimeSpan delta && delta > TimeSpan.Zero)
        {
            retryAfter = delta;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(responseBody))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(responseBody);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("parameters", out JsonElement parameters)
                    && parameters.ValueKind == JsonValueKind.Object
                    && parameters.TryGetProperty("retry_after", out JsonElement retryAfterElement)
                    && retryAfterElement.ValueKind == JsonValueKind.Number
                    && retryAfterElement.TryGetInt32(out int seconds)
                    && seconds > 0)
                {
                    retryAfter = TimeSpan.FromSeconds(seconds);
                    return true;
                }
            }
            catch (JsonException)
            {
                // Ignore malformed retry-after bodies and use the default retry path.
            }
        }

        retryAfter = default;
        return false;
    }

    private async Task WaitForTelegramSendWindowAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        string? chatId = payload[chatIdProperty]?.ToString();

        while (true)
        {
            TimeSpan delay;
            lock (telegramRateLimitSync)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                DateTimeOffset nextAllowed = nextGlobalSendAt;

                if (!string.IsNullOrWhiteSpace(chatId)
                    && nextPerChatSendAt.TryGetValue(chatId, out DateTimeOffset nextChatSendAt)
                    && nextChatSendAt > nextAllowed)
                {
                    nextAllowed = nextChatSendAt;
                }

                if (nextAllowed <= now)
                {
                    nextGlobalSendAt = now + telegramGlobalMinInterval;
                    if (!string.IsNullOrWhiteSpace(chatId))
                    {
                        nextPerChatSendAt[chatId] = now + telegramPerChatMinInterval;
                    }

                    return;
                }

                delay = nextAllowed - now;
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsUnrecoverableTelegramStatus(System.Net.HttpStatusCode statusCode)
        => statusCode is System.Net.HttpStatusCode.BadRequest
            or System.Net.HttpStatusCode.Unauthorized
            or System.Net.HttpStatusCode.Forbidden
            or System.Net.HttpStatusCode.NotFound;

    private static async Task<string> SafeReadResponseBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return body.Length > 512 ? body[..512] : body;
        }
        catch (HttpRequestException)
        {
            return string.Empty;
        }
        catch (TaskCanceledException)
        {
            return string.Empty;
        }
        catch (ObjectDisposedException)
        {
            return string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }
}
