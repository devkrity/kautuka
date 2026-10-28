internal static class SlackSimulatorModule
{
    public static void Map(WebApplication app, SlackProtocolService protocol)
    {
        app.Map("/slack/api/{method}", async (string method, HttpRequest r) => await protocol.HandleApiAsync(method, r));
        app.MapPut("/slack/upload/{id}", async (string id, HttpRequest r) => await protocol.UploadAsync(id, r));
        app.Map("/slack/socket", async (HttpContext c) => await protocol.SocketAsync(c));
    }
}
