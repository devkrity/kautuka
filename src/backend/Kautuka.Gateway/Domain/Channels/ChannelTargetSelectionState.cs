namespace Kautuka.Gateway.Domain.Channels;

/// <summary>Bounded, expiring state for one-time target picker callbacks.</summary>
internal sealed class ChannelTargetSelectionState(TimeProvider? timeProvider = null)
{
    private const int maximumEntries = 1024;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly object sync = new();
    private readonly Dictionary<string, Picker> pickers = new(StringComparer.Ordinal);
    public string Create(string registration, string user, string chat, GatewayChannelTargetBinding[] targets)
    {
        lock (sync)
        {
            Cleanup();
            while (pickers.Count >= maximumEntries) pickers.Remove(pickers.First().Key);
            string id = Guid.NewGuid().ToString("N");
            pickers[id] = new Picker(registration, user, chat, targets, clock.GetUtcNow().AddMinutes(10));
            return id;
        }
    }

    public bool TrySelect(string id, int index, string registration, string user, string chat, out GatewayChannelTargetBinding? target)
    {
        lock (sync)
        {
            Cleanup();
            target = null;
            if (!pickers.TryGetValue(id, out Picker? picker)
                || picker.Registration != registration || picker.User != user || picker.Chat != chat
                || index < 0 || index >= picker.Targets.Length) return false;
            foreach (string key in pickers.Where(item => item.Value.Registration == registration && item.Value.User == user && item.Value.Chat == chat).Select(item => item.Key).ToArray()) pickers.Remove(key);
            target = picker.Targets[index];
            return true;
        }
    }

    public void Invalidate(string registration, string user, string chat)
    {
        lock (sync)
        {
            foreach (string key in pickers.Where(item => item.Value.Registration == registration
                && item.Value.User == user && item.Value.Chat == chat).Select(item => item.Key).ToArray())
                pickers.Remove(key);
        }
    }

    private void Cleanup()
    {
        DateTimeOffset now = clock.GetUtcNow();
        foreach (string key in pickers.Where(item => item.Value.ExpiresAt <= now).Select(item => item.Key).ToArray()) pickers.Remove(key);
    }

    private sealed record Picker(string Registration, string User, string Chat, GatewayChannelTargetBinding[] Targets, DateTimeOffset ExpiresAt);
}
