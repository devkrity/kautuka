using Kautuka.Gateway.Domain.Sandbox;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Kautuka.IntegrationTests;

public sealed class KautukaAgentSessionWorkspaceTests
{
    [Fact]
    public async Task BindWorkspaceRejectsSwapWithoutStageTransition()
    {
        AgentSession session = await CreateSessionAsync().ConfigureAwait(true);

        KautukaAgentSession.BindWorkspace(
            session,
            workspaceId: "agent:alpha:shared",
            scopeType: "agent",
            scopeId: "alpha",
            userId: null,
            stageTransitionId: null);

        Assert.Throws<InvalidOperationException>(() =>
            KautukaAgentSession.BindWorkspace(
                session,
                workspaceId: "agent:delta:shared",
                scopeType: "agent",
                scopeId: "delta",
                userId: null,
                stageTransitionId: null));
    }

    [Fact]
    public async Task BindWorkspaceAllowsSwapWithNewStageTransition()
    {
        AgentSession session = await CreateSessionAsync().ConfigureAwait(true);

        KautukaAgentSession.BindWorkspace(
            session,
            workspaceId: "agent:alpha:shared",
            scopeType: "agent",
            scopeId: "alpha",
            userId: null,
            stageTransitionId: null);

        KautukaAgentSession.BindWorkspace(
            session,
            workspaceId: "agent:delta:shared",
            scopeType: "agent",
            scopeId: "delta",
            userId: null,
            stageTransitionId: "stage-2");

        Assert.True(session.StateBag.TryGetValue<string>("workspace.id", out string? workspaceId));
        Assert.Equal("agent:delta:shared", workspaceId);
    }

    [Fact]
    public void BuildAgentSessionBindingUsesResolvedWorkspaceScopeId()
    {
        var workspaceTool = new FakeWorkspaceTool();

        WorkspaceSessionBinding binding = WorkspaceTooling.BuildAgentSessionBinding(
            workspaceTool,
            agentId: "agent-177725",
            userId: "user-1",
            stageTransitionId: null);

        Assert.Equal("ash-faster-brain", binding.ScopeId);
        Assert.Equal("agent:ash-faster-brain:user-1", binding.WorkspaceId);
    }

    private static async Task<AgentSession> CreateSessionAsync()
    {
        using var chatClient = new NoopChatClient();
        ChatClientAgent agent = chatClient.AsAIAgent(new ChatClientAgentOptions { Id = "test" });
        return await agent.CreateSessionAsync(CancellationToken.None).ConfigureAwait(true);
    }

    private sealed class NoopChatClient : IChatClient
    {
        private readonly ChatClientMetadata metadata = new("noop", null, null);

        public ChatClientMetadata Metadata => metadata;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse([]));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<ChatResponseUpdate>();

        public void Dispose() { }

        public object? GetService(Type serviceType, object? key = null) => null;
    }

    private sealed class FakeWorkspaceTool : IWorkspaceRuntimeTool
    {
        public WorkspaceRuntimeContext DescribeAgentWorkspace(string agentId, string? userId = null)
            => new(
                ScopeType: "agent",
                ScopeId: "ash-faster-brain",
                UserId: userId,
                HostWorkspacePath: Path.Combine(Path.GetTempPath(), "agents", "ash-faster-brain"),
                ContainerWorkspacePath: "/workspace",
                RuntimeHostWorkspacePath: "/workspace",
                Image: "kautuka-base:latest",
                DefaultTimeoutSeconds: 120,
                BaseCapabilities: [],
                ContainerEnvironment: new Dictionary<string, string>());

        public WorkspaceRuntimeContext RebuildAgentWorkspace(string agentId, string? userId = null) => DescribeAgentWorkspace(agentId, userId);

        public bool DeleteAgentWorkspace(string agentId) => true;

        public Task<WorkspaceSandboxSession> SpawnAgentSandboxAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceCommandResult> RunAgentShellAsync(string agentId, string command, string? userId = null, int? timeoutSeconds = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceSandboxSession> InspectAgentSandboxAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task CloseAgentSandboxAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
