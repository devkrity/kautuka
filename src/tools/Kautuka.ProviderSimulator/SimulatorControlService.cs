using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

internal sealed class SimulatorControlService(string controlKey, SimulatorState state, SimulatorFaultExecutor faults)
{
    public bool Authorize(HttpRequest request) => request.Headers.TryGetValue("X-Kautuka-Simulator-Key", out var key) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key.ToString()), Encoding.UTF8.GetBytes(controlKey));
    public object Reset() => state.Reset();
    public async Task<object> AdvanceAsync(HttpRequest request) => new { virtualMilliseconds = state.Advance((await SimulatorJson.ReadAsync(request))["milliseconds"]?.GetValue<long>() ?? 0) };
    public async Task<object> ScheduleFaultAsync(HttpRequest request)
    {
        JsonObject body = await SimulatorJson.ReadAsync(request); string target = body["target"]?.GetValue<string>() ?? throw new BadHttpRequestException("fault_target_required");
        faults.Schedule(target, new(body["status"]?.GetValue<int>() ?? 503, body["kind"]?.GetValue<string>() ?? "transient", body["count"]?.GetValue<int>() ?? 1, body["delayMilliseconds"]?.GetValue<int>() ?? 0)); return new { scheduled = target };
    }
    public async Task<object> InjectAsync(string provider, HttpRequest request)
    {
        if (provider is not ("telegram" or "slack")) throw new BadHttpRequestException("provider_unknown");
        JsonObject payload = await SimulatorJson.ReadAsync(request);
        if (provider == "telegram") state.InjectTelegramUpdate(payload);
        else state.Write(s =>
        {
            s.Transcript.Add(SimulatorTranscript.FromJson("inbound", provider, "injected", payload)); string wrapped = provider == "slack" ? new JsonObject { ["envelope_id"] = "sim-" + s.Transcript.Count, ["type"] = payload["_simulatorEnvelopeType"]?.GetValue<string>() ?? "events_api", ["payload"] = payload }.ToJsonString() : new JsonObject { ["op"] = 0, ["s"] = s.Transcript.Count, ["t"] = payload["_simulatorDispatchType"]?.GetValue<string>() ?? "MESSAGE_CREATE", ["d"] = payload }.ToJsonString(); payload.Remove("_simulatorEnvelopeType"); payload.Remove("_simulatorDispatchType"); s.SocketEvents[provider].Enqueue(wrapped); return 0;
        });
        return new { injected = provider };
    }
    public object Evidence() => state.Read(s => new { virtualMilliseconds = s.VirtualMilliseconds, transcript = s.Transcript.ToArray(), pendingTelegramUpdates = s.TelegramUpdates.Count, pendingSocketEvents = s.SocketEvents.ToDictionary(x => x.Key, x => x.Value.Count) });
}

internal static class SimulatorJson { public static async Task<JsonObject> ReadAsync(HttpRequest request) { if (request.ContentLength == 0 || (!request.ContentLength.HasValue && !request.Headers.ContainsKey("Transfer-Encoding"))) return []; try { return (await JsonNode.ParseAsync(request.Body) as JsonObject) ?? []; } catch (System.Text.Json.JsonException) { throw new BadHttpRequestException("malformed_json"); } } }
