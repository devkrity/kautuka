using System.Text.Json;

using Kautuka.Gateway.Domain.Connectors;

namespace Kautuka.Gateway.Infrastructure.Connectors;

internal sealed record ConnectorTokenResult(
    bool Success,
    string? AccessToken,
    string? RefreshToken,
    string? Error,
    string? DpopNonce = null,
    bool UseDpopNonce = false);

internal sealed record ConnectorDpopRevokeResult(bool Success, string? DpopNonce, bool UseDpopNonce);

internal sealed class ConnectorAuthorizationClient : IDisposable
{
    private readonly HttpClient http;
    private readonly HttpMessageHandler? ownedHandler;

    public ConnectorAuthorizationClient(HttpMessageHandler? handler = null)
    {
        if (handler is null)
        {
            var sockets = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            };
            ownedHandler = sockets;
            http = new HttpClient(sockets, disposeHandler: false);
        }
        else
        {
            http = new HttpClient(handler, disposeHandler: false);
        }

        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("KautukaConnectorAuth/0.1");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public Task<ConnectorTokenResult> ExchangeCodeAsync(
        ConnectorAuthProfile profile,
        string code,
        string codeVerifier,
        string? dpopProof,
        string redirectUri,
        string tokenUrl,
        bool includeClientCredentials,
        string? brokerTicket,
        string? returnOrigin,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier,
            ["scope"] = profile.Scopes,
        };
        if (!string.IsNullOrWhiteSpace(brokerTicket))
        {
            form["broker_ticket"] = brokerTicket;
            form["return_origin"] = returnOrigin ?? string.Empty;
        }
        ApplyClientAuthentication(form, profile, includeClientCredentials);
        return PostAsync(tokenUrl, form, dpopProof, cancellationToken);
    }

    public Task<ConnectorTokenResult> RefreshAsync(
        ConnectorAuthProfile profile,
        string refreshToken,
        string? dpopProof,
        string tokenUrl,
        bool includeClientCredentials,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = profile.Scopes,
        };
        ApplyClientAuthentication(form, profile, includeClientCredentials);
        return PostAsync(tokenUrl, form, dpopProof, cancellationToken);
    }

    private static void ApplyClientAuthentication(
        Dictionary<string, string> form,
        ConnectorAuthProfile profile,
        bool includeClientCredentials)
    {
        if (!includeClientCredentials)
        {
            return;
        }

        form["client_id"] = profile.ClientId ?? string.Empty;
        AddClientSecret(form, profile);
    }

    private static void AddClientSecret(Dictionary<string, string> form, ConnectorAuthProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.ClientSecret))
        {
            form["client_secret"] = profile.ClientSecret;
        }
    }

    public async Task<ConnectorDpopRevokeResult> RevokeDpopKeyAsync(
        string url,
        string refreshToken,
        string dpopProof,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["refresh_token"] = refreshToken,
        });
        _ = request.Headers.TryAddWithoutValidation("DPoP", dpopProof);
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new ConnectorDpopRevokeResult(
            response.StatusCode == System.Net.HttpStatusCode.NoContent,
            ReadNonce(response),
            IsUseDpopNonce(body));
    }

    public void Dispose()
    {
        http.Dispose();
        ownedHandler?.Dispose();
    }

    private async Task<ConnectorTokenResult> PostAsync(
        string url,
        Dictionary<string, string> form,
        string? dpopProof,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        using var content = new FormUrlEncodedContent(form);
        request.Content = content;
        if (!string.IsNullOrWhiteSpace(dpopProof))
        {
            _ = request.Headers.TryAddWithoutValidation("DPoP", dpopProof);
        }

        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        string? nonce = ReadNonce(response);
        if (!response.IsSuccessStatusCode)
        {
            bool useNonce = IsUseDpopNonce(body);
            return new ConnectorTokenResult(false, null, null, "The sign-in was not accepted.", nonce, useNonce);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            JsonElement root = document.RootElement;
            string? accessToken = ReadString(root, "access_token");
            string? refreshToken = ReadString(root, "refresh_token");
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return new ConnectorTokenResult(false, null, null, "The sign-in did not return an access token.", nonce);
            }

            return new ConnectorTokenResult(true, accessToken, refreshToken, null, nonce);
        }
        catch (JsonException)
        {
            return new ConnectorTokenResult(false, null, null, "The sign-in response could not be read.", nonce);
        }
    }

    private static string? ReadNonce(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("DPoP-Nonce", out IEnumerable<string>? values))
        {
            return null;
        }

        foreach (string value in values)
        {
            if (!string.IsNullOrWhiteSpace(value) && value.Length <= 200)
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static bool IsUseDpopNonce(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            return document.RootElement.TryGetProperty("error", out JsonElement error)
                && error.ValueKind == JsonValueKind.String
                && string.Equals(error.GetString(), "use_dpop_nonce", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
