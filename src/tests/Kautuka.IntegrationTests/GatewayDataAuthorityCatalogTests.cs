namespace Kautuka.IntegrationTests;

public sealed class GatewayDataAuthorityCatalogTests
{
    [Fact]
    public void CatalogDeclaresOneCompleteAuthorityContractForEveryRequiredDataClass()
    {
        string[] required = ["providers", "models", "agents", "skills", "users", "sessions", "runs", "memory", "agent_tool_surface", "approvals", "approval_bypass", "run_stop", "configuration"];

        Assert.Equal(required.Order(), GatewayDataAuthorityCatalog.Entries.Select(static entry => entry.DataClass).Order());
        Assert.Equal(required.Length, GatewayDataAuthorityCatalog.Entries.Select(static entry => entry.DataClass).Distinct(StringComparer.Ordinal).Count());
        Assert.All(GatewayDataAuthorityCatalog.Entries, static entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Authority));
            Assert.False(string.IsNullOrWhiteSpace(entry.Projection));
            Assert.False(string.IsNullOrWhiteSpace(entry.Cache));
            Assert.False(string.IsNullOrWhiteSpace(entry.CommitPoint));
            Assert.False(string.IsNullOrWhiteSpace(entry.RepairStrategy));
        });

        GatewayDataAuthority sessions = GatewayDataAuthorityCatalog.Get("sessions");
        Assert.Contains("runtime_sessions", sessions.Authority, StringComparison.Ordinal);
        Assert.Contains("transaction commit", sessions.CommitPoint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reload", sessions.RepairStrategy, StringComparison.OrdinalIgnoreCase);

        GatewayDataAuthority approvals = GatewayDataAuthorityCatalog.Get("approvals");
        Assert.Equal("kautuka.db/tool_approval_requests", approvals.Authority);

        GatewayDataAuthority configuration = GatewayDataAuthorityCatalog.Get("configuration");
        Assert.Contains("config_snapshots", configuration.Authority, StringComparison.Ordinal);
    }
}
