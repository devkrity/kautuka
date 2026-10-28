using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kautuka.Gateway.Domain.Connectors;

internal static class ConnectorDpop
{
    public const string TokenEndpoint = "https://oauth2.googleapis.com/token";

    public static string CreatePrivateJwk()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ECParameters parameters = key.ExportParameters(includePrivateParameters: true);
        return WriteJwk(parameters, includePrivate: true);
    }

    public static string CreateProof(string privateJwk, string? nonce, string? authorizationCode)
        => CreateProof(privateJwk, nonce, authorizationCode, "POST", TokenEndpoint);

    public static string CreateProof(string privateJwk, string? nonce, string? authorizationCode, string method, string url)
    {
        // The code is intentionally not used as jti; every proof, including a retry, needs a fresh identifier.
        _ = authorizationCode;
        using ECDsa key = Import(privateJwk);
        ECParameters publicParameters = key.ExportParameters(includePrivateParameters: false);
        string headerJson = WriteHeader(publicParameters);
        string payloadJson = WritePayload(nonce, method, url);
        string signingInput = Base64Url(Encoding.ASCII.GetBytes(headerJson)) + "." + Base64Url(Encoding.ASCII.GetBytes(payloadJson));
        byte[] signature = key.SignData(
            Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return signingInput + "." + Base64Url(signature);
    }

    private static ECDsa Import(string privateJwk)
    {
        using JsonDocument document = JsonDocument.Parse(privateJwk);
        JsonElement root = document.RootElement;
        byte[] x = Decode(Required(root, "x"));
        byte[] y = Decode(Required(root, "y"));
        byte[] d = Decode(Required(root, "d"));
        var parameters = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = x, Y = y },
            D = d,
        };
        return ECDsa.Create(parameters);
    }

    private static string WriteHeader(ECParameters parameters)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("typ", "dpop+jwt");
            writer.WriteString("alg", "ES256");
            writer.WritePropertyName("jwk");
            writer.WriteStartObject();
            writer.WriteString("kty", "EC");
            writer.WriteString("crv", "P-256");
            writer.WriteString("x", Base64Url(parameters.Q.X!));
            writer.WriteString("y", Base64Url(parameters.Q.Y!));
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string Thumbprint(string privateJwk)
    {
        using ECDsa key = Import(privateJwk);
        ECParameters publicParameters = key.ExportParameters(includePrivateParameters: false);
        string canonical = "{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\""
            + Base64Url(publicParameters.Q.X!)
            + "\",\"y\":\""
            + Base64Url(publicParameters.Q.Y!)
            + "\"}";
        return Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(canonical)));
    }

    public static string? KeyRevokeUrl(string host, string connectorRoute, string thumbprint)
    {
        if (thumbprint.Length != 43
            || connectorRoute is not ("google-mail" or "microsoft-mail")
            || !Uri.TryCreate(host, UriKind.Absolute, out Uri? uri)
            || !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath is not "/" and not ""
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return null;
        }

        foreach (char character in thumbprint)
        {
            if (character is not ((>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-'))
            {
                return null;
            }
        }

        return uri.GetLeftPart(UriPartial.Authority) + "/" + connectorRoute + "/keys/" + thumbprint;
    }

    private static string WritePayload(string? nonce, string method, string url)
    {
        // DPoP jti identifies this proof, not the authorization code. A nonce retry
        // must use a fresh identifier even when the request body is unchanged.
        string jti = Base64Url(RandomNumberGenerator.GetBytes(32));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("htm", method);
            writer.WriteString("htu", url);
            writer.WriteNumber("iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            writer.WriteString("jti", jti);
            if (!string.IsNullOrWhiteSpace(nonce))
            {
                writer.WriteString("nonce", nonce);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string WriteJwk(ECParameters parameters, bool includePrivate)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("kty", "EC");
            writer.WriteString("crv", "P-256");
            writer.WriteString("x", Base64Url(parameters.Q.X!));
            writer.WriteString("y", Base64Url(parameters.Q.Y!));
            if (includePrivate)
            {
                writer.WriteString("d", Base64Url(parameters.D!));
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string Required(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            throw new CryptographicException("The DPoP key is not valid.");
        }

        string? text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new CryptographicException("The DPoP key is not valid.");
        }

        return text;
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Decode(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        int remainder = padded.Length % 4;
        if (remainder == 2)
        {
            padded += "==";
        }
        else if (remainder == 3)
        {
            padded += "=";
        }

        return Convert.FromBase64String(padded);
    }
}
