using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;


namespace Kautuka.ChannelHost.Providers.Slack;

/// <summary>Provider setup validation only; it does not own attachment state or activation.</summary>
internal static class SlackSetupValidator
{
    internal const string PrivateSetupMismatch = "This Slack app doesn't match Kautuka's private setup. Recreate it from the manifest link.";
    internal sealed record Result(bool Success, string Detail);

    public static async Task<Result> ValidateAsync(
        IHttpClientFactory httpClientFactory,
        Func<string, string, CancellationToken, Task<string?>> syncIdentity,
        string attachmentId,
        string botToken,
        string appToken,
        CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(botToken)) return new(false, "missing_bot_token");
            if (string.IsNullOrWhiteSpace(appToken)) return new(false, "missing_app_token");
            if (!botToken.StartsWith("xoxb-", StringComparison.Ordinal)) return new(false, "invalid_bot_token_type_expected_xoxb");
            if (!appToken.StartsWith("xapp-", StringComparison.Ordinal)) return new(false, "invalid_app_token_type_expected_xapp");

            using HttpRequestMessage botRequest = AuthorizedPost("https://slack.com/api/auth.test", botToken);
            using HttpResponseMessage botResponse = await httpClientFactory.CreateClient("slack-api").SendAsync(botRequest, cancellationToken).ConfigureAwait(false);
            using JsonDocument botDocument = await ParseResponseAsync(botResponse, cancellationToken).ConfigureAwait(false);
            if (GetSlackError(botDocument.RootElement) is string botError) return new(false, $"bot_token_{botError}");
            if (!botResponse.Headers.TryGetValues("x-oauth-scopes", out IEnumerable<string>? scopeHeaders)) return new(false, PrivateSetupMismatch);

            HashSet<string> scopes = scopeHeaders.SelectMany(static value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToHashSet(StringComparer.Ordinal);
            if (scopes.Any(static scope => scope.StartsWith("channels:", StringComparison.Ordinal)
                    || scope.StartsWith("groups:", StringComparison.Ordinal)
                    || scope.StartsWith("mpim:", StringComparison.Ordinal)
                    || scope.StartsWith("app_mentions:", StringComparison.Ordinal)))
                return new(false, PrivateSetupMismatch);
            string[] missing = [.. SlackSetupManifest.RequiredBotScopes.Where(scope => !scopes.Contains(scope))];
            if (missing.Length > 0) return new(false, PrivateSetupMismatch);
            string? identityError = await syncIdentity(attachmentId, botToken, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(identityError)) return new(false, identityError);

            using HttpRequestMessage appRequest = AuthorizedPost("https://slack.com/api/apps.connections.open", appToken);
            using HttpResponseMessage appResponse = await httpClientFactory.CreateClient("slack-api").SendAsync(appRequest, cancellationToken).ConfigureAwait(false);
            using JsonDocument appDocument = await ParseResponseAsync(appResponse, cancellationToken).ConfigureAwait(false);
            if (GetSlackError(appDocument.RootElement) is string appError) return new(false, $"app_token_{appError}");
            return new(true, "ready:bot_identity_verified,bot_scopes_verified,socket_token_verified");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (HttpRequestException) { return new(false, "provider_unreachable"); }
        catch (InvalidDataException) { return new(false, "provider_response_invalid"); }
        catch (JsonException) { return new(false, "provider_response_invalid"); }
    }

    private static HttpRequestMessage AuthorizedPost(string url, string token)
    {
        HttpRequestMessage request = new(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<JsonDocument> ParseResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        const int MaximumResponseBytes = 64 * 1024;
        if (response.Content.Headers.ContentLength is long length && length > MaximumResponseBytes) throw new InvalidDataException("provider_response_too_large");
        await response.Content.LoadIntoBufferAsync(MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (Encoding.UTF8.GetByteCount(json) > MaximumResponseBytes) throw new InvalidDataException("provider_response_too_large");
        return JsonDocument.Parse(json);
    }

    private static string? GetSlackError(JsonElement root) => root.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind == JsonValueKind.True
        ? null
        : root.TryGetProperty("error", out JsonElement error) && !string.IsNullOrWhiteSpace(error.GetString()) ? error.GetString() : "unreachable";
}
