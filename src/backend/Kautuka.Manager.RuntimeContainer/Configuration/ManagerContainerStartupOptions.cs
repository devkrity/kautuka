namespace Kautuka.Manager.RuntimeContainer.Configuration;

public sealed class ManagerContainerStartupOptions
{
    public const string Section = "ManagerContainerStartup";
    private const int maxAllowedConcurrency = 32;

    public int SidecarMaxConcurrency { get; set; } = 5;

    public int SandboxMaxConcurrency { get; set; } = 5;

    public int GetEffectiveSidecarMaxConcurrency()
        => Math.Clamp(SidecarMaxConcurrency, 1, maxAllowedConcurrency);

    public int GetEffectiveSandboxMaxConcurrency()
        => Math.Clamp(SandboxMaxConcurrency, 1, maxAllowedConcurrency);
}
