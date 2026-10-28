using System.Security.Cryptography.X509Certificates;

using Kautuka.Hosting.Hosting;

namespace Kautuka.UpdateService.Hosting;

public sealed class UpdateServiceMtlsCerts
{
    private const string caCertFile = ".ca.cer";
    private const string clientPfxFile = Kautuka.Manager.Protos.ManagerClientIdentities.UpdateServiceCertificateFileName;

    public UpdateServiceMtlsCerts(IConfiguration configuration)
    {
        string dataRoot = KautukaHostPathPolicy.ResolveServiceStateRoot(configuration, "manager");
        dataRoot = Path.GetFullPath(dataRoot);

        string caPath = Path.Combine(dataRoot, caCertFile);
        string pfxPath = Path.Combine(dataRoot, clientPfxFile);

        if (!File.Exists(caPath))
        {
            throw new InvalidOperationException($"Manager CA cert not found at '{caPath}'. Start Kautuka.Manager before UpdateService.");
        }

        if (!File.Exists(pfxPath))
        {
            throw new InvalidOperationException($"Manager client cert not found at '{pfxPath}'. Start Kautuka.Manager before UpdateService.");
        }

        CaCert = X509CertificateLoader.LoadCertificateFromFile(caPath);
        ClientCert = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, password: null);
    }

    public X509Certificate2 CaCert { get; }

    public X509Certificate2 ClientCert { get; }
}
