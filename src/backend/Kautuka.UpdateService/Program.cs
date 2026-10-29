using Kautuka.Hosting.Hosting;
using Kautuka.Telemetry.DependencyInjection;
using Kautuka.Telemetry.Security;
using Kautuka.Telemetry.Telemetry;
using Kautuka.UpdateService.Abstractions;
using Kautuka.UpdateService.Grpc;
using Kautuka.UpdateService.Hosting;

using Microsoft.AspNetCore.Server.Kestrel.Core;

KautukaAspNetCoreStartupPolicy.SanitizeHostingStartupAssemblies();

var builder = WebApplication.CreateSlimBuilder(args);
var hostLayout = KautukaHostPathPolicy.Resolve(builder.Configuration);
builder.WebHost.UseKestrelHttpsConfiguration();
builder.AddKautukaLogging(LogLevel.Debug);
builder.AddKautukaTelemetry(
    KautukaActivitySources.ProcessKind.UpdateService,
    KautukaActivitySources.Component.UpdateService);

string updateServiceDataRoot = KautukaHostPathPolicy.ResolveServiceStateRoot(builder.Configuration, "updateService");
updateServiceDataRoot = Path.GetFullPath(updateServiceDataRoot);
Directory.CreateDirectory(updateServiceDataRoot);

var (serverCert, caCert) = LocalCaMtlsBootstrap.EnsureAndLoad(updateServiceDataRoot, "UpdateService", ".updateservice-client.pfx");

builder.Services.AddGrpc();
builder.Services.AddSingleton<UpdateServiceRuntimePaths>();
builder.Services.AddSingleton<UpdateServiceMtlsCerts>();
builder.Services.AddSingleton<IManagerStatusClient, ManagerStatusClient>();
builder.Services.AddSingleton<IUpdateServiceIntegration, UpdateServiceIntegration>();
builder.Services.AddUpdateService();
builder.Services.AddOptions<UpdateOptions>()
    .Configure<UpdateServiceRuntimePaths, IHostEnvironment>((options, paths, environment) =>
    {
        options.StateRootPath = paths.RuntimeRoot;

        if (hostLayout.Mode == KautukaHostMode.Prod)
        {
            // Production never permits the unsigned local-feed escape hatch.
            options.AllowUnsignedLocalFeeds = false;
            options.RepositoryOwner = "devkrity";
            options.RepositoryName = "kautuka";
            options.ManifestPublicKeyPath ??= ProvisionedUpdateSourcePolicy.DefaultManifestPublicKeyPath;
            // Production discovers strict channel releases through the anonymous GitHub Releases
            // API, then verifies the signed manifest from the exact immutable release tag.
            options.ReleaseManifestUrl = null;

            ProvisionedUpdateSourceDocument? source = ProvisionedUpdateSourcePolicy.Resolve();

            if (source is not null)
            {
                if (!string.IsNullOrWhiteSpace(source.CatalogManifestUrl))
                {
                    options.CatalogManifestUrl = source.CatalogManifestUrl;
                    options.CatalogManifestPublicKeyPath = ProvisionedUpdateSourcePolicy.DefaultManifestPublicKeyPath;
                }

                return;
            }

            return;
        }

        string localManifestPublicKeyPath = Path.GetFullPath(Path.Combine(
            environment.ContentRootPath,
            "..",
            "..",
            "..",
            "artifacts",
            "local-release-publish",
            "current",
            "manifest-public-key.pem"));

        if (File.Exists(localManifestPublicKeyPath))
        {
            options.ManifestPublicKeyPath ??= localManifestPublicKeyPath;
            options.CatalogManifestPublicKeyPath ??= localManifestPublicKeyPath;
        }
    });

KautukaEndpoint updateServiceEndpoint = KautukaHostNetworkPolicy.Resolve(builder.Configuration).UpdateService;
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Listen(updateServiceEndpoint, opts =>
    {
        opts.Protocols = HttpProtocols.Http2;
        opts.UseHttps(serverCert, https =>
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

var app = builder.Build();

app.GrantSocketGroupAccessWhenStarted(updateServiceEndpoint);

app.MapDefaultEndpoints();
app.MapGrpcService<UpdateServiceGrpcService>();

await app.RunAsync().ConfigureAwait(false);
