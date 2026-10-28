using System.Text.Json;

using Kautuka.Gateway.Domain.Execution;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class AgentChatServiceApprovalTurnMessageTests
{
    [Fact]
    public void ContinuationRunIdSeparatesSequentialDeliveryMessageLifecycles()
    {
        var first = new ToolApprovalResponseInfo(
            "approval-outer",
            "call-parent",
            "invoke_agent_roller",
            "{}",
            Approved: true,
            Reason: null,
            RunId: "run-parent",
            ContinuationRunId: "continuation-parent");
        ToolApprovalResponseInfo second = first with
        {
            RequestId = "approval-child",
            ContinuationRunId = "continuation-child",
            ResultJson = "{\"result\":\"42\"}",
        };

        string firstDelivery = AgentChatService.ResolveDeliveryAttemptId(
            first,
            new AgentContinuationRequest(
                Enabled: true,
                ResumeToken: null,
                ContinuationRunId: "continuation-parent",
                CheckpointId: null,
                CheckpointIndex: null,
                SessionStateJson: null,
                EffectiveBehaviorJson: null,
                BoundaryType: null,
                ArtifactManifest: null,
                PriorToolCalls: null),
            "run-parent");
        string secondDelivery = AgentChatService.ResolveDeliveryAttemptId(
            second,
            new AgentContinuationRequest(
                Enabled: true,
                ResumeToken: null,
                ContinuationRunId: "continuation-child",
                CheckpointId: null,
                CheckpointIndex: null,
                SessionStateJson: null,
                EffectiveBehaviorJson: null,
                BoundaryType: null,
                ArtifactManifest: null,
                PriorToolCalls: null),
            "run-child");

        Assert.Equal("continuation-parent", firstDelivery);
        Assert.Equal("continuation-child", secondDelivery);
        Assert.NotEqual(firstDelivery, secondDelivery);
    }

    [Fact]
    public void ResolveDeliveryAttemptIdFallsBackToApprovalContinuationRunId()
    {
        var approval = new ToolApprovalResponseInfo(
            "approval-1",
            "tool-call-1",
            "invoke_agent_roller",
            "{}",
            Approved: true,
            Reason: null,
            RunId: "run-parent",
            ContinuationRunId: "continuation-from-approval");

        string deliveryAttemptId = AgentChatService.ResolveDeliveryAttemptId(
            approval,
            continuationRequest: null,
            runId: "run-parent");

        Assert.Equal("continuation-from-approval", deliveryAttemptId);
    }

    [Fact]
    public void BuildApprovalTurnMessagesForAaatResultWithoutExistingCallPairsFunctionCall()
    {
        var response = new ToolApprovalResponseInfo(
            RequestId: "approval-1",
            ToolCallId: "call-agent-tool-1",
            ToolName: "invoke_agent_roller",
            ArgumentsJson: null,
            Approved: false,
            Reason: "denied",
            RunId: "run-1",
            ResultJson: "{\"result\":\"Tool 'invoke_agent_roller' was denied by the user.\",\"outcome\":\"denied\"}");

        IList<ChatMessage> messages = AgentChatService.BuildApprovalTurnMessages(
            "session-1",
            "user-1",
            "roll a d100, then set the patio lights",
            response,
            NullLogger.Instance);

        Assert.Equal(3, messages.Count);
        Assert.Equal(ChatRole.User, messages[0].Role);
        Assert.Equal(ChatRole.Assistant, messages[1].Role);
        Assert.Equal(ChatRole.Tool, messages[2].Role);

        FunctionCallContent functionCall = Assert.IsType<FunctionCallContent>(Assert.Single(messages[1].Contents));
        Assert.Equal("call-agent-tool-1", functionCall.CallId);
        Assert.Equal("invoke_agent_roller", functionCall.Name);
        FunctionResultContent functionResult = Assert.IsType<FunctionResultContent>(Assert.Single(messages[2].Contents));
        Assert.Equal("call-agent-tool-1", functionResult.CallId);
    }

    [Fact]
    public void BuildApprovalTurnMessagesForAaatResultWithExistingCallInjectsFunctionResultOnly()
    {
        var response = new ToolApprovalResponseInfo(
            RequestId: "approval-1",
            ToolCallId: "call-agent-tool-1",
            ToolName: "invoke_agent_roller",
            ArgumentsJson: null,
            Approved: true,
            Reason: "chat-ui",
            RunId: "run-1",
            ResultJson: "{\"result\":\"78\"}");

        IList<ChatMessage> messages = AgentChatService.BuildApprovalTurnMessages(
            "session-1",
            "user-1",
            "roll a d100, then set the patio lights",
            response,
            NullLogger.Instance,
            resultCompletesExistingToolCall: true);

        Assert.Equal(2, messages.Count);
        Assert.Equal(ChatRole.User, messages[0].Role);
        Assert.Equal(ChatRole.Tool, messages[1].Role);
        Assert.DoesNotContain(messages, static message => message.Role == ChatRole.Assistant);

        FunctionResultContent functionResult = Assert.IsType<FunctionResultContent>(Assert.Single(messages[1].Contents));
        Assert.Equal("call-agent-tool-1", functionResult.CallId);
    }

    [Fact]
    public void BuildApprovalTurnMessagesCanCompleteExistingFunctionCallWithToolResultOnly()
    {
        var response = new ToolApprovalResponseInfo(
            RequestId: "approval-1",
            ToolCallId: "call-agent-tool-1",
            ToolName: "invoke_agent_roller",
            ArgumentsJson: null,
            Approved: true,
            Reason: "chat-ui",
            RunId: "run-1",
            ResultJson: "{\"result\":\"78\"}");

        IList<ChatMessage> messages = AgentChatService.BuildApprovalTurnMessages(
            "session-1",
            "user-1",
            string.Empty,
            response,
            NullLogger.Instance,
            resultCompletesExistingToolCall: true);

        ChatMessage message = Assert.Single(messages);
        Assert.Equal(ChatRole.Tool, message.Role);

        FunctionResultContent functionResult = Assert.IsType<FunctionResultContent>(Assert.Single(message.Contents));
        Assert.Equal("call-agent-tool-1", functionResult.CallId);
    }

    [Fact]
    public void SessionHistoryContainsToolCallIdFindsMatchingFunctionCall()
    {
        using JsonDocument document = JsonDocument.Parse("""
            {
              "stateBag": {
                "InMemoryChatHistoryProvider": {
                  "messages": [
                    {
                      "role": "assistant",
                      "contents": [
                        {
                          "$type": "functionCall",
                          "callId": "call-agent-tool-1",
                          "name": "invoke_agent_roller"
                        }
                      ]
                    }
                  ]
                }
              }
            }
            """);

        Assert.True(AgentChatService.SessionHistoryContainsToolCallId(document.RootElement, "call-agent-tool-1"));
    }

    [Fact]
    public void SessionHistoryContainsToolCallIdDoesNotTreatAnyHistoryAsMatch()
    {
        using JsonDocument document = JsonDocument.Parse("""
            {
              "stateBag": {
                "InMemoryChatHistoryProvider": {
                  "messages": [
                    {
                      "role": "user",
                      "contents": [
                        {
                          "$type": "text",
                          "text": "roll a d100"
                        }
                      ]
                    }
                  ]
                }
              }
            }
            """);

        Assert.False(AgentChatService.SessionHistoryContainsToolCallId(document.RootElement, "call-agent-tool-1"));
    }

    [Fact]
    public void BuildApprovalTurnMessagesForNonAaatResultStillPairsSyntheticFunctionCall()
    {
        var response = new ToolApprovalResponseInfo(
            RequestId: "approval-1",
            ToolCallId: "call-tool-1",
            ToolName: "run_shell",
            ArgumentsJson: null,
            Approved: true,
            Reason: "chat-ui",
            RunId: "run-1",
            ResultJson: "{\"exitCode\":0}");

        IList<ChatMessage> messages = AgentChatService.BuildApprovalTurnMessages(
            "session-1",
            "user-1",
            string.Empty,
            response,
            NullLogger.Instance);

        Assert.Equal(2, messages.Count);
        Assert.Equal(ChatRole.Assistant, messages[0].Role);
        Assert.Equal(ChatRole.Tool, messages[1].Role);

        FunctionCallContent functionCall = Assert.IsType<FunctionCallContent>(Assert.Single(messages[0].Contents));
        Assert.Equal("call-tool-1", functionCall.CallId);
        Assert.Equal("run_shell", functionCall.Name);
    }

    [Fact]
    public void BuildApprovalTurnMessagesEmitsOneResponsePerPackedCall()
    {
        var response = new ToolApprovalResponseInfo(
            RequestId: "apr-roller",
            ToolCallId: "call-roller",
            ToolName: "invoke_agent_roller",
            ArgumentsJson: """{"message":"Roll a d20"}""",
            Approved: true,
            Reason: "chat-ui",
            RunId: "run-1",
            PackedSiblingResponses:
            [
                new ToolApprovalResponseInfo(
                    RequestId: "apr-bbc",
                    ToolCallId: "call-bbc",
                    ToolName: "kautuka_web_fetch",
                    ArgumentsJson: """{"url":"https://www.bbc.com"}""",
                    Approved: true,
                    Reason: "policy:auto",
                    RunId: "run-1"),
                new ToolApprovalResponseInfo(
                    RequestId: "apr-cnn",
                    ToolCallId: "call-cnn",
                    ToolName: "kautuka_web_fetch",
                    ArgumentsJson: """{"url":"https://www.cnn.com"}""",
                    Approved: false,
                    Reason: "denied",
                    RunId: "run-1"),
            ]);

        IList<ChatMessage> messages = AgentChatService.BuildApprovalTurnMessages(
            "session-1",
            "user-1",
            string.Empty,
            response,
            NullLogger.Instance);

        ChatMessage toolMessage = Assert.Single(messages);
        Assert.Equal(ChatRole.Tool, toolMessage.Role);
        Assert.Equal(3, toolMessage.Contents.Count);
        string[] requestIds =
        [
            .. toolMessage.Contents.OfType<ToolApprovalResponseContent>().Select(static item => item.RequestId),
        ];
        Assert.Equal(["apr-roller", "apr-bbc", "apr-cnn"], requestIds);
        Assert.Equal(
            [true, true, false],
            toolMessage.Contents.OfType<ToolApprovalResponseContent>().Select(static item => item.Approved).ToArray());
    }

    [Fact]
    public void BuildApprovalTurnMessagesEmitsFunctionResultsWhenEveryPackedCallHasAResult()
    {
        var response = new ToolApprovalResponseInfo(
            RequestId: "apr-bbc",
            ToolCallId: "call-bbc",
            ToolName: "kautuka_web_fetch",
            ArgumentsJson: """{"url":"https://www.bbc.com"}""",
            Approved: true,
            Reason: "approved",
            RunId: "run-1",
            ResultJson: """{"result":"bbc"}""",
            PackedSiblingResponses:
            [
                new ToolApprovalResponseInfo(
                    RequestId: "apr-cnn",
                    ToolCallId: "call-cnn",
                    ToolName: "kautuka_web_fetch",
                    ArgumentsJson: """{"url":"https://www.cnn.com"}""",
                    Approved: false,
                    Reason: "denied",
                    RunId: "run-1",
                    ResultJson: """{"result":"denied","outcome":"denied"}"""),
            ]);

        IList<ChatMessage> messages = AgentChatService.BuildApprovalTurnMessages(
            "session-1",
            "user-1",
            string.Empty,
            response,
            NullLogger.Instance);

        Assert.Equal(2, messages.Count);
        Assert.Equal(ChatRole.Assistant, messages[0].Role);
        Assert.Equal(ChatRole.Tool, messages[1].Role);
        Assert.Equal(2, messages[0].Contents.OfType<FunctionCallContent>().Count());
        Assert.Equal(2, messages[1].Contents.OfType<FunctionResultContent>().Count());
        Assert.Empty(messages[1].Contents.OfType<ToolApprovalResponseContent>());
    }

    [Fact]
    public void BuildApprovalTurnMessagesKeepsExecutedResultsAndAaatOnFicc()
    {
        var response = new ToolApprovalResponseInfo(
            RequestId: "apr-bbc",
            ToolCallId: "call-bbc",
            ToolName: "kautuka_web_fetch",
            ArgumentsJson: """{"url":"https://www.bbc.com"}""",
            Approved: true,
            Reason: "approved",
            RunId: "run-1",
            ResultJson: """{"result":"bbc"}""",
            PackedSiblingResponses:
            [
                new ToolApprovalResponseInfo(
                    RequestId: "apr-roller",
                    ToolCallId: "call-roller",
                    ToolName: "invoke_agent_roller",
                    ArgumentsJson: """{"message":"roll a d20"}""",
                    Approved: true,
                    Reason: "approved",
                    RunId: "run-1"),
            ]);

        IList<ChatMessage> messages = AgentChatService.BuildApprovalTurnMessages(
            "session-1",
            "user-1",
            string.Empty,
            response,
            NullLogger.Instance);

        Assert.Equal(3, messages.Count);
        Assert.Equal(ChatRole.Assistant, messages[0].Role);
        Assert.Equal(ChatRole.Tool, messages[1].Role);
        Assert.Equal(ChatRole.Tool, messages[2].Role);
        FunctionCallContent fetchCall = Assert.Single(messages[0].Contents.OfType<FunctionCallContent>());
        Assert.Equal("call-bbc", fetchCall.CallId);
        FunctionResultContent fetchResult = Assert.Single(messages[1].Contents.OfType<FunctionResultContent>());
        Assert.Equal("call-bbc", fetchResult.CallId);
        ToolApprovalResponseContent roller = Assert.Single(messages[2].Contents.OfType<ToolApprovalResponseContent>());
        Assert.Equal("apr-roller", roller.RequestId);
        Assert.True(roller.Approved);
    }
}
