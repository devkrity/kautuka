using Kautuka.Manager.Protos;
using Kautuka.Gateway.Domain.Providers;

using System.Net;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.AI;

namespace Kautuka.IntegrationTests;

public sealed class AgentRuntimeNetworkPreflightTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderTestAttemptsConnectionBeforeReadingRecordedDenial(bool httpResponse)
    {
        var manager = new PolicyManager();
        var provider = new DeniedProvider(manager, httpResponse);
        var validation = new ProviderValidationService(null!, new KautukaAiClientFactory([provider], null!), null!,
            NullLogger<ProviderValidationService>.Instance, manager);
        GatewayProviderTestResult result = await validation.ValidateProviderConnectivityAsync("ollama", "ollama",
            new JsonObject { ["base_url"] = "http://192.168.1.20:11434" }, TestContext.Current.CancellationToken);
        Assert.True(provider.Attempted);
        Assert.False(result.Success);
        Assert.Equal(httpResponse ? 0 : 1, manager.Reads);
        if (httpResponse)
        {
            Assert.Null(result.NetworkBlock);
            Assert.Contains("authentication failed", result.Message, StringComparison.Ordinal);
        }
        else Assert.Equal("request-1", result.NetworkBlock?.RequestId);
    }

    private sealed class DeniedProvider(PolicyManager manager, bool httpResponse) : IAiClientProvider
    {
        public string ProviderType => "ollama";
        public bool Attempted { get; private set; }
        public IChatClient? TryCreate(JsonObject settings, string modelName, string? apiKey) => null;
        public Task<IReadOnlyList<DiscoveredModelDefinition>> DiscoverModelsAsync(JsonObject settings, string? apiKey,
            bool throwOnFailure = false, CancellationToken cancellationToken = default)
        {
            Attempted = true;
            manager.Policy.Requests.Add(new NetworkAccessRequestMessage
            {
                Id = "request-1",
                Identity = Gateway(),
                Destination = "192.168.1.20",
                Port = 11434,
                Status = "pending",
                LastSeenAtUtc = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
            });
            throw new HttpRequestException(httpResponse ? "authentication failed" : "connection closed", null,
                httpResponse ? HttpStatusCode.Unauthorized : null);
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrivateProviderProceedsToClientCreationWithoutReadingPolicy(bool legacyAllow)
    {
        var config = new PlatformConfiguration();
        config.Agents.Add(new() { Id = "agent", ModelId = "model" });
        config.Models.Add(new() { Id = "model", ProviderId = "ollama", ModelName = "test-model" });
        config.Providers.Add(new()
        {
            Id = "ollama",
            Type = "unknown-fixture",
            Settings = new(StringComparer.OrdinalIgnoreCase)
            {
                ["base_url"] = "http://192.168.1.20:11434/private-path?secret=never-display",
                ["api_key"] = "never-display-key",
            }
        });
        var manager = new PolicyManager();
        if (legacyAllow) manager.Policy.Rules.Add(new NetworkAccessRuleMessage() { Identity = Gateway(), Destination = "192.168.1.20", Port = 11434, Allowed = true });
        using ServiceProvider services = new ServiceCollection().AddSingleton<IManagerGrpcService>(manager).BuildServiceProvider();
        var factory = new AgentRuntimeFactory(new FixedConfiguration(config), new KautukaAiClientFactory([], null!), null!, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, services, NullLogger<AgentRuntimeFactory>.Instance);
        AgentRuntimeResult result = await factory.BuildAsync("agent", "session", "owner", null, [], false, false, null, null,
            TestContext.Current.CancellationToken);
        Assert.False(result.IsSuccess);
        Assert.Equal(AgentRuntimeError.ClientCreationFailed, result.Error);
        Assert.DoesNotContain("never-display", result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("private-path", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(0, manager.Reads);
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
