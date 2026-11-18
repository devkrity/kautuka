namespace Kautuka.NetworkAccess.Client;

public interface INetworkAccessChecker
{
    Task<NetworkAccessCheckResult> CheckAsync(OutboundTarget target, CancellationToken cancellationToken = default);
}
