using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

internal static class SimulatorSocketProtocol
{
    public static async Task RunAsync(HttpContext context, string provider, SimulatorState state, SimulatorFaultExecutor faults, string hello, bool heartbeatAck)
    {
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
        state.Write(s =>
        {
            s.Transcript.Add(new SimulatorTranscript("inbound", provider, "websocket_connect", string.Empty, 0));
            return 0;
        });
        await SendAsync(socket, hello, context.RequestAborted);
        if (faults.Take(provider + "/socket") is { Kind: "disconnect" })
        {
            await socket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "simulated_disconnect", context.RequestAborted);
            return;
        }
        byte[] bytes = new byte[8192];
        while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
        {
            if (faults.Take(provider + "/socket") is { Kind: "disconnect" })
            {
                await socket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "simulated_disconnect", context.RequestAborted);
                return;
            }
            string? pending = state.Write(s => s.SocketEvents[provider].Count == 0 ? null : s.SocketEvents[provider].Dequeue());
            if (pending is not null) { await SendAsync(socket, pending, context.RequestAborted); continue; }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted); timeout.CancelAfter(TimeSpan.FromMilliseconds(100));
            WebSocketReceiveResult received;
            try { received = await socket.ReceiveAsync(bytes, timeout.Token); } catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested) { continue; }
            if (received.MessageType == WebSocketMessageType.Close) break;
            state.Write(s => { s.Transcript.Add(new("outbound", provider, "websocket", Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, received.Count))), received.Count)); return 0; });
            if (heartbeatAck) await SendAsync(socket, "{\"op\":11,\"d\":null}", context.RequestAborted);
        }
    }
    private static Task SendAsync(WebSocket socket, string value, CancellationToken cancellationToken) => socket.SendAsync(Encoding.UTF8.GetBytes(value), WebSocketMessageType.Text, true, cancellationToken);
}
