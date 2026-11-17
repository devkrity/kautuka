namespace Kautuka.ContractTests;

internal static class ContractTestRepository
{
    internal static string FindRoot()
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

        throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }
}
