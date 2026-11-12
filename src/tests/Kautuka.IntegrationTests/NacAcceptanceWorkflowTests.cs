namespace Kautuka.IntegrationTests;

public sealed class NacAcceptanceWorkflowTests
{
    [Fact]
    public void NacAcceptanceWorkflowInstallsPinnedPnpmDependenciesBeforeIntegrationTests()
    {
        string root = FindRepositoryRoot();
        string workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "nac-acceptance.yml"));

        int setupNode = workflow.IndexOf("actions/setup-node@", StringComparison.Ordinal);
        int enablePnpm = workflow.IndexOf("corepack prepare pnpm@11.5.2 --activate", StringComparison.Ordinal);
        int install = workflow.IndexOf("pnpm install --frozen-lockfile", StringComparison.Ordinal);
        int integrationTests = workflow.IndexOf("dotnet test src/tests/Kautuka.IntegrationTests/Kautuka.IntegrationTests.csproj", StringComparison.Ordinal);

        Assert.True(setupNode >= 0, "The acceptance workflow must set up the pinned Node runtime.");
        Assert.True(enablePnpm > setupNode, "The acceptance workflow must enable the repository-pinned pnpm version after Node setup.");
        Assert.True(install > enablePnpm, "The acceptance workflow must install lockfile dependencies before running integration tests.");
        Assert.True(integrationTests > install, "The integration tests must run after pnpm install.");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root from the test assembly.");
    }
}
