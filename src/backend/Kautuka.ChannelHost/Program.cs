using Kautuka.ChannelHost;
using Kautuka.ChannelHost.Grpc;
using Kautuka.ChannelHost.Providers.Slack;
using Kautuka.ChannelHost.Providers.Telegram;
using Kautuka.Telemetry.Security;

using Microsoft.AspNetCore.Server.Kestrel.Core;

if (args.Length > 0 && string.Equals(args[0], "--provision-channel-security", StringComparison.Ordinal))
{
    if (args.Length is not 5 and not 6)
        throw new InvalidOperationException("Usage: --provision-channel-security <gateway-private-root> <gateway-trust-root> <channel-private-root> <channel-trust-root> [offline-authority-root]");
    ChannelSecurityMaterialProvisioner.Provision(new ChannelSecurityMaterialDirectories(args[1], args[2], args[3], args[4], args.Length == 6 ? args[5] : null));
    return;
}
if (args.Length > 0 && string.Equals(args[0], "--rotate-channel-security", StringComparison.Ordinal))
{
    if (args.Length != 7)
        throw new InvalidOperationException("Usage: --rotate-channel-security <stage|activate|rollback|finalize> <gateway-private-root> <gateway-trust-root> <channel-private-root> <channel-trust-root> <offline-authority-root>");
    var directories = new ChannelSecurityMaterialDirectories(args[2], args[3], args[4], args[5], args[6]);
    switch (args[1])
    {
        case "stage": ChannelSecurityMaterialProvisioner.StageGeneratedRotation(directories); break;
        case "activate": _ = ChannelSecurityMaterialProvisioner.ActivateStagedRotation(directories, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(15)); break;
        case "rollback": ChannelSecurityMaterialProvisioner.RollbackRotation(directories); break;
        case "finalize": ChannelSecurityMaterialProvisioner.FinalizeRotation(directories, DateTimeOffset.UtcNow); break;
        default: throw new InvalidOperationException("channel_security_rotation_action_invalid");
    }
    return;
}

KautukaAspNetCoreStartupPolicy.SanitizeHostingStartupAssemblies();

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseKestrelHttpsConfiguration();
builder.AddKautukaLogging(LogLevel.Debug);
builder.AddKautukaTelemetry(
    KautukaActivitySources.ProcessKind.Channel,
    KautukaActivitySources.Component.ChannelHost);

// -----------------------------------------------------------------------
// Proto-first gRPC server — Gateway dials this service to deliver intents,
// subscribe to inbound events, and push registration updates.
// -----------------------------------------------------------------------

builder.Services.AddGrpc();
builder.Services.AddSingleton<ChannelHostDatabase>();
builder.Services.AddSingleton<ChannelAttachmentStore>();
builder.Services.AddSingleton<ChannelSessionFactsViewStore>();
builder.Services.AddSingleton<ChannelSessionFactsOutboundExecutor>();
builder.Services.AddSingleton<ChannelRuntimeCredentialStore>();
builder.Services.AddSingleton<ChannelIngressStore>();
builder.Services.AddSingleton<ChannelProviderRegistry>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ChannelResourceGovernor>();
builder.Services.AddSingleton<ChannelOperationalHealthReader>();
builder.Services.AddSingleton<ChannelOutboundAssetStore>();
builder.Services.AddSingleton<ChannelProviderTransportProfile>();
builder.Services.AddTransient<ChannelProviderTransportRewriteHandler>();

builder.Services.AddSingleton(sp => new TelegramLongPollingWorker(
    sp.GetRequiredService<ChannelAttachmentStore>(),
    sp.GetRequiredService<ChannelIngressStore>(),
    sp.GetRequiredService<ChannelOutboundAssetStore>(),
    sp.GetRequiredService<IHttpClientFactory>(),
    sp.GetRequiredService<ILogger<TelegramLongPollingWorker>>(),
    sp.GetRequiredService<ChannelResourceGovernor>()));
builder.Services.AddSingleton(sp => new SlackSocketModeWorker(
    sp.GetRequiredService<ChannelAttachmentStore>(),
    sp.GetRequiredService<ChannelRuntimeCredentialStore>(),
    sp.GetRequiredService<ChannelIngressStore>(),
    sp.GetRequiredService<ChannelOutboundAssetStore>(),
    sp.GetRequiredService<IHttpClientFactory>(),
    sp.GetRequiredService<ILogger<SlackSocketModeWorker>>(),
    sp.GetRequiredService<ChannelResourceGovernor>()));
builder.Services.AddSingleton(sp => new ChannelHostGrpcService(
    sp.GetRequiredService<ChannelAttachmentStore>(),
    sp.GetRequiredService<ChannelProviderRegistry>(),
    sp.GetRequiredService<ChannelRuntimeCredentialStore>(),
    sp.GetRequiredService<ChannelIngressStore>(),
    sp.GetRequiredService<ChannelRequestAuthorizer>(),
    sp.GetRequiredService<ChannelResourceGovernor>(),
    sp.GetRequiredService<ChannelOperationalHealthReader>(),
    sp.GetRequiredService<ChannelOutboundAssetStore>(),
    sp.GetRequiredService<ChannelSessionFactsOutboundExecutor>(),
    sp.GetRequiredService<ILogger<ChannelHostGrpcService>>()));
builder.Services.AddSingleton<IChannelAdapter>(sp => sp.GetRequiredService<TelegramLongPollingWorker>());
builder.Services.AddSingleton<IChannelAdapter>(sp => sp.GetRequiredService<SlackSocketModeWorker>());
builder.Services.AddSingleton<IHostedService>(sp => new ChannelAdapterHostedService(
    sp.GetServices<IChannelAdapter>()));
builder.Services.AddHostedService<ChannelQueueCardExpiryWorker>();

builder.Services.AddHttpClient("telegram-api", client =>
{
    // Max Telegram long-poll server timeout is clamped at 25 s (well under typical NAT
    // idle TCP timeout of ~30 s). Allow 15 s headroom for TLS + network overhead.
    client.Timeout = TimeSpan.FromSeconds(40);
}).AddHttpMessageHandler<ChannelProviderTransportRewriteHandler>();
builder.Services.AddHttpClient("slack-api", client => client.Timeout = TimeSpan.FromSeconds(30)).AddHttpMessageHandler<ChannelProviderTransportRewriteHandler>();

// -----------------------------------------------------------------------
// Resolve canonical channel data root.
// Containerized runs use /data internally; host-native runs use the shared host path policy.
// -----------------------------------------------------------------------
string channelDataRoot = KautukaHostPathPolicy.ResolveServiceStateRoot(builder.Configuration, "channelHost");
channelDataRoot = Path.GetFullPath(channelDataRoot);

System.Security.Cryptography.X509Certificates.X509Certificate2 serverCert;
System.Security.Cryptography.X509Certificates.X509Certificate2 caCert;
var activeGatewayClientThumbprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var delegationCertificates = new List<System.Security.Cryptography.X509Certificates.X509Certificate2>();
DateTimeOffset clientCertificateNotAfterUtc = DateTimeOffset.MaxValue;
string rotationStateName = "STABLE";
DateTimeOffset? rotationOverlapExpiresAtUtc = null;
var revokedIdentityKeyIds = new List<string>();
string previousPath;
if (builder.Environment.IsDevelopment())
{
    await Console.Error.WriteLineAsync("SECURITY WARNING: Channel Gateway development bootstrap shares private material and is not a hostile-process security boundary.").ConfigureAwait(false);
    (serverCert, caCert) = LocalCaMtlsBootstrap.EnsureAndLoad(channelDataRoot, "Channel", ".channel-client.pfx");
    using var activeGatewayClient = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(
        Path.Combine(channelDataRoot, ".channel-client.pfx"), null);
    _ = activeGatewayClientThumbprints.Add(activeGatewayClient.Thumbprint);
    clientCertificateNotAfterUtc = activeGatewayClient.NotAfter.ToUniversalTime();
    using (LocalCaMtlsBootstrap.EnsureDelegationSigningCertificate(
        channelDataRoot, "Channel", ".channel-delegation.pfx", ".channel-delegation.cer")) { }
    delegationCertificates.Add(System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificateFromFile(
        Path.Combine(channelDataRoot, ".channel-delegation.cer")));
    previousPath = Path.Combine(channelDataRoot, ".channel-delegation.cer.previous");
}
else
{
    string channelPrivateRoot = builder.Configuration["Kautuka:ChannelGateway:ProductionMaterial:ChannelPrivateRoot"]
        ?? throw new InvalidOperationException("production_channel_private_material_root_required");
    string channelTrustRoot = builder.Configuration["Kautuka:ChannelGateway:ProductionMaterial:ChannelTrustRoot"]
        ?? throw new InvalidOperationException("production_channel_trust_material_root_required");
    if (string.Equals(Path.GetFullPath(channelPrivateRoot), Path.GetFullPath(channelTrustRoot), StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("production_channel_private_and_trust_roots_must_be_distinct");
    ChannelSecurityMaterialProvisioner.ValidateChannelRuntimeInventory(channelPrivateRoot, channelTrustRoot);
    string serverPath = Path.Combine(channelPrivateRoot, ChannelSecurityMaterialProvisioner.ServerPfx);
    string caPath = Path.Combine(channelTrustRoot, ChannelSecurityMaterialProvisioner.CaCertificate);
    string clientPath = Path.Combine(channelTrustRoot, ChannelSecurityMaterialProvisioner.GatewayClientCertificate);
    string delegationPath = Path.Combine(channelTrustRoot, ChannelSecurityMaterialProvisioner.GatewayDelegationCertificate);
    serverCert = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(serverPath, null);
    caCert = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificateFromFile(caPath);
    using var activeGatewayClient = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificateFromFile(clientPath);
    if (activeGatewayClient.HasPrivateKey) throw new InvalidOperationException("channel_runtime_gateway_identity_must_be_public_only");
    _ = activeGatewayClientThumbprints.Add(activeGatewayClient.Thumbprint);
    clientCertificateNotAfterUtc = activeGatewayClient.NotAfter.ToUniversalTime();
    var delegation = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificateFromFile(delegationPath);
    if (delegation.HasPrivateKey) throw new InvalidOperationException("channel_runtime_delegation_key_must_be_public_only");
    delegationCertificates.Add(delegation);
    if (Directory.EnumerateFiles(channelTrustRoot).Any(static path => !string.Equals(Path.GetExtension(path), ".cer", StringComparison.OrdinalIgnoreCase)))
        throw new InvalidOperationException("channel_runtime_trust_inventory_contains_private_material");
    previousPath = string.Empty;
    ChannelSecurityRotationState? rotation = ChannelSecurityMaterialProvisioner.ReadRotationState(channelTrustRoot);
    if (rotation is not null)
    {
        if (!string.Equals(rotation.CurrentClientThumbprint, activeGatewayClient.Thumbprint, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(rotation.CurrentDelegationThumbprint, delegation.Thumbprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("channel_security_rotation_manifest_current_identity_mismatch");
        rotationOverlapExpiresAtUtc = rotation.PreviousAcceptedUntilUtc;
        bool overlapActive = rotation.PreviousAcceptedUntilUtc > DateTimeOffset.UtcNow;
        rotationStateName = overlapActive ? "OVERLAP_ACTIVE" : "OVERLAP_EXPIRED_PENDING_FINALIZE";
        string previousClientPath = clientPath + ChannelSecurityMaterialProvisioner.PreviousSuffix;
        string previousDelegationPath = delegationPath + ChannelSecurityMaterialProvisioner.PreviousSuffix;
        using var previousClient = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificateFromFile(previousClientPath);
        using var previousDelegation = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificateFromFile(previousDelegationPath);
        if (!string.Equals(rotation.PreviousClientThumbprint, previousClient.Thumbprint, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(rotation.PreviousDelegationThumbprint, previousDelegation.Thumbprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("channel_security_rotation_manifest_previous_identity_mismatch");
        if (overlapActive)
        {
            _ = activeGatewayClientThumbprints.Add(previousClient.Thumbprint);
            delegationCertificates.Add(new System.Security.Cryptography.X509Certificates.X509Certificate2(previousDelegation));
        }
        else
        {
            revokedIdentityKeyIds.Add(previousClient.Thumbprint);
            revokedIdentityKeyIds.Add(previousDelegation.Thumbprint);
        }
    }
}
if (!string.IsNullOrWhiteSpace(previousPath) && File.Exists(previousPath))
{
    var previous = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificateFromFile(previousPath);
    if (previous.NotAfter.ToUniversalTime() > DateTime.UtcNow) delegationCertificates.Add(previous); else previous.Dispose();
}
var verificationKeys = new ChannelDelegationVerificationKeys(delegationCertificates);
builder.Services.AddSingleton(verificationKeys);
builder.Services.AddSingleton(new ChannelSecurityRuntimeHealth(
    clientCertificateNotAfterUtc,
    delegationCertificates.Count == 0 ? DateTimeOffset.MinValue : delegationCertificates.Min(static certificate => new DateTimeOffset(certificate.NotAfter.ToUniversalTime())),
    rotationStateName,
    rotationOverlapExpiresAtUtc,
    revokedIdentityKeyIds));
var gatewayPolicy = new ChannelWorkloadPolicy(
    "kautuka-gateway",
    activeGatewayClientThumbprints,
    "Kautuka Gateway Client",
    LocalCaMtlsBootstrap.GatewayChannelWorkloadIdentity,
    new HashSet<string>(StringComparer.Ordinal)
    {
        "Ping", "ListProviders", "StreamIngress", "AcknowledgeIngress", "SetRuntimeCredentials", "ReadInboundMedia",
        "GetAttachments", "GetAttachment", "GetAttachmentSchema", "UpsertAttachment", "SyncOwnerTargets",
        "ProvisionAttachment", "DeactivateAttachment", "TransitionAttachmentLifecycle", "SendOutboundMessage", "UploadOutboundAsset",
    },
    new HashSet<string>(StringComparer.Ordinal)
    {
        ChannelRequestAuthorizer.Read, ChannelRequestAuthorizer.Write, ChannelRequestAuthorizer.Runtime, ChannelRequestAuthorizer.Ingress,
    },
    "kautuka-gateway", "kautuka-channel-gateway", delegationCertificates,
    new HashSet<string>(StringComparer.Ordinal), true, true, true, true);
ChannelWorkloadPolicyRegistry workloadPolicies = ChannelWorkloadPolicyRegistry.Create(caCert, gatewayPolicy, builder.Configuration);
builder.Services.AddSingleton(workloadPolicies);
builder.Services.AddSingleton<ChannelRequestAuthorizer>();

// Kestrel: HTTP/2 with mTLS — TLS is mandatory, NO unencrypted support.
KautukaEndpoint channelHostEndpoint = KautukaHostNetworkPolicy.Resolve(builder.Configuration).ChannelHost;
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Listen(channelHostEndpoint, opts =>
    {
        opts.Protocols = HttpProtocols.Http2;
        opts.UseHttps(serverCert, https =>
        {
            https.ClientCertificateMode = Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.RequireCertificate;
            https.ClientCertificateValidation = (cert, _, _) =>
            {
                if (cert is null) return false;
                using var cert2 = new System.Security.Cryptography.X509Certificates.X509Certificate2(cert);
                return workloadPolicies.ValidateTransport(cert2);
            };
        });
    });
});

var app = builder.Build();

app.GrantSocketGroupAccessWhenStarted(channelHostEndpoint);

ChannelProviderTransportProfile configuredTransport = app.Services.GetRequiredService<ChannelProviderTransportProfile>();
LoggerMessage.Define<ChannelProviderTransportMode, string>(LogLevel.Information, new EventId(5401, "ChannelProviderTransport"),
    "Channel provider transport configured. Mode={Mode}; Endpoint={Endpoint}")(app.Logger, configuredTransport.Mode,
    configuredTransport.SanitizedEndpointIdentity, null);

app.Services.GetRequiredService<ChannelHostDatabase>().EnsureReady();

app.MapDefaultEndpoints();
app.MapGrpcService<ChannelHostGrpcService>();

await app.RunAsync().ConfigureAwait(false);
