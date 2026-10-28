namespace Kautuka.ContractTests;

public sealed class InstalledLinuxSmokeContractTests
{
    [Fact]
    public void SmokeRequiresCoreUnitsActiveNotActivating()
    {
        string projectRoot = FindProjectRoot();
        string source = File.ReadAllText(Path.Combine(projectRoot, "scripts", "installed-linux-smoke.sh"));

        Assert.Contains("assert_unit_active system kautuka-gateway.service", source, StringComparison.Ordinal);
        Assert.Contains("assert_unit_active user kautuka-updateservice.service", source, StringComparison.Ordinal);
        Assert.Contains("activating auto-restart is a crash loop", source, StringComparison.Ordinal);
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
