namespace Kautuka.ContractTests;

public sealed class GatewayToolPolicyAgentToolGuardTests
{
    [Fact]
    public void AgentExecutionPathsDoNotReadSandboxEnabledDirectlyOutsideBindingResolution()
    {
        string projectRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", ".."));

        string[] guardedFiles =
        [
            Path.Combine(projectRoot, "src", "backend", "Kautuka.Gateway", "Infrastructure", "Agents", "McpAgentToolProvider.Connect.cs"),
            Path.Combine(projectRoot, "src", "backend", "Kautuka.Gateway", "Infrastructure", "Agents", "SkillAgentToolProvider.cs"),
            Path.Combine(projectRoot, "src", "backend", "Kautuka.Gateway", "Infrastructure", "Agents", "RunShellAgentToolProvider.cs"),
        ];

        foreach (string filePath in guardedFiles)
        {
            Assert.True(File.Exists(filePath), $"Expected guarded file to exist: {filePath}");
            string source = File.ReadAllText(filePath);

            Assert.DoesNotContain("Sandbox.Enabled", source, StringComparison.Ordinal);
            Assert.DoesNotContain("sandboxEnabled", source, StringComparison.Ordinal);
        }
    }
}
