using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace Kautuka.CredentialsHost.Security;

[SuppressMessage("Performance", "CA1812", Justification = "Constructed by ASP.NET Core DI")]
internal sealed class CredentialsVaultService(CredentialKeyRing keyRing)
{
    public string Encrypt(string plaintext, CredentialEnvelopeIdentity identity)
    {
        (string keyId, byte[] key) = keyRing.GetActiveKey();
        try { return AesGcmEnvelope.Serialize(AesGcmEnvelope.Encrypt(plaintext, keyId, key, identity)); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public string Decrypt(string serialized, CredentialEnvelopeIdentity identity)
    {
        if (!AesGcmEnvelope.TryDeserialize(serialized, out SecretEnvelope? envelope) || envelope is null)
            throw new CredentialIntegrityException("Credential envelope is malformed.");
        byte[] key = keyRing.GetKey(envelope.KeyId);
        try
        {
            return AesGcmEnvelope.TryDecrypt(envelope, key, identity)
                ?? throw new CredentialIntegrityException("Credential envelope authentication failed.");
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}

internal sealed class CredentialIntegrityException : CryptographicException
{
    public CredentialIntegrityException() { }
    public CredentialIntegrityException(string message) : base(message) { }
    public CredentialIntegrityException(string message, Exception innerException) : base(message, innerException) { }
}
