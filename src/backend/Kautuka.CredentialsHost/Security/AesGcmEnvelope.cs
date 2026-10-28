using System.Security.Cryptography;
using System.Text;

namespace Kautuka.CredentialsHost.Security;

internal readonly record struct CredentialEnvelopeIdentity(string Scope, string Subject, string Key)
{
    public byte[] ToAssociatedData()
        => Encoding.UTF8.GetBytes($"kautuka.credentials|2|{Scope.Length}:{Scope}|{Subject.Length}:{Subject}|{Key.Length}:{Key}");
}

internal sealed record SecretEnvelope(
    string KeyId,
    string NonceHex,
    string CiphertextHex,
    string AuthTagHex);

internal static class AesGcmEnvelope
{
    private const string prefix = "KAUTUKA-CREDENTIAL-V2:";

    public static SecretEnvelope Encrypt(
        string plaintext,
        string keyId,
        ReadOnlySpan<byte> key,
        CredentialEnvelopeIdentity identity)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        byte[] ciphertext = new byte[plaintextBytes.Length];
        byte[] tag = new byte[16];
        byte[] associatedData = identity.ToAssociatedData();

        try
        {
            using var aesGcm = new AesGcm(key, tagSizeInBytes: 16);
            aesGcm.Encrypt(nonce, plaintextBytes, ciphertext, tag, associatedData);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
            CryptographicOperations.ZeroMemory(associatedData);
        }

        return new SecretEnvelope(
            keyId,
            Convert.ToHexString(nonce),
            Convert.ToHexString(ciphertext),
            Convert.ToHexString(tag));
    }

    public static string? TryDecrypt(
        SecretEnvelope envelope,
        ReadOnlySpan<byte> key,
        CredentialEnvelopeIdentity identity)
    {
        byte[]? plaintext = null;
        byte[]? associatedData = null;
        try
        {
            byte[] nonce = Convert.FromHexString(envelope.NonceHex);
            byte[] ciphertext = Convert.FromHexString(envelope.CiphertextHex);
            byte[] tag = Convert.FromHexString(envelope.AuthTagHex);
            if (nonce.Length != 12 || tag.Length != 16) return null;

            plaintext = new byte[ciphertext.Length];
            associatedData = identity.ToAssociatedData();
            using var aesGcm = new AesGcm(key, tagSizeInBytes: 16);
            aesGcm.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException) { return null; }
        catch (FormatException) { return null; }
        finally
        {
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            if (associatedData is not null) CryptographicOperations.ZeroMemory(associatedData);
        }
    }

    public static string Serialize(SecretEnvelope envelope)
        => $"{prefix}{envelope.KeyId}:{envelope.NonceHex}:{envelope.CiphertextHex}:{envelope.AuthTagHex}";

    public static bool TryDeserialize(string value, out SecretEnvelope? envelope)
    {
        envelope = null;
        if (!value.StartsWith(prefix, StringComparison.Ordinal)) return false;
        string[] parts = value[prefix.Length..].Split(':');
        if (parts.Length != 4 || parts.Any(string.IsNullOrEmpty)) return false;
        if (parts[0].Length is < 8 or > 64) return false;
        envelope = new SecretEnvelope(parts[0], parts[1], parts[2], parts[3]);
        return true;
    }
}
