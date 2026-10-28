using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal sealed class SimulatorState(string? stateFile)
{
    private readonly object gate = new();
    private List<JsonObject> telegramUpdates = [];
    private List<SimulatorTranscript> transcript = [];
    private Dictionary<string, SimulatorFault> faults = new(StringComparer.Ordinal);
    private Dictionary<string, Queue<string>> socketEvents = new(StringComparer.Ordinal) { ["slack"] = [] };
    private readonly TelegramUpdateSignal telegramUpdatesChanged = new();
    private long virtualMilliseconds;
    private long nextMessageId = 100;

    public void Recover()
    {
        if (string.IsNullOrWhiteSpace(stateFile) || !File.Exists(stateFile)) return;
        lock (gate)
        {
            PersistedSimulatorState? saved = JsonSerializer.Deserialize<PersistedSimulatorState>(File.ReadAllText(stateFile));
            if (saved is null) return;
            virtualMilliseconds = saved.VirtualMilliseconds; nextMessageId = saved.NextMessageId;
            telegramUpdates = saved.TelegramUpdates ?? []; transcript = saved.Transcript ?? [];
            faults = saved.Faults ?? new(StringComparer.Ordinal);
            socketEvents = saved.SocketEvents ?? new(StringComparer.Ordinal);
            socketEvents.TryAdd("slack", []);
        }
    }
    public T Read<T>(Func<SimulatorSnapshot, T> operation) { lock (gate) return operation(new(virtualMilliseconds, nextMessageId, telegramUpdates, transcript, faults, socketEvents)); }
    public T Write<T>(Func<SimulatorSnapshot, T> operation) { lock (gate) { var value = operation(new(virtualMilliseconds, nextMessageId, telegramUpdates, transcript, faults, socketEvents)); Persist(); return value; } }
    public void RecordTelegramPoll(JsonObject body) => Write(s => { s.Transcript.Add(SimulatorTranscript.FromJson("outbound", "telegram", "getUpdates", body)); return 0; });

    public async Task<JsonArray> WaitForTelegramUpdatesAsync(long offset, TimeSpan timeout, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            Task changed;
            lock (gate)
            {
                JsonArray available = TelegramUpdatesFromOffset(offset);
                if (available.Count > 0 || timeout <= TimeSpan.Zero) return available;
                changed = telegramUpdatesChanged.Capture();
            }

            TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) return [];
            try { await changed.WaitAsync(remaining, cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException) { return []; }
        }
    }

    public void InjectTelegramUpdate(JsonObject payload)
    {
        lock (gate)
        {
            payload["update_id"] ??= checked((long)telegramUpdates.Count + 1L);
            bool duplicate = payload["_simulatorDuplicate"]?.GetValue<bool>() == true;
            bool order = payload["_simulatorOutOfOrder"]?.GetValue<bool>() == true;
            payload.Remove("_simulatorDuplicate"); payload.Remove("_simulatorOutOfOrder");
            telegramUpdates.Add(payload);
            if (duplicate) telegramUpdates.Add((JsonObject)payload.DeepClone());
            if (order) telegramUpdates.Reverse();
            Persist();
        }
        telegramUpdatesChanged.Pulse();
    }
    public long NextMessageId() => Write(s => ++nextMessageId);
    public void SetNextMessageId(long value) => Write(s => nextMessageId = value);
    public object Reset()
    {
        object result = Write(s => { telegramUpdates.Clear(); transcript.Clear(); faults.Clear(); foreach (var q in socketEvents.Values) q.Clear(); virtualMilliseconds = 0; nextMessageId = 100; return new { reset = true }; });
        telegramUpdatesChanged.Pulse();
        return result;
    }
    public long Advance(long milliseconds) => Write(s => virtualMilliseconds += milliseconds);
    private void Persist()
    {
        if (string.IsNullOrWhiteSpace(stateFile)) return;
        string fullPath = Path.GetFullPath(stateFile); Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporary = fullPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new PersistedSimulatorState(virtualMilliseconds, nextMessageId, telegramUpdates, transcript, faults, socketEvents)));
        File.Move(temporary, fullPath, true);
    }

    private JsonArray TelegramUpdatesFromOffset(long offset)
        => new(telegramUpdates
            .Where(update => update["update_id"]?.GetValue<long>() >= offset)
            .Select(update => update.DeepClone())
            .ToArray());
}

internal sealed record SimulatorSnapshot(long VirtualMilliseconds, long NextMessageId, List<JsonObject> TelegramUpdates, List<SimulatorTranscript> Transcript, Dictionary<string, SimulatorFault> Faults, Dictionary<string, Queue<string>> SocketEvents);
internal sealed record PersistedSimulatorState(long VirtualMilliseconds, long NextMessageId, List<JsonObject>? TelegramUpdates, List<SimulatorTranscript>? Transcript, Dictionary<string, SimulatorFault>? Faults, Dictionary<string, Queue<string>>? SocketEvents);
internal sealed class SimulatorFault(int status, string kind, int count, int delayMilliseconds) { public int Status { get; } = status; public string Kind { get; } = kind; public int Count { get; set; } = count; public int DelayMilliseconds { get; } = delayMilliseconds; }
internal sealed record SimulatorTranscript(
    string Direction,
    string Provider,
    string Operation,
    string PayloadSha256,
    int PayloadBytes,
    IReadOnlyList<string>? ActionTokens = null,
    string? TransportMessageId = null)
{
    public static SimulatorTranscript FromJson(
        string direction,
        string provider,
        string operation,
        JsonObject payload,
        string? transportMessageId = null)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        return new(
            direction,
            provider,
            operation,
            Convert.ToHexString(SHA256.HashData(bytes)),
            bytes.Length,
            FindActionTokens(payload),
            transportMessageId);
    }

    private static string[] FindActionTokens(JsonNode node)
    {
        List<string> tokens = [];
        Visit(node, tokens);
        return [.. tokens.Distinct(StringComparer.Ordinal)];
    }

    private static void Visit(JsonNode? node, List<string> tokens)
    {
        if (node is JsonValue value
            && value.TryGetValue(out string? text)
            && text.StartsWith("hitl:", StringComparison.Ordinal))
        {
            tokens.Add(text);
            return;
        }

        if (node is JsonObject item)
        {
            foreach (KeyValuePair<string, JsonNode?> property in item)
            {
                Visit(property.Value, tokens);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (JsonNode? child in array)
            {
                Visit(child, tokens);
            }
        }
    }
}
