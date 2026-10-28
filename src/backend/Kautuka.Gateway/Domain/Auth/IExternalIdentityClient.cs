using System.Security.Cryptography;

namespace Kautuka.Gateway.Domain.Auth;

internal interface IExternalIdentityClient
{
    Task<string?> ExchangeCodeAsync(string provider, string code, string verifier, string brokerTicket, string returnOrigin, CancellationToken cancellationToken);

    Task<RSA?> FindKeyAsync(string provider, string keyId, CancellationToken cancellationToken);
}
