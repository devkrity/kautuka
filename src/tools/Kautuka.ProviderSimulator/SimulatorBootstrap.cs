internal static class SimulatorBootstrap
{
    public static async Task RunAsync(string[] args)
    {
        WebApplication app = Build(args);
        await app.RunAsync();
    }

    internal static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        string controlKey = builder.Configuration["ProviderSimulator:ControlKey"] ?? throw new InvalidOperationException("provider_simulator_control_key_required");
        string listen = builder.Configuration["ProviderSimulator:Listen"] ?? "http://127.0.0.1:0";
        if (!Uri.TryCreate(listen, UriKind.Absolute, out Uri? uri) || !uri.IsLoopback) throw new InvalidOperationException("provider_simulator_loopback_required");
        builder.WebHost.UseUrls(listen);
        builder.Services.AddSingleton(new SimulatorState(builder.Configuration["ProviderSimulator:StateFile"]));
        builder.Services.AddSingleton<SimulatorFaultExecutor>();
        builder.Services.AddSingleton(sp => new SimulatorControlService(controlKey, sp.GetRequiredService<SimulatorState>(), sp.GetRequiredService<SimulatorFaultExecutor>()));
        builder.Services.AddSingleton<TelegramProtocolService>();
        builder.Services.AddSingleton<SlackProtocolService>();
        WebApplication app = builder.Build(); app.UseWebSockets();
        app.Services.GetRequiredService<SimulatorState>().Recover();
        SimulatorControl.Map(app, app.Services.GetRequiredService<SimulatorControlService>());
        TelegramSimulatorModule.Map(app, app.Services.GetRequiredService<TelegramProtocolService>());
        SlackSimulatorModule.Map(app, app.Services.GetRequiredService<SlackProtocolService>());
        app.MapGet("/health", () => Results.Ok(new { status = "ready", protocolVersions = new { telegram = "bot-api-consumed-v1", slack = "socket-mode-consumed-v1" } }));
        return app;
    }
}
