using System.Collections.Concurrent;

namespace Kautuka.Gateway.Domain.Memory;

internal sealed class GatewayMemoryStore : IGatewayMemoryStore
{
    private readonly ConcurrentDictionary<string, List<GatewayMemoryFact>> factsByTarget = new(StringComparer.Ordinal);
    private readonly object lockObject = new();

    public void Restore(IReadOnlyList<GatewayMemoryFact> facts)
    {
        lock (lockObject)
        {
            factsByTarget.Clear();
            foreach (GatewayMemoryFact fact in facts)
            {
                string key = BuildKey(fact.TargetType, fact.TargetId);
                if (!factsByTarget.TryGetValue(key, out List<GatewayMemoryFact>? bucket) || bucket is null)
                {
                    bucket = [];
                    factsByTarget[key] = bucket;
                }

                bucket.Add(fact);
            }
        }
    }

    public IReadOnlyList<GatewayMemoryFact> Snapshot()
    {
        lock (lockObject)
        {
            return [.. factsByTarget.Values.SelectMany(static item => item)];
        }
    }

    public IReadOnlyList<GatewayMemoryFact> List(string targetType, string targetId)
    {
        string key = BuildKey(targetType, targetId);
        lock (lockObject)
        {
            return factsByTarget.TryGetValue(key, out List<GatewayMemoryFact>? facts) && facts is not null
                ? [.. facts.OrderByDescending(static item => item.UpdatedAt)]
                : [];
        }
    }

    public bool TryGet(string factId, out GatewayMemoryFact? fact)
    {
        lock (lockObject)
        {
            foreach (List<GatewayMemoryFact> bucket in factsByTarget.Values)
            {
                GatewayMemoryFact? existing = bucket.FirstOrDefault(item => string.Equals(item.Id, factId, StringComparison.Ordinal));
                if (existing is not null)
                {
                    fact = existing;
                    return true;
                }
            }
        }

        fact = null;
        return false;
    }

    public void AddRange(string targetType, string targetId, IReadOnlyList<GatewayMemoryFact> facts)
    {
        if (facts.Count == 0)
        {
            return;
        }

        string key = BuildKey(targetType, targetId);
        lock (lockObject)
        {
            if (!factsByTarget.TryGetValue(key, out List<GatewayMemoryFact>? bucket) || bucket is null)
            {
                bucket = [];
                factsByTarget[key] = bucket;
            }

            foreach (GatewayMemoryFact fact in facts)
            {
                int existingIndex = bucket.FindIndex(existing => string.Equals(existing.Id, fact.Id, StringComparison.Ordinal));
                if (existingIndex >= 0)
                {
                    bucket[existingIndex] = fact;
                }
                else
                {
                    bucket.Add(fact);
                }
            }
        }
    }

    public bool Remove(string factId)
    {
        lock (lockObject)
        {
            foreach (List<GatewayMemoryFact> bucket in factsByTarget.Values)
            {
                int idx = bucket.FindIndex(f => f.Id == factId);
                if (idx >= 0)
                {
                    bucket.RemoveAt(idx);
                    return true;
                }
            }
        }

        return false;
    }

    private static string BuildKey(string targetType, string targetId) => $"{targetType}:{targetId}";
}
