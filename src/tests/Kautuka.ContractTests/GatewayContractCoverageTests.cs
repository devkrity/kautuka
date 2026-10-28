namespace Kautuka.ContractTests;

public sealed class GatewayContractCoverageTests
{
    [Fact]
    public void GatewayDefinesAllUiCriticalApiRoutes()
    {
        string source = ReadGatewayEndpointSources();

        string[] requiredRoutes =
        [
            "/api/auth/login",
            "/api/auth/refresh",
            "/api/auth/me",
            "/api/auth/logout",
            "/api/sessions",
            "/api/memories/agents/{agentId}",
            "/api/narad/v1/capabilities",
            "/api/narad/v1/sessions/{sessionId}/stream",
            "/api/narad/v1/sessions/{sessionId}/watch",
            "/api/narad/v1/workspace/watch",
            "/api/narad/v1/client/attach",
            "/api/narad/v1/client/heartbeat",
            "/api/narad/v1/agents/{agentId}/sessions/{sessionId}/messages",
            "/api/narad/v1/commands/resolve-interrupt",
            "/api/approvals/pending",
            "/api/approvals/session/{sessionId}",
            "/api/agents/{agentId}/stats",
            "/api/connectors/{serverId}/stats",
            "/api/skills/{skillId}/stats",
            "/api/settings/me/profile",
            "/api/settings/me/password",
            "/api/settings/me/channels/{channelType}",
            "/api/settings/me/channels/{channelType}/hitl-test",
            "/api/settings/system",
            "/api/settings/provider-types",
            "/api/settings/providers",
            "/api/internal/shutdown",
            "/api/settings/models",
            "/api/settings/agents",
            "/api/settings/agents/{agentId}/tool-policy",
            "/api/settings/agents/{agentId}/revisions",
            "/api/settings/agents/{agentId}/revisions/{revision}/diff",
            "/api/settings/users",
            "/api/settings/users/{userId}/channels/{channelType}",
            "/api/settings/users/{userId}/channels/{channelType}/hitl-test",
            "/api/runtime/diagnostics/manager/containers",
            "/api/runtime/diagnostics/pkgs-proxy/stats",
            "/api/workflows",
            "/api/workflows/{workflowId}",
            "/api/workflows/{workflowId}/instances",
            "/api/workflows/{workflowId}/instances/{instanceId}",
            "/api/workflows/{workflowId}/instances/{instanceId}/stop",
            "/api/narad/v1/workflows/{workflowId}/instances/{instanceId}/stream",
        ];

        foreach (string route in requiredRoutes)
        {
            Assert.Contains(route, source, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("{requestId:regex", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\"/api/approvals/stats\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/approvals/{requestId}/resolve", source, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/approvals/{id}/resolve", source, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/workflows/{workflowId}/instances/{instanceId}/continue", source, StringComparison.Ordinal);
    }

    [Fact]
    public void GatewayApprovalMappingsDoNotExposePatchResolve()
    {
        string approvalMappings = File.ReadAllText(GetGatewayApprovalMappingsPath());
        Assert.DoesNotContain("MapPatch", approvalMappings, StringComparison.Ordinal);
        Assert.DoesNotContain("/resolve", approvalMappings, StringComparison.Ordinal);
    }

    [Fact]
    public void GatewayEnforcesExplicitAuthenticatedAndAdminChecks()
    {
        string source = ReadGatewayEndpointSources();
        string authGuardSource = File.ReadAllText(GetGatewayAuthGuardPath());

        Assert.Contains("RequireAuthenticated(context", source, StringComparison.Ordinal);
        Assert.Contains("RequireAdmin(context", source, StringComparison.Ordinal);
        Assert.Contains("IResult? RequireAuthenticated(HttpContext context, out GatewayUser? user)", authGuardSource, StringComparison.Ordinal);
        Assert.Contains("IResult? RequireAdmin(HttpContext context, out GatewayUser? user)", authGuardSource, StringComparison.Ordinal);
    }

    private static string ReadGatewayEndpointSources()
    {
        string endpointDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "src", "backend", "Kautuka.Gateway", "Endpoints"));
        return string.Join(Environment.NewLine, Directory.EnumerateFiles(endpointDirectory, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText));
    }

    private static string GetGatewayApprovalMappingsPath()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "src", "backend", "Kautuka.Gateway", "Endpoints", "Runtime", "GatewayApprovalEndpointMappings.cs"));
    }

    private static string GetGatewayAuthGuardPath()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "src", "backend", "Kautuka.Gateway", "Domain", "Auth", "GatewayAuthGuard.cs"));
    }
}
