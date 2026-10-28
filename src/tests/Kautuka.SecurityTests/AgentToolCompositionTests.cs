using Kautuka.Gateway.Domain.Sandbox;
using Kautuka.Gateway.Infrastructure.Agents;
using Kautuka.Gateway.Infrastructure.Configuration;

using Microsoft.Extensions.AI;

namespace Kautuka.SecurityTests;

public sealed class AgentToolCompositionTests
{
    [Fact]
    public async Task RunShellProviderReturnsNoToolsWhenCommandExecutionDisabled()
    {
        var provider = new RunShellAgentToolProvider(new ToolExecutionPolicyService());
        AgentToolContext context = BuildContext(new AgentDefinition
        {
            Id = "agent-1",
            Name = "Agent 1",
            ModelId = "phi",
            SuperExecMode = "disabled",
        });

        IReadOnlyList<AIFunction> tools = [.. await provider.BuildToolsAsync(context, TestContext.Current.CancellationToken).ConfigureAwait(true)];

        Assert.Empty(tools);
    }

    [Fact]
    public async Task RunShellProviderReturnsToolWhenCommandExecutionEnabled()
    {
        var provider = new RunShellAgentToolProvider(new ToolExecutionPolicyService());
        AgentToolContext context = BuildContext(new AgentDefinition
        {
            Id = "agent-1",
            Name = "Agent 1",
            ModelId = "phi",
            SuperExecMode = "trusted",
            Sandbox = new AgentSandboxDefinition { Enabled = true },
        });

        IReadOnlyList<AIFunction> tools = [.. await provider.BuildToolsAsync(context, TestContext.Current.CancellationToken).ConfigureAwait(true)];

        Assert.Single(tools);
        Assert.Equal("run_shell", tools[0].Name);
    }

    [Fact]
    public async Task RunShellProviderWrapsToolWithApprovalWhenSuperExecAskModeEnabled()
    {
        var provider = new RunShellAgentToolProvider(new ToolExecutionPolicyService());
        AgentToolContext context = BuildContext(new AgentDefinition
        {
            Id = "agent-1",
            Name = "Agent 1",
            ModelId = "phi",
            SuperExecMode = "ask",
            Sandbox = new AgentSandboxDefinition { Enabled = true },
        });

        IReadOnlyList<AIFunction> tools = [.. await provider.BuildToolsAsync(context, TestContext.Current.CancellationToken).ConfigureAwait(true)];

        Assert.Single(tools);
        Assert.IsType<ApprovalRequiredAIFunction>(tools[0]);
        Assert.Equal("run_shell", tools[0].Name);
    }

    [Fact]
    public async Task AgentToolCatalogAggregatesMultipleProviders()
    {
        AgentToolContext context = BuildContext(new AgentDefinition
        {
            Id = "agent-1",
            Name = "Agent 1",
            ModelId = "phi",
        });

        var catalog = new AgentToolCatalog(
            [
                new FakeToolProvider("first_tool"),
                new FakeToolProvider("second_tool"),
            ],
            new MemoryAgentToolSurfaceStore(),
            new StaticPlatformConfigurationProvider(new PlatformConfiguration()));

        IReadOnlyList<AIFunction> tools = await catalog.BuildToolsAsync(context, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(2, tools.Count);
        Assert.Contains(tools, tool => tool.Name == "first_tool");
        Assert.Contains(tools, tool => tool.Name == "second_tool");
    }

    private static AgentToolContext BuildContext(AgentDefinition agentDefinition)
    {
        var workspaceTool = new FakeWorkspaceTool();
        WorkspaceSessionBinding binding = WorkspaceTooling.BuildAgentSessionBinding(workspaceTool, agentDefinition.Id, "user-1", null);
        return new AgentToolContext(agentDefinition, agentDefinition.Id, "user-1", workspaceTool, binding);
    }

    private sealed class StaticPlatformConfigurationProvider(PlatformConfiguration config) : IPlatformConfigurationProvider
    {
        public PlatformConfiguration GetCurrent() => config;
    }

    private sealed class MemoryAgentToolSurfaceStore : IAgentToolSurfaceStore
    {
        private readonly Dictionary<string, AgentToolSurfaceSnapshot> snapshots = new(StringComparer.OrdinalIgnoreCase);

        public AgentToolSurfaceSnapshot? TryGet(string agentId, string fingerprint)
        {
            if (!snapshots.TryGetValue(agentId, out AgentToolSurfaceSnapshot? snapshot))
            {
                return null;
            }

            return string.Equals(snapshot.Fingerprint, fingerprint, StringComparison.Ordinal)
                ? snapshot
                : null;
        }

        public void Save(string agentId, string fingerprint, AgentToolSurfaceCatalogue catalogue)
        {
            snapshots[agentId] = new AgentToolSurfaceSnapshot(
                fingerprint,
                catalogue,
                DateTimeOffset.UtcNow.ToString("O"));
        }
    }

    private sealed class FakeToolProvider(string toolName) : IAgentToolSurfaceContributor
    {
        public string SurfaceSource => "other";

        public ValueTask<IEnumerable<AIFunction>> BuildToolsAsync(AgentToolContext context, CancellationToken ct)
        {
            AIFunction tool = AIFunctionFactory.Create(
                () => "ok",
                toolName,
                $"Fake tool {toolName}",
                serializerOptions: null);
            return ValueTask.FromResult<IEnumerable<AIFunction>>([tool]);
        }
    }

    private sealed class FakeWorkspaceTool : IWorkspaceRuntimeTool
    {
        public WorkspaceRuntimeContext DescribeAgentWorkspace(string agentId, string? userId = null) =>
            new(
                ScopeType: "agent",
                ScopeId: agentId,
                UserId: userId,
                HostWorkspacePath: Path.Combine("/tmp", "agents", agentId),
                ContainerWorkspacePath: $"/home/{agentId}",
                RuntimeHostWorkspacePath: $"/tmp/agents/{agentId}",
                Image: "kautuka-base:latest",
                DefaultTimeoutSeconds: 120,
                BaseCapabilities: ["spawn_sandbox", "run_shell"],
                ContainerEnvironment: new Dictionary<string, string>());

        public WorkspaceRuntimeContext RebuildAgentWorkspace(string agentId, string? userId = null) => DescribeAgentWorkspace(agentId, userId);

        public bool DeleteAgentWorkspace(string agentId) => true;

        public Task<WorkspaceSandboxSession> SpawnAgentSandboxAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<WorkspaceCommandResult> RunAgentShellAsync(string agentId, string command, string? userId = null, int? timeoutSeconds = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<WorkspaceSandboxSession> InspectAgentSandboxAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task CloseAgentSandboxAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
