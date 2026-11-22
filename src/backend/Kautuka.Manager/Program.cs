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

bool verifyAppliance = KautukaMaintenanceMode.IsApplianceVerification;
bool prepareApplianceImages = args.Contains("--prepare-appliance-images", StringComparer.Ordinal);
bool checkApplianceReadiness = args.Contains("--check-appliance-ready", StringComparer.Ordinal);
if (prepareApplianceImages || checkApplianceReadiness || args.Contains("--verify-appliance-release", StringComparer.Ordinal))
{
    bool commandFirst = args[0] is "--verify-appliance-release" or "--prepare-appliance-images" or "--check-appliance-ready";
    bool selectCandidate = args is [_, "--candidate-version", _];
    if (!OperatingSystem.IsLinux() || !commandFirst || (args.Length != 1 && !selectCandidate))
    {
        await Console.Error.WriteLineAsync("Appliance release commands require Linux and accept an optional --candidate-version argument.").ConfigureAwait(false);
        return 1;
    }

    try
    {
        const string managerRoot = "/opt/kautuka/manager";
        VerifiedApplianceRelease release = VerifiedApplianceRelease.Verify(
            TrustedApplianceReleaseFiles.Read(managerRoot, "/etc/kautuka", selectCandidate ? args[2] : null));
        if (checkApplianceReadiness)
        {
            if (verifyAppliance)
                TrustedApplianceReleaseFiles.RequireVerificationTarget(release.Version, Path.GetDirectoryName(KautukaMaintenanceMode.MarkerPath)!);
            if (!await ApplianceServiceReadiness.CheckManagerAsync(release.ReleaseId, verifyAppliance, CancellationToken.None)
                    .ConfigureAwait(false))
            {
                await Console.Error.WriteLineAsync($"Manager has not reported readiness for {release.ReleaseId} in the requested boot mode.").ConfigureAwait(false);
                return 1;
            }
            await Console.Out.WriteLineAsync($"Manager reports {release.ReleaseId} ready.").ConfigureAwait(false);
            return 0;
        }
        if (prepareApplianceImages)
        {
            var images = new ApplianceRoleImageImporter(new PodmanCommandRunner());
            await images.EnsureAsync(release.Deployment, Path.Combine(managerRoot, "versions", release.Version), CancellationToken.None,
                requireVerificationFence: verifyAppliance)
                .ConfigureAwait(false);
            await Console.Out.WriteLineAsync($"Prepared appliance images for {release.ReleaseId}.").ConfigureAwait(false);
            return 0;
        }
        await Console.Out.WriteLineAsync($"Verified appliance release {release.ReleaseId}.").ConfigureAwait(false);
        return 0;
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
        or InvalidOperationException or System.Text.Json.JsonException or System.Text.DecoderFallbackException)
    {
        await Console.Error.WriteLineAsync($"Appliance release rejected: {error.Message}").ConfigureAwait(false);
        return 1;
    }
}

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
string? gatewayConnectEnvoyDirectory = null;
string? telemetryAuthorization = null;
bool applianceEnabled = builder.Configuration.GetValue<bool>("Kautuka:Appliance:Enabled");
if (verifyAppliance && !applianceEnabled)
    throw new InvalidOperationException("Verification requires appliance service supervision.");

List<ServiceDescriptor> verificationServices = [];
void AddApplianceVerificationService(Func<IServiceProvider, IHostedService> factory)
{
    ServiceDescriptor service = ServiceDescriptor.Singleton(factory);
    builder.Services.Add(service);
    if (verifyAppliance) verificationServices.Add(service);
}

if (applianceEnabled)
{
    int publishedWebPort = WorkloadRulesetRenderer.WebHostPort;
    if (builder.Configuration["Kautuka:Appliance:PublishedWebPort"] is { } port &&
        (!int.TryParse(port, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out publishedWebPort)
            || publishedWebPort is < 1024 or > 65535))
        throw new InvalidOperationException("The appliance browser port must be an integer between 1024 and 65535.");

    if (!OperatingSystem.IsLinux() || builder.Environment.IsDevelopment()
        || !string.Equals(builder.Configuration["Kautuka:Mode"], "prod", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("Appliance service supervision requires Linux production mode.");
    }

    string stateRoot = KautukaHostPathPolicy.ResolveSharedStateRoot(builder.Configuration);
    if (Path.TrimEndingDirectorySeparator(stateRoot) == Path.TrimEndingDirectorySeparator(socketRoot))
        throw new InvalidOperationException("Appliance service sockets must be stored separately from service state and private keys.");

    const string managerRoot = "/opt/kautuka/manager";
    VerifiedApplianceRelease release = VerifiedApplianceRelease.Verify(
        TrustedApplianceReleaseFiles.Read(managerRoot, "/etc/kautuka"));
    if (verifyAppliance)
        TrustedApplianceReleaseFiles.RequireVerificationTarget(release.Version, Path.GetDirectoryName(KautukaMaintenanceMode.MarkerPath)!);
    GatewayConnectCertificateMaterial connectMaterial = GatewayConnectCertificateMaterial.Ensure(Path.Combine(managerDataRoot, "networkAccess", "gateway-connect"));
    gatewayConnectEnvoyDirectory = connectMaterial.EnvoyDirectory;
    string gatewayConnectDirectory = connectMaterial.GatewayDirectory;
    DateTimeOffset connectRenewal = connectMaterial.RenewAfterUtc;
    if (!verifyAppliance)
        builder.Services.AddHostedService(sp => new GatewayConnectRenewalService(
            connectRenewal, sp.GetRequiredService<IHostApplicationLifetime>(),
            sp.GetRequiredService<ILogger<GatewayConnectRenewalService>>()));
    await ApplianceTelemetry.EnsureRootSecretAsync(podmanCommandRunner, stateRoot, CancellationToken.None)
        .ConfigureAwait(false);
    telemetryAuthorization = await ApplianceTelemetry.ReadIngestionAuthorizationAsync(podmanCommandRunner, CancellationToken.None)
        .ConfigureAwait(false);
    builder.Services.AddSingleton(sp => new ApplianceServiceSupervisor(
        podmanCommandRunner, release, Path.Combine(managerRoot, "versions", release.Version),
        stateRoot, socketRoot,
        sp.GetRequiredService<ILogger<ApplianceServiceSupervisor>>(), sp.GetRequiredService<EgressTopologyManager>(),
        gatewayConnectDirectory, sp.GetRequiredService<PodmanImageArchiveStore>(), superviseObservability: true, superviseUpdateService: true,
        verificationMode: verifyAppliance,
        verificationSidecars: verifyAppliance ? sp.GetRequiredService<WorkspaceSidecarSupervisor>() : null,
        publishedWebPort: publishedWebPort, recoveryPage: new ApplianceRecoveryPage(publishedWebPort)));
    AddApplianceVerificationService(sp => sp.GetRequiredService<ApplianceServiceSupervisor>());
    builder.Services.AddSingleton<Func<(string ReleaseId, bool VerificationMode, bool Ready)>>(sp =>
    {
        ApplianceServiceSupervisor supervisor = sp.GetRequiredService<ApplianceServiceSupervisor>();
        return () => (release.ReleaseId, verifyAppliance, supervisor.IsReady);
    });
}
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
builder.Services.AddSingleton(sp =>
{
    WorkspaceSidecarCatalog catalog = WorkspaceSidecarCatalogLoader.Load(
        Path.Combine(AppContext.BaseDirectory, "sidecars.json"),
        sp.GetRequiredService<WorkspaceRuntimePaths>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger("Kautuka.Manager.Configuration.WorkspaceSidecarCatalogLoader"));
    return applianceEnabled ? catalog with { Sidecars = [.. catalog.Sidecars.Where(sidecar => sidecar.Name != "kautuka-openobserve")] } : catalog;
});
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
builder.Services.AddSingleton(sp =>
    new NetworkAccessAuditRing(Path.Combine(managerDataRoot, "networkAccess", "audit", "decisions.jsonl"),
        10 * 1024 * 1024, sp.GetRequiredService<ILogger<NetworkAccessAuditRing>>()));
builder.Services.AddSingleton(_ =>
    new ImageCompatibilityStore(Path.Combine(managerDataRoot, "networkAccess", "compat", "image-compat.json")));
builder.Services.AddSingleton(_ =>
    new WorkloadAddressAllocator(Path.Combine(managerDataRoot, "networkAccess", "ip-cursor.json")));
builder.Services.AddSingleton<WorkloadPostStartVerifier>();
string envoySocketDir = Path.Combine(socketRoot, "networkaccess-envoy");
EnvoySocketSelinuxLabel.RelabelIfRequired(OperatingSystem.IsLinux(), Environment.GetEnvironmentVariable("KAUTUKA_ENVOY_SELINUX"), envoySocketDir);
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
        windowsPodmanHookStateDirectory: windowsPodmanDevEgress?.RemoteHookStateDirectory,
        desiredStateStore: sp.GetRequiredService<WorkspaceDesiredStateStore>(),
        imageCompatibilityStore: sp.GetRequiredService<ImageCompatibilityStore>(),
        policyStore: sp.GetRequiredService<NetworkAccessPolicyStore>(),
        useDevelopmentSelinuxDomain: EgressTopologyManager.ShouldUseEnvoySelinuxDomain(
            OperatingSystem.IsLinux(), builder.Environment.IsDevelopment(),
            Environment.GetEnvironmentVariable("KAUTUKA_ENVOY_SELINUX"),
            Environment.GetEnvironmentVariable("KAUTUKA_DEV_ENVOY_SELINUX")),
        imageArchives: sp.GetRequiredService<PodmanImageArchiveStore>(),
        gatewayConnectEnvoyDirectory: gatewayConnectEnvoyDirectory,
        telemetryAuthorization: telemetryAuthorization));
AddApplianceVerificationService(sp => sp.GetRequiredService<EgressTopologyManager>());
builder.Services.AddSingleton<IHostedService, DevelopmentNacStartupGuard>();
builder.Services.AddSingleton<IWorkloadStartupState>(sp => sp.GetRequiredService<EgressTopologyManager>());
builder.Services.AddSingleton<WorkloadLaunchGate>();
builder.Services.AddManagerNetworkAccessController();

if (OperatingSystem.IsLinux())
{
    AddApplianceVerificationService(sp =>
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
builder.Services.AddSingleton(sp => new WorkspaceSidecarSupervisor(
    sp.GetRequiredService<WorkspaceSidecarCatalog>(),
    sp.GetRequiredService<IHttpClientFactory>(),
    podmanCommandRunner,
    sp.GetRequiredService<IOptions<ManagerContainerStartupOptions>>(),
    sp.GetRequiredService<ILogger<WorkspaceSidecarSupervisor>>(),
    sp.GetRequiredService<RuntimeImageAllowlist>(),
    sp.GetRequiredService<PodmanImageArchiveStore>(),
    sp.GetRequiredService<EgressTopologyManager>(),
    applianceMode: applianceEnabled));
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
foreach (ServiceDescriptor service in verificationServices)
    builder.Services.Add(service);

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

if (KautukaMaintenanceMode.IsActive &&
    app.Services.GetService<ApplianceServiceSupervisor>() is { } applianceSupervisor &&
    !await applianceSupervisor.StopCohortAsync(CancellationToken.None).ConfigureAwait(false))
{
    throw new InvalidOperationException("Could not stop the appliance service containers for maintenance.");
}

app.GrantSocketGroupAccessWhenStarted(managerEndpoint);

app.UseAdmissionFence(path => KautukaMaintenanceMode.IsAllowedGrpcRequest(path, "GetHostStatus", "Ping", "UpdateDesiredState"));

app.MapDefaultEndpoints();
app.MapGrpcService<WorkspaceGrpcService>();

await app.RunAsync().ConfigureAwait(false);
return 0;
