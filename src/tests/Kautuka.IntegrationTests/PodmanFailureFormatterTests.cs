using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class PodmanFailureFormatterTests
{
    [Fact]
    public void FormatFailureIncludesExitCodeAndStreams()
    {
        PodmanCommandResult result = new(
            125,
            "container stdout\nsecond line",
            "container stderr\nmore detail");

        string message = PodmanFailureFormatter.FormatFailure("Pull sandbox image 'demo'", result);

        Assert.Equal(
            "Pull sandbox image 'demo' failed (exit code 125). | stderr: container stderr more detail | stdout: container stdout second line",
            message);
    }

    [Fact]
    public void FormatDetailsFallsBackToTimeoutWhenNoOutputExists()
    {
        string details = PodmanFailureFormatter.FormatDetails(124, string.Empty, string.Empty);

        Assert.Equal("exit code 124 | timed out", details);
    }

    [Fact]
    public void FormatFailurePreservesTerminalCauseAfterVerbosePodmanBuildContext()
    {
        string verboseBuildContext = $"Error: committing container for step {{Env:[{new string('x', 1_200)}]}}: writing blob";
        const string terminalCause = "no space left on device";

        string message = PodmanFailureFormatter.FormatFailure(
            "Build MCPB runtime image",
            125,
            string.Empty,
            $"{verboseBuildContext}: {terminalCause}");

        Assert.Contains("Error: committing container", message, StringComparison.Ordinal);
        Assert.Contains(terminalCause, message, StringComparison.Ordinal);
        Assert.True(message.Length < 900);
    }
}
