using Kautuka.Hosting.Hosting;
using Kautuka.Manager.Grpc;
using Kautuka.Manager.Hosting;
using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.Manager.RuntimeContainer.Sandbox;
using Kautuka.NetworkAccessController;
using Kautuka.Telemetry.DependencyInjection;
using Kautuka.Telemetry.Security;
using Kautuka.Telemetry.Telemetry;

using Microsoft.Extensions.Options;

using var databaseWriterLease = KautukaDatabaseWriterLease.AcquireForMode(
    Environment.GetEnvironmentVariable("Kautuka__Mode"));

const int GrpcMaxMessageSizeBytes = 4 * 1024 * 1024;

KautukaAspNetCoreStartupPolicy.SanitizeHostingStartupAssemblies();

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseKestrelHttpsConfiguration();
builder.AddKautukaLogging(LogLevel.Debug);
builder.AddKautukaTelemetry(
    KautukaActivitySources.ProcessKind.Manager,
    KautukaActivitySources.Component.Manager);

// -----------------------------------------------------------------------
// Resolve canonical shared data root.
// All mutable state is anchored through the shared host path policy.
// -----------------------------------------------------------------------
string managerDataRoot = KautukaHostPathPolicy.ResolveServiceStateRoot(builder.Configuration, "manager");
managerDataRoot = Path.GetFullPath(managerDataRoot);
KautukaEndpoint managerEndpoint = KautukaHostNetworkPolicy.Resolve(builder.Configuration).Manager;
string socketRoot = KautukaHostNetworkPolicy.ResolveSocketRoot(builder.Configuration);
var podmanCommandRunner = new PodmanCommandRunner();
WindowsPodmanDevEgressOptions? windowsPodmanDevEgress = null;
if (OperatingSystem.IsWindows())
{
    try
    {
        windowsPodmanDevEgress = await WindowsPodmanDevEgressOptions.CreateAsync(
            podmanCommandRunner,
            Path.Combine(managerDataRoot, "networkAccess", "envoy-certs"),
            CancellationToken.None).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        await Console.Error.WriteLineAsync(
            $"Windows Podman NAC mTLS setup failed; workload startup will remain closed: {ex.Message}").ConfigureAwait(false);
    }
}

builder.Services.AddSingleton<WorkspaceRuntimePaths>();
builder.Services.AddSingleton(podmanCommandRunner);
builder.Services.AddSingleton(static sp => WorkspaceSidecarCatalogLoader.Load(
    Path.Combine(AppContext.BaseDirectory, "sidecars.json"),
    sp.GetRequiredService<WorkspaceRuntimePaths>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger("Kautuka.Manager.Configuration.WorkspaceSidecarCatalogLoader")));
builder.Services.AddSingleton(static sp =>
{
    IHostEnvironment environment = sp.GetRequiredService<IHostEnvironment>();
    ILogger logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Kautuka.Manager.RuntimeImageAllowlist");
    return RuntimeImageAllowlistLoader.Load(
        Path.Combine(AppContext.BaseDirectory, RuntimeImageAllowlistLoader.FileName),
        requirePresent: !environment.IsDevelopment(),
        logger);
});

// -----------------------------------------------------------------------
// Network Access Controller (NAC v7) module & Egress infrastructure
// -----------------------------------------------------------------------
builder.Services.AddSingleton<IWorkloadRegistry>(sp =>
    new WorkloadRegistry(sp.GetRequiredService<ILogger<WorkloadRegistry>>()));
builder.Services.AddSingleton(sp =>
    new NetworkAccessPolicyStore(
        Path.Combine(managerDataRoot, "networkAccess", "policy", "state.json"),
        logger: sp.GetRequiredService<ILogger<NetworkAccessPolicyStore>>()));
builder.Services.AddSingleton(_ =>
    new NetworkAccessAuditRing(Path.Combine(managerDataRoot, "networkAccess", "audit", "decisions.jsonl")));
builder.Services.AddSingleton(_ =>
    new ImageCompatibilityStore(Path.Combine(managerDataRoot, "networkAccess", "compat", "image-compat.json")));
builder.Services.AddSingleton(_ =>
    new WorkloadAddressAllocator(Path.Combine(managerDataRoot, "networkAccess", "ip-cursor.json")));
builder.Services.AddSingleton<WorkloadPostStartVerifier>();
string envoySocketDir = Path.Combine(socketRoot, "networkaccess-envoy");
string hookStateDir = Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } xdgState
    ? Path.Combine(xdgState, "kautuka", "nac")
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state", "kautuka", "nac");
builder.Services.AddSingleton(sp =>
    new EgressTopologyManager(
        sp.GetRequiredService<PodmanCommandRunner>(),
        sp.GetRequiredService<WorkloadAddressAllocator>(),
        sp.GetRequiredService<WorkloadPostStartVerifier>(),
        sp.GetRequiredService<IWorkloadRegistry>(),
        Path.Combine(managerDataRoot, "networkAccess"),
        sp.GetService<ILogger<EgressTopologyManager>>(),
        envoySocketHostDir: envoySocketDir,
        hookStateDirectory: hookStateDir,
        windowsDecisionEndpointIp: windowsPodmanDevEgress?.DecisionEndpointAddress.ToString(),
        windowsEnvoyCertificateDirectory: windowsPodmanDevEgress?.EnvoyCertificateDirectory,
        windowsPodmanMachineName: windowsPodmanDevEgress?.PodmanMachineName,
        windowsPodmanHookStateDirectory: windowsPodmanDevEgress?.RemoteHookStateDirectory));
builder.Services.AddHostedService(sp => sp.GetRequiredService<EgressTopologyManager>());
builder.Services.AddSingleton<IHostedService, DevelopmentNacStartupGuard>();
builder.Services.AddSingleton<IWorkloadStartupState>(sp => sp.GetRequiredService<EgressTopologyManager>());
builder.Services.AddSingleton<WorkloadLaunchGate>();
builder.Services.AddSingleton(sp =>
    new Kautuka.NetworkAccessController.NetworkAccessController(
        sp.GetRequiredService<NetworkAccessPolicyStore>(),
        sp.GetRequiredService<IWorkloadRegistry>(),
        sp.GetRequiredService<NetworkAccessAuditRing>()));

if (OperatingSystem.IsLinux())
{
    builder.Services.AddSingleton<IHostedService>(sp =>
        EnvoyAuthorizationHost.Create(
            sp.GetRequiredService<Kautuka.NetworkAccessController.NetworkAccessController>(),
            new EnvoyAuthorizationHostOptions
            {
                TransportMode = EnvoyListenerTransportMode.UnixDomainSocket,
                LockFilePath = Path.Combine(managerDataRoot, "networkAccess", "networkaccess.lock"),
                SocketPath = Path.Combine(socketRoot, "networkaccess-envoy", "envoy.sock"),
            }));
}
else if (OperatingSystem.IsWindows() && windowsPodmanDevEgress is not null)
{
    builder.Services.AddSingleton<IHostedService>(sp =>
        EnvoyAuthorizationHost.Create(
            sp.GetRequiredService<Kautuka.NetworkAccessController.NetworkAccessController>(),
            new EnvoyAuthorizationHostOptions
            {
                TransportMode = EnvoyListenerTransportMode.TcpMtls,
                LockFilePath = Path.Combine(managerDataRoot, "networkAccess", "networkaccess.lock"),
                TcpBindAddress = windowsPodmanDevEgress.DecisionEndpointAddress,
                TcpPort = 50051,
                ServerCertificate = windowsPodmanDevEgress.ServerCertificate,
                CaCertificate = windowsPodmanDevEgress.CaCertificate,
            }));
}

builder.Services.AddSingleton<PodmanImageArchiveStore>();
builder.Services.AddSingleton<IContainerTool, PodmanContainerTool>();
builder.Services.AddSingleton<IManagerRuntimeAdapter, ManagerRuntimeAdapter>();
builder.Services.AddSingleton<IManagerRuntimeHealthCommand, PodmanManagerRuntimeHealthCommand>();
builder.Services.AddSingleton<IManagerRuntimeHealthProbe, ManagerRuntimeHealthProbe>();
builder.Services.AddSingleton<IHostedService, ManagerRuntimeHealthWarmWorker>();
builder.Services.AddSingleton<ManagerRuntimeContainerDiagnosticsService>();
builder.Services.AddSingleton<PkgsProxyCacheDiagnosticsService>();
builder.Services.AddSingleton<WorkspaceSidecarSupervisor>();
builder.Services.AddSingleton<WorkspaceDesiredStateStore>();
builder.Services.AddSingleton<McpbImageMaterializer>();
builder.Services.AddSingleton<WorkspaceContainerConnectorExecutionService>();
builder.Services.AddSingleton<WorkspaceReadinessGate>();
builder.Services.AddSingleton<WorkspaceExecTracker>();
builder.Services.AddOptions<ManagerContainerStartupOptions>()
    .Bind(builder.Configuration.GetSection(ManagerContainerStartupOptions.Section));
builder.Services.AddHttpClient("manager.sidecars");
builder.Services.AddSingleton<IHostedService, WorkspaceSidecarAssetStagingService>();
builder.Services.AddSingleton<IHostedService>(sp => new WorkspaceContainerSupervisorWorker(
    sp.GetRequiredService<WorkspaceDesiredStateStore>(),
    sp.GetRequiredService<IManagerRuntimeAdapter>(),
    sp.GetRequiredService<WorkspaceSidecarSupervisor>(),
    sp.GetRequiredService<WorkspaceContainerConnectorExecutionService>(),
    sp.GetRequiredService<WorkspaceRuntimePaths>(),
    sp.GetRequiredService<WorkspaceReadinessGate>(),
    sp.GetRequiredService<WorkspaceExecTracker>(),
    sp.GetRequiredService<IOptions<ManagerContainerStartupOptions>>(),
    sp.GetRequiredService<ILogger<WorkspaceContainerSupervisorWorker>>(),
    socketRoot,
    sp.GetRequiredService<Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess.EgressTopologyManager>()));

builder.Services.AddGrpc(options =>
{
    options.MaxReceiveMessageSize = GrpcMaxMessageSizeBytes;
    options.MaxSendMessageSize = GrpcMaxMessageSizeBytes;
    options.Interceptors.Add<ManagerClientAuthorizationInterceptor>();
});
builder.Services.AddSingleton<ManagerClientAuthorizationInterceptor>();
KautukaMaintenanceMode.SuppressApplicationHostedServices(
    builder.Services,
    typeof(ManagerRuntimeHealthWarmWorker));

var (serverCert, caCert) = LocalCaMtlsBootstrap.EnsureAndLoad(
    managerDataRoot,
    "Manager",
    [
        new LocalMtlsClientCertificate(
            Kautuka.Manager.Protos.ManagerClientIdentities.GatewayCommonName,
            Kautuka.Manager.Protos.ManagerClientIdentities.GatewayCertificateFileName),
        new LocalMtlsClientCertificate(
            Kautuka.Manager.Protos.ManagerClientIdentities.UpdateServiceCommonName,
            Kautuka.Manager.Protos.ManagerClientIdentities.UpdateServiceCertificateFileName),
        new LocalMtlsClientCertificate(
            Kautuka.Manager.Protos.ManagerClientIdentities.ChannelHostCommonName,
            Kautuka.Manager.Protos.ManagerClientIdentities.ChannelHostCertificateFileName),
    ]);

builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(managerEndpoint, listenOptions =>
    {
        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
        listenOptions.UseHttps(serverCert, https =>
        {
            https.ClientCertificateMode = Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.RequireCertificate;
            https.ClientCertificateValidation = (cert, _, _) =>
            {
                if (cert is null) return false;
                using var cert2 = new System.Security.Cryptography.X509Certificates.X509Certificate2(cert);
                return LocalCaMtlsBootstrap.ValidateClientCert(cert2, caCert);
            };
        });
    });
});

using WebApplication app = builder.Build();

app.GrantSocketGroupAccessWhenStarted(managerEndpoint);

app.UseAdmissionFence(path => KautukaMaintenanceMode.IsAllowedGrpcRequest(path, "GetHostStatus", "Ping", "UpdateDesiredState"));

app.MapDefaultEndpoints();
app.MapGrpcService<WorkspaceGrpcService>();

await app.RunAsync().ConfigureAwait(false);
