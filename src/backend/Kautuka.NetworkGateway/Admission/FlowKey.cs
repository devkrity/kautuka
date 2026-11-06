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
    uint IngressInterfaceIndex = 0,
    AdmissionPath Path = AdmissionPath.Forwarded,
    uint EgressInterfaceIndex = 0,
    uint? SocketUid = null,
    long NamespaceGeneration = 1);

// Values match the supported Linux NF_INET hooks; no other hooks admit traffic.
internal enum AdmissionPath { Forwarded = 2, LocalOutput = 3 }

internal abstract record ActorRegistration(string Subject, long Generation, long NamespaceGeneration);

internal sealed record SourceRegistration(IPAddress Address, string Subject, long Generation, uint IngressInterfaceIndex, long NamespaceGeneration = 1)
    : ActorRegistration(Subject, Generation, NamespaceGeneration);

internal sealed record LocalOutputRegistration(IPAddress Address, string Subject, long Generation, uint SocketUid, uint EgressInterfaceIndex, long NamespaceGeneration = 1)
    : ActorRegistration(Subject, Generation, NamespaceGeneration);

internal sealed record EndpointRule(string Subject, IPAddress Destination, ushort Port, byte Protocol);

internal sealed record AdmissionLease(
    uint Handle,
    FlowKey Flow,
    string Subject,
    long IdentityGeneration,
    long PolicyGeneration,
    DateTimeOffset ExpiresAt);

internal sealed record AdmissionResult(bool Allowed, string Reason, uint Handle = 0);
