using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kautuka.NetworkAccessController;

public enum EnvoyListenerTransportMode
{
    UnixDomainSocket,
    TcpMtls,
}

public sealed record EnvoyAuthorizationHostOptions
{
    public EnvoyListenerTransportMode TransportMode { get; init; } = EnvoyListenerTransportMode.UnixDomainSocket;

    public required string LockFilePath { get; init; }

    /// <summary>Used when <see cref="TransportMode"/> is <see cref="EnvoyListenerTransportMode.UnixDomainSocket"/>.</summary>
    public string? SocketPath { get; init; }

    /// <summary>Used when <see cref="TransportMode"/> is <see cref="EnvoyListenerTransportMode.TcpMtls"/>.</summary>
    public IPAddress? TcpBindAddress { get; init; }

    public int TcpPort { get; init; } = 50051;

    public X509Certificate2? ServerCertificate { get; init; }

    public X509Certificate2? CaCertificate { get; init; }

    public string ExpectedServerSan { get; init; } = EnvoyMtlsCertificateAuthority.DefaultServerDnsSan;

    public string ExpectedClientSan { get; init; } = EnvoyMtlsCertificateAuthority.DefaultClientDnsSan;

    /// <summary>Test-only negative control flag for C5.NC to allow binding 0.0.0.0 in a negative control fixture.</summary>
    public bool AllowWildcardBindForNegativeControl { get; init; }

    public EnvoyAuthorizationFaultHook? FaultHook { get; init; }
}

/// <summary>
/// Second, minimal Kestrel host inside Manager (Section 4.1.2).
/// <list type="bullet">
/// <item>Uses its own DI container, sharing only the <see cref="NetworkAccessController"/> singleton.</item>
/// <item>Maps <b>only</b> <see cref="EnvoyAuthorizationService"/> (<c>envoy.service.auth.v3.Authorization/Check</c>) — none of Manager's own gRPC services (C16).</item>
/// <item>Sets <c>AllowAlternateSchemes = true</c> on this host only so Envoy's <c>:scheme http</c> over TLS is accepted.</item>
/// <item>Acquires an exclusive lock on <c>networkaccess.lock</c> <b>before</b> unlinking any stale UDS socket (MF2 / C2).</item>
/// <item>Creates a dedicated <c>0710</c> directory and <c>0660</c> socket on Linux for rootless Envoy group access (MF3 / C3).</item>
/// <item>Validates the dedicated mTLS CA, server SAN, client SAN, and non-wildcard bind address on Windows TCP+mTLS (MF4, MF5 / C4, C5).</item>
/// </list>
/// </summary>
public sealed class EnvoyAuthorizationHost : IHostedService, IAsyncDisposable
{
    private const int lockExclusiveNonBlocking = 2 | 4; // LOCK_EX | LOCK_NB
    private const int lockUnlock = 8;                   // LOCK_UN

    private readonly NetworkAccessController controller;
    private readonly EnvoyAuthorizationHostOptions options;
    private readonly EnvoyAuthorizationService authService;
    private FileStream? lockFileStream;
    private WebApplication? server;

    public EnvoyAuthorizationHost(NetworkAccessController controller, EnvoyAuthorizationHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(options);
        this.controller = controller;
        this.options = options;
        this.authService = new EnvoyAuthorizationService(controller, faultHook: options.FaultHook);
    }

    public bool IsListening { get; private set; }

    public int? BoundTcpPort { get; private set; }

    public IReadOnlyList<string> BoundAddresses { get; private set; } = [];

    public EnvoyAuthorizationService AuthorizationService => authService;

    public static EnvoyAuthorizationHost Create(
        NetworkAccessController controller,
        EnvoyAuthorizationHostOptions options)
        => new(controller, options);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsListening)
        {
            return;
        }

        // Step 1 (MF2 / C2): Acquire exclusive single-instance lock BEFORE touching any socket file!
        if (!TryAcquireSingleInstanceLock())
        {
            controller.EnvoyListenerHealthy = false;
            return;
        }

        try
        {
            string contentRoot;
            if (options.TransportMode == EnvoyListenerTransportMode.UnixDomainSocket)
            {
                if (string.IsNullOrWhiteSpace(options.SocketPath))
                {
                    throw new InvalidOperationException("SocketPath is required for UnixDomainSocket transport.");
                }

                string fullSocketPath = Path.GetFullPath(options.SocketPath);
                string socketDir = Path.GetDirectoryName(fullSocketPath)
                    ?? throw new InvalidOperationException("SocketPath must have a parent directory.");
                contentRoot = socketDir;

                PrepareUnixSocketDirectoryAndRemoveStaleSocket(socketDir, fullSocketPath);
            }
            else
            {
                if (!ValidateTcpMtlsOptions())
                {
                    ReleaseSingleInstanceLock();
                    controller.EnvoyListenerHealthy = false;
                    return;
                }

                contentRoot = Path.GetDirectoryName(Path.GetFullPath(options.LockFilePath)) ?? AppContext.BaseDirectory;
                Directory.CreateDirectory(contentRoot);
            }

            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                Args = [],
                ContentRootPath = contentRoot,
            });

            // Clear ambient configuration so ASPNETCORE_URLS or Kestrel env vars never add listeners.
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection();

            builder.Logging.ClearProviders();
            builder.Logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);

            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                // Envoy's envoy_grpc sends :scheme http even over TLS; enable on this host ONLY (4.1.2 / 4.1.3).
                kestrel.AllowAlternateSchemes = true;

                if (options.TransportMode == EnvoyListenerTransportMode.UnixDomainSocket)
                {
                    string fullSocketPath = Path.GetFullPath(options.SocketPath!);
                    kestrel.ListenUnixSocket(fullSocketPath, listenOptions =>
                    {
                        listenOptions.Protocols = HttpProtocols.Http2;
                    });
                }
                else
                {
                    kestrel.Listen(options.TcpBindAddress!, options.TcpPort, listenOptions =>
                    {
                        listenOptions.Protocols = HttpProtocols.Http2;
                        listenOptions.UseHttps(options.ServerCertificate!, https =>
                        {
                            https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                            https.ClientCertificateValidation = (cert, _, _) =>
                            {
                                if (cert is null)
                                {
                                    return false;
                                }

                                using var cert2 = new X509Certificate2(cert);
                                return EnvoyMtlsCertificateAuthority.ValidateEnvoyClientCertificate(
                                    cert2,
                                    options.CaCertificate,
                                    options.ExpectedClientSan);
                            };
                        });
                    });
                }
            });

            builder.Services.AddGrpc(grpc =>
            {
                grpc.MaxReceiveMessageSize = 64 * 1024;
                grpc.MaxSendMessageSize = 16 * 1024;
            });
            builder.Services.AddSingleton(controller);
            builder.Services.AddSingleton(authService);

            server = builder.Build();
            server.MapGrpcService<EnvoyAuthorizationService>();

            await server.StartAsync(cancellationToken).ConfigureAwait(false);

            if (options.TransportMode == EnvoyListenerTransportMode.UnixDomainSocket)
            {
                string fullSocketPath = Path.GetFullPath(options.SocketPath!);
                if (OperatingSystem.IsLinux() && File.Exists(fullSocketPath))
                {
                    // MF3 / C3: Envoy uses Podman's keep-groups feature to access the host service group.
                    // Permit that group only; no other users can traverse the dedicated directory or access the socket.
                    File.SetUnixFileMode(
                        fullSocketPath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
                }
            }
            else
            {
                IServerAddressesFeature? addresses = server.Services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>();
                if (addresses is not null)
                {
                    BoundAddresses = [.. addresses.Addresses];
                    foreach (string addr in addresses.Addresses)
                    {
                        if (Uri.TryCreate(addr, UriKind.Absolute, out Uri? uri) && uri.Port > 0)
                        {
                            BoundTcpPort = uri.Port;
                            break;
                        }
                    }
                }

                BoundTcpPort ??= options.TcpPort;
            }

            IsListening = true;
            controller.EnvoyListenerHealthy = true;
        }
        catch
        {
            controller.EnvoyListenerHealthy = false;
            IsListening = false;
            if (server is not null)
            {
                await server.DisposeAsync().ConfigureAwait(false);
                server = null;
            }

            ReleaseSingleInstanceLock();
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        controller.EnvoyListenerHealthy = false;
        IsListening = false;
        BoundTcpPort = null;
        BoundAddresses = [];

        try
        {
            if (server is not null)
            {
                await server.StopAsync(cancellationToken).ConfigureAwait(false);
                await server.DisposeAsync().ConfigureAwait(false);
                server = null;
            }
        }
        finally
        {
            ReleaseSingleInstanceLock();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private bool ValidateTcpMtlsOptions()
    {
        if (options.TcpBindAddress is null)
        {
            return false;
        }

        if (!options.AllowWildcardBindForNegativeControl
            && (IPAddress.Any.Equals(options.TcpBindAddress) || IPAddress.IPv6Any.Equals(options.TcpBindAddress)))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(options.ExpectedClientSan) || string.IsNullOrWhiteSpace(options.ExpectedServerSan))
        {
            return false;
        }

        return EnvoyMtlsCertificateAuthority.ValidateServerCertificateConfiguration(
            options.ServerCertificate,
            options.CaCertificate,
            options.ExpectedServerSan);
    }

    private static void PrepareUnixSocketDirectoryAndRemoveStaleSocket(string socketDir, string fullSocketPath)
    {
        if (OperatingSystem.IsLinux())
        {
            const UnixFileMode dirMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupExecute; // 0710
            Directory.CreateDirectory(socketDir, dirMode);
            File.SetUnixFileMode(socketDir, dirMode);
        }
        else
        {
            Directory.CreateDirectory(socketDir);
        }

        if (Directory.Exists(fullSocketPath))
        {
            throw new IOException($"Envoy authorization socket path '{fullSocketPath}' is a directory.");
        }

        if (File.Exists(fullSocketPath))
        {
            File.Delete(fullSocketPath);
        }
    }

    private bool TryAcquireSingleInstanceLock()
    {
        if (string.IsNullOrWhiteSpace(options.LockFilePath))
        {
            throw new InvalidOperationException("LockFilePath is required.");
        }

        string fullLockPath = Path.GetFullPath(options.LockFilePath);
        string? lockDir = Path.GetDirectoryName(fullLockPath);
        if (!string.IsNullOrEmpty(lockDir))
        {
            Directory.CreateDirectory(lockDir);
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(lockDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        try
        {
            var stream = new FileStream(
                fullLockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);

            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(fullLockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                if (flock((int)stream.SafeFileHandle.DangerousGetHandle(), lockExclusiveNonBlocking) != 0)
                {
                    stream.Dispose();
                    return false;
                }
            }

            lockFileStream = stream;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void ReleaseSingleInstanceLock()
    {
        FileStream? stream = Interlocked.Exchange(ref lockFileStream, null);
        if (stream is null)
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsLinux() && !stream.SafeFileHandle.IsClosed)
            {
                _ = flock((int)stream.SafeFileHandle.DangerousGetHandle(), lockUnlock);
            }
        }
        finally
        {
            stream.Dispose();
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", SetLastError = true)]
    private static extern int flock(int fd, int operation);
}
