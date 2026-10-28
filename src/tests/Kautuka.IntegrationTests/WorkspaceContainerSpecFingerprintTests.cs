using Kautuka.Manager.RuntimeContainer.Sandbox;
using Kautuka.Manager.RuntimeContainer.Sandbox.Models;

namespace Kautuka.IntegrationTests;

public sealed class WorkspaceContainerSpecFingerprintTests
{
    [Fact]
    public void FingerprintIsOrderIndependentAndChangesForBehavioralFields()
    {
        ContainerContext first = CreateContext(
            new Dictionary<string, string> { ["B"] = "2", ["A"] = "1" },
            ["cap-b", "cap-a"]);
        ContainerContext reordered = CreateContext(
            new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" },
            ["cap-a", "cap-b"]);
        ContainerContext changedMemory = first with { DefaultMemoryLimit = "1g" };

        Assert.Equal(
            WorkspaceContainerSpecFingerprint.Compute(first, ["connector-b", "connector-a"]),
            WorkspaceContainerSpecFingerprint.Compute(reordered, ["connector-a", "connector-b"]));
        Assert.NotEqual(
            WorkspaceContainerSpecFingerprint.Compute(first, []),
            WorkspaceContainerSpecFingerprint.Compute(changedMemory, []));
        ContainerContext otherCapabilities = CreateContext(
            new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" },
            ["run_shell"]);
        Assert.Equal(
            WorkspaceContainerSpecFingerprint.Compute(first, []),
            WorkspaceContainerSpecFingerprint.Compute(otherCapabilities, []));
    }

    private static ContainerContext CreateContext(
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<string> capabilities)
        => new(
            "agent", "alpha", "user", Path.Combine(Path.GetTempPath(), "kautuka", "alpha"),
            "/workspace", "image@sha256:digest", 1, "512m", 30, capabilities, environment);
}
