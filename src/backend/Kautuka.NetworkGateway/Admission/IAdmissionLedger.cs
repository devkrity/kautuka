namespace Kautuka.NetworkGateway.Admission;

internal interface IAdmissionLedger
{
    /// <summary>Returns a never-reused durable sequence after synchronous commit.</summary>
    long Append(FlowKey flow, ActorRegistration source, long policyGeneration, DateTimeOffset expiresAt);

    void RecordReleaseFailure(long admissionId);

    bool IsRevoked(string subject, long identityGeneration);

    void Revoke(string subject, long identityGeneration);
}

internal interface IAdmissionKernel
{
    void Publish(AdmissionLease lease);

    /// <summary>Returns only after the handle has been removed from kernel permission state.</summary>
    void Revoke(uint handle);
}

internal interface IQueueVerdicts
{
    void Allow(uint packetId, uint handle);

    void Drop(uint packetId);
}
