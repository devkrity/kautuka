using Kautuka.NetworkAccess.Acceptance;

namespace Kautuka.ContractTests;

public class AcceptanceReportTests
{
    [Fact]
    public void NegativeControlCannotPassJustBecauseItsFieldsDiffer()
    {
        var result = AcceptanceHarness.Record("different-fields", "blocked=True:card=True", "blocked=True:card=True",
            "card=True:extra=True", "card=True:extra=True");

        Assert.False(result.NcFlipped);
        Assert.StartsWith("FAIL", result.Outcome, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeControlDetectsChangedCommonObservation()
    {
        var result = AcceptanceHarness.Record("changed-field", "blocked=True:card=True", "blocked=True:card=True",
            "blocked=False:extra=True", "blocked=False:extra=True");

        Assert.True(result.NcFlipped);
        Assert.Equal("PASS", result.Outcome);
    }

    [Fact]
    public void NegativeControlWithNoCommonFieldsDoesNotProveAFlip()
    {
        var result = AcceptanceHarness.Record("no-common-fields", "blocked=True", "blocked=True", "other=False", "other=False");
        Assert.False(result.NcFlipped);
    }

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
