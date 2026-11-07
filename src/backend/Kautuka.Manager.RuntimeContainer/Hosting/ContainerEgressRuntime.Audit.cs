using System.Globalization;
using System.Text.Json;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

public sealed partial class ContainerEgressRuntime
{
    private readonly NetworkAccessAuditStore networkAudit = new(Path.Combine(stateRoot, "egress", "audit"));
    private string? auditCollectionError;

    public NetworkAuditPage GetNetworkAudit(NetworkAccessActor actor, long before = 0, int limit = 50)
        => networkAudit.Read(actor, before, limit, Volatile.Read(ref auditCollectionError));

    public async Task CollectNetworkAuditAsync(CancellationToken cancellationToken)
    {
        if (!Enabled) return;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await CollectNetworkAuditCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task CollectNetworkAuditCoreAsync(CancellationToken ct)
    {
        try
        {
            string? raw = await PodmanContainerInspector.ReadAsync(runner, "kautuka-egress-envoy", ct).ConfigureAwait(false);
            if (raw is null) return;
            using JsonDocument document = JsonDocument.Parse(raw);
            JsonElement container = PodmanContainerInspector.Container(document.RootElement);
            JsonElement labels = container.GetProperty("Config").GetProperty("Labels");
            string id = container.GetProperty("Id").GetString()!;
            if (!IsId(id) || labels.GetProperty(ownerLabel).GetString() != "true"
                || labels.GetProperty("kautuka.managed").GetString() != "manager"
                || container.GetProperty("Name").GetString()!.TrimStart('/') != "kautuka-egress-envoy")
                throw new InvalidOperationException("Cannot confirm the managed audit log source.");

            (string? previous, DateTimeOffset? cursor, string? lastLineId) = networkAudit.Checkpoint;
            DateTimeOffset until = DateTimeOffset.UtcNow;
            bool changedContainer = previous is not null && previous != id;
            // Rotation/removal may have retired the previous container before
            // its final read. Report the gap rather than claiming continuity.
            bool gap = changedContainer || cursor > until;
            // A bounded overlap with the previous actual log record detects
            // same-container rotation as well as a burst exceeding the tail.
            PodmanLogBatch batch = await runner.ReadLogsAsync(id, DateTimeOffset.UnixEpoch.ToString("O", CultureInfo.InvariantCulture),
                until.ToString("O", CultureInfo.InvariantCulture), 512 * 1024, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (batch.Result.ExitCode != 0)
                throw new IOException("The native proxy log read did not complete.");
            string[] lines = [.. batch.Result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Concat(batch.Result.StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries)).Order(StringComparer.Ordinal)];
            string emptyMarker = NetworkAccessAuditStore.LogLineId(id, string.Empty);
            bool overlaps = previous == id && lastLineId is not null
                && (lastLineId == emptyMarker && !batch.TailSaturated || lines.Any(line => NetworkAccessAuditStore.LogLineId(id, line) == lastLineId));
            gap |= batch.Truncated || lastLineId is not null && !overlaps || batch.TailSaturated && !overlaps;
            NetworkAuditBatch observed = networkAudit.Observe(id, lines, gap);
            networkPolicy.ObserveDeniedBatch(observed.Denials);
            // This is a read high-water mark, not a completeness guarantee.
            // Any truncated/rotated interval remains explicitly recorded above.
            // Advancing after a known gap lets later traffic be collected.
            string marker = observed.LastLineId ?? emptyMarker;
            if (observed.Changed || previous != id || marker != lastLineId)
                networkAudit.CommitCheckpoint(id, until, marker);
            auditCollectionError = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            auditCollectionError = "Network audit collection is unavailable. Recent connection records may be missing.";
            throw;
        }
    }

    private async Task TryFinalNetworkAuditAsync(CancellationToken ct)
    {
        if (!Enabled) return;
        try { await CollectNetworkAuditCoreAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Logging must not prevent a fail-closed shutdown. If storage is
            // still usable, retain a durable indication of the missed drain.
            try { networkAudit.Observe(string.Empty, [], coverageGap: true); }
            catch (Exception) { }
        }
    }
}
