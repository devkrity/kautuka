using System.Security.Cryptography;

namespace Kautuka.Telemetry.Security;

/// <summary>
/// ECDSA P-256 / SHA-256 detached signature verification for release and catalog manifests.
/// Accepts IEEE P1363 and RFC 3279 DER encodings (openssl / Node crypto).
/// </summary>
public static class ReleaseManifestSignatureVerifier
{
    public static bool TryVerify(byte[] manifestBytes, string signatureContent, string publicKeyPem, out string? error)
    {
        ArgumentNullException.ThrowIfNull(manifestBytes);
        ArgumentNullException.ThrowIfNull(signatureContent);
        ArgumentNullException.ThrowIfNull(publicKeyPem);

        if (manifestBytes.Length == 0)
        {
            error = "Platform release manifest was empty.";
            return false;
        }

        byte[] signatureBytes;
        try
        {
            signatureBytes = Convert.FromBase64String(RemoveWhitespace(signatureContent));
        }
        catch (FormatException)
        {
            error = "Platform release signature was not valid base64.";
            return false;
        }

        try
        {
            using ECDsa ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(publicKeyPem);
            bool isValid = ecdsa.VerifyData(manifestBytes, signatureBytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)
                || ecdsa.VerifyData(manifestBytes, signatureBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            error = isValid ? null : "Platform release signature verification failed.";
            return isValid;
        }
        catch (CryptographicException ex)
        {
            error = $"Platform release signature could not be verified: {ex.Message}";
            return false;
        }
    }

    private static string RemoveWhitespace(string content)
    {
        var builder = new System.Text.StringBuilder(content.Length);
        foreach (char character in content)
        {
            if (!char.IsWhiteSpace(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }
}
