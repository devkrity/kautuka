using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class WorkspaceHostRuntimeAdapterTests
{
    [Theory]
    [InlineData("ABC123", "ABC123", true)]
    [InlineData("abc123", "ABC123", true)]
    [InlineData("DEF456", "ABC123", false)]
    [InlineData(null, "ABC123", false)]
    [InlineData("", "ABC123", false)]
    public void IsSandboxImageSourceCurrentReturnsExpectedResult(string? currentSourceHash, string expectedSourceHash, bool expected)
    {
        bool actual = ManagerRuntimeAdapter.IsSandboxImageSourceCurrent(currentSourceHash, expectedSourceHash);

        Assert.Equal(expected, actual);
    }
}
