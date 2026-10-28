using Kautuka.Gateway.Domain.Sandbox;

using Microsoft.Extensions.AI;

namespace Kautuka.IntegrationTests;

public sealed class RunShellApprovalIntegrationTests
{
    [Fact]
    public async Task BuildToolsAsyncWrapsRunShellWhenSuperExecModeIsAsk()
    {
        var provider = new RunShellAgentToolProvider(new ToolExecutionPolicyService());
        AgentToolContext context = BuildContext(new AgentDefinition
        {
            Id = "agent-ask",
            Name = "Agent Ask",
            ModelId = "phi",
            SuperExecMode = "ask",
            Sandbox = new AgentSandboxDefinition { Enabled = true },
        });

        IReadOnlyList<AIFunction> tools = [.. await provider.BuildToolsAsync(context, TestContext.Current.CancellationToken).ConfigureAwait(true)];

        Assert.Single(tools);
        Assert.Equal("run_shell", tools[0].Name);
        Assert.IsType<ApprovalRequiredAIFunction>(tools[0]);
    }

    [Fact]
    public async Task BuildToolsAsyncLeavesRunShellUnwrappedWhenSuperExecModeIsTrusted()
    {
        var provider = new RunShellAgentToolProvider(new ToolExecutionPolicyService());
        AgentToolContext context = BuildContext(new AgentDefinition
        {
            Id = "agent-trusted",
            Name = "Agent Trusted",
            ModelId = "phi",
            SuperExecMode = "trusted",
            Sandbox = new AgentSandboxDefinition { Enabled = true },
        });

        IReadOnlyList<AIFunction> tools = [.. await provider.BuildToolsAsync(context, TestContext.Current.CancellationToken).ConfigureAwait(true)];

        Assert.Single(tools);
        Assert.Equal("run_shell", tools[0].Name);
        Assert.IsNotType<ApprovalRequiredAIFunction>(tools[0]);
    }

    private static AgentToolContext BuildContext(AgentDefinition agentDefinition)
    {
        var workspaceTool = new FakeWorkspaceTool();
        WorkspaceSessionBinding binding = WorkspaceTooling.BuildAgentSessionBinding(workspaceTool, agentDefinition.Id, "user-1", null);
        return new AgentToolContext(agentDefinition, agentDefinition.Id, "user-1", workspaceTool, binding);
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
