namespace Kautuka.Manager.RuntimeContainer.Configuration;

/// <summary>Host-owned application deployment settings while the installer is migrated.</summary>
public sealed class CoreServiceOptions
{
    public const string Section = "CoreServices";
    public bool Enabled { get; set; }
    public string? WorkspaceRoot { get; set; }
    public string? ConfigurationRoot { get; set; }
}
