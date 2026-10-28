using System.Security.Cryptography;
using System.Text.Json.Nodes;

internal sealed class SlackProtocolService(SimulatorState state, SimulatorFaultExecutor faults)
{
    private static readonly HashSet<string> consumedMethods = new(StringComparer.Ordinal) { "apps.connections.open", "auth.test", "users.info", "conversations.replies", "files.info", "chat.postMessage", "chat.postEphemeral", "chat.update", "chat.delete", "reactions.add", "views.open", "files.getUploadURLExternal", "files.completeUploadExternal" };
    public async Task<IResult> HandleApiAsync(string method, HttpRequest request)
    {
        if (!consumedMethods.Contains(method)) return Results.NotFound(new { ok = false, error = "unmodelled_slack_method" });
        IResult? fault = await faults.ApplyAsync("slack/" + method, request.HttpContext.RequestAborted); if (fault is not null) return fault;
        JsonObject body = await SimulatorJson.ReadAsync(request);
        string? transportMessageId = method is "chat.postMessage" or "chat.postEphemeral" ? "1.000001" : null;
        state.Write(s => { s.Transcript.Add(SimulatorTranscript.FromJson("outbound", "slack", method, body, transportMessageId)); return 0; });
        if (method == "apps.connections.open") return Results.Json(new { ok = true, url = $"ws://{request.Host}/slack/socket" });
        if (method == "auth.test")
        {
            request.HttpContext.Response.Headers.Append("x-oauth-scopes", "app_mentions:read,channels:history,chat:write,files:write,groups:history,im:history,mpim:history,reactions:write");
            return Results.Json(new { ok = true, user_id = "USIM", team_id = "TSIM", bot_id = "BSIM" });
        }
        if (method == "users.info") return Results.Json(new { ok = true, user = new { id = "USIM", name = "simulator" } });
        if (method == "files.getUploadURLExternal") return Results.Json(new { ok = true, upload_url = $"http://{request.Host}/slack/upload/simulated", file_id = "FSIM" });
        return Results.Json(new { ok = true, channel = body["channel"]?.GetValue<string>() ?? "CSIM", ts = "1.000001", message = new { client_msg_id = "simulated" } });
    }
    public async Task<IResult> UploadAsync(string id, HttpRequest request) { using var body = new MemoryStream(); await request.Body.CopyToAsync(body); state.Write(s => { s.Transcript.Add(new("outbound", "slack", "upload:" + id, Convert.ToHexString(SHA256.HashData(body.ToArray())), checked((int)body.Length))); return 0; }); return Results.Ok(); }
    public Task SocketAsync(HttpContext context) => SimulatorSocketProtocol.RunAsync(context, "slack", state, faults, "{\"type\":\"hello\",\"num_connections\":1}", false);
}
