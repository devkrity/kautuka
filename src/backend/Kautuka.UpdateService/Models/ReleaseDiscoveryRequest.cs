namespace Kautuka.UpdateService.Models;

public sealed record ReleaseDiscoveryRequest(
    string RepositoryOwner,
    string RepositoryName,
    string ReleaseChannel,
    string? IfNoneMatch);
