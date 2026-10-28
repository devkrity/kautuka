using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kautuka.Gateway.Domain.Auth;

internal static class ExternalIdentityToken
{
    private static readonly TimeSpan clockSkew = TimeSpan.FromSeconds(60);

    public static bool TryValidate(
        string token,
        RSA key,
        string provider,
        string audience,
        string nonce,
        DateTimeOffset now,
        out ExternalIdentity? identity,
        out string reason)
    {
        identity = null;
        reason = "invalid_token";
        if (string.IsNullOrWhiteSpace(token) || key is null || string.IsNullOrWhiteSpace(audience) || string.IsNullOrWhiteSpace(nonce))
        {
            return false;
        }

        string[] parts = token.Split('.');
        if (parts.Length != 3 || !TryDecodeJson(parts[0], out JsonDocument? header) || header is null)
        {
            return false;
        }

        using (header)
        {
            if (!header.RootElement.TryGetProperty("alg", out JsonElement algorithm)
                || algorithm.ValueKind != JsonValueKind.String
                || !string.Equals(algorithm.GetString(), "RS256", StringComparison.Ordinal))
            {
                reason = "disallowed_alg";
                return false;
            }

            if (!VerifySignature(parts[0], parts[1], parts[2], key))
            {
                reason = "invalid_signature";
                return false;
            }
        }

        if (!TryDecodeJson(parts[1], out JsonDocument? payload) || payload is null)
        {
            return false;
        }

        using (payload)
        {
            JsonElement root = payload.RootElement;
            if (!TryReadTimes(root, now, out reason))
            {
                return false;
            }

            if (!root.TryGetProperty("nonce", out JsonElement nonceClaim)
                || nonceClaim.ValueKind != JsonValueKind.String
                || !string.Equals(nonceClaim.GetString(), nonce, StringComparison.Ordinal))
            {
                reason = "nonce_mismatch";
                return false;
            }

            if (!root.TryGetProperty("aud", out JsonElement audienceClaim)
                || audienceClaim.ValueKind != JsonValueKind.String
                || !string.Equals(audienceClaim.GetString(), audience, StringComparison.Ordinal))
            {
                reason = "wrong_audience";
                return false;
            }

            if (!root.TryGetProperty("sub", out JsonElement subjectClaim)
                || subjectClaim.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(subjectClaim.GetString()))
            {
                return false;
            }

            string? email = provider switch
            {
                "google" => ReadGoogleEmail(root, out reason),
                "microsoft" => ReadMicrosoftEmail(root, out reason),
                _ => null,
            };
            if (email is null)
            {
                return false;
            }

            identity = new ExternalIdentity(
                provider,
                subjectClaim.GetString()!,
                email,
                ReadDisplayName(root),
                ReadStringClaim(root, "iss"),
                ReadStringClaim(root, "tid"),
                ReadStringClaim(root, "oid"),
                EmailVerified: IsEmailVerified(root, provider));
            reason = "ok";
            return true;
        }
    }

    public static bool TryReadKeyId(string token, out string? kid, out string reason)
    {
        kid = null;
        reason = "invalid_token";
        string[] parts = token.Split('.');
        if (parts.Length != 3 || !TryDecodeJson(parts[0], out JsonDocument? header) || header is null)
        {
            return false;
        }

        using (header)
        {
            if (!header.RootElement.TryGetProperty("alg", out JsonElement algorithm)
                || algorithm.ValueKind != JsonValueKind.String
                || !string.Equals(algorithm.GetString(), "RS256", StringComparison.Ordinal))
            {
                reason = "disallowed_alg";
                return false;
            }

            if (!header.RootElement.TryGetProperty("kid", out JsonElement kidClaim)
                || kidClaim.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(kidClaim.GetString()))
            {
                reason = "unknown_kid";
                return false;
            }

            kid = kidClaim.GetString();
            reason = "ok";
            return true;
        }
    }

    private static string? ReadGoogleEmail(JsonElement root, out string reason)
    {
        reason = "invalid_token";
        if (!root.TryGetProperty("iss", out JsonElement issuer) || issuer.ValueKind != JsonValueKind.String)
        {
            reason = "wrong_issuer";
            return null;
        }

        string? iss = issuer.GetString();
        if (!string.Equals(iss, "https://accounts.google.com", StringComparison.Ordinal)
            && !string.Equals(iss, "accounts.google.com", StringComparison.Ordinal))
        {
            reason = "wrong_issuer";
            return null;
        }

        if (!root.TryGetProperty("email_verified", out JsonElement verified) || verified.ValueKind != JsonValueKind.True)
        {
            reason = "unverified_email";
            return null;
        }

        return ReadEmail(root, out reason);
    }

    private static string? ReadMicrosoftEmail(JsonElement root, out string reason)
    {
        reason = "invalid_token";
        if (!root.TryGetProperty("iss", out JsonElement issuer) || issuer.ValueKind != JsonValueKind.String)
        {
            reason = "wrong_issuer";
            return null;
        }

        if (!root.TryGetProperty("tid", out JsonElement tenant) || tenant.ValueKind != JsonValueKind.String)
        {
            reason = "wrong_issuer";
            return null;
        }

        string? iss = issuer.GetString();
        const string prefix = "https://login.microsoftonline.com/";
        const string suffix = "/v2.0";
        if (iss is null
            || !iss.StartsWith(prefix, StringComparison.Ordinal)
            || !iss.EndsWith(suffix, StringComparison.Ordinal))
        {
            reason = "wrong_issuer";
            return null;
        }

        string tenantInIssuer = iss[prefix.Length..^suffix.Length];
        if (!Guid.TryParseExact(tenantInIssuer, "D", out Guid issuerTenant)
            || !Guid.TryParse(tenant.GetString(), out Guid tokenTenant)
            || issuerTenant != tokenTenant)
        {
            reason = "wrong_issuer";
            return null;
        }

        return ReadEmail(root, out reason);
    }

    private static bool IsEmailVerified(JsonElement root, string provider)
    {
        if (root.TryGetProperty("email_verified", out JsonElement emailVerified)
            && emailVerified.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (!string.Equals(provider, "microsoft", StringComparison.Ordinal)
            || !root.TryGetProperty("xms_edov", out JsonElement domainVerified))
        {
            return false;
        }

        return domainVerified.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => domainVerified.TryGetInt32(out int value) && value == 1,
            JsonValueKind.String => string.Equals(domainVerified.GetString(), "1", StringComparison.Ordinal),
            _ => false,
        };
    }

    private static string? ReadEmail(JsonElement root, out string reason)
    {
        if (!root.TryGetProperty("email", out JsonElement email) || email.ValueKind != JsonValueKind.String)
        {
            reason = "missing_email";
            return null;
        }

        string? canonical = CanonicalEmail.Normalize(email.GetString());
        if (canonical is null)
        {
            reason = "missing_email";
            return null;
        }

        reason = "ok";
        return canonical;
    }

    private static string? ReadStringClaim(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement claim) || claim.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? value = claim.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? ReadDisplayName(JsonElement root)
    {
        if (!root.TryGetProperty("name", out JsonElement name) || name.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? text = name.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(text) || text.Length > 80)
        {
            return null;
        }

        foreach (char character in text)
        {
            if (char.IsControl(character))
            {
                return null;
            }
        }

        return text;
    }

    private static bool TryReadTimes(JsonElement root, DateTimeOffset now, out string reason)
    {
        reason = "expired_token";
        if (!root.TryGetProperty("exp", out JsonElement expires) || !TryReadUnix(expires, out DateTimeOffset expiry))
        {
            return false;
        }

        if (expiry + clockSkew < now)
        {
            return false;
        }

        if (root.TryGetProperty("nbf", out JsonElement notBefore)
            && (!TryReadUnix(notBefore, out DateTimeOffset start) || start > now + clockSkew))
        {
            reason = "invalid_token";
            return false;
        }

        reason = "ok";
        return true;
    }

    private static bool TryReadUnix(JsonElement value, out DateTimeOffset time)
    {
        time = default;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long seconds))
        {
            return false;
        }

        try
        {
            time = DateTimeOffset.FromUnixTimeSeconds(seconds);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool VerifySignature(string header, string payload, string signature, RSA key)
    {
        byte[] signed = Encoding.ASCII.GetBytes(header + "." + payload);
        byte[] signatureBytes;
        try
        {
            signatureBytes = Base64UrlDecode(signature);
        }
        catch (FormatException)
        {
            return false;
        }

        return key.VerifyData(signed, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    private static bool TryDecodeJson(string segment, out JsonDocument? document)
    {
        document = null;
        try
        {
            byte[] json = Base64UrlDecode(segment);
            document = JsonDocument.Parse(json);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static byte[] Base64UrlDecode(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        return Convert.FromBase64String(padded);
    }
}
