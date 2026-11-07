using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class EgressAdminProbeTests
{
    [Fact]
    [SuppressMessage("Reliability", "CA2025", Justification = "Task.WhenAll drains both the listener and curl tasks before either using scope is disposed.")]
    public async Task AdminProbeWaitsForDelayedListenerWhereOriginalProbeFails()
    {
        using Socket server = BoundSocket();
        string url = Address(server);
        Result original = await CurlAsync(["--silent", "--show-error", "--fail", "--max-time", "2", url]);
        Assert.Equal(7, original.ExitCode);
        string? export = Environment.GetEnvironmentVariable("KAUTUKA_EGRESS_ADMIN_ARGUMENTS_OUTPUT");
        if (!string.IsNullOrWhiteSpace(export))
            await File.WriteAllTextAsync(export, JsonSerializer.Serialize(ContainerEgressRuntime.AdminProbeArguments()), TestContext.Current.CancellationToken);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task response = RespondOnceAsync(server, 200, TimeSpan.FromSeconds(2), deadline.Token);
        Task<Result> request = CurlAsync(Arguments(url));
        await Task.WhenAll(response, request);
        Result actual = await request;
        Assert.Equal(0, actual.ExitCode);
        Assert.Equal("{\"configs\":[]}", actual.StdOut);
    }

    [Fact]
    public async Task AdminProbeStillFailsWhenListenerNeverStarts()
    {
        using Socket server = BoundSocket();
        Result actual = await CurlAsync(Arguments(Address(server)));
        Assert.Equal(7, actual.ExitCode);
        Assert.Empty(actual.StdOut);
    }

    [Fact]
    [SuppressMessage("Reliability", "CA2025", Justification = "Task.WhenAll drains both the listener and curl tasks before either using scope is disposed.")]
    public async Task AdminProbeDoesNotConvertForbiddenResponseToSuccess()
    {
        using Socket server = BoundSocket();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task response = RespondOnceAsync(server, 403, TimeSpan.Zero, deadline.Token);
        Task<Result> request = CurlAsync(Arguments(Address(server)));
        await Task.WhenAll(response, request);
        Result actual = await request;
        Assert.Equal(22, actual.ExitCode);
        Assert.Empty(actual.StdOut);
    }

    private static Socket BoundSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.ExclusiveAddressUse = true;
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return socket;
    }

    private static string Address(Socket socket) => $"http://127.0.0.1:{((IPEndPoint)socket.LocalEndPoint!).Port}/config_dump";

    private static string[] Arguments(string url) => ContainerEgressRuntime.AdminProbeArguments()
        .Select(argument => argument == "http://127.0.0.1:9901/config_dump" ? url : argument).ToArray();

    private static async Task RespondOnceAsync(Socket server, int status, TimeSpan delay, CancellationToken cancellationToken)
    {
        await Task.Delay(delay, cancellationToken);
        server.Listen(1);
        using Socket client = await server.AcceptAsync(cancellationToken);
        using var stream = new NetworkStream(client, ownsSocket: false);
        byte[] buffer = new byte[4096];
        int count = 0;
        while (count < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(count), cancellationToken);
            if (read == 0) throw new IOException("Probe closed before sending its request.");
            count += read;
            if (Encoding.ASCII.GetString(buffer, 0, count).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
        }
        string body = status == 200 ? "{\"configs\":[]}" : "";
        byte[] response = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Fixture\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");
        await stream.WriteAsync(response, cancellationToken);
    }

    private static async Task<Result> CurlAsync(string[] arguments)
    {
        string executable = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "curl.exe")
            : "/usr/bin/curl";
        Assert.SkipUnless(File.Exists(executable), "Requires the operating system curl executable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        foreach (string name in start.Environment.Keys.Where(static name => name.EndsWith("_proxy", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(name);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start curl.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task<string> output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            return new Result(process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    private sealed record Result(int ExitCode, string StdOut, string StdErr);
}
