internal sealed class SimulatorFaultExecutor(SimulatorState state)
{
    public void Schedule(string target, SimulatorFault fault) => state.Write(s => { s.Faults[target] = fault; return 0; });

    /// <summary>Consumes one deterministic transport fault without translating it into an HTTP response.</summary>
    public SimulatorFault? Take(string target) => state.Write(s =>
    {
        if (!s.Faults.TryGetValue(target, out SimulatorFault? fault))
        {
            return null;
        }

        if (--fault.Count == 0)
        {
            s.Faults.Remove(target);
        }

        return fault;
    });

    public async Task<IResult?> ApplyAsync(string target, CancellationToken cancellationToken)
    {
        SimulatorFault? fault = Take(target);
        if (fault is null) return null;
        if (fault.Kind == "timeout" && fault.DelayMilliseconds > 0) await Task.Delay(fault.DelayMilliseconds, cancellationToken);
        return fault.Kind switch { "truncated" => Results.Text("{\"ok\":", "application/json", statusCode: 200), "rate_limit" => new SimulatorRateLimitResult(fault.Status == 503 ? 429 : fault.Status), "uncertain_effect" => Results.Json(new { ok = false, error = "uncertain_effect" }, statusCode: 503), "drop" => Results.StatusCode(503), _ => Results.Json(new { ok = false, error = fault.Kind }, statusCode: fault.Status) };
    }
}
internal sealed class SimulatorRateLimitResult(int status) : IResult { public Task ExecuteAsync(HttpContext context) { context.Response.StatusCode = status; context.Response.Headers.RetryAfter = "1"; return context.Response.WriteAsJsonAsync(new { ok = false, error = "rate_limited" }); } }
