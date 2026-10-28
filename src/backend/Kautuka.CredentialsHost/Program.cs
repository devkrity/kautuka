using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Kautuka.CredentialsHost.Database;
using Kautuka.CredentialsHost.Grpc;
using Kautuka.CredentialsHost.Hosting;
using Kautuka.CredentialsHost.Security;
using Kautuka.Telemetry.Security;

using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;

KautukaAspNetCoreStartupPolicy.SanitizeHostingStartupAssemblies();

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseKestrelHttpsConfiguration();
builder.AddKautukaLogging(LogLevel.Debug);
builder.AddKautukaTelemetry(
    KautukaActivitySources.ProcessKind.Credentials,
    KautukaActivitySources.Component.CredentialsHost);

// -----------------------------------------------------------------------
// Resolve canonical credentials data root.
// Containerized runs use /data internally; host-native runs use the shared host path policy.
// -----------------------------------------------------------------------
string credDataRoot = KautukaHostPathPolicy.ResolveServiceStateRoot(builder.Configuration, "credentialsHost");
credDataRoot = Path.GetFullPath(credDataRoot);
Directory.CreateDirectory(credDataRoot);
var storageLayout = new CredentialStorageLayout(credDataRoot);
var credentialsDatabase = new CredentialsDatabase(storageLayout.VaultRoot);

string? recoveryKeyFile = ReadOption(args, "--recovery-key-file");
string? backupPath = ReadOption(args, "--credential-backup");
string? restorePath = ReadOption(args, "--credential-restore");
bool rotateCredentialKey = args.Contains("--rotate-credential-key", StringComparer.Ordinal);
if (backupPath is not null || restorePath is not null || rotateCredentialKey)
{
    int operationCount = (backupPath is null ? 0 : 1) + (restorePath is null ? 0 : 1) + (rotateCredentialKey ? 1 : 0);
    if (operationCount != 1 || ((backupPath is not null || restorePath is not null) && string.IsNullOrWhiteSpace(recoveryKeyFile)))
        throw new InvalidOperationException("Specify exactly one credential maintenance operation and its required external recovery key.");
    if (rotateCredentialKey)
    {
        CredentialsSchemaInitializer.EnsureReady(credentialsDatabase);
        using var keyRing = new CredentialKeyRing(storageLayout.VaultRoot);
        var audit = new CredentialAuditStore(credentialsDatabase);
        var rotation = new CredentialKeyRotationService(
            credentialsDatabase,
            keyRing,
            new CredentialsVaultService(keyRing),
            audit);
        await rotation.RunToCompletionAsync(CancellationToken.None).ConfigureAwait(false);
    }
    else if (backupPath is not null)
    {
        var recovery = new CredentialRecoveryArchive(storageLayout, credentialsDatabase);
        CredentialsSchemaInitializer.EnsureReady(credentialsDatabase);
        await recovery.CreateAsync(backupPath, recoveryKeyFile!, CancellationToken.None).ConfigureAwait(false);
    }
    else
    {
        var recovery = new CredentialRecoveryArchive(storageLayout, credentialsDatabase);
        await recovery.RestoreBeforeStartupAsync(restorePath!, recoveryKeyFile!, CancellationToken.None).ConfigureAwait(false);
    }
    return;
}

// -----------------------------------------------------------------------
// Stable local CA mTLS bootstrap.
// CA persisted on first start; only leaf certs rotate.
// -----------------------------------------------------------------------
var (serverCert, caCert) = LocalCaMtlsBootstrap.EnsureAndLoad(storageLayout.TransportRoot, "Credentials", ".credentials-client.pfx");
CredentialStorageLayout.PublishGatewayReadableFile(Path.Combine(storageLayout.TransportRoot, ".ca.cer"));
CredentialStorageLayout.PublishGatewayReadableFile(Path.Combine(storageLayout.TransportRoot, ".credentials-client.pfx"));
using X509Certificate2 activeGatewayClientCert = X509CertificateLoader.LoadPkcs12FromFile(
    Path.Combine(storageLayout.TransportRoot, ".credentials-client.pfx"),
    password: null);
var clientIdentityState = new CredentialClientIdentityState(activeGatewayClientCert.Thumbprint, activeGatewayClientCert.NotAfter);

// -----------------------------------------------------------------------
// gRPC services
// -----------------------------------------------------------------------
builder.Services.AddGrpc();
builder.Services.AddSingleton(storageLayout);
builder.Services.AddSingleton(credentialsDatabase);
builder.Services.AddSingleton(clientIdentityState);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHostedService<CredentialClientCertificateRenewalService>();
builder.Services.AddSingleton(_ => new CredentialKeyRing(storageLayout.VaultRoot));
builder.Services.AddSingleton<CredentialRecoveryArchive>();
builder.Services.AddSingleton<CredentialsVaultService>();
builder.Services.AddSingleton<CredentialAuditStore>();
builder.Services.AddSingleton<CredentialRepository>();
builder.Services.AddSingleton<CredentialsGrpcService>();
builder.Services.AddHealthChecks().AddCheck<CredentialReadinessHealthCheck>("credentials-ready");

// -----------------------------------------------------------------------
// Kestrel: HTTP/2 with mTLS — TLS is mandatory, NO unencrypted support.
// -----------------------------------------------------------------------
KautukaEndpoint credentialsEndpoint = KautukaHostNetworkPolicy.Resolve(builder.Configuration).Credentials;
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Listen(credentialsEndpoint, opts =>
    {
        opts.Protocols = HttpProtocols.Http2;
        opts.UseHttps(serverCert, https =>
        {
            https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
            https.ClientCertificateValidation = (cert, _, _) =>
            {
                if (cert is null) return false;
                using var cert2 = new X509Certificate2(cert);
                return ValidateGatewayClientCertificate(cert2, caCert, clientIdentityState.Current.Thumbprint);
            };
        });
    });
});

var app = builder.Build();

app.GrantSocketGroupAccessWhenStarted(credentialsEndpoint);

// Schema initializer runs synchronously at startup — safe because nothing else is running yet.
CredentialsSchemaInitializer.EnsureReady(app.Services.GetRequiredService<CredentialsDatabase>());
CredentialsHostProgramLog.HostStarted(app.Logger, credentialsEndpoint.Port);

app.MapDefaultEndpoints();
app.MapGrpcService<CredentialsGrpcService>();

await app.RunAsync().ConfigureAwait(false);

static string? ReadOption(string[] arguments, string name)
{
    int index = Array.IndexOf(arguments, name);
    if (index < 0) return null;
    if (index == arguments.Length - 1 || string.IsNullOrWhiteSpace(arguments[index + 1]))
        throw new InvalidOperationException($"Option {name} requires a value.");
    return arguments[index + 1];
}

static bool ValidateGatewayClientCertificate(
    X509Certificate2 clientCertificate,
    X509Certificate2 caCertificate,
    string activeThumbprint)
{
    if (!string.Equals(clientCertificate.Thumbprint, activeThumbprint, StringComparison.OrdinalIgnoreCase)
        || !string.Equals(
            clientCertificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false),
            "Kautuka Gateway Client",
            StringComparison.Ordinal))
    {
        return false;
    }

    using var chain = new X509Chain();
    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
    chain.ChainPolicy.CustomTrustStore.Add(caCertificate);
    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
    chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.2"));
    return chain.Build(clientCertificate);
}

static partial class CredentialsHostProgramLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "CredentialsHost started on gRPC port {Port}")]
    public static partial void HostStarted(ILogger logger, int port);
}
