using System.Text.Json.Nodes;

namespace Kautuka.Gateway.Endpoints.Runtime;

internal sealed class NaradSseWriteContext : IAsyncDisposable
{
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly HttpContext httpContext;
    private readonly CancellationTokenSource heartbeatCancellation = new();
    private readonly Task heartbeatTask;
    private bool started;

    public NaradSseWriteContext(HttpContext httpContext, int heartbeatIntervalMs)
    {
        this.httpContext = httpContext;
        heartbeatTask = RunHeartbeatAsync(heartbeatIntervalMs, heartbeatCancellation.Token);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (started)
        {
            return;
        }

        started = true;
        await httpContext.Response.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteCommentAsync(string comment, CancellationToken cancellationToken)
    {
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await httpContext.Response.WriteAsync($": {comment}\n\n", cancellationToken).ConfigureAwait(false);
            await httpContext.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }

    public Task WriteControlAsync(JsonObject payload, CancellationToken cancellationToken)
        => WriteDataAsync(payload, eventId: null, cancellationToken);

    public Task WriteAuthorityAsync(JsonObject payload, string eventId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        return WriteDataAsync(payload, eventId, cancellationToken);
    }

    public Task WriteWatchAsync(JsonObject payload, string eventId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        return WriteDataAsync(payload, eventId, cancellationToken);
    }

    public Task WriteHydrateAsync(JsonObject payload, CancellationToken cancellationToken)
        => WriteDataAsync(payload, eventId: null, cancellationToken);

    public async Task WriteStreamErrorAsync(string code, string message, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["type"] = "server.error",
            ["code"] = code,
            ["message"] = message,
        };
        await WriteControlAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteDataAsync(JsonObject payload, string? eventId, CancellationToken cancellationToken)
    {
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrWhiteSpace(eventId))
            {
                await httpContext.Response.WriteAsync($"id: {eventId}\n", cancellationToken).ConfigureAwait(false);
            }

            await httpContext.Response.WriteAsync($"event: {NaradSseWriter.EventName}\n", cancellationToken).ConfigureAwait(false);
            await httpContext.Response.WriteAsync($"data: {payload.ToJsonString()}\n\n", cancellationToken).ConfigureAwait(false);
            await httpContext.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }

    private async Task RunHeartbeatAsync(int heartbeatIntervalMs, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(1_000, heartbeatIntervalMs)));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var heartbeat = new JsonObject
            {
                ["type"] = "server.heartbeat",
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
            };
            try
            {
                await WriteControlAsync(heartbeat, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (InvalidOperationException)
            {
                break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await heartbeatCancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await heartbeatTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        writeLock.Dispose();
        heartbeatCancellation.Dispose();
    }
}

internal static class NaradSseWriter
{
    internal const string EventName = "narad";

    public static void ConfigureResponse(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";
        context.Response.Headers["X-Accel-Buffering"] = "no";
    }

    public static Task WriteControlAsync(HttpContext context, JsonObject payload, CancellationToken cancellationToken)
        => WriteDataAsync(context, payload, eventId: null, cancellationToken);

    public static Task WriteAuthorityAsync(HttpContext context, JsonObject payload, string eventId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        return WriteDataAsync(context, payload, eventId, cancellationToken);
    }

    public static Task WriteWatchAsync(HttpContext context, JsonObject payload, string eventId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        return WriteDataAsync(context, payload, eventId, cancellationToken);
    }

    public static Task WriteHydrateAsync(HttpContext context, JsonObject payload, CancellationToken cancellationToken)
        => WriteDataAsync(context, payload, eventId: null, cancellationToken);

    private static async Task WriteDataAsync(
        HttpContext context,
        JsonObject payload,
        string? eventId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(eventId))
        {
            await context.Response.WriteAsync($"id: {eventId}\n", cancellationToken).ConfigureAwait(false);
        }

        await context.Response.WriteAsync($"event: {EventName}\n", cancellationToken).ConfigureAwait(false);
        await context.Response.WriteAsync($"data: {payload.ToJsonString()}\n\n", cancellationToken).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
