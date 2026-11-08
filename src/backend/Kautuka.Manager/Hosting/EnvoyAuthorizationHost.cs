using Kautuka.Manager.Grpc;
using Kautuka.Manager.RuntimeContainer.Hosting;

using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Kautuka.Manager.Hosting;

// Same Manager process, a dedicated Unix listener exposing only Envoy Check.
internal sealed class EnvoyAuthorizationHost(ContainerEgressRuntime runtime) : IHostedService, IAsyncDisposable
{
    private WebApplication? server;
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!runtime.Enabled) return;
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Egress authorization requires Linux.");
        string path = runtime.AuthorizationSocketPath;
        string directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Authorization socket path has no parent directory.");
        const UnixFileMode directoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
        Directory.CreateDirectory(directory, directoryMode);
        // Explicitly repair permissions if this directory already existed. Its owner and
        // group are the Manager's rootless runtime identity, inherited from the process.
        File.SetUnixFileMode(directory, directoryMode);
        if (Directory.Exists(path)) throw new IOException("Authorization socket path is a directory.");
        File.Delete(path);
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = directory,
        });
        // This private listener must not inherit ambient ASP.NET/Kestrel endpoint
        // configuration, which could add a TCP listener beside the Unix socket.
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        // Core update verification writes a machine-readable JSON result to stdout.
        // Keep this private server's request/startup diagnostics on stderr only.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.WebHost.ConfigureKestrel(options => options.ListenUnixSocket(path, listener => listener.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc(options => { options.MaxReceiveMessageSize = 16384; options.MaxSendMessageSize = 4096; });
        builder.Services.AddSingleton(runtime);
        server = builder.Build();
        server.MapGrpcService<EnvoyAuthorizationService>();
        try
        {
            await server.StartAsync(cancellationToken).ConfigureAwait(false);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
        }
        catch
        {
            await server.DisposeAsync().ConfigureAwait(false);
            server = null;
            throw;
        }
    }
    public Task StopAsync(CancellationToken cancellationToken) => server?.StopAsync(cancellationToken) ?? Task.CompletedTask;
    public async ValueTask DisposeAsync()
    {
        if (server is not null) await server.DisposeAsync().ConfigureAwait(false);
    }
}
