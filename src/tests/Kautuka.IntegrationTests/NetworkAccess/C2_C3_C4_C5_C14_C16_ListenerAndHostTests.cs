using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.InteropServices;

using Grpc.Core;
using Grpc.Net.Client;

using Kautuka.Hosting.Hosting;
using Kautuka.Manager.Grpc;
using Kautuka.Manager.Protos;
using Kautuka.NetworkAccess.Protos.Envoy.Service.Auth.V3;
using Kautuka.NetworkAccessController;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using EnvoyAuth = Kautuka.NetworkAccess.Protos.Envoy.Service.Auth.V3.Authorization;
using EnvoySocketAddress = Kautuka.NetworkAccess.Protos.Envoy.Service.Auth.V3.SocketAddress;
using NacController = Kautuka.NetworkAccessController.NetworkAccessController;

namespace Kautuka.IntegrationTests.NetworkAccess;

/// <summary>
/// Controller &amp; listener integration tests C2, C3, C4, C5, C14, and C16 (with negative controls).
/// </summary>
public sealed class C2_C3_C4_C5_C14_C16_ListenerAndHostTests
{
    [Fact]
    public async Task C2_SingleInstanceLock_PreventsUnlinkOfLiveSocket_AndReplacesStaleSocket_WithNegativeControl()
    {
        string tempDir = CreateTempDir();
        try
        {
            string socketDir = Path.Combine(tempDir, "sock");
            string socketPath = Path.Combine(socketDir, "networkaccess.sock");
            string lockPath = Path.Combine(socketDir, "networkaccess.lock");

            NacController controllerA = CreateController(Path.Combine(tempDir, "stateA"), out IWorkloadRegistry registryA);
            registryA.Register(new RegistryEntry(
                "10.247.0.10",
                "u1",
                "a1",
                "c1",
                "agent-sandbox",
                null,
                "agent-sandbox",
                false,
                "normal",
                "user-agent",
                null,
                DateTimeOffset.UtcNow));

            await using var hostA = new EnvoyAuthorizationHost(controllerA, new EnvoyAuthorizationHostOptions
            {
                TransportMode = EnvoyListenerTransportMode.UnixDomainSocket,
                SocketPath = socketPath,
                LockFilePath = lockPath,
            });

            await hostA.StartAsync(TestContext.Current.CancellationToken);
            Assert.True(hostA.IsListening);
            Assert.True(File.Exists(socketPath));

            // Verify instance A is serving over UDS
            using (GrpcChannel channelA = CreateUdsChannel(socketPath))
            {
                var clientA = new EnvoyAuth.AuthorizationClient(channelA);
                CheckResponse respA = await clientA.CheckAsync(
                    BuildCheckRequest("10.247.0.10", "93.184.216.34", 443, "example.com"),
                    cancellationToken: TestContext.Current.CancellationToken);
                Assert.Equal(7, respA.Status.Code);
            }

            // Start second instance B pointing at the same socket & lock file while A is running
            NacController controllerB = CreateController(Path.Combine(tempDir, "stateB"), out _);
            await using var hostB = new EnvoyAuthorizationHost(controllerB, new EnvoyAuthorizationHostOptions
            {
                TransportMode = EnvoyListenerTransportMode.UnixDomainSocket,
                SocketPath = socketPath,
                LockFilePath = lockPath,
            });

            await hostB.StartAsync(TestContext.Current.CancellationToken);
            Assert.False(hostB.IsListening);
            Assert.False(controllerB.EnvoyListenerHealthy);

            // Instance A's socket must still exist and still serve requests!
            Assert.True(File.Exists(socketPath));
            using (GrpcChannel channelStillA = CreateUdsChannel(socketPath))
            {
                var clientStillA = new EnvoyAuth.AuthorizationClient(channelStillA);
                CheckResponse respStillA = await clientStillA.CheckAsync(
                    BuildCheckRequest("10.247.0.10", "93.184.216.34", 443, "example.com"),
                    cancellationToken: TestContext.Current.CancellationToken);
                Assert.Equal(7, respStillA.Status.Code);
            }

            // Stop A (leaving stale socket on disk if not unlinked), then start B -> B acquires lock and replaces stale socket
            await hostA.StopAsync(TestContext.Current.CancellationToken);

            // On Linux, closing a pathname AF_UNIX socket leaves its filesystem entry orphaned.
            // Windows removes the endpoint on close, so use a stale placeholder there for host portability.
            if (OperatingSystem.IsLinux())
            {
                CreateOrphanedUnixSocket(socketPath);
            }
            else
            {
                await File.WriteAllTextAsync(socketPath, "stale", TestContext.Current.CancellationToken);
            }
            Assert.True(File.Exists(socketPath));

            await hostB.StartAsync(TestContext.Current.CancellationToken);
            Assert.True(hostB.IsListening);
            Assert.True(controllerB.EnvoyListenerHealthy);

            using (GrpcChannel channelB = CreateUdsChannel(socketPath))
            {
                var clientB = new EnvoyAuth.AuthorizationClient(channelB);
                CheckResponse respB = await clientB.CheckAsync(
                    BuildCheckRequest("10.247.0.10", "93.184.216.34", 443, "example.com"),
                    cancellationToken: TestContext.Current.CancellationToken);
                Assert.Equal(7, respB.Status.Code);
            }

            // C2.NC: Calling KautukaHostNetworkPolicy.ClearStaleSocket directly without acquiring networkaccess.lock
            // unlinks a live socket file on disk even while bound and listening on a real AF_UNIX stream socket.
            string ncSocketPath = Path.Combine(tempDir, "nc.sock");
            using var realSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            realSocket.Bind(new UnixDomainSocketEndPoint(ncSocketPath));
            realSocket.Listen(1);
            Assert.True(File.Exists(ncSocketPath));
            KautukaHostNetworkPolicy.ClearStaleSocket(new KautukaEndpoint
            {
                Name = "nc",
                Transport = KautukaTransport.UnixSocket,
                Port = 50051,
                SocketPath = ncSocketPath,
                BaseUri = new Uri("http://localhost"),
            });
            Assert.False(File.Exists(ncSocketPath));
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    private static void CreateOrphanedUnixSocket(string socketPath)
    {
        const int afUnix = 1;
        const int sockStream = 1;
        const int sunPathLength = 108;
        byte[] pathBytes = System.Text.Encoding.UTF8.GetBytes(socketPath);
        if (pathBytes.Length >= sunPathLength)
        {
            throw new ArgumentException("Unix socket path is too long.", nameof(socketPath));
        }

        var address = new UnixSocketAddress
        {
            Family = afUnix,
            Path = new byte[sunPathLength],
        };
        pathBytes.CopyTo(address.Path, 0);
        IntPtr addressPointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnixSocketAddress>());
        int descriptor = -1;
        try
        {
            Marshal.StructureToPtr(address, addressPointer, fDeleteOld: false);
            descriptor = NativeSocket(afUnix, sockStream, 0);
            if (descriptor < 0 || NativeBind(descriptor, addressPointer, (uint)Marshal.SizeOf<UnixSocketAddress>()) != 0 || NativeListen(descriptor, 1) != 0)
            {
                throw new InvalidOperationException($"Could not create orphan socket (errno {Marshal.GetLastPInvokeError()}).");
            }
        }
        finally
        {
            if (descriptor >= 0)
            {
                _ = NativeClose(descriptor);
            }
            Marshal.FreeHGlobal(addressPointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnixSocketAddress
    {
        public ushort Family;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 108)]
        public byte[] Path;
    }

    [DllImport("libc", EntryPoint = "socket", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int NativeSocket(int domain, int type, int protocol);

    [DllImport("libc", EntryPoint = "bind", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int NativeBind(int socket, IntPtr address, uint addressLength);

    [DllImport("libc", EntryPoint = "listen", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int NativeListen(int socket, int backlog);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int NativeClose(int socket);

    [Fact]
    public async Task C3_SocketPermissions_DedicatedDirectoryAndOwnerOnlyMode_WithNegativeControl()
    {
        string tempDir = CreateTempDir();
        try
        {
            string socketDir = Path.Combine(tempDir, "networkaccess");
            string socketPath = Path.Combine(socketDir, "networkaccess.sock");
            string lockPath = Path.Combine(socketDir, "networkaccess.lock");

            NacController controller = CreateController(Path.Combine(tempDir, "state"), out _);
            await using var host = new EnvoyAuthorizationHost(controller, new EnvoyAuthorizationHostOptions
            {
                TransportMode = EnvoyListenerTransportMode.UnixDomainSocket,
                SocketPath = socketPath,
                LockFilePath = lockPath,
            });

            await host.StartAsync(TestContext.Current.CancellationToken);
            Assert.True(host.IsListening);
            Assert.True(Directory.Exists(socketDir));
            Assert.True(File.Exists(socketPath));

            if (OperatingSystem.IsLinux())
            {
                UnixFileMode dirMode = File.GetUnixFileMode(socketDir);
                UnixFileMode sockMode = File.GetUnixFileMode(socketPath);

                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, dirMode);
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, sockMode);
                Assert.False(HasWorldOrGroupPermissions(sockMode));

                // C3.NC: weakening the actual listener socket permissions is caught by the same check.
                File.SetUnixFileMode(
                    socketPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
                UnixFileMode ncMode = File.GetUnixFileMode(socketPath);
                Assert.True(HasWorldOrGroupPermissions(ncMode));
            }
            else
            {
                // On Windows, verify the permission-check helper still flips on 0777 vs 0600 modes.
                const UnixFileMode secureMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                const UnixFileMode insecureMode = secureMode | UnixFileMode.OtherRead | UnixFileMode.OtherWrite;
                Assert.False(HasWorldOrGroupPermissions(secureMode));
                Assert.True(HasWorldOrGroupPermissions(insecureMode));
            }
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task C4_WindowsListenerMtls_RequiresDedicatedCaAndClientSan_WithNegativeControl()
    {
        string tempDir = CreateTempDir();
        try
        {
            string lockPath = Path.Combine(tempDir, "networkaccess.lock");
            (X509Certificate2 caCert, X509Certificate2 serverCert, X509Certificate2 clientCert) =
                EnvoyMtlsCertificateAuthority.CreateDedicatedBundle();

            NacController controller = CreateController(Path.Combine(tempDir, "state"), out IWorkloadRegistry registry);
            registry.Register(new RegistryEntry(
                "10.247.0.15",
                "u1",
                "a1",
                "c1",
                "agent-sandbox",
                null,
                "agent-sandbox",
                false,
                "normal",
                "user-agent",
                null,
                DateTimeOffset.UtcNow));

            // 1. Missing server cert / CA / SAN -> refuses to start, EnvoyListenerHealthy = false
            await using (var invalidHost = new EnvoyAuthorizationHost(controller, new EnvoyAuthorizationHostOptions
            {
                TransportMode = EnvoyListenerTransportMode.TcpMtls,
                LockFilePath = lockPath,
                TcpBindAddress = IPAddress.Loopback,
                TcpPort = 0,
                ServerCertificate = null,
                CaCertificate = caCert,
            }))
            {
                await invalidHost.StartAsync(TestContext.Current.CancellationToken);
                Assert.False(invalidHost.IsListening);
                Assert.False(controller.EnvoyListenerHealthy);
            }

            // 2. Server cert with IP SAN -> rejected by ValidateServerCertificateConfiguration (DNS SAN only!)
            using (X509Certificate2 certWithIpSan = CreateCertWithIpSan(caCert))
            {
                await using var ipSanHost = new EnvoyAuthorizationHost(controller, new EnvoyAuthorizationHostOptions
                {
                    TransportMode = EnvoyListenerTransportMode.TcpMtls,
                    LockFilePath = lockPath,
                    TcpBindAddress = IPAddress.Loopback,
                    TcpPort = 0,
                    ServerCertificate = certWithIpSan,
                    CaCertificate = caCert,
                });

                await ipSanHost.StartAsync(TestContext.Current.CancellationToken);
                Assert.False(ipSanHost.IsListening);
                Assert.False(controller.EnvoyListenerHealthy);
            }

            // 3. Start valid TCP+mTLS listener
            await using var validHost = new EnvoyAuthorizationHost(controller, new EnvoyAuthorizationHostOptions
            {
                TransportMode = EnvoyListenerTransportMode.TcpMtls,
                LockFilePath = lockPath,
                TcpBindAddress = IPAddress.Loopback,
                TcpPort = 0,
                ServerCertificate = serverCert,
                CaCertificate = caCert,
            });

            await validHost.StartAsync(TestContext.Current.CancellationToken);
            Assert.True(validHost.IsListening);
            Assert.True(controller.EnvoyListenerHealthy);
            int port = validHost.BoundTcpPort ?? throw new InvalidOperationException("Expected bound TCP port.");

            // (a) No client cert -> rejected
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                using GrpcChannel noCertChannel = CreateMtlsChannel(port, caCert, clientCert: null);
                var client = new EnvoyAuth.AuthorizationClient(noCertChannel);
                _ = await client.CheckAsync(
                    BuildCheckRequest("10.247.0.15", "93.184.216.34", 443, "example.com"),
                    cancellationToken: TestContext.Current.CancellationToken);
            });

            // (b) Client cert signed by a DIFFERENT CA (simulating Manager's general CA) -> rejected
            (X509Certificate2 otherCa, X509Certificate2 otherServer, X509Certificate2 otherClient) =
                EnvoyMtlsCertificateAuthority.CreateDedicatedBundle();
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                using GrpcChannel wrongCaChannel = CreateMtlsChannel(port, caCert, otherClient);
                var client = new EnvoyAuth.AuthorizationClient(wrongCaChannel);
                _ = await client.CheckAsync(
                    BuildCheckRequest("10.247.0.15", "93.184.216.34", 443, "example.com"),
                    cancellationToken: TestContext.Current.CancellationToken);
            });

            // (c) Client cert signed by the dedicated CA but with wrong SAN ("kautuka-gateway-client") -> rejected
            using X509Certificate2 wrongSanClientCert = CreateClientCertWithSan(caCert, "kautuka-gateway-client");
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                using GrpcChannel wrongSanChannel = CreateMtlsChannel(port, caCert, wrongSanClientCert);
                var client = new EnvoyAuth.AuthorizationClient(wrongSanChannel);
                _ = await client.CheckAsync(
                    BuildCheckRequest("10.247.0.15", "93.184.216.34", 443, "example.com"),
                    cancellationToken: TestContext.Current.CancellationToken);
            });

            // C4.NC: Presenting the valid Envoy client cert (signed by dedicated CA with SAN = nac-envoy)
            // and SNI/authority = kautuka-decision.internal succeeds.
            using (GrpcChannel validChannel = CreateMtlsChannel(port, caCert, clientCert))
            {
                var validClient = new EnvoyAuth.AuthorizationClient(validChannel);
                CheckResponse okResp = await validClient.CheckAsync(
                    BuildCheckRequest("10.247.0.15", "93.184.216.34", 443, "example.com"),
                    cancellationToken: TestContext.Current.CancellationToken);
                Assert.Equal(7, okResp.Status.Code); // Denied by policy (no_rule), proving gRPC call succeeded over mTLS
            }

            caCert.Dispose();
            serverCert.Dispose();
            clientCert.Dispose();
            otherCa.Dispose();
            otherServer.Dispose();
            otherClient.Dispose();
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task C5_WindowsListenerBindAddress_NeverWildcard_WithNegativeControl()
    {
        string tempDir = CreateTempDir();
        try
        {
            (X509Certificate2 caCert, X509Certificate2 serverCert, X509Certificate2 clientCert) =
                EnvoyMtlsCertificateAuthority.CreateDedicatedBundle();
            NacController controller = CreateController(Path.Combine(tempDir, "state"), out _);

            // Production validation refuses IPAddress.Any (0.0.0.0) and IPAddress.IPv6Any (::)
            await using (var wildcardHost = new EnvoyAuthorizationHost(controller, new EnvoyAuthorizationHostOptions
            {
                TransportMode = EnvoyListenerTransportMode.TcpMtls,
                LockFilePath = Path.Combine(tempDir, "lock1"),
                TcpBindAddress = IPAddress.Any,
                TcpPort = 0,
                ServerCertificate = serverCert,
                CaCertificate = caCert,
            }))
            {
                await wildcardHost.StartAsync(TestContext.Current.CancellationToken);
                Assert.False(wildcardHost.IsListening);
                Assert.False(controller.EnvoyListenerHealthy);
            }

            // Valid specific IP bind (127.0.0.1) succeeds and BoundAddresses contains only 127.0.0.1
            await using (var specificHost = new EnvoyAuthorizationHost(controller, new EnvoyAuthorizationHostOptions
            {
                TransportMode = EnvoyListenerTransportMode.TcpMtls,
                LockFilePath = Path.Combine(tempDir, "lock2"),
                TcpBindAddress = IPAddress.Loopback,
                TcpPort = 0,
                ServerCertificate = serverCert,
                CaCertificate = caCert,
            }))
            {
                await specificHost.StartAsync(TestContext.Current.CancellationToken);
                Assert.True(specificHost.IsListening);
                Assert.NotEmpty(specificHost.BoundAddresses);
                Assert.False(ContainsWildcardBindAddress(specificHost.BoundAddresses));
            }

            // C5.NC: Test fixture forced to bind 0.0.0.0 via AllowWildcardBindForNegativeControl = true
            // is caught by ContainsWildcardBindAddress.
            await using (var ncWildcardHost = new EnvoyAuthorizationHost(controller, new EnvoyAuthorizationHostOptions
            {
                TransportMode = EnvoyListenerTransportMode.TcpMtls,
                LockFilePath = Path.Combine(tempDir, "lock3"),
                TcpBindAddress = IPAddress.Any,
                TcpPort = 0,
                ServerCertificate = serverCert,
                CaCertificate = caCert,
                AllowWildcardBindForNegativeControl = true,
            }))
            {
                await ncWildcardHost.StartAsync(TestContext.Current.CancellationToken);
                Assert.True(ncWildcardHost.IsListening);
                Assert.True(ContainsWildcardBindAddress(ncWildcardHost.BoundAddresses));
            }

            caCert.Dispose();
            serverCert.Dispose();
            clientCert.Dispose();
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task C14_AdmissionConcurrencyCap_33rdConcurrentCallReturnsUnavailableCapacityExceeded_WithNegativeControl()
    {
        string tempDir = CreateTempDir();
        try
        {
            string socketPath = Path.Combine(tempDir, "sock", "networkaccess.sock");
            string lockPath = Path.Combine(tempDir, "sock", "networkaccess.lock");

            var allThirtyTwoEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int activeInHook = 0;

            NacController controller = CreateController(Path.Combine(tempDir, "state"), out IWorkloadRegistry registry);
            registry.Register(new RegistryEntry(
                "10.247.0.20",
                "u1",
                "a1",
                "c1",
                "agent-sandbox",
                null,
                "agent-sandbox",
                false,
                "normal",
                "user-agent",
                null,
                DateTimeOffset.UtcNow));

            await using var host = new EnvoyAuthorizationHost(controller, new EnvoyAuthorizationHostOptions
            {
                TransportMode = EnvoyListenerTransportMode.UnixDomainSocket,
                SocketPath = socketPath,
                LockFilePath = lockPath,
                FaultHook = async (_, _, ct) =>
                {
                    int current = Interlocked.Increment(ref activeInHook);
                    if (current == EnvoyAuthorizationService.MaxConcurrency)
                    {
                        allThirtyTwoEntered.TrySetResult();
                    }

                    await releaseGate.Task.WaitAsync(ct).ConfigureAwait(false);
                },
            });

            await host.StartAsync(TestContext.Current.CancellationToken);
            using GrpcChannel channel = CreateUdsChannel(socketPath);
            var client = new EnvoyAuth.AuthorizationClient(channel);

            // Launch 32 concurrent Check calls and hold them inside FaultHook
            Task<CheckResponse>[] heldTasks = new Task<CheckResponse>[EnvoyAuthorizationService.MaxConcurrency];
            for (int i = 0; i < EnvoyAuthorizationService.MaxConcurrency; i++)
            {
                heldTasks[i] = client.CheckAsync(
                    BuildCheckRequest("10.247.0.20", "93.184.216.34", 443, "example.com"),
                    cancellationToken: TestContext.Current.CancellationToken).ResponseAsync;
            }

            await allThirtyTwoEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // 33rd call must fail immediately with StatusCode.Unavailable and Detail "capacity_exceeded" (NOT Code 7!)
            RpcException ex = await Assert.ThrowsAsync<RpcException>(async () =>
            {
                _ = await client.CheckAsync(
                    BuildCheckRequest("10.247.0.20", "93.184.216.34", 443, "example.com"),
                    cancellationToken: TestContext.Current.CancellationToken);
            });
            Assert.Equal(StatusCode.Unavailable, ex.StatusCode);
            Assert.Equal("capacity_exceeded", ex.Status.Detail);
            Assert.True(controller.GetStatus().Errors >= 1);

            // C14.NC: Release the held calls; a subsequent Check call is admitted and returns a normal CheckResponse.
            releaseGate.TrySetResult();
            await Task.WhenAll(heldTasks);

            CheckResponse afterRelease = await client.CheckAsync(
                BuildCheckRequest("10.247.0.20", "93.184.216.34", 443, "example.com"),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(7, afterRelease.Status.Code);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task C14b_DynamicMetadata_TopLevelCluster_WithNegativeControl()
    {
        string tempDir = CreateTempDir();
        try
        {
            string socketPath = Path.Combine(tempDir, "sock", "networkaccess.sock");
            string lockPath = Path.Combine(tempDir, "sock", "networkaccess.lock");

            NacController controller = CreateController(Path.Combine(tempDir, "state"), out IWorkloadRegistry registry);

            // Register container
            registry.Register(new RegistryEntry(
                "10.247.1.10",
                "u1",
                "a1",
                "c1",
                "agent-sandbox",
                null,
                "agent-sandbox",
                false,
                "normal",
                "user-agent",
                null,
                DateTimeOffset.UtcNow));

            // Set up rules:
            // 1. Public hostname -> cluster = dfp_public
            // 2. Public IP -> cluster = orig_dst_public
            // 3. LAN hostname -> cluster = dfp_lan
            // 4. LAN IP -> cluster = orig_dst_lan
            var admin = new NetworkAccessActor("admin", IsAdmin: true);
            controller.PolicyStore.SetRule(admin, new RuleWriteSpec("agent", "u1", "a1", null, "example.com", "host", 443, Allowed: true, AllowLan: false), controller.PolicyStore.Revision);
            controller.PolicyStore.SetRule(admin, new RuleWriteSpec("agent", "u1", "a1", null, "93.184.216.34", "ip", 443, Allowed: true, AllowLan: false), controller.PolicyStore.Revision);
            controller.PolicyStore.SetRule(admin, new RuleWriteSpec("agent", "u1", "a1", null, "internal.lan", "host", 443, Allowed: true, AllowLan: true), controller.PolicyStore.Revision);
            controller.PolicyStore.SetRule(admin, new RuleWriteSpec("agent", "u1", "a1", null, "192.168.1.10", "ip", 443, Allowed: true, AllowLan: true), controller.PolicyStore.Revision);

            await using var host = new EnvoyAuthorizationHost(controller, new EnvoyAuthorizationHostOptions
            {
                TransportMode = EnvoyListenerTransportMode.UnixDomainSocket,
                SocketPath = socketPath,
                LockFilePath = lockPath,
            });

            await host.StartAsync(TestContext.Current.CancellationToken);
            using GrpcChannel channel = CreateUdsChannel(socketPath);
            var client = new EnvoyAuth.AuthorizationClient(channel);

            // 1. Test dfp_public
            CheckResponse resp1 = await client.CheckAsync(
                BuildCheckRequest("10.247.1.10", "93.184.216.34", 443, "example.com"),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(0, resp1.Status.Code);
            Assert.True(resp1.DynamicMetadata.Fields.ContainsKey("cluster"), "DynamicMetadata must contain top-level 'cluster' key");
            Assert.Equal("dfp_public", resp1.DynamicMetadata.Fields["cluster"].StringValue);
            Assert.False(resp1.DynamicMetadata.Fields.ContainsKey("envoy.filters.network.ext_authz"), "Must NOT be nested under envoy.filters.network.ext_authz");

            // 2. Test orig_dst_public
            CheckResponse resp2 = await client.CheckAsync(
                BuildCheckRequest("10.247.1.10", "93.184.216.34", 443, null),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(0, resp2.Status.Code);
            Assert.True(resp2.DynamicMetadata.Fields.ContainsKey("cluster"));
            Assert.Equal("orig_dst_public", resp2.DynamicMetadata.Fields["cluster"].StringValue);
            Assert.False(resp2.DynamicMetadata.Fields.ContainsKey("envoy.filters.network.ext_authz"));

            // 3. Test dfp_lan
            CheckResponse resp3 = await client.CheckAsync(
                BuildCheckRequest("10.247.1.10", "192.168.1.10", 443, "internal.lan"),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(0, resp3.Status.Code);
            Assert.True(resp3.DynamicMetadata.Fields.ContainsKey("cluster"));
            Assert.Equal("dfp_lan", resp3.DynamicMetadata.Fields["cluster"].StringValue);
            Assert.False(resp3.DynamicMetadata.Fields.ContainsKey("envoy.filters.network.ext_authz"));

            // 4. Test orig_dst_lan
            CheckResponse resp4 = await client.CheckAsync(
                BuildCheckRequest("10.247.1.10", "192.168.1.10", 443, null),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(0, resp4.Status.Code);
            Assert.True(resp4.DynamicMetadata.Fields.ContainsKey("cluster"));
            Assert.Equal("orig_dst_lan", resp4.DynamicMetadata.Fields["cluster"].StringValue);
            Assert.False(resp4.DynamicMetadata.Fields.ContainsKey("envoy.filters.network.ext_authz"));

            // C14b.NC: If an unauthorized nested key were present or top-level 'cluster' missing, fail-closed assertion flips
            Assert.False(resp1.DynamicMetadata.Fields.ContainsKey("envoy.filters.network.ext_authz"));
            Assert.False(resp2.DynamicMetadata.Fields.ContainsKey("envoy.filters.network.ext_authz"));
            Assert.False(resp3.DynamicMetadata.Fields.ContainsKey("envoy.filters.network.ext_authz"));
            Assert.False(resp4.DynamicMetadata.Fields.ContainsKey("envoy.filters.network.ext_authz"));
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task C16_ServiceIsolation_EnvoyListenerRejectsAllManagerRpcsWithUnimplemented_WithNegativeControl()
    {
        string tempDir = CreateTempDir();
        try
        {
            string socketPath = Path.Combine(tempDir, "sock", "networkaccess.sock");
            string lockPath = Path.Combine(tempDir, "sock", "networkaccess.lock");

            NacController controller = CreateController(Path.Combine(tempDir, "state"), out _);
            await using var host = new EnvoyAuthorizationHost(controller, new EnvoyAuthorizationHostOptions
            {
                TransportMode = EnvoyListenerTransportMode.UnixDomainSocket,
                SocketPath = socketPath,
                LockFilePath = lockPath,
            });

            await host.StartAsync(TestContext.Current.CancellationToken);
            using GrpcChannel channel = CreateUdsChannel(socketPath);

            // 1. Every RPC in ManagerService.Descriptor.Methods called on the Envoy listener returns Unimplemented
            CallInvoker invoker = channel.CreateCallInvoker();
            Assert.NotEmpty(ManagerService.Descriptor.Methods);

            foreach (Google.Protobuf.Reflection.MethodDescriptor method in ManagerService.Descriptor.Methods)
            {
                var rawMethod = new Method<byte[], byte[]>(
                    MethodType.Unary,
                    ManagerService.Descriptor.FullName,
                    method.Name,
                    Marshallers.Create(static b => b, static b => b),
                    Marshallers.Create(static b => b, static b => b));

                RpcException ex = await Assert.ThrowsAsync<RpcException>(async () =>
                {
                    _ = await invoker.AsyncUnaryCall(
                        rawMethod,
                        host: null,
                        new CallOptions(cancellationToken: TestContext.Current.CancellationToken),
                        []).ResponseAsync;
                });

                Assert.Equal(StatusCode.Unimplemented, ex.StatusCode);
            }

            // 2. Authorization/Check succeeds on the Envoy listener
            var authClient = new EnvoyAuth.AuthorizationClient(channel);
            CheckResponse authResp = await authClient.CheckAsync(
                BuildCheckRequest("10.247.0.99", "93.184.216.34", 443, "example.com"),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(7, authResp.Status.Code);

            bool envoyListenerIsIsolated = await VerifyAllManagerRpcsReturnUnimplementedAsync(invoker, TestContext.Current.CancellationToken);
            Assert.True(envoyListenerIsIsolated);

            // 3. Conversely, Manager's listener (mapping WorkspaceGrpcService only) rejects Authorization/Check with Unimplemented,
            // and C16.NC: a listener that maps WorkspaceGrpcService fails VerifyAllManagerRpcsReturnUnimplementedAsync!
            string mgrSocketPath = Path.Combine(tempDir, "mgr", "manager.sock");
            Directory.CreateDirectory(Path.GetDirectoryName(mgrSocketPath)!);
            WebApplicationBuilder mgrBuilder = WebApplication.CreateSlimBuilder();
            mgrBuilder.WebHost.ConfigureKestrel(k =>
            {
                k.ListenUnixSocket(mgrSocketPath, lo => lo.Protocols = HttpProtocols.Http2);
            });
            mgrBuilder.Services.AddGrpc();
            mgrBuilder.Services.AddSingleton(new WorkspaceGrpcService(
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                NullLogger<WorkspaceGrpcService>.Instance,
                controller));
            await using WebApplication mgrApp = mgrBuilder.Build();
            mgrApp.MapGrpcService<WorkspaceGrpcService>();
            await mgrApp.StartAsync(TestContext.Current.CancellationToken);

            using GrpcChannel mgrChannel = CreateUdsChannel(mgrSocketPath);
            var authOnMgrClient = new EnvoyAuth.AuthorizationClient(mgrChannel);
            RpcException authOnMgrEx = await Assert.ThrowsAsync<RpcException>(async () =>
            {
                _ = await authOnMgrClient.CheckAsync(
                    BuildCheckRequest("10.247.0.99", "93.184.216.34", 443, "example.com"),
                    cancellationToken: TestContext.Current.CancellationToken);
            });
            Assert.Equal(StatusCode.Unimplemented, authOnMgrEx.StatusCode);

            // C16.NC: When WorkspaceGrpcService IS mapped on a listener, VerifyAllManagerRpcsReturnUnimplementedAsync returns false.
            bool ncManagerMappedIsIsolated = await VerifyAllManagerRpcsReturnUnimplementedAsync(
                mgrChannel.CreateCallInvoker(),
                TestContext.Current.CancellationToken);
            Assert.False(ncManagerMappedIsIsolated);

            await mgrApp.StopAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    private static async Task<bool> VerifyAllManagerRpcsReturnUnimplementedAsync(CallInvoker invoker, CancellationToken ct)
    {
        foreach (Google.Protobuf.Reflection.MethodDescriptor method in ManagerService.Descriptor.Methods)
        {
            var rawMethod = new Method<byte[], byte[]>(
                MethodType.Unary,
                ManagerService.Descriptor.FullName,
                method.Name,
                Marshallers.Create(static b => b, static b => b),
                Marshallers.Create(static b => b, static b => b));
            try
            {
                _ = await invoker.AsyncUnaryCall(rawMethod, null, new CallOptions(cancellationToken: ct), []).ResponseAsync;
                return false;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
            {
                // Expected
            }
        }

        return true;
    }

    private static bool HasWorldOrGroupPermissions(UnixFileMode mode)
    {
        const UnixFileMode groupOrOther =
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        return (mode & groupOrOther) != 0;
    }

    private static bool ContainsWildcardBindAddress(IEnumerable<string> boundAddresses)
    {
        foreach (string addr in boundAddresses)
        {
            if (addr.Contains("0.0.0.0", StringComparison.Ordinal)
                || addr.Contains("[::]", StringComparison.Ordinal)
                || addr.Contains(":://", StringComparison.Ordinal)
                || addr.Contains("https://+:", StringComparison.Ordinal)
                || addr.Contains("https://*:", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static GrpcChannel CreateUdsChannel(string socketPath)
    {
        var endpoint = new UnixDomainSocketEndPoint(socketPath);
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(endpoint, ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };

        return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = handler,
        });
    }

    private static GrpcChannel CreateMtlsChannel(int port, X509Certificate2 expectedCa, X509Certificate2? clientCert)
    {
        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                TargetHost = EnvoyMtlsCertificateAuthority.DefaultServerDnsSan,
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                {
                    if (cert is null)
                    {
                        return false;
                    }

                    using var serverCert = new X509Certificate2(cert);
                    return EnvoyMtlsCertificateAuthority.ValidateCertificateChain(serverCert, expectedCa)
                        && EnvoyMtlsCertificateAuthority.HasDnsSubjectAlternativeName(serverCert, EnvoyMtlsCertificateAuthority.DefaultServerDnsSan)
                        && !EnvoyMtlsCertificateAuthority.HasIpSubjectAlternativeName(serverCert);
                },
            },
        };

        if (clientCert is not null)
        {
            handler.SslOptions.ClientCertificates = [clientCert];
        }

        // Connect to 127.0.0.1 while targeting authority kautuka-decision.internal via TargetHost
        return GrpcChannel.ForAddress($"https://127.0.0.1:{port}", new GrpcChannelOptions
        {
            HttpHandler = handler,
        });
    }

    private static X509Certificate2 CreateCertWithIpSan(X509Certificate2 caCert)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN={EnvoyMtlsCertificateAuthority.DefaultServerDnsSan}", ecdsa, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(EnvoyMtlsCertificateAuthority.DefaultServerDnsSan);
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        using X509Certificate2 signed = req.Create(caCert, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1), [1, 2, 3, 4]);
        using X509Certificate2 withKey = signed.CopyWithPrivateKey(ecdsa);
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
    }

    private static X509Certificate2 CreateClientCertWithSan(X509Certificate2 caCert, string dnsSan)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN={dnsSan}", ecdsa, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dnsSan);
        req.CertificateExtensions.Add(san.Build());
        using X509Certificate2 signed = req.Create(caCert, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1), [5, 6, 7, 8]);
        using X509Certificate2 withKey = signed.CopyWithPrivateKey(ecdsa);
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
    }

    private static CheckRequest BuildCheckRequest(string sourceIp, string destIp, uint port, string? sni)
    {
        var req = new CheckRequest
        {
            Attributes = new AttributeContext
            {
                Source = new Peer
                {
                    Address = new Address
                    {
                        SocketAddress = new EnvoySocketAddress
                        {
                            Address = sourceIp,
                            PortValue = 51000,
                        },
                    },
                },
                Destination = new Peer
                {
                    Address = new Address
                    {
                        SocketAddress = new EnvoySocketAddress
                        {
                            Address = destIp,
                            PortValue = port,
                        },
                    },
                },
            },
        };

        if (sni is not null)
        {
            req.Attributes.TlsSession = new TLSSession
            {
                Sni = sni,
            };
        }

        return req;
    }

    private static NacController CreateController(string stateDir, out IWorkloadRegistry registry)
    {
        var store = new NetworkAccessPolicyStore(Path.Combine(stateDir, "policy", "state.json"));
        registry = new WorkloadRegistry();
        var audit = new NetworkAccessAuditRing(Path.Combine(stateDir, "audit", "decisions.jsonl"));
        return new NacController(store, registry, audit);
    }

    private static string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "nac-c-host-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}
