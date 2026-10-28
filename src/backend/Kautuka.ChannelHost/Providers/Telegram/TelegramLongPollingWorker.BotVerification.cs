using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kautuka.ChannelHost.Providers.Telegram;

internal sealed partial class TelegramLongPollingWorker
{
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Credential verification failure must be isolated and reported without token-bearing request exceptions.")]
    private async Task<(string? BotId, int RetrySeconds)> VerifyBotIdentityAsync(PollingRegistration registration, CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            HttpClient client = httpClientFactory.CreateClient("telegram-api");
            using HttpResponseMessage response = await client.PostAsJsonAsync(
                $"https://api.telegram.org/bot{registration.BotToken}/getMe", new JsonObject(), TelegramJsonContext.Default.JsonObject, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string body = await SafeReadResponseBodyAsync(response, cancellationToken).ConfigureAwait(false);
                if (TryGetTelegramRetryAfter(response, body, out TimeSpan retryAfter)) return (null, Math.Clamp((int)retryAfter.TotalSeconds, 1, 86400));
                return (null, (int)response.StatusCode is 401 or 403 or 404 ? 300 : 3);
            }
            using JsonDocument document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind == JsonValueKind.True
                && root.TryGetProperty("result", out JsonElement result) && result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("id", out JsonElement id) && long.TryParse(id.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out long botId) && botId > 0)
                return (botId.ToString(CultureInfo.InvariantCulture), 0);
            return (null, 3);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogPollingRosterFailure(logger, ex.GetType().Name);
            return (null, 3);
        }
    }
}
