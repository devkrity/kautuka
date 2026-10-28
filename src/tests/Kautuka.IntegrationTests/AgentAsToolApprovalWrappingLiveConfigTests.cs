using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Sandbox;
using Kautuka.Gateway.Infrastructure.Agents.AgentTools;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace Kautuka.IntegrationTests;

public sealed class AgentAsToolApprovalWrappingLiveConfigTests
{
    private const string callerAgentId = "agent-1780826845566";
    private const string rollerAgentId = "agent-1780828776848";
    private const string homeAssistantAgentId = "agent-1780908138083";

    [Fact]
    public async Task MyAssistantAgentToolsMatchApprovalPolicyFromRuntimeStore()
    {
        string runtimeRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".kautuka",
            "runtime");
        string dbPath = Path.Combine(runtimeRoot, "kautuka.db");
        if (!File.Exists(dbPath))
        {
            return;
        }

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kautuka:DataDirectory"] = runtimeRoot,
            })
            .Build();
        var database = new KautukaDatabase(configuration);
        if (!database.IsInitialized())
        {
            return;
        }

        var definitionStore = new SqliteAgentDefinitionStore(configuration);
        var provider = new SqlitePlatformConfigurationProvider(database, definitionStore);
        PlatformConfiguration config = provider.GetCurrent();

        AgentDefinition? caller = config.Agents.FirstOrDefault(agent =>
            string.Equals(agent.Id, callerAgentId, StringComparison.Ordinal));
        if (caller is null)
        {
            return;
        }

        Assert.Contains(rollerAgentId, caller!.ApprovalPolicy!.ApprovalRequiredAgentToolIds);
        Assert.DoesNotContain(homeAssistantAgentId, caller.ApprovalPolicy!.ApprovalRequiredAgentToolIds);

        var policyService = new ToolExecutionPolicyService();
        var orchestrator = new NoOpAgentToolInvocationOrchestrator();
        var toolProvider = new AgentAsToolProvider(
            new FixedPlatformConfigurationProvider(config),
            orchestrator,
            policyService);

        AgentToolContext context = new(
            caller,
            callerAgentId,
            caller.OwnerUserId,
            new NoOpWorkspaceRuntimeTool(),
            new WorkspaceSessionBinding("ws", "agent", callerAgentId, "user-test", null));

        IReadOnlyList<AIFunction> tools = [.. await toolProvider.BuildToolsAsync(context, TestContext.Current.CancellationToken)];
        AIFunction? homeAssistantTool = tools.FirstOrDefault(tool =>
            string.Equals(tool.Name, "invoke_agent_home_assistant", StringComparison.Ordinal));
        AIFunction? rollerTool = tools.FirstOrDefault(tool =>
            string.Equals(tool.Name, "invoke_agent_roller", StringComparison.Ordinal));

        Assert.NotNull(homeAssistantTool);
        Assert.NotNull(rollerTool);
        Assert.IsNotType<ApprovalRequiredAIFunction>(homeAssistantTool);
        Assert.IsType<ApprovalRequiredAIFunction>(rollerTool);
    }

    private sealed class FixedPlatformConfigurationProvider(PlatformConfiguration config) : IPlatformConfigurationProvider
    {
        public PlatformConfiguration GetCurrent() => config;
    }

    private sealed class NoOpAgentToolInvocationOrchestrator : IAgentToolInvocationOrchestrator
    {
        public ValueTask<object?> InvokeAsync(
            string callerAgentId,
            string targetAgentId,
            string message,
            string? userId,
            string callerSessionId,
            AgentToolCallerContextState callerContext,
            CancellationToken ct,
            TimeSpan maxRunDuration = default)
            => ValueTask.FromResult<object?>(null);
    }

    private sealed class NoOpWorkspaceRuntimeTool : IWorkspaceRuntimeTool
    {
        public WorkspaceRuntimeContext DescribeAgentWorkspace(string agentId, string? userId = null)
            => CreateContext(agentId, userId);

        public WorkspaceRuntimeContext RebuildAgentWorkspace(string agentId, string? userId = null)
            => CreateContext(agentId, userId);

        public bool DeleteAgentWorkspace(string agentId) => true;

        public Task<WorkspaceSandboxSession> SpawnAgentSandboxAsync(
            string agentId,
            string? userId = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CreateSession(agentId, userId));

        public Task<WorkspaceCommandResult> RunAgentShellAsync(
            string agentId,
            string command,
            string? userId = null,
            int? timeoutSeconds = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkspaceCommandResult("completed", 0, string.Empty, string.Empty));

        public Task<WorkspaceSandboxSession> InspectAgentSandboxAsync(
            string agentId,
            string? userId = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CreateSession(agentId, userId));

        public Task CloseAgentSandboxAsync(
            string agentId,
            string? userId = null,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        private static WorkspaceRuntimeContext CreateContext(string agentId, string? userId)
            => new(
                "agent",
                agentId,
                userId,
                Path.GetTempPath(),
                "/workspace",
                "/runtime-workspace",
                "kautuka-base:latest",
                120,
                [],
                new Dictionary<string, string>());

        private static WorkspaceSandboxSession CreateSession(string agentId, string? userId)
            => new($"agent-{agentId}", false, "agent", agentId, userId);
    }
}
