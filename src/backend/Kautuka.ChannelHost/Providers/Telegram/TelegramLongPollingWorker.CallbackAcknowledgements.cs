using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kautuka.ChannelHost.Providers.Telegram;

internal sealed partial class TelegramLongPollingWorker
{
    private async Task<OutboxExecutionResult> SendCallbackAcknowledgementAsync(
        PollingRegistration registration, JsonObject body, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        int httpStatus = 0;
        bool accepted = false;
        string outcome = "cancelled";
        LogCallbackAcknowledgementStarted(logger, registration.RegistrationId);
        try
        {
            await WaitForTelegramSendWindowAsync(body, cancellationToken).ConfigureAwait(false);
            HttpClient client = httpClientFactory.CreateClient("telegram-api");
            using HttpResponseMessage response = await client.PostAsJsonAsync(
                $"https://api.telegram.org/bot{registration.BotToken}/answerCallbackQuery",
                body, TelegramJsonContext.Default.JsonObject, cancellationToken).ConfigureAwait(false);
            httpStatus = (int)response.StatusCode;
            string responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            bool expired = false;
            bool responseObject = false;
            int? apiError = null;
            try
            {
                using JsonDocument document = JsonDocument.Parse(responseBody);
                JsonElement root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    responseObject = true;
                    accepted = response.IsSuccessStatusCode
                        && root.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind == JsonValueKind.True
                        && root.TryGetProperty("result", out JsonElement result) && result.ValueKind == JsonValueKind.True;
                    if (root.TryGetProperty("error_code", out JsonElement code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out int parsedCode)) apiError = parsedCode;
                    if (root.TryGetProperty("description", out JsonElement description) && description.ValueKind == JsonValueKind.String)
                    {
                        string text = description.GetString() ?? string.Empty;
                        expired = text.Contains("query is too old", StringComparison.OrdinalIgnoreCase)
                            || text.Contains("query ID is invalid", StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
            catch (JsonException)
            {
                // HTTP success alone cannot confirm that Telegram accepted the callback.
            }

            if (accepted)
            {
                outcome = "accepted";
                return new OutboxExecutionResult(true, null, null);
            }

            // Keep provider error codes, but never persist raw response bodies, callback ids,
            // notification text or request URLs in diagnostic records.
            outcome = expired ? "telegram_callback_expired_or_invalid"
                : $"telegram_callback_rejected:http={httpStatus};api={apiError?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}";
            bool permanent = IsUnrecoverableTelegramStatus(response.StatusCode)
                || apiError is 400 or 401 or 403 or 404;
            int retryAfter = TryGetTelegramRetryAfter(response, responseObject ? responseBody : null, out TimeSpan delay)
                ? Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds)) : 3;
            // A provider rate limit remains retryable, including a JSON error on HTTP 200.
            if (response.StatusCode == HttpStatusCode.TooManyRequests || apiError == 429) permanent = false;
            return new OutboxExecutionResult(false, outcome, permanent ? null : retryAfter, PermanentFailure: permanent);
        }
        catch (HttpRequestException)
        {
            outcome = "telegram_callback_network_error";
            return new OutboxExecutionResult(false, outcome, 3);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            outcome = "telegram_callback_timeout";
            return new OutboxExecutionResult(false, outcome, 3);
        }
        finally
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                double elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                LogCallbackAcknowledgement(logger, registration.RegistrationId, httpStatus, accepted,
                    elapsedMs, outcome);
            }
        }
    }
}
