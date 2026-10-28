namespace Kautuka.ContractTests;

public sealed class BuiltInSkillBootstrapContractTests
{
    [Fact]
    public void BuiltInSkillBootstrapDoesNotCrashTheHostWhenPersistedPackageIsLocked()
    {
        string projectRoot = FindProjectRoot();
        string source = File.ReadAllText(
            Path.Combine(projectRoot, "src", "backend", "Kautuka.Gateway", "Domain", "Skills", "BuiltInSkillPackageBootstrapService.cs"));

        Assert.Contains("ValidateImportOutcomeAsync", source, StringComparison.Ordinal);
        Assert.Contains("existing.PackageHash", source, StringComparison.Ordinal);
        Assert.Contains("ex is InvalidOperationException or IOException or UnauthorizedAccessException", source, StringComparison.Ordinal);
        Assert.Contains("if (ex is UnauthorizedAccessException)", source, StringComparison.Ordinal);
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
