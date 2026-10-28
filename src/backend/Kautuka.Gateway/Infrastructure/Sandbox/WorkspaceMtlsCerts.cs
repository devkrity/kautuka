using System.Security.Cryptography.X509Certificates;

namespace Kautuka.Gateway.Infrastructure.Sandbox;

/// <summary>
/// Loads the stable CA cert and rotating client cert for mTLS communication with Kautuka.Manager.
/// The CA cert is stable (persisted by Kautuka.Manager on first start) — Gateway can survive
/// Kautuka.Manager restarts without needing its own restart.
/// The client PFX is overwritten by Kautuka.Manager on every restart.
/// </summary>
internal sealed class WorkspaceMtlsCerts
{
    private const string caCertFile = ".ca.cer";
    private const string clientPfxFile = Kautuka.Manager.Protos.ManagerClientIdentities.GatewayCertificateFileName;

    public X509Certificate2 CaCert { get; }
    public X509Certificate2 ClientCert { get; }

    public WorkspaceMtlsCerts(IConfiguration configuration, ILogger<WorkspaceMtlsCerts> logger)
    {
        string dataRoot = KautukaHostPathPolicy.ResolveServiceStateRoot(configuration, "manager");
        dataRoot = Path.GetFullPath(dataRoot);

        string caPath = Path.Combine(dataRoot, caCertFile);
        string pfxPath = Path.Combine(dataRoot, clientPfxFile);

        if (!File.Exists(caPath))
            throw new InvalidOperationException(
                $"Manager CA cert not found at '{caPath}'. Start Kautuka.Manager before Gateway.");

        if (!File.Exists(pfxPath))
            throw new InvalidOperationException(
                $"Manager client cert not found at '{pfxPath}'. Start Kautuka.Manager before Gateway.");

        CaCert = X509CertificateLoader.LoadCertificateFromFile(caPath);
        ClientCert = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, password: null);

        WorkspaceMtlsCertsLog.CertsLoaded(logger, CaCert.Thumbprint, ClientCert.Thumbprint);
    }
}

internal static partial class WorkspaceMtlsCertsLog
{
    [LoggerMessage(Level = LogLevel.Information,
        Message = "Manager mTLS certs loaded: CA thumbprint={CaThumbprint}, client thumbprint={ClientThumbprint}")]
    public static partial void CertsLoaded(ILogger logger, string caThumbprint, string clientThumbprint);
}
