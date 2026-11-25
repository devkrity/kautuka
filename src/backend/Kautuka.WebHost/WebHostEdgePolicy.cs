using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace Kautuka.WebHost;

internal sealed record WebHostEdgeBinding(IPAddress Address, int Port, X509Certificate2? Certificate)
    : IDisposable
{
    public bool UsesTls => Certificate is not null;

    public void Dispose() => Certificate?.Dispose();
}

internal static class WebHostEdgePolicy
{
    public static WebHostEdgeBinding Resolve(
        IConfiguration configuration,
        int port,
        bool appliance = false)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);

        // The rootless host publish owns the loopback listener. Kestrel binds only
        // inside the dedicated namespace whose firewall permits incoming HTTP.
        if (appliance) return new WebHostEdgeBinding(IPAddress.Any, port, null);

        string configuredAddress = configuration["WebHost:ListenAddress"]?.Trim() ?? IPAddress.Loopback.ToString();
        if (!IPAddress.TryParse(configuredAddress, out IPAddress? address))
            throw new InvalidOperationException("WebHost:ListenAddress must be an IP address.");

        string? certificatePath = configuration["WebHost:Tls:CertificatePath"]?.Trim();
        X509Certificate2? certificate = null;
        if (!string.IsNullOrWhiteSpace(certificatePath))
        {
            string fullPath = Path.GetFullPath(certificatePath);
            if (!File.Exists(fullPath))
                throw new InvalidOperationException($"WebHost TLS certificate was not found at '{fullPath}'.");

            certificate = X509CertificateLoader.LoadPkcs12FromFile(
                fullPath,
                configuration["WebHost:Tls:CertificatePassword"]);
        }

        if (!IPAddress.IsLoopback(address) && certificate is null)
            throw new InvalidOperationException("WebHost requires a TLS certificate when listening on a non-loopback address.");

        return new WebHostEdgeBinding(address, port, certificate);
    }
}
