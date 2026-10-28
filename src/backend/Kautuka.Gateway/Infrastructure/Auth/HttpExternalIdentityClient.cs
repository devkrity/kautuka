using System.Security.Cryptography;
using System.Text.Json;

namespace Kautuka.Gateway.Infrastructure.Auth;

internal sealed class HttpExternalIdentityClient(IHttpClientFactory httpClientFactory, ExternalLoginProviders providers) : IExternalIdentityClient
{
    public const string HttpClientName = "ExternalLogin";

    private readonly object gate = new();
    private readonly Dictionary<string, Dictionary<string, RSA>> keys = new(StringComparer.Ordinal);

    public async Task<string?> ExchangeCodeAsync(string provider, string code, string verifier, string brokerTicket, string returnOrigin, CancellationToken cancellationToken)
    {
        if (providers.Host is null)
        {
            return null;
        }

        string url = providers.Host + "/login/" + provider + "/token";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["code_verifier"] = verifier,
                ["redirect_uri"] = providers.Host + "/callback",
                ["broker_ticket"] = brokerTicket,
                ["return_origin"] = returnOrigin,
            }),
        };
        HttpClient client = httpClientFactory.CreateClient(HttpClientName);
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("id_token", out JsonElement token) || token.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return token.GetString();
    }

    public async Task<RSA?> FindKeyAsync(string provider, string keyId, CancellationToken cancellationToken)
    {
        if (TryGet(provider, keyId, out RSA? cached))
        {
            return cached;
        }

        await RefreshAsync(provider, cancellationToken).ConfigureAwait(false);
        return TryGet(provider, keyId, out RSA? refreshed) ? refreshed : null;
    }

    private bool TryGet(string provider, string keyId, out RSA? key)
    {
        lock (gate)
        {
            if (keys.TryGetValue(provider, out Dictionary<string, RSA>? providerKeys)
                && providerKeys.TryGetValue(keyId, out RSA? found))
            {
                key = found;
                return true;
            }
        }

        key = null;
        return false;
    }

    private async Task RefreshAsync(string provider, CancellationToken cancellationToken)
    {
        string? url = provider switch
        {
            "google" => "https://www.googleapis.com/oauth2/v3/certs",
            "microsoft" => "https://login.microsoftonline.com/common/discovery/v2.0/keys",
            _ => null,
        };
        if (url is null)
        {
            return;
        }

        HttpClient client = httpClientFactory.CreateClient(HttpClientName);
        using HttpResponseMessage response = await client.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return;
        }

        using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
        var fresh = new Dictionary<string, RSA>(StringComparer.Ordinal);
        if (document.RootElement.TryGetProperty("keys", out JsonElement keyList) && keyList.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in keyList.EnumerateArray())
            {
                if (!TryImport(item, out string? kid, out RSA? rsa) || kid is null || rsa is null || !fresh.TryAdd(kid, rsa))
                {
                    rsa?.Dispose();
                }
            }
        }

        lock (gate)
        {
            if (keys.TryGetValue(provider, out Dictionary<string, RSA>? previous))
            {
                foreach (RSA old in previous.Values)
                {
                    old.Dispose();
                }
            }

            keys[provider] = fresh;
        }
    }

    private static bool TryImport(JsonElement item, out string? kid, out RSA? key)
    {
        kid = null;
        key = null;
        if (!item.TryGetProperty("kty", out JsonElement kty)
            || !string.Equals(kty.GetString(), "RSA", StringComparison.Ordinal)
            || !item.TryGetProperty("kid", out JsonElement kidElement)
            || !item.TryGetProperty("n", out JsonElement modulus)
            || !item.TryGetProperty("e", out JsonElement exponent))
        {
            return false;
        }

        kid = kidElement.GetString();
        if (string.IsNullOrWhiteSpace(kid) || modulus.ValueKind != JsonValueKind.String || exponent.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        RSA rsa = RSA.Create();
        try
        {
            rsa.ImportParameters(new RSAParameters
            {
                Modulus = Base64Url(modulus.GetString()!),
                Exponent = Base64Url(exponent.GetString()!),
            });
        }
        catch (CryptographicException)
        {
            rsa.Dispose();
            return false;
        }
        catch (FormatException)
        {
            rsa.Dispose();
            return false;
        }

        key = rsa;
        return true;
    }

    private static byte[] Base64Url(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        return Convert.FromBase64String(padded);
    }
}
