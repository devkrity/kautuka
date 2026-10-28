internal static class SimulatorControl
{
    public static void Map(WebApplication app, SimulatorControlService control)
    {
        app.MapPost("/control/reset", (HttpRequest r) => control.Authorize(r) ? Results.Ok(control.Reset()) : Results.Unauthorized());
        app.MapPost("/control/faults", async (HttpRequest r) => control.Authorize(r) ? Results.Ok(await control.ScheduleFaultAsync(r)) : Results.Unauthorized());
        app.MapPost("/control/inject/{provider}", async (string provider, HttpRequest r) => control.Authorize(r) ? Results.Ok(await control.InjectAsync(provider, r)) : Results.Unauthorized());
        app.MapPost("/control/time/advance", async (HttpRequest r) => control.Authorize(r) ? Results.Ok(await control.AdvanceAsync(r)) : Results.Unauthorized());
        app.MapGet("/control/evidence", (HttpRequest r) => control.Authorize(r) ? Results.Json(control.Evidence()) : Results.Unauthorized());
    }
}
