internal static class TelegramSimulatorModule
{
    public static void Map(WebApplication app, TelegramProtocolService protocol)
    {
        app.Map("/telegram/{**path}", async (HttpRequest r) => await protocol.HandleAsync(r));
    }
}
