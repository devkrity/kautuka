using System.Security.Cryptography.X509Certificates;

namespace Kautuka.Gateway.Infrastructure.Credentials;

/// <summary>
/// Loads the stable CA cert and rotating client cert for mTLS communication with CredentialsHost.
/// The CA cert is stable (persisted by CredentialsHost on first start) — Gateway can survive
/// CredentialsHost restarts without needing its own restart.
/// The client PFX is overwritten by CredentialsHost on every restart.
/// </summary>
internal sealed class CredentialsMtlsCerts : IDisposable
{
    private const string caCertFile = ".ca.cer";
    private const string clientPfxFile = ".credentials-client.pfx";

    private readonly object sync = new();
    private readonly string pfxPath;
    private readonly ILogger<CredentialsMtlsCerts> logger;
    private readonly List<X509Certificate2> retiredClientCertificates = [];
    private DateTime clientPfxLastWriteUtc;
    private X509Certificate2 clientCert;

    public X509Certificate2 CaCert { get; }

    public X509Certificate2 ClientCert
    {
        get
        {
            lock (sync)
            {
                DateTime currentLastWriteUtc = File.GetLastWriteTimeUtc(pfxPath);
                if (currentLastWriteUtc != clientPfxLastWriteUtc)
                {
                    X509Certificate2 replacement = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, password: null);
                    retiredClientCertificates.Add(clientCert);
                    clientCert = replacement;
                    clientPfxLastWriteUtc = currentLastWriteUtc;
                    CredentialsMtlsCertsLog.CertsLoaded(logger, CaCert.Thumbprint, clientCert.Thumbprint);
                }

                return clientCert;
            }
        }
    }

    public CredentialsMtlsCerts(IConfiguration configuration, ILogger<CredentialsMtlsCerts> logger)
    {
        string dataRoot = KautukaHostPathPolicy.ResolveServiceStateRoot(configuration, "credentialsHost");
        dataRoot = Path.Combine(Path.GetFullPath(dataRoot), "transport");

        string caPath = Path.Combine(dataRoot, caCertFile);
        pfxPath = Path.Combine(dataRoot, clientPfxFile);
        this.logger = logger;

        if (!File.Exists(caPath))
            throw new InvalidOperationException(
                $"CredentialsHost CA cert not found at '{caPath}'. Start CredentialsHost before Gateway.");

        if (!File.Exists(pfxPath))
            throw new InvalidOperationException(
                $"CredentialsHost client cert not found at '{pfxPath}'. Start CredentialsHost before Gateway.");

        CaCert = X509CertificateLoader.LoadCertificateFromFile(caPath);
        clientCert = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, password: null);
        clientPfxLastWriteUtc = File.GetLastWriteTimeUtc(pfxPath);

        CredentialsMtlsCertsLog.CertsLoaded(logger, CaCert.Thumbprint, clientCert.Thumbprint);
    }

    public void Dispose()
    {
        lock (sync)
        {
            clientCert.Dispose();
            foreach (X509Certificate2 certificate in retiredClientCertificates)
            {
                certificate.Dispose();
            }

            retiredClientCertificates.Clear();
            CaCert.Dispose();
        }
    }
}

internal static partial class CredentialsMtlsCertsLog
{
    [LoggerMessage(Level = LogLevel.Information,
        Message = "CredentialsHost mTLS certs loaded: CA thumbprint={CaThumbprint}, client thumbprint={ClientThumbprint}")]
    public static partial void CertsLoaded(ILogger logger, string caThumbprint, string clientThumbprint);
}
