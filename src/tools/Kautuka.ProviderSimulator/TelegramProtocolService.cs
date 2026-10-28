using System.Text.Json.Nodes;

internal sealed class TelegramProtocolService(SimulatorState state, SimulatorFaultExecutor faults)
{
    private static readonly HashSet<string> consumedMethods = new(StringComparer.Ordinal) { "getMe", "getUpdates", "sendMessage", "sendRichMessage", "editMessageText", "sendChatAction", "sendDocument", "getFile", "getChat", "answerCallbackQuery", "deleteMessage", "pinChatMessage", "setMyCommands", "setChatMenuButton", "createForumTopic", "editForumTopic", "closeForumTopic" };
    public async Task<IResult> HandleAsync(HttpRequest request)
    {
        if (string.Equals(request.Path.Value, "/telegram/", StringComparison.Ordinal)) return Results.Ok(new { ok = true });
        if (request.Path.Value?.Contains("/file/bot", StringComparison.Ordinal) == true) return Download(request);
        string method = request.Path.Value?.Split('/').LastOrDefault() ?? string.Empty;
        if (!consumedMethods.Contains(method)) return Results.NotFound(new { ok = false, error = "unmodelled_telegram_method" });
        IResult? fault = await faults.ApplyAsync("telegram/" + method, request.HttpContext.RequestAborted); if (fault is not null) return fault;
        JsonObject body = await SimulatorJson.ReadAsync(request);
        if (method == "getUpdates") return await GetUpdatesAsync(body, request.HttpContext.RequestAborted);
        if (method == "getMe") { state.Write(s => { s.Transcript.Add(SimulatorTranscript.FromJson("outbound", "telegram", method, body)); return 0; }); return Results.Json(new { ok = true, result = new { id = 1, is_bot = true, first_name = "Kautuka Simulator", username = "kautuka_simulator", has_topics_enabled = true } }); }
        if (method == "getFile") { state.Write(s => { s.Transcript.Add(SimulatorTranscript.FromJson("outbound", "telegram", method, body)); return 0; }); return Results.Json(new { ok = true, result = new { file_id = "file", file_path = "files/empty" } }); }
        if (method == "sendRichMessage" && (body["rich_message"] is not JsonObject rich
            || new[] { "markdown", "html", "blocks" }.Count(key => rich[key] is not null) != 1))
            return Results.BadRequest(new { ok = false, error = "invalid_rich_message" });
        long messageId = state.NextMessageId();
        state.Write(s => { s.Transcript.Add(SimulatorTranscript.FromJson("outbound", "telegram", method, body, messageId.ToString(System.Globalization.CultureInfo.InvariantCulture))); return 0; });
        if (method == "createForumTopic") return Results.Json(new { ok = true, result = new { message_thread_id = messageId, name = body["name"]?.GetValue<string>() ?? "Topic" } });
        if (method == "pinChatMessage") return Results.Json(new { ok = true, result = true });
        if (method == "getChat") return Results.Json(new { ok = true, result = new { id = 100, type = "private" } });
        return Results.Json(new { ok = true, result = new { message_id = messageId, date = 0 } });
    }

    private async Task<IResult> GetUpdatesAsync(JsonObject body, CancellationToken cancellationToken)
    {
        long offset = body["offset"]?.GetValue<long>() ?? 0;
        int timeoutSeconds = body["timeout"]?.GetValue<int>() ?? 0;
        if (timeoutSeconds < 0) return Results.BadRequest(new { ok = false, error = "telegram_timeout_must_not_be_negative" });
        state.RecordTelegramPoll(body);
        JsonArray result = await state.WaitForTelegramUpdatesAsync(offset, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken).ConfigureAwait(false);
        return Results.Json(new { ok = true, result });
    }
    private IResult Download(HttpRequest request) { state.Write(s => { s.Transcript.Add(SimulatorTranscript.FromJson("outbound", "telegram", "download", new() { ["path"] = request.Path.Value })); return 0; }); return Results.File(Array.Empty<byte>(), "application/octet-stream"); }
}
