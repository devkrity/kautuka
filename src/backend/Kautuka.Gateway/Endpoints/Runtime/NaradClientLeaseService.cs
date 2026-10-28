using System.Collections.Concurrent;

namespace Kautuka.Gateway.Endpoints.Runtime;

internal sealed record NaradClientLease(
    string ClientExecutorId,
    string LeaseId,
    string UserId,
    DateTimeOffset ExpiresAt,
    TimeSpan LeaseDuration);

internal interface INaradClientLeaseService
{
    bool IsClientExecutorSupported { get; }
    NaradClientLease Attach(string userId, string clientExecutorId, TimeSpan leaseDuration);
    bool TryRenew(string userId, string clientExecutorId, string leaseId, out NaradClientLease? lease);
    bool IsLeaseValid(string userId, string clientExecutorId, string leaseId);
}

internal sealed class NaradClientLeaseService : INaradClientLeaseService
{
    internal const int MaxExecutorsPerUser = 8;
    internal static readonly TimeSpan ExpiredLeaseRetention = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, NaradClientLease> leases = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> executorCounts = new(StringComparer.Ordinal);

    public bool IsClientExecutorSupported => false;

    public NaradClientLease Attach(string userId, string clientExecutorId, TimeSpan leaseDuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientExecutorId);
        SweepExpiredLeases();

        int count = executorCounts.AddOrUpdate(userId, 1, static (_, current) => current + 1);
        if (count > MaxExecutorsPerUser)
        {
            executorCounts.AddOrUpdate(userId, 0, static (_, current) => Math.Max(0, current - 1));
            throw new InvalidOperationException($"User '{userId}' exceeded the maximum of {MaxExecutorsPerUser} client executors.");
        }

        string key = LeaseKey(userId, clientExecutorId);
        var lease = new NaradClientLease(
            clientExecutorId,
            Guid.NewGuid().ToString("N"),
            userId,
            DateTimeOffset.UtcNow.Add(leaseDuration),
            leaseDuration);
        leases[key] = lease;
        return lease;
    }

    public bool TryRenew(string userId, string clientExecutorId, string leaseId, out NaradClientLease? lease)
    {
        lease = null;
        SweepExpiredLeases();
        string key = LeaseKey(userId, clientExecutorId);
        if (!leases.TryGetValue(key, out NaradClientLease? current)
            || !string.Equals(current.LeaseId, leaseId, StringComparison.Ordinal)
            || current.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            leases.TryRemove(key, out _);
            return false;
        }

        lease = current with { ExpiresAt = DateTimeOffset.UtcNow.Add(current.LeaseDuration) };
        leases[key] = lease;
        return true;
    }

    public bool IsLeaseValid(string userId, string clientExecutorId, string leaseId)
    {
        SweepExpiredLeases();
        string key = LeaseKey(userId, clientExecutorId);
        return leases.TryGetValue(key, out NaradClientLease? current)
            && string.Equals(current.LeaseId, leaseId, StringComparison.Ordinal)
            && current.ExpiresAt > DateTimeOffset.UtcNow;
    }

    private void SweepExpiredLeases()
    {
        DateTimeOffset threshold = DateTimeOffset.UtcNow.Subtract(ExpiredLeaseRetention);
        foreach (KeyValuePair<string, NaradClientLease> entry in leases)
        {
            if (entry.Value.ExpiresAt < threshold)
            {
                if (leases.TryRemove(entry.Key, out NaradClientLease? removed))
                {
                    DecrementExecutorCount(removed.UserId);
                }
            }
        }
    }

    private void DecrementExecutorCount(string userId)
        => executorCounts.AddOrUpdate(userId, 0, static (_, current) => Math.Max(0, current - 1));

    private static string LeaseKey(string userId, string clientExecutorId) => $"{userId}:{clientExecutorId}";
}
