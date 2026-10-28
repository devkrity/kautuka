using System.Security.Cryptography.X509Certificates;

namespace Kautuka.Gateway.Infrastructure.Updates;

internal sealed class UpdateServiceMtlsCerts
{
    private const string caCertFile = ".ca.cer";
    private const string clientPfxFile = ".updateservice-client.pfx";

    public UpdateServiceMtlsCerts(IConfiguration configuration, ILogger<UpdateServiceMtlsCerts> logger)
    {
        string dataRoot = KautukaHostPathPolicy.ResolveServiceStateRoot(configuration, "updateService");
        dataRoot = Path.GetFullPath(dataRoot);

        string caPath = Path.Combine(dataRoot, caCertFile);
        string pfxPath = Path.Combine(dataRoot, clientPfxFile);

        if (!File.Exists(caPath))
            throw new InvalidOperationException($"UpdateService CA cert not found at '{caPath}'. Start UpdateService before Gateway.");

        if (!File.Exists(pfxPath))
            throw new InvalidOperationException($"UpdateService client cert not found at '{pfxPath}'. Start UpdateService before Gateway.");

        CaCert = X509CertificateLoader.LoadCertificateFromFile(caPath);
        ClientCert = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, password: null);

        UpdateServiceMtlsCertsLog.CertsLoaded(logger, CaCert.Thumbprint, ClientCert.Thumbprint);
    }

    public X509Certificate2 CaCert { get; }

    public X509Certificate2 ClientCert { get; }

}

internal static partial class UpdateServiceMtlsCertsLog
{
    [LoggerMessage(Level = LogLevel.Information,
        Message = "UpdateService mTLS certs loaded: CA thumbprint={CaThumbprint}, client thumbprint={ClientThumbprint}")]
    public static partial void CertsLoaded(ILogger logger, string caThumbprint, string clientThumbprint);
}
