namespace Kautuka.Manager.RuntimeContainer.Configuration;

/// <summary>Host-owned application deployment settings while the installer is migrated.</summary>
public sealed class CoreServiceOptions
{
    public const string Section = "CoreServices";
    public bool Enabled { get; set; }
    public string? WorkspaceRoot { get; set; }
    public string? ConfigurationRoot { get; set; }
    public string WebHostBindAddress { get; set; } = "127.0.0.1";
    public int WebHostPort { get; set; } = 20229;
    public string? WebHostCertificateDirectory { get; set; }
}
