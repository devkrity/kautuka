namespace Kautuka.CredentialsHost.Hosting;

internal sealed class CredentialClientIdentityState(string thumbprint, DateTimeOffset notAfter)
{
    private readonly object sync = new();
    private string thumbprint = thumbprint;
    private DateTimeOffset notAfter = notAfter;

    public (string Thumbprint, DateTimeOffset NotAfter) Current
    {
        get { lock (sync) return (thumbprint, notAfter); }
    }

    public void Activate(string newThumbprint, DateTimeOffset newNotAfter)
    {
        lock (sync)
        {
            thumbprint = newThumbprint;
            notAfter = newNotAfter;
        }
    }
}
