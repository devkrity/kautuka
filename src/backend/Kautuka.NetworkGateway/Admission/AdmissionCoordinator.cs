namespace Kautuka.NetworkGateway.Admission;

/// <summary>
/// Serializes durable admission, kernel permission publication, verdict release and revocation.
/// Native queue buffering and topology/mark validation are separate required boundaries.
/// </summary>
internal sealed class AdmissionCoordinator
{
    private readonly object sync = new();
    private readonly EndpointPolicy policy;
    private readonly IAdmissionLedger ledger;
    private readonly IAdmissionKernel kernel;
    private readonly TimeProvider time;
    private readonly int capacity;
    private readonly Dictionary<FlowKey, AdmissionLease> leases = [];
    private readonly HashSet<string> revokedSubjects = new(StringComparer.Ordinal);
    private bool faulted;
    private static readonly TimeSpan leaseDuration = TimeSpan.FromSeconds(120);

    public AdmissionCoordinator(EndpointPolicy policy, IAdmissionLedger ledger, IAdmissionKernel kernel, TimeProvider time, int capacity = 4096)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(kernel);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        this.policy = policy;
        this.ledger = ledger;
        this.kernel = kernel;
        this.time = time;
        this.capacity = capacity;
    }

    public AdmissionResult Process(uint packetId, FlowKey flow, IQueueVerdicts verdicts)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(verdicts);
        lock (sync)
        {
            if (faulted)
            {
                verdicts.Drop(packetId);
                return new AdmissionResult(false, "coordinator_faulted");
            }

            try
            {
                return ProcessLocked(packetId, flow, verdicts);
            }
            catch
            {
                // The native consumer must close on this exception. Never continue issuing allows.
                faulted = true;
                throw;
            }
        }
    }

    private AdmissionResult ProcessLocked(uint packetId, FlowKey flow, IQueueVerdicts verdicts)
    {
        SourceRegistration? source = policy.Resolve(flow);
        if (source is null || revokedSubjects.Contains(source.Subject) || !policy.Allows(source, flow))
        {
            return Deny("policy_denied");
        }

        try
        {
            if (ledger.IsRevoked(source.Subject, source.Generation))
            {
                return Deny("identity_revoked");
            }
        }
        catch (Exception error) when (error is IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            return Deny("audit_unavailable");
        }

        DateTimeOffset now = time.GetUtcNow();
        if (leases.TryGetValue(flow, out AdmissionLease? existing))
        {
            if (existing.ExpiresAt <= now)
            {
                // Never re-admit the same conntrack generation after its permission expired.
                return Deny("lease_expired");
            }

            verdicts.Allow(packetId, existing.Handle);
            return new AdmissionResult(true, "admitted", existing.Handle);
        }

        if (leases.Count >= capacity)
        {
            return Deny("capacity_exhausted");
        }

        long admissionId;
        DateTimeOffset expiresAt = now + leaseDuration;
        try
        {
            admissionId = ledger.Append(flow, source, policy.Generation, expiresAt);
        }
        catch (Exception error) when (error is IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            return Deny("audit_unavailable");
        }

        if (admissionId <= 0 || admissionId > uint.MaxValue)
        {
            // A full handle space requires a deliberate epoch reset, never wrapping/reuse.
            ledger.RecordReleaseFailure(admissionId);
            return Deny("handle_space_exhausted");
        }

        if (expiresAt <= time.GetUtcNow())
        {
            ledger.RecordReleaseFailure(admissionId);
            return Deny("lease_expired_before_release");
        }

        var lease = new AdmissionLease((uint)admissionId, flow, source.Subject, source.Generation, policy.Generation, expiresAt);
        try
        {
            kernel.Publish(lease);
        }
        catch (IOException)
        {
            // A publish that partially succeeded must not leave an acknowledged permission.
            try
            {
                kernel.Revoke(lease.Handle);
                ledger.RecordReleaseFailure(admissionId);
            }
            finally
            {
                verdicts.Drop(packetId);
            }
            return new AdmissionResult(false, "kernel_unavailable");
        }

        if (expiresAt <= time.GetUtcNow())
        {
            try
            {
                kernel.Revoke(lease.Handle);
                ledger.RecordReleaseFailure(admissionId);
            }
            finally
            {
                verdicts.Drop(packetId);
            }
            return new AdmissionResult(false, "lease_expired_before_release");
        }

        leases.Add(flow, lease);
        // RevokeSubject cannot interleave between evaluation/commit and this release.
        verdicts.Allow(packetId, lease.Handle);
        return new AdmissionResult(true, "admitted", lease.Handle);

        AdmissionResult Deny(string reason)
        {
            verdicts.Drop(packetId);
            return new AdmissionResult(false, reason);
        }
    }

    public void RevokeSubject(string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        lock (sync)
        {
            revokedSubjects.Add(subject);
            foreach (long generation in policy.IdentityGenerationsFor(subject))
            {
                ledger.Revoke(subject, generation);
            }
            foreach (AdmissionLease lease in leases.Values.Where(lease => lease.Subject == subject))
            {
                // An exception means no successful acknowledgement; new admissions stay denied.
                kernel.Revoke(lease.Handle);
            }
        }
    }

    public void ForgetDestroyedFlow(FlowKey flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        lock (sync)
        {
            if (leases.TryGetValue(flow, out AdmissionLease? lease))
            {
                kernel.Revoke(lease.Handle);
                leases.Remove(flow);
            }
        }
    }
}
