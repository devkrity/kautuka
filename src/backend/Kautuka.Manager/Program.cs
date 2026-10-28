using Kautuka.Hosting.Hosting;
using Kautuka.Manager.Grpc;
using Kautuka.Manager.Hosting;
using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Sandbox;
using Kautuka.Telemetry.DependencyInjection;
using Kautuka.Telemetry.Security;
using Kautuka.Telemetry.Telemetry;

using Microsoft.Extensions.Options;

const int GrpcMaxMessageSizeBytes = 4 * 1024 * 1024;

KautukaAspNetCoreStartupPolicy.SanitizeHostingStartupAssemblies();

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseKestrelHttpsConfiguration();
builder.AddKautukaLogging(LogLevel.Debug);
builder.AddKautukaTelemetry(
    KautukaActivitySources.ProcessKind.Manager,
    KautukaActivitySources.Component.Manager);
builder.Services.AddSingleton<WorkspaceRuntimePaths>();
builder.Services.AddSingleton<PodmanCommandRunner>();
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
    sp.GetRequiredService<ILogger<WorkspaceContainerSupervisorWorker>>()));
builder.Services.AddGrpc(options =>
{
    options.MaxReceiveMessageSize = GrpcMaxMessageSizeBytes;
    options.MaxSendMessageSize = GrpcMaxMessageSizeBytes;
    options.Interceptors.Add<ManagerClientAuthorizationInterceptor>();
});
builder.Services.AddSingleton<ManagerClientAuthorizationInterceptor>();

// -----------------------------------------------------------------------
// Resolve canonical shared data root.
// All mutable state is anchored through the shared host path policy.
// -----------------------------------------------------------------------
string managerDataRoot = KautukaHostPathPolicy.ResolveServiceStateRoot(builder.Configuration, "manager");
managerDataRoot = Path.GetFullPath(managerDataRoot);
KautukaEndpoint managerEndpoint = KautukaHostNetworkPolicy.Resolve(builder.Configuration).Manager;

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

app.MapDefaultEndpoints();
app.MapGrpcService<WorkspaceGrpcService>();

await app.RunAsync().ConfigureAwait(false);
