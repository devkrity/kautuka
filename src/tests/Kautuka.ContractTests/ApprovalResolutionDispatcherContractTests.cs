namespace Kautuka.ContractTests;

public sealed class ApprovalResolutionDispatcherContractTests
{
    [Fact]
    public void DispatcherDoesNotStopTheHostWhenLeaseThrowsObjectDisposed()
    {
        string projectRoot = FindProjectRoot();
        string source = File.ReadAllText(
            Path.Combine(projectRoot, "src", "backend", "Kautuka.Gateway", "Domain", "Hitl", "ApprovalResolutionEventQueue.cs"));

        Assert.Contains("catch (ObjectDisposedException ex)", source, StringComparison.Ordinal);
        Assert.Contains("LeaseAttemptFailed", source, StringComparison.Ordinal);
        Assert.Contains("DispatchCycleFailed", source, StringComparison.Ordinal);
        Assert.Contains("BackgroundServiceExceptionBehavior=StopHost", source, StringComparison.Ordinal);
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
