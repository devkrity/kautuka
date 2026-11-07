namespace Kautuka.ContractTests;

public sealed class InstalledLinuxSmokeContractTests
{
    [Fact]
    public void SmokeRequiresHostControllersActiveNotActivating()
    {
        string projectRoot = FindProjectRoot();
        string source = File.ReadAllText(Path.Combine(projectRoot, "scripts", "installed-linux-smoke.sh"));

        Assert.Contains("assert_unit_active user kautuka-manager.service", source, StringComparison.Ordinal);
        Assert.Contains("assert_unit_active user kautuka-updateservice.service", source, StringComparison.Ordinal);
        Assert.Contains("activating auto-restart is a crash loop", source, StringComparison.Ordinal);
        Assert.Contains("KAUTUKA_SMOKE_SETUP_TOKEN", source, StringComparison.Ordinal);
        Assert.Contains("\"setupToken\":sys.argv[3]", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsInstallerShowsTheSetupTokenOnlyAfterProtectedExplicitRequestAndWaitsForAcknowledgement()
    {
        string projectRoot = FindProjectRoot();
        string endpoint = File.ReadAllText(Path.Combine(projectRoot, "src", "installer", "Kautuka.Installer.Windows", "Api", "InstallerEndpoints.cs"));
        string ui = File.ReadAllText(Path.Combine(projectRoot, "src", "installer", "Kautuka.Installer.Windows", "wwwroot", "index.html"));

        string system = File.ReadAllText(Path.Combine(projectRoot, "src", "installer", "Kautuka.Installer.Windows", "Installation", "WindowsApplianceInstaller.System.cs"));
        Assert.Contains("/var/lib/kautuka/gateway-data/setup-token", system, StringComparison.Ordinal);
        Assert.DoesNotContain("\"/var/lib/kautuka/setup-token\"", system, StringComparison.Ordinal);

        Assert.Contains("app.MapPost(\"/api/appliance/setup-token\"", endpoint, StringComparison.Ordinal);
        Assert.Contains("if (!HasActionToken(ctx))", endpoint, StringComparison.Ordinal);
        Assert.Contains("CacheControl = \"no-store, no-cache\"", endpoint, StringComparison.Ordinal);
        Assert.Contains("/api/appliance/setup-token", ui, StringComparison.Ordinal);
        Assert.Contains("Copy token", ui, StringComparison.Ordinal);
        Assert.Contains("I copied it and understand it creates my first administrator on the setup page", ui, StringComparison.Ordinal);
        Assert.Contains("state.setupTokenAckResolver", ui, StringComparison.Ordinal);
        Assert.DoesNotContain("appendLog(setupTokenState.token", ui, StringComparison.Ordinal);
    }

    private static string FindProjectRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "src", "Kautuka.slnx")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("Could not locate the repository root from the test base directory.");
    }
}
