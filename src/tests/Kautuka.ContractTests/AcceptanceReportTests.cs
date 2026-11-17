using Kautuka.NetworkAccess.Acceptance;

namespace Kautuka.ContractTests;

public class AcceptanceReportTests
{
    [Fact]
    public void ProductionResultsIncludePackageProxyFailures()
    {
        var proxy = new AcceptanceObservation("proxy", "bad", "good", "bad", "bad", false, "FAIL", DateTimeOffset.UtcNow);
        var other = new AcceptanceObservation("other", "good", "good", "bad", "bad", true, "PASS", DateTimeOffset.UtcNow);

        var results = AcceptanceHarness.ProductionObservations([proxy], other);

        Assert.Contains(results, observation => observation.TestId == "proxy" && observation.Outcome == "FAIL");
        Assert.Contains(results, observation => observation.TestId == "other");
    }
}
