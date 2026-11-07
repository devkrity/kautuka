namespace Kautuka.Manager.RuntimeContainer.Configuration;

/// <summary>Appliance-owned configuration while the container deployment is being integrated.</summary>
public sealed class ContainerEgressOptions
{
    public const string Section = "ContainerEgress";

    public bool Enabled { get; set; }

    // The two-private-network topology is measured. Raise the shipped limit only with evidence.
    public int MaxPrivateNetworks { get; set; } = 2;

    public IReadOnlyList<string> DnsUpstreams { get; set; } = [];

    public IReadOnlyList<string> ArtifactHosts { get; set; } = [];

    public IReadOnlyList<ContainerEgressGrant> Grants { get; set; } = [];
}

public sealed class ContainerEgressGrant
{
    public string ScopeType { get; set; } = "agent";

    public string UserId { get; set; } = "system";

    public string AgentId { get; set; } = "management";

    public string Role { get; set; } = "connector";

    public string ComponentId { get; set; } = string.Empty;

    public string Destination { get; set; } = string.Empty;

    public int Port { get; set; }
}
