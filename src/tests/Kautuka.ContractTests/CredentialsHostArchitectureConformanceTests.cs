namespace Kautuka.ContractTests;

public sealed class CredentialsHostArchitectureConformanceTests
{
    private static readonly string credentialsHostRoot = ResolveCredentialsHostRoot();

    [Fact]
    public void NativeAotCredentialPersistenceUsesOnlyExplicitSqliteCommands()
    {
        string project = File.ReadAllText(Path.Combine(credentialsHostRoot, "Kautuka.CredentialsHost.csproj"));
        Assert.DoesNotContain("Dapper", project, StringComparison.OrdinalIgnoreCase);

        foreach (string path in Directory.EnumerateFiles(credentialsHostRoot, "*.cs", SearchOption.AllDirectories)
                     .Where(static path => !path.Contains(
                         $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                         StringComparison.Ordinal)))
        {
            string source = File.ReadAllText(path);
            Assert.DoesNotContain("using Dapper", source, StringComparison.Ordinal);
            Assert.DoesNotContain("CommandDefinition", source, StringComparison.Ordinal);
            Assert.DoesNotContain("[module: DapperAot]", source, StringComparison.Ordinal);
        }

        string auditStore = File.ReadAllText(Path.Combine(credentialsHostRoot, "Database", "CredentialAuditStore.cs"));
        string repository = File.ReadAllText(Path.Combine(credentialsHostRoot, "Database", "CredentialRepository.cs"));
        Assert.Contains("SqliteCommand", auditStore, StringComparison.Ordinal);
        Assert.Contains("SqliteDataReader", repository, StringComparison.Ordinal);
    }

    private static string ResolveCredentialsHostRoot()
    {
        foreach (string origin in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (DirectoryInfo? directory = new(origin); directory is not null; directory = directory.Parent)
            {
                string candidate = Path.Combine(directory.FullName, "src", "backend", "Kautuka.CredentialsHost");
                if (Directory.Exists(candidate)) return candidate;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Kautuka.CredentialsHost source directory.");
    }
}
