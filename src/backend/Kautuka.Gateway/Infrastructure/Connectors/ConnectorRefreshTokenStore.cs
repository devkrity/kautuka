using Kautuka.Gateway.Infrastructure.Credentials;

namespace Kautuka.Gateway.Infrastructure.Connectors;

internal interface IConnectorRefreshTokenStore
{
    Task<string?> GetAsync(string userId, string key, CancellationToken cancellationToken);

    Task SetAsync(string userId, string key, string refreshToken, CancellationToken cancellationToken);

    Task DeleteAsync(string userId, string key, CancellationToken cancellationToken);
}

internal sealed class CredentialsHostConnectorRefreshTokenStore(CredentialResolverService resolver) : IConnectorRefreshTokenStore
{
    public async Task<string?> GetAsync(string userId, string key, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> keys = await resolver.ListUserKeysAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!keys.Contains(key, StringComparer.Ordinal))
        {
            return null;
        }

        IReadOnlyDictionary<string, string> resolved = await resolver.GetResolvedAsync(
            userId,
            [key],
            includeSystemScope: false,
            includeHeldSecrets: true,
            cancellationToken).ConfigureAwait(false);
        return resolved.TryGetValue(key, out string? token) && !string.IsNullOrWhiteSpace(token)
            ? token
            : null;
    }

    public async Task SetAsync(string userId, string key, string refreshToken, CancellationToken cancellationToken)
    {
        _ = await resolver.SetUserAsync(
            userId,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [key] = refreshToken,
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string userId, string key, CancellationToken cancellationToken)
    {
        _ = await resolver.DeleteUserAsync(userId, key, cancellationToken).ConfigureAwait(false);
    }
}
