using System.Collections.Concurrent;
using System.Text.Json;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Infrastructure.Agents.AgentTools;

using Microsoft.Extensions.AI;

namespace Kautuka.IntegrationTests;

public sealed class AgentAsToolProviderInvocationBoundaryTests
{
    [Fact]
    public async Task FunctionInvokingChatClientBindsConcurrentSameToolCallsToExactParentCalls()
    {
        const string callerSessionId = "session-aaat-boundary";
        const string toolName = "invoke_agent_target";
        var orchestrator = new SuspendingInvocationOrchestrator();
        PlatformConfiguration configuration = CreateConfiguration();
        var provider = new AgentAsToolProvider(
            new FixedPlatformConfigurationProvider(configuration),
            orchestrator,
            new ToolExecutionPolicyService());
        AgentDefinition caller = configuration.Agents.Single(agent => agent.Id == "agent-caller");
        var toolContext = new AgentToolContext(
            caller,
            caller.Id,
            "user-1",
            WorkspaceTool: null!,
            WorkspaceBinding: null!,
            SessionId: callerSessionId);
        AIFunction tool = Assert.Single(await provider.BuildToolsAsync(toolContext, TestContext.Current.CancellationToken));

        IDisposable firstScope = PushCallerScope("run-boundary-parent-1", "call-parent-1", callerSessionId, toolName);
        IDisposable secondScope = PushCallerScope("run-boundary-parent-2", "call-parent-2", callerSessionId, toolName);
        var modelClient = new ConcurrentFunctionCallChatClient(toolName);
        using var functionClient = new FunctionInvokingChatClient(modelClient)
        {
            AllowConcurrentInvocation = true,
        };

        try
        {
            _ = await functionClient.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "invoke both")],
                new ChatOptions { Tools = [tool] },
                TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
        finally
        {
            secondScope.Dispose();
            firstScope.Dispose();
        }

        Assert.Equal("call-parent-1", orchestrator.CallerContextsByMessage["first"].CallerToolCallId);
        Assert.Equal("run-boundary-parent-1", orchestrator.CallerContextsByMessage["first"].CallerRunId);
        Assert.Equal("call-parent-2", orchestrator.CallerContextsByMessage["second"].CallerToolCallId);
        Assert.Equal("run-boundary-parent-2", orchestrator.CallerContextsByMessage["second"].CallerRunId);
        Assert.Equal(["call-parent-1", "call-parent-2"], modelClient.ResultCallIds.Order(StringComparer.Ordinal));
        Assert.Equal(["call-parent-1", "call-parent-2"], modelClient.PendingApprovalResultCallIds.Order(StringComparer.Ordinal));

        AssertPendingApproval("run-boundary-parent-1", "call-parent-1", "approval-call-parent-1");
        AssertPendingApproval("run-boundary-parent-2", "call-parent-2", "approval-call-parent-2");
    }

    [Fact]
    public async Task BuildToolsAsyncOmitsAnotherUsersExposedAgent()
    {
        PlatformConfiguration configuration = new()
        {
            Agents =
            {
                new AgentDefinition
                {
                    Id = "agent-caller",
                    Name = "Caller",
                    OwnerUserId = "user-a",
                    ApprovalPolicy = new ApprovalPolicy
                    {
                        AgentToolIds = ["agent-own", "agent-foreign", "agent-shared"],
                    },
                },
                new AgentDefinition
                {
                    Id = "agent-own",
                    Name = "Own",
                    OwnerUserId = "user-a",
                    ToolExposure = new AgentToolExposureDefinition { ExposedAsTool = true },
                },
                new AgentDefinition
                {
                    Id = "agent-foreign",
                    Name = "Foreign",
                    OwnerUserId = "user-b",
                    ToolExposure = new AgentToolExposureDefinition { ExposedAsTool = true },
                },
                new AgentDefinition
                {
                    Id = "agent-shared",
                    Name = "Shared",
                    Scope = GatewayOwnershipScopes.System,
                    ToolExposure = new AgentToolExposureDefinition { ExposedAsTool = true },
                },
            },
        };
        var provider = new AgentAsToolProvider(
            new FixedPlatformConfigurationProvider(configuration),
            new SuspendingInvocationOrchestrator(),
            new ToolExecutionPolicyService());
        AgentDefinition caller = configuration.Agents.Single(agent => agent.Id == "agent-caller");
        IReadOnlyList<AIFunction> tools = [.. await provider.BuildToolsAsync(
            new AgentToolContext(caller, caller.Id, "user-a", WorkspaceTool: null!, WorkspaceBinding: null!),
            TestContext.Current.CancellationToken)];

        Assert.Equal(
            ["invoke_agent_own", "invoke_agent_shared"],
            tools.Select(tool => tool.Name).OrderBy(name => name, StringComparer.Ordinal));
    }

    private static PlatformConfiguration CreateConfiguration()
        => new()
        {
            Agents =
            {
                new AgentDefinition
                {
                    Id = "agent-caller",
                    Name = "Caller",
                    OwnerUserId = "user-1",
                    ApprovalPolicy = new ApprovalPolicy { AgentToolIds = ["agent-target"] },
                },
                new AgentDefinition
                {
                    Id = "agent-target",
                    Name = "Target",
                    OwnerUserId = "user-1",
                    ToolExposure = new AgentToolExposureDefinition
                    {
                        ExposedAsTool = true,
                        InvocationTimeoutSeconds = 30,
                    },
                },
            },
        };

    private static IDisposable PushCallerScope(string runId, string callId, string sessionId, string toolName)
        => AgentToolCallerContext.Push(
            runId,
            $"continuation-{runId}",
            callId,
            toolName,
            sessionId,
            "agent-caller",
            "user-1",
            rootRunId: runId);

    private static void AssertPendingApproval(string runId, string callId, string expectedRequestId)
    {
        Assert.True(AgentToolCallerContext.TryTakePendingApproval(runId, callId, out AgentToolApprovalSuspension? suspension));
        Assert.NotNull(suspension);
        Assert.Equal(expectedRequestId, suspension.ApprovalRequest.RequestId);
        Assert.Equal(runId, suspension.CallerContext.CallerRunId);
        Assert.Equal(callId, suspension.CallerContext.CallerToolCallId);
    }

    private sealed class FixedPlatformConfigurationProvider(PlatformConfiguration configuration) : IPlatformConfigurationProvider
    {
        public PlatformConfiguration GetCurrent() => configuration;
    }

    private sealed class SuspendingInvocationOrchestrator : IAgentToolInvocationOrchestrator
    {
        private readonly TaskCompletionSource allCallsEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int callCount;

        public ConcurrentDictionary<string, AgentToolCallerContextState> CallerContextsByMessage { get; } = new(StringComparer.Ordinal);

        public async ValueTask<object?> InvokeAsync(
            string callerAgentId,
            string targetAgentId,
            string message,
            string? userId,
            string callerSessionId,
            AgentToolCallerContextState callerContext,
            CancellationToken ct,
            TimeSpan maxRunDuration = default)
        {
            CallerContextsByMessage[message] = callerContext;
            if (Interlocked.Increment(ref callCount) == 2)
            {
                allCallsEntered.TrySetResult();
            }

            await allCallsEntered.Task.WaitAsync(ct).ConfigureAwait(false);
            AgentToolCallerContext.RegisterPendingApproval(
                new GatewayChatApprovalRequest(
                    $"approval-{callerContext.CallerToolCallId}",
                    $"child-{callerContext.CallerToolCallId}",
                    "run_shell",
                    "{}",
                    $"child-run-{callerContext.CallerToolCallId}",
                    $"checkpoint-{callerContext.CallerToolCallId}"),
                callerContext);
            return JsonSerializer.SerializeToElement(new { result = "pending_approval" });
        }
    }

    private sealed class ConcurrentFunctionCallChatClient(string toolName) : IChatClient
    {
        private int responseCount;

        public ChatClientMetadata Metadata { get; } = new("aaat-boundary-test", null, null);

        public IReadOnlyList<string> ResultCallIds { get; private set; } = [];

        public IReadOnlyList<string> PendingApprovalResultCallIds { get; private set; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref responseCount) == 1)
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(
                    ChatRole.Assistant,
                    [
                        new FunctionCallContent("call-parent-1", toolName, new Dictionary<string, object?> { ["message"] = "first" }),
                        new FunctionCallContent("call-parent-2", toolName, new Dictionary<string, object?> { ["message"] = "second" }),
                    ])));
            }

            FunctionResultContent[] results = [
                .. messages.SelectMany(static message => message.Contents).OfType<FunctionResultContent>(),
            ];
            ResultCallIds = [.. results.Select(static result => result.CallId)];
            PendingApprovalResultCallIds = [
                .. results
                    .Where(static result => result.Result is JsonElement element
                        && element.ValueKind == JsonValueKind.Object
                        && element.TryGetProperty("result", out JsonElement value)
                        && string.Equals(value.GetString(), "pending_approval", StringComparison.Ordinal))
                    .Select(static result => result.CallId),
            ];
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "complete")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<ChatResponseUpdate>();

        public object? GetService(Type serviceType, object? key = null) => null;

        public void Dispose() { }
    }
}
