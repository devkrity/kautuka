namespace Kautuka.ContractTests;

public sealed class McpbBundleShareContractTests
{
    [Fact]
    public void PersistedBundlesAreGroupReadableOnUnix()
    {
        string projectRoot = FindProjectRoot();
        string source = File.ReadAllText(
            Path.Combine(projectRoot, "src", "backend", "Kautuka.Gateway", "Domain", "Connectors", "IMcpbPackageService.cs"));

        Assert.Contains("ApplyManagerReadableUnixMode", source, StringComparison.Ordinal);
        Assert.Contains("UnixFileMode.GroupRead", source, StringComparison.Ordinal);
        Assert.Contains("UnixFileMode.SetGroup", source, StringComparison.Ordinal);
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
