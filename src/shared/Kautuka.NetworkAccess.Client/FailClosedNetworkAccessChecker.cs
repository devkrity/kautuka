namespace Kautuka.NetworkAccess.Client;

/// <summary>
/// Fail-closed <see cref="INetworkAccessChecker"/> used whenever a client is constructed without
/// an explicit network access checker. Ensures outbound requests never bypass NAC evaluation.
/// </summary>
public sealed class FailClosedNetworkAccessChecker : INetworkAccessChecker
{
    public static FailClosedNetworkAccessChecker Instance { get; } = new();

    private FailClosedNetworkAccessChecker()
    {
    }

    public Task<NetworkAccessCheckResult> CheckAsync(OutboundTarget target, CancellationToken cancellationToken = default)
        => Task.FromResult(NetworkAccessCheckResult.CreateDenied("checker_missing"));
}
