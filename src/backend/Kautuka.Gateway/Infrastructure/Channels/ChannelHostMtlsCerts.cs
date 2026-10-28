using System.Security.Cryptography.X509Certificates;

using Kautuka.Telemetry.Security;

namespace Kautuka.Gateway.Infrastructure.Channels;

/// <summary>
/// Loads installer-provisioned Gateway-only private material and public ChannelHost trust.
/// Legacy shared-state bootstrap is available only in the Development environment.
/// </summary>
internal sealed class ChannelHostMtlsCerts
{
    private const string caCertFile = ".ca.cer";
    private const string clientPfxFile = ".channel-client.pfx";
    private const string delegationPfxFile = ".channel-delegation.pfx";

    public X509Certificate2 CaCert { get; }
    public X509Certificate2 ClientCert { get; }
    public X509Certificate2 DelegationSigningCert { get; }

    public ChannelHostMtlsCerts(IConfiguration configuration, IHostEnvironment environment, ILogger<ChannelHostMtlsCerts> logger)
    {
        string caPath;
        string pfxPath;
        string delegationPfxPath;
        if (environment.IsDevelopment())
        {
            string dataRoot = Path.GetFullPath(KautukaHostPathPolicy.ResolveServiceStateRoot(configuration, "channelHost"));
            caPath = Path.Combine(dataRoot, caCertFile);
            pfxPath = Path.Combine(dataRoot, clientPfxFile);
            delegationPfxPath = Path.Combine(dataRoot, delegationPfxFile);
            ChannelHostMtlsCertsLog.DevelopmentBootstrapWarning(logger);
        }
        else
        {
            string privateRoot = configuration["Kautuka:ChannelGateway:ProductionMaterial:GatewayPrivateRoot"]
                ?? throw new InvalidOperationException("production_gateway_private_material_root_required");
            string trustRoot = configuration["Kautuka:ChannelGateway:ProductionMaterial:GatewayTrustRoot"]
                ?? throw new InvalidOperationException("production_gateway_trust_material_root_required");
            if (string.Equals(Path.GetFullPath(privateRoot), Path.GetFullPath(trustRoot), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("production_gateway_private_and_trust_roots_must_be_distinct");
            ChannelSecurityMaterialProvisioner.ValidateGatewayRuntimeInventory(privateRoot, trustRoot);
            caPath = Path.Combine(trustRoot, ChannelSecurityMaterialProvisioner.CaCertificate);
            pfxPath = Path.Combine(privateRoot, ChannelSecurityMaterialProvisioner.GatewayClientPfx);
            delegationPfxPath = Path.Combine(privateRoot, ChannelSecurityMaterialProvisioner.GatewayDelegationPfx);
            if (Directory.EnumerateFiles(trustRoot).Any(static path => !string.Equals(Path.GetExtension(path), ".cer", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("gateway_runtime_trust_inventory_contains_private_material");
        }

        if (!File.Exists(caPath))
            throw new InvalidOperationException(
                $"ChannelHost CA cert not found at '{caPath}'. Start ChannelHost before Gateway.");

        if (!File.Exists(pfxPath))
            throw new InvalidOperationException(
                $"ChannelHost client cert not found at '{pfxPath}'. Start ChannelHost before Gateway.");
        if (!File.Exists(delegationPfxPath))
            throw new InvalidOperationException(
                $"ChannelHost delegation signing cert not found at '{delegationPfxPath}'. Start ChannelHost before Gateway.");

        CaCert = X509CertificateLoader.LoadCertificateFromFile(caPath);
        ClientCert = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, password: null);
        DelegationSigningCert = X509CertificateLoader.LoadPkcs12FromFile(delegationPfxPath, password: null);

        ChannelHostMtlsCertsLog.CertsLoaded(logger, CaCert.Thumbprint, ClientCert.Thumbprint);
    }
}

internal static partial class ChannelHostMtlsCertsLog
{
    [LoggerMessage(Level = LogLevel.Information,
        Message = "ChannelHost mTLS certs loaded: CA thumbprint={CaThumbprint}, client thumbprint={ClientThumbprint}")]
    public static partial void CertsLoaded(ILogger logger, string caThumbprint, string clientThumbprint);

    [LoggerMessage(Level = LogLevel.Critical,
        Message = "Channel Gateway is using Development-only shared private-key bootstrap. This mode does not isolate hostile same-user processes and must never be used in production.")]
    public static partial void DevelopmentBootstrapWarning(ILogger logger);
}
