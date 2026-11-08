using Kautuka.Manager.Protos;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class AgentRuntimeNetworkPreflightTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrivateProviderIsBlockedBeforeAnyClientInitialization(bool legacyAllow)
    {
        var config = new PlatformConfiguration();
        config.Agents.Add(new() { Id = "agent", ModelId = "model" });
        config.Models.Add(new() { Id = "model", ProviderId = "ollama", ModelName = "test-model" });
        config.Providers.Add(new()
        {
            Id = "ollama",
            Type = "ollama",
            Settings = new(StringComparer.OrdinalIgnoreCase)
            {
                ["base_url"] = "http://192.168.1.20:11434/private-path?secret=never-display",
                ["api_key"] = "never-display-key",
            }
        });
        var manager = new PolicyManager();
        if (legacyAllow) manager.Policy.Rules.Add(new NetworkAccessRuleMessage() { Identity = Gateway(), Destination = "192.168.1.20", Port = 11434, Allowed = true });
        using ServiceProvider services = new ServiceCollection().AddSingleton<IManagerGrpcService>(manager).BuildServiceProvider();
        // A client factory (or any later runtime dependency) must never be used
        // when the accepted policy already proves this target is blocked.
        var factory = new AgentRuntimeFactory(new FixedConfiguration(config), null!, null!, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, services, NullLogger<AgentRuntimeFactory>.Instance);
        AgentRuntimeResult result = await factory.BuildAsync("agent", "session", "owner", null, [], false, false, null, null,
            TestContext.Current.CancellationToken);
        Assert.False(result.IsSuccess);
        Assert.Equal(AgentRuntimeError.NetworkAccessBlocked, result.Error);
        Assert.Contains("192.168.1.20:11434", result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("never-display", result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("private-path", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(1, manager.Reads);
    }

    private static NetworkAccessIdentityMessage Gateway() => new() { ScopeType = "core", UserId = "system", AgentId = "gateway", Role = "core", ComponentId = "gateway" };
    private sealed class FixedConfiguration(PlatformConfiguration configuration) : IPlatformConfigurationProvider
    {
        public PlatformConfiguration GetCurrent() => configuration;
    }
    private sealed class PolicyManager : IManagerGrpcService
    {
        public int Reads { get; private set; }
        public NetworkAccessPolicyResponse Policy { get; } = CreatePolicy();
        private static NetworkAccessPolicyResponse CreatePolicy()
        {
            var policy = new NetworkAccessPolicyResponse { Enabled = true, Revision = 2, ActiveRevision = 2, ActiveVersion = "accepted", ActivationState = "active" };
            policy.Sources.Add(Gateway());
            return policy;
        }
        public Task<NetworkAccessPolicyResponse> GetNetworkAccessPolicyAsync(NetworkAccessReadRequest request, CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult(Policy);
        }
        public Task<WorkspaceBoolResponse> PingAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ManagerStatusResponse> GetHostStatusAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ManagerRuntimeContainersResponse> ListRuntimeContainersAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkspaceContextResponse> DescribeWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkspaceContextResponse> RebuildWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkspaceDeleteResponse> DeleteWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkspaceSandboxResponse> SpawnSandboxAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkspaceSandboxResponse> InspectSandboxAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkspaceExecutionResponse> ExecAsync(WorkspaceExecRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkspaceBoolResponse> StopSandboxAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkspaceBoolResponse> PrimeWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkspaceMcpProbeResponse> ProbeMcpContainerAsync(WorkspaceMcpContainerProbeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkspaceMcpContainerListToolsResponse> ListMcpContainerToolsAsync(WorkspaceMcpContainerCallRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkspaceMcpContainerCallResponse> CallMcpContainerToolAsync(WorkspaceMcpContainerCallRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DesiredStateUpdateResponse> UpdateDesiredStateAsync(ManagerDesiredStateUpdateRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
