using System.Net;

namespace Kautuka.NetworkGateway.Admission;

/// <summary>A conntrack generation plus the original, pre-NAT transport tuple.</summary>
internal sealed record FlowKey(
    uint ConntrackId,
    ushort Zone,
    IPAddress Source,
    ushort SourcePort,
    IPAddress Destination,
    ushort DestinationPort,
    byte Protocol,
    uint IngressInterfaceIndex = 0);

internal sealed record SourceRegistration(IPAddress Address, string Subject, long Generation, uint IngressInterfaceIndex);

internal sealed record EndpointRule(string Subject, IPAddress Destination, ushort Port, byte Protocol);

internal sealed record AdmissionLease(
    uint Handle,
    FlowKey Flow,
    string Subject,
    long IdentityGeneration,
    long PolicyGeneration,
    DateTimeOffset ExpiresAt);

internal sealed record AdmissionResult(bool Allowed, string Reason, uint Handle = 0);
