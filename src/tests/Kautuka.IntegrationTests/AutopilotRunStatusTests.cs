using Kautuka.Gateway.Domain.Autopilot;

namespace Kautuka.IntegrationTests;

public sealed class AutopilotRunStatusTests
{
    [Theory]
    [InlineData(AutopilotRunStatuses.AwaitingPreRunApproval, AutopilotRunStateCategories.PendingApproval)]
    [InlineData(AutopilotRunStatuses.WaitingForInRunApproval, AutopilotRunStateCategories.PendingApproval)]
    [InlineData(AutopilotRunStatuses.Scheduled, AutopilotRunStateCategories.Executing)]
    [InlineData(AutopilotRunStatuses.Running, AutopilotRunStateCategories.Executing)]
    [InlineData(AutopilotRunStatuses.Completed, AutopilotRunStateCategories.Terminal)]
    [InlineData(AutopilotRunStatuses.TimedOut, AutopilotRunStateCategories.Terminal)]
    [InlineData(AutopilotRunStatuses.Skipped, AutopilotRunStateCategories.Terminal)]
    public void StatusMapsToCanonicalCategory(string status, string expected)
    {
        Assert.Equal(expected, AutopilotRunStatuses.GetStateCategory(status));
        Assert.Equal(
            string.Equals(expected, AutopilotRunStateCategories.Terminal, StringComparison.Ordinal),
            AutopilotRunStatuses.IsTerminal(status));
    }

    [Fact]
    public void UnknownStatusMustBeExplicitlyClassified()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AutopilotRunStatuses.GetStateCategory("future_status"));
    }
}
