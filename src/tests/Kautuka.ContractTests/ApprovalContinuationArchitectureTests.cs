using Kautuka.Gateway.Domain.Hitl;

namespace Kautuka.ContractTests;

public sealed class ApprovalContinuationArchitectureTests
{
    [Fact]
    public void ChildContinuationCollectionHasOneFocusedOwnerAndRatchetedSize()
    {
        Type collector = typeof(AaatChildContinuationCollector);
        Assert.True(collector.IsSealed);
        Assert.NotNull(collector.GetMethod(nameof(AaatChildContinuationCollector.CollectAsync)));
        Assert.NotNull(collector.GetMethod(nameof(AaatChildContinuationCollector.ObserveTerminal)));
        Assert.NotNull(collector.GetMethod(nameof(AaatChildContinuationCollector.ToResult)));
        Assert.Equal(
            typeof(AaatChildContinuationResult),
            collector.GetMethod(nameof(AaatChildContinuationCollector.ToResult))!.ReturnType);

        Type orchestrator = typeof(ApprovalContinuationOrchestrator);
        Assert.Null(orchestrator.GetMethod(
            "CollectAgentToolContinuationAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic));
        Assert.DoesNotContain(
            orchestrator.GetNestedTypes(System.Reflection.BindingFlags.NonPublic),
            static type => type.Name.Contains("ContinuationCollector", StringComparison.Ordinal));

        string hitlDirectory = Path.Combine(ResolveGatewayRoot(), "Domain", "Hitl");
        AssertFileLineLimit(hitlDirectory, "ApprovalContinuationOrchestrator.cs", 500);
        AssertFileLineLimit(hitlDirectory, "AaatChildContinuationCollector.cs", 140);
    }

    private static void AssertFileLineLimit(string directory, string fileName, int maximumLines)
    {
        string path = Path.Combine(directory, fileName);
        Assert.True(File.Exists(path), $"Missing approval continuation owner '{fileName}'.");
        int lineCount = File.ReadLines(path).Count();
        Assert.True(
            lineCount <= maximumLines,
            $"Approval continuation owner '{fileName}' has {lineCount} lines; ratcheted maximum is {maximumLines}.");
    }

    private static string ResolveGatewayRoot()
    {
        foreach (string origin in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (DirectoryInfo? directory = new(origin); directory is not null; directory = directory.Parent)
            {
                string candidate = Path.Combine(directory.FullName, "src", "backend", "Kautuka.Gateway");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Kautuka.Gateway source directory.");
    }
}
