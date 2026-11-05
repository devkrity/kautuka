using System.Security.Cryptography;

using Kautuka.Gateway.Domain.Connectors;
using Kautuka.Gateway.Infrastructure.Credentials;

namespace Kautuka.Gateway.Infrastructure.Connectors;

internal sealed record BrokerInstallationIdentity(string Id, string PrivateJwk)
{
    public override string ToString() => $"BrokerInstallationIdentity {{ Id = {Id}, PrivateJwk = [redacted] }}";
}

internal interface IBrokerInstallationIdentityStore
{
    string? BrokerOrigin => null;
    bool EnforceOriginScope => false;
    Task<string?> ReadAsync(CancellationToken cancellationToken);
    Task WriteAsync(string value, CancellationToken cancellationToken);
}

internal sealed class CredentialsHostBrokerInstallationIdentityStore(CredentialResolverService resolver, ConnectorAuthEndpoint endpoint) : IBrokerInstallationIdentityStore
{
    public string? BrokerOrigin => endpoint.Host;
    public bool EnforceOriginScope => true;
    private string CredentialKey => KeyForOrigin(BrokerOrigin ?? string.Empty);

    internal static string KeyForOrigin(string origin)
        => "kautuka.internal.broker_installation_identity." + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(origin)));

    public Task<string?> ReadAsync(CancellationToken cancellationToken)
        => resolver.GetSystemAsync(CredentialKey, cancellationToken);

    public async Task WriteAsync(string value, CancellationToken cancellationToken)
        => _ = await resolver.SetSystemAsync(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CredentialKey] = value,
        }, cancellationToken).ConfigureAwait(false);
}

/// <summary>
/// Persists a local installation identity; it is not proof of official software.
/// Gateway is a single service instance. The lock serializes its initialization.
/// </summary>
internal sealed class BrokerInstallationIdentityProvider(IBrokerInstallationIdentityStore store) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public void ValidateBrokerOrigin(Uri origin)
    {
        if ((store.EnforceOriginScope && !string.Equals(store.BrokerOrigin, origin.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal))
            || (store.BrokerOrigin is string bound && !string.Equals(bound, origin.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal)))
            throw new InvalidOperationException("The installation identity belongs to a different authentication broker.");
    }

    public async Task<bool> HasPendingRotationAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return (await store.ReadAsync(ct).ConfigureAwait(false))?.StartsWith("rotation|", StringComparison.Ordinal) == true; }
        finally { gate.Release(); }
    }

    // Inspection must never create or rotate an identity while handling an incident.
    public async Task<string?> GetExistingIdAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string? saved = await store.ReadAsync(ct).ConfigureAwait(false);
            if (saved is null) return null;
            if (!saved.StartsWith("rotation|", StringComparison.Ordinal)) return Parse(saved).Id;
            string[] parts = saved.Split('|');
            if (parts.Length != 6 || parts[2].Length != 43 || parts[2].Any(static c => c is not ((>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-')))
                throw new CryptographicException("The broker installation identity is invalid.");
            return parts[2];
        }
        finally { gate.Release(); }
    }

    public async Task<BrokerInstallationIdentity> GetOrCreateAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? saved = await store.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (saved is not null) return Parse(saved);

            string id = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            string privateJwk = ConnectorDpop.CreatePrivateJwk();
            await store.WriteAsync(id + ":" + privateJwk, cancellationToken).ConfigureAwait(false);
            string? persisted = await store.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(persisted, id + ":" + privateJwk, StringComparison.Ordinal))
                throw new CryptographicException("The broker installation identity could not be persisted.");
            return new BrokerInstallationIdentity(id, privateJwk);
        }
        finally
        {
            gate.Release();
        }
    }

    // The durable pending record survives a broker commit or local write whose
    // response is lost. Normal identity use fails closed until it is resolved.
    public async Task RotateAsync(string origin, Func<BrokerInstallationIdentity, BrokerInstallationIdentity, string, CancellationToken, Task> send, bool create, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string? saved = await store.ReadAsync(ct).ConfigureAwait(false);
            if (saved is null) { if (!create) return; throw new InvalidOperationException("The installation identity must be registered before rotation."); }
            if (!saved.StartsWith("rotation|", StringComparison.Ordinal))
            {
                if (!create) return;
                BrokerInstallationIdentity current = Parse(saved);
                string operation = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                string next = ConnectorDpop.CreatePrivateJwk();
                saved = string.Join('|', "rotation", Encode(origin), current.Id, operation, Encode(current.PrivateJwk), Encode(next));
                await WriteVerifiedAsync(saved, ct).ConfigureAwait(false);
            }
            string[] parts = saved.Split('|');
            if (parts.Length != 6 || Decode(parts[1]) != origin) throw new CryptographicException("The pending broker key rotation is invalid or belongs to another broker.");
            var oldIdentity = Parse(parts[2] + ":" + Decode(parts[4]));
            var newIdentity = Parse(parts[2] + ":" + Decode(parts[5]));
            if (parts[3].Length != 43 || parts[3].Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-')))
                throw new CryptographicException("The pending broker key rotation is invalid.");
            await send(oldIdentity, newIdentity, parts[3], ct).ConfigureAwait(false);
            await WriteVerifiedAsync(newIdentity.Id + ":" + newIdentity.PrivateJwk, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task WriteVerifiedAsync(string value, CancellationToken ct)
    {
        await store.WriteAsync(value, ct).ConfigureAwait(false);
        if (!string.Equals(await store.ReadAsync(ct).ConfigureAwait(false), value, StringComparison.Ordinal))
            throw new CryptographicException("The broker installation identity could not be persisted.");
    }

    private static string Encode(string value) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));
    private static string Decode(string value) => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value));

    private static BrokerInstallationIdentity Parse(string value)
    {
        int separator = value.IndexOf(':', StringComparison.Ordinal);
        if (separator != 43 || value.Length > 4096)
            throw new CryptographicException("The broker installation identity is invalid.");
        string id = value[..separator];
        foreach (char c in id)
            if (c is not ((>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-'))
                throw new CryptographicException("The broker installation identity is invalid.");
        string privateJwk = value[(separator + 1)..];
        _ = ConnectorDpop.Thumbprint(privateJwk); // Fail closed on missing or invalid private key.
        return new BrokerInstallationIdentity(id, privateJwk);
    }

    public void Dispose() => gate.Dispose();
}
