using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.ChannelContracts.Channels;
using Kautuka.ChannelContracts.Serialization;
using Kautuka.ChannelHost.Hosting;

namespace Kautuka.ChannelHost.Tests;

public sealed class ChannelRuntimeEventTranslatorTests
{
    private static readonly string[] sessionFactActionLabels = ["Remove #1", "Switch session", "New session"];

    [Theory]
    [InlineData("approve")]
    [InlineData("deny")]
    public void TelegramApprovalKeepsArgumentsCollapsedAndSeparateFromPromptAfterDecision(string decision)
    {
        ChannelRuntimeEventTranslator translator = new();
        string details = "Allow run_skill_script? <tg-button>literal</tg-button>\nParameters: " + new string('x', 5000);
        string argumentsJson = "{\"sides\":\"446\",\"note\":\"full details\"}";
        ChannelRuntimeEventAction request = Base("tool.approval.requested", JsonSerializer.Serialize(new
        { requestId = "approval", toolName = "run_skill_script", prompt = details, argumentsJson }), "root-context", interactionId: "hitl-example") with
        { SessionDisplayName = "Dice", PromptText = "roll d446" };
        ChannelTranslatedOutboundAction pendingAction = Assert.Single(translator.Translate(hitlSettings, request, richContext: true));
        ChannelSemanticMessageAction pending = JsonSerializer.Deserialize(pendingAction.PayloadJson, ChannelContractsJsonContext.Default.ChannelSemanticMessageAction)!;
        Assert.Equal("Session: Dice", pending.Document.Blocks[0].Text);
        Assert.Equal("Prompt: roll d446", pending.Document.Blocks[1].Text);
        ChannelSemanticBlock pendingDetails = Assert.Single(pending.Document.Blocks, block => block.Text == "Request details");
        Assert.False(pendingDetails.Expanded);
        string pendingDetailsText = Assert.Single(pendingDetails.Blocks!).Text!;
        Assert.Equal(argumentsJson, pendingDetailsText);
        Assert.DoesNotContain("Allow run_skill_script?", pendingDetailsText, StringComparison.Ordinal);
        Assert.DoesNotContain("Parameters:", pendingDetailsText, StringComparison.Ordinal);
        Assert.Equal(2, pending.Document.Blocks.SelectMany(block => block.Actions ?? []).Count());
        Assert.Contains("&lt;tg-button&gt;literal", ChannelSemanticRendering.RenderTelegram(pending)["rich_message"]!["markdown"]!.GetValue<string>(), StringComparison.Ordinal);

        ChannelRuntimeEventAction resolution = request with
        {
            EventType = "tool.approval.resolved",
            PayloadJson = JsonSerializer.Serialize(new
            { decision, toolName = "run_skill_script", prompt = details, argumentsJson, transportMessageId = "123", reason = "by admin" })
        };
        ChannelTranslatedOutboundAction resolvedAction = Assert.Single(translator.Translate(hitlSettings, resolution, richContext: true));
        Assert.Equal(ChannelOutboundActionTypes.EditSemanticMessage, resolvedAction.ActionType);
        ChannelSemanticMessageAction resolved = JsonSerializer.Deserialize(resolvedAction.PayloadJson, ChannelContractsJsonContext.Default.ChannelSemanticMessageAction)!;
        Assert.Equal("123", resolved.TransportMessageId);
        Assert.Equal("Session: Dice", resolved.Document.Blocks[0].Text);
        Assert.Equal("Prompt: roll d446", resolved.Document.Blocks[1].Text);
        ChannelSemanticBlock resolvedDetails = Assert.Single(resolved.Document.Blocks, block => block.Text == "Request details");
        Assert.False(resolvedDetails.Expanded);
        string renderedDetails = Assert.Single(resolvedDetails.Blocks!).Text!;
        Assert.Equal(argumentsJson, renderedDetails);
        Assert.Empty(resolved.Document.Blocks.SelectMany(block => block.Actions ?? []));
        Assert.Equal(decision, resolved.Document.TerminalState);
        Assert.Contains("run_skill_script", resolved.Document.FallbackText, StringComparison.Ordinal);
        _ = ChannelSemanticRendering.RenderTelegram(resolved);
    }

    [Fact]
    public void ChannelHostRendersProviderNeutralSessionFactsIntoOneSessionQueueCard()
    {
        const long revision = 7;
        var facts = new ChannelSessionFactsEnvelope(
            ChannelFactContractVersions.SessionFactsEnvelope,
            "chat-1",
            "topic-1",
            "summary",
            0,
            false,
            revision,
            new ChannelSelectedSessionFact(ChannelFactContractVersions.SelectedSession, "chat-1", "topic-1", "session-a", "Invoices", "Assistant", revision, "agent", "agent-1", "#0F766E"),
            new ChannelActiveSessionsFact(ChannelFactContractVersions.ActiveSessions, "chat-1", "topic-1", revision,
            [
                new("session-a", "Invoices", "waiting for approval"),
                new("session-b", "Dice", "running"),
            ]),
            new ChannelQueueFact(ChannelFactContractVersions.Queue, "chat-1", "topic-1", revision, 1,
            [new("turn-1", "session-b", 1, "roll d55")]),
            [
                new("remove-turn-1", "remove", "session-b", "turn-1", null, "ui:remove"),
                new("switch-sessions", "sessions", null, null, 0, "sessions:0"),
                new("new-session", "new", null, null, null, "sessions:new"),
            ]);

        ChannelSemanticMessageAction rendered = ChannelSessionFactsRenderer.Render(
            JsonSerializer.Serialize(facts, ChannelContractsJsonContext.Default.ChannelSessionFactsEnvelope));

        Assert.Equal("session_panel", rendered.Document.Template);
        Assert.Contains(rendered.Document.Blocks, block => block.Type == ChannelSemanticBlockTypes.Header && block.Text == "Assistant · Invoices");
        Assert.Contains(rendered.Document.Blocks, block => block.Type == ChannelSemanticBlockTypes.Details && block.Text == "2 active sessions · 1 messages waiting");
        Assert.Contains(rendered.Document.Blocks.SelectMany(block => block.Blocks ?? []), block => block.Text == "1. roll d55");
        Assert.Equal(sessionFactActionLabels.Skip(1),
            rendered.Document.Blocks.SelectMany(block => block.Actions ?? []).Select(action => action.Label));
        Assert.Contains(rendered.Document.Blocks.SelectMany(FlattenBlocks).SelectMany(block => block.Actions ?? []),
            action => action.Label == sessionFactActionLabels[0]);
    }

    [Fact]
    public void TelegramQueueViewContainsOnlyBotWideWaitingPromptsAndRemoveActions()
    {
        const long revision = 11;
        var facts = new ChannelSessionFactsEnvelope(
            ChannelFactContractVersions.SessionFactsEnvelope, "chat-1", null, "queue", 0, false, revision,
            new ChannelSelectedSessionFact(ChannelFactContractVersions.SelectedSession, "chat-1", null, null, null, null, revision),
            new ChannelActiveSessionsFact(ChannelFactContractVersions.ActiveSessions, "chat-1", null, revision, []),
            new ChannelQueueFact(ChannelFactContractVersions.Queue, "chat-1", null, revision, 2,
            [
                new("turn-1", "session-a", 1, "Review invoices", "Secretary", "Overnight email", "#DB2777"),
                new("turn-2", "session-b", 2, "Roll d99", "Roller", "Dice run", "#EA580C"),
            ]),
            [
                new("remove-turn-1", "remove", "session-a", "turn-1", null, "ui:remove-1"),
                new("remove-turn-2", "remove", "session-b", "turn-2", null, "ui:remove-2"),
            ]);

        ChannelSemanticMessageAction rendered = ChannelSessionFactsRenderer.Render(
            JsonSerializer.Serialize(facts, ChannelContractsJsonContext.Default.ChannelSessionFactsEnvelope));

        Assert.Contains(rendered.Document.Blocks, block => block.Type == ChannelSemanticBlockTypes.Header && block.Text == "Queued messages");
        Assert.Contains(rendered.Document.Blocks, block => block.Text == "Secretary");
        Assert.Contains(rendered.Document.Blocks, block => block.Text == "Roller");
        Assert.DoesNotContain(rendered.Document.Blocks, block => block.Text is "Overnight email" or "Dice run");
        Assert.DoesNotContain(rendered.Document.Blocks, block => block.Text?.Contains("active sessions", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(rendered.Document.Blocks.SelectMany(FlattenBlocks), block => block.Text is "Switch session" or "New session");
        Assert.Equal(2, rendered.Document.Blocks.SelectMany(FlattenBlocks).SelectMany(block => block.Actions ?? []).Count(action => action.Label == "Remove"));
    }

    private static IEnumerable<ChannelSemanticBlock> FlattenBlocks(ChannelSemanticBlock block)
    {
        yield return block;
        if (block.Blocks is null) yield break;
        foreach (ChannelSemanticBlock child in block.Blocks)
            foreach (ChannelSemanticBlock nested in FlattenBlocks(child))
                yield return nested;
    }

    [Fact]
    public void TelegramNormalizationRetainsTheOriginalPromptMessageId()
    {
        ChannelInboundEvent? message = Kautuka.ChannelHost.Providers.Telegram.TelegramLongPollingWorker.CreateInboundEventForTest("reg", "owner",
            JsonSerializer.SerializeToElement(new { update_id = 1, message = new { message_id = 123, from = new { id = 42 }, chat = new { id = 42 }, text = "Hello" } }));
        Assert.NotNull(message);
        Assert.Equal(123L, message.SourceMessageId);
    }

    [Theory]
    [InlineData("run.finished", "{\"response\":\"Rolled 42\"}")]
    [InlineData("tool.approval.requested", "{\"toolName\":\"roller\",\"prompt\":\"Roll d55?\"}")]
    public void TelegramPrivateChatMessagesKeepAgentSessionAndPromptContext(string type, string payload)
    {
        ChannelRuntimeEventAction input = Base(type, payload, "root-unique") with
        { AgentDisplayName = "My Assistant", SessionDisplayName = "Dice *today*", ReplyToMessageId = "123", ConversationThreadId = "topic-1" };
        ChannelTranslatedOutboundAction translated = Assert.Single(new ChannelRuntimeEventTranslator().Translate(telegramAgentTopicSettings, input, richContext: true));
        ChannelSemanticMessageAction message = JsonSerializer.Deserialize(translated.PayloadJson,
            Kautuka.ChannelContracts.Serialization.ChannelContractsJsonContext.Default.ChannelSemanticMessageAction)!;
        Assert.Equal("My Assistant", message.Document.SenderDisplayName);
        Assert.Equal("123", message.ReplyToMessageId);
        System.Text.Json.Nodes.JsonObject body = ChannelSemanticRendering.RenderTelegram(message);
        Assert.Equal(123L, body["reply_parameters"]!["message_id"]!.GetValue<long>());
        Assert.True(body["reply_parameters"]!["allow_sending_without_reply"]!.GetValue<bool>());
        Assert.Contains("Session:", body["rich_message"]!["markdown"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("My Assistant", body["rich_message"]!["markdown"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    private static readonly JsonElement hitlSettings = JsonDocument.Parse("""{"hitl_tether_mode":"hitl"}""").RootElement.Clone();
    private static readonly JsonElement telegramAgentTopicSettings = JsonDocument.Parse("""{"hitl_tether_mode":"hitl","target_threads":{"agent:agent-1":"topic-1"}}""").RootElement.Clone();
    private static readonly JsonElement mirrorSettings = JsonDocument.Parse("""{"hitl_tether_mode":"mirror"}""").RootElement.Clone();

    [Fact]
    public void WorkflowReviewRendersStructuredFieldsAndAllResolutionActions()
    {
        ChannelRuntimeEventAction input = Base(
            "tool.approval.requested",
            """{"requestId":"approval","toolName":"workflow_review","kind":"workflow_review","title":"Workflow review: Nightly · step 2 of 3","summary":"This step finished. Check its result before the next step runs.","fields":[{"label":"Agent","value":"Writer"},{"label":"status","value":"ready"}],"actions":[{"value":"approve","label":"Approve","style":"primary"},{"value":"request_changes","label":"Request changes","style":"default"},{"value":"stop_run","label":"Stop","style":"danger"}],"userMessage":"private prompt"}""",
            "run-review",
            interactionId: "review-interaction") with
        { PromptText = "Do not append this run prompt." };

        ChannelSemanticMessageAction pending = JsonSerializer.Deserialize(
            Assert.Single(new ChannelRuntimeEventTranslator().Translate(hitlSettings, input, richContext: true)).PayloadJson,
            ChannelContractsJsonContext.Default.ChannelSemanticMessageAction)!;
        Assert.Equal("Workflow review: Nightly · step 2 of 3", pending.Document.Blocks[0].Text);
        Assert.Contains(pending.Document.Blocks, block => block.Type == ChannelSemanticBlockTypes.Fields
            && block.Fields is { Count: 2 } fields && fields[0].Label == "Agent" && fields[1].Value == "ready");
        Assert.Contains(pending.Document.Blocks, block => block.Text == "Open Kautuka → Approvals for full details.");
        Assert.DoesNotContain("private prompt", pending.Document.FallbackText, StringComparison.Ordinal);
        Assert.DoesNotContain("Do not append this run prompt", pending.Document.FallbackText, StringComparison.Ordinal);
        ChannelSemanticAction[] actions = pending.Document.Blocks.SelectMany(block => block.Actions ?? []).ToArray();
        Assert.Equal(["Approve", "Request changes", "Stop"], actions.Select(action => action.Label));
        Assert.All(actions, action => Assert.InRange(Encoding.UTF8.GetByteCount(action.CallbackToken!), 1, 64));

        JsonObject telegram = ChannelSemanticRendering.RenderTelegram(pending);
        Assert.Contains("Request changes", telegram["rich_message"]!["markdown"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Count(
            telegram["rich_message"]!["markdown"]!.GetValue<string>(), "<tg-button "));
        JsonObject slack = ChannelSemanticRendering.RenderSlack(pending);
        Assert.Equal(3, slack["blocks"]!.AsArray()
            .Where(block => block!["type"]!.GetValue<string>() == "actions")
            .SelectMany(block => block!["elements"]!.AsArray()).Count());
    }

    [Theory]
    [InlineData("approve", "Approved. The next step has started.")]
    [InlineData("request_changes", "Changes requested. This step will run again.")]
    [InlineData("stop_run", "Stopped.")]
    [InlineData("expired", "Expired with no decision.")]
    public void WorkflowReviewResolutionEditsTheApprovalCard(string decision, string expected)
    {
        ChannelRuntimeEventAction input = Base("tool.approval.resolved",
            JsonSerializer.Serialize(new { kind = "workflow_review", title = "Workflow review: Order review", decision, transportMessageId = "approval-message" }),
            "run-review", interactionId: "review-interaction") with
        { PromptText = "private workflow prompt", SessionDisplayName = "workflow session" };
        ChannelTranslatedOutboundAction action = Assert.Single(new ChannelRuntimeEventTranslator().Translate(hitlSettings, input));
        Assert.Equal(ChannelOutboundActionTypes.EditSemanticMessage, action.ActionType);
        ChannelSemanticMessageAction message = JsonSerializer.Deserialize(action.PayloadJson,
            ChannelContractsJsonContext.Default.ChannelSemanticMessageAction)!;
        Assert.Equal("approval-message", message.TransportMessageId);
        Assert.Equal("Workflow review: Order review\n" + expected, message.Document.FallbackText);
        Assert.Equal(ChannelSemanticBlockTypes.Header, message.Document.Blocks[0].Type);
        Assert.Equal("Workflow review: Order review", message.Document.Blocks[0].Text);
        Assert.Equal(expected, message.Document.Blocks[1].Text);
        Assert.DoesNotContain("private workflow prompt", message.Document.FallbackText, StringComparison.Ordinal);
        Assert.DoesNotContain("workflow session", message.Document.FallbackText, StringComparison.Ordinal);
        Assert.Empty(message.Document.Blocks.SelectMany(block => block.Actions ?? []));
        JsonObject slack = ChannelSemanticRendering.RenderSlack(message);
        JsonObject telegram = ChannelSemanticRendering.RenderTelegram(message);
        Assert.Contains(expected, slack.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains(expected, telegram.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("actions", slack.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("<tg-button ", telegram.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void EverySemanticDocumentFromTheTranslatorValidatesForSlackAndTelegram()
    {
        ChannelRuntimeEventTranslator translator = new();
        ChannelRuntimeEventAction[] events =
        [
            Base("user.message.recorded", """{"message":"hello"}""", "guard-user"),
            Base("assistant.message.completed", """{"messageId":"m1","content":"hello"}""", "guard-complete"),
            Base("run.finished", """{"response":"done"}""", "guard-finished"),
            Base("tool.approval.requested", """{"requestId":"a1","toolName":"send","prompt":"Send?"}""", "guard-approval", interactionId: "i1"),
            Base("tool.approval.requested", """{"requestId":"a2","toolName":"workflow_review","kind":"workflow_review","title":"Workflow review: Orders","summary":"Review this step","actions":[{"value":"approve","label":"Approve"}]}""", "guard-review", interactionId: "i2"),
            Base("tool.approval.resolved", """{"kind":"workflow_review","decision":"approve","transportMessageId":"m2"}""", "guard-resolved", interactionId: "i2"),
        ];
        JsonElement settings = JsonDocument.Parse("""{"hitl_tether_mode":"hitl"}""").RootElement.Clone();

        foreach (ChannelRuntimeEventAction runtimeEvent in events)
        {
            foreach (ChannelTranslatedOutboundAction translated in translator.Translate(settings, runtimeEvent, richContext: true))
            {
                if (translated.ActionType is not (ChannelOutboundActionTypes.SendSemanticMessage or ChannelOutboundActionTypes.EditSemanticMessage)) continue;
                ChannelSemanticMessageAction message = JsonSerializer.Deserialize(translated.PayloadJson,
                    ChannelContractsJsonContext.Default.ChannelSemanticMessageAction)!;
                ChannelSemanticRendering.Validate(message, ChannelProviderCapabilityCatalog.Slack);
                ChannelSemanticRendering.Validate(message, ChannelProviderCapabilityCatalog.Telegram);
            }
        }
    }

    [Fact]
    public void MessageCompletedUsesWireContentWithoutPriorDeltaBuffer()
    {
        ChannelRuntimeEventTranslator translator = new();
        IReadOnlyList<ChannelTranslatedOutboundAction> actions = translator.Translate(
            mirrorSettings,
            Base(
                "assistant.message.completed",
                """{"messageId":"m-content","content":"Final answer from gateway"}""",
                "run-content"));
        Assert.Single(actions);
        Assert.Equal(ChannelOutboundActionTypes.SendSemanticMessage, actions[0].ActionType);
        Assert.Contains("Final answer from gateway", actions[0].PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("stream_message", actions[0].ActionType, StringComparison.Ordinal);
    }

    [Fact]
    public void HitlDropsTextDeltaButEmitsTerminalAgentResponse()
    {
        ChannelRuntimeEventTranslator translator = new();
        ChannelRuntimeEventAction delta = Base("assistant.text.delta", """{"messageId":"m1","delta":"Hello"}""", "run-a");
        Assert.Empty(translator.Translate(hitlSettings, delta));

        ChannelRuntimeEventAction completed = Base("assistant.message.completed", """{"messageId":"m1"}""", "run-a");
        Assert.Empty(translator.Translate(hitlSettings, completed));

        IReadOnlyList<ChannelTranslatedOutboundAction> actions = translator.Translate(
            hitlSettings,
            Base("run.finished", """{"response":"Hello"}""", "run-a"));
        Assert.Single(actions);
        Assert.Equal(ChannelOutboundActionTypes.SendSemanticMessage, actions[0].ActionType);
        Assert.DoesNotContain("stream_message", actions[0].ActionType, StringComparison.Ordinal);
        Assert.Contains("Hello", actions[0].PayloadJson, StringComparison.Ordinal);
        Assert.Contains("agent_response", actions[0].PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public void HitlNeverEmitsStreamMessageEvenAfterManyDeltas()
    {
        ChannelRuntimeEventTranslator translator = new();
        for (int i = 0; i < 5; i++)
        {
            string payload = "{\"messageId\":\"m-stream\",\"delta\":\"w" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + " \"}";
            Assert.Empty(translator.Translate(
                hitlSettings,
                Base("assistant.text.delta", payload, "run-hitl-stream")));
        }

        IReadOnlyList<ChannelTranslatedOutboundAction> completed = translator.Translate(
            hitlSettings,
            Base("assistant.message.completed", """{"messageId":"m-stream"}""", "run-hitl-stream"));
        Assert.Empty(completed);

        IReadOnlyList<ChannelTranslatedOutboundAction> finished = translator.Translate(
            hitlSettings,
            Base("run.finished", """{"response":"w0 w1 w2 w3 w4 "}""", "run-hitl-stream"));
        Assert.Single(finished);
        Assert.Equal(ChannelOutboundActionTypes.SendSemanticMessage, finished[0].ActionType);
    }

    [Fact]
    public void MirrorBuffersTextDeltaWithoutStreamingThenEmitsOneShotCompletedMessage()
    {
        ChannelRuntimeEventTranslator translator = new();
        ChannelRuntimeEventAction delta = Base("assistant.text.delta", """{"messageId":"m2","delta":"Hi"}""", "run-b");
        Assert.Empty(translator.Translate(mirrorSettings, delta));

        IReadOnlyList<ChannelTranslatedOutboundAction> completed = translator.Translate(
            mirrorSettings,
            Base("assistant.message.completed", """{"messageId":"m2"}""", "run-b"));
        Assert.Single(completed);
        Assert.Equal(ChannelOutboundActionTypes.SendSemanticMessage, completed[0].ActionType);
        Assert.Contains("Hi", completed[0].PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("stream_message", completed[0].ActionType, StringComparison.Ordinal);
    }

    [Fact]
    public void AttachmentWithoutTetherModeEmitsCompletedMessageAsOneShot()
    {
        ChannelRuntimeEventTranslator translator = new();
        JsonElement settings = JsonDocument.Parse("{}").RootElement.Clone();

        IReadOnlyList<ChannelTranslatedOutboundAction> completed = translator.Translate(
            settings,
            Base("assistant.message.completed", """{"messageId":"m-no-mode","content":"Completed without mode"}""", "run-no-mode"));

        Assert.Single(completed);
        Assert.Equal(ChannelOutboundActionTypes.SendSemanticMessage, completed[0].ActionType);
        Assert.Contains("Completed without mode", completed[0].PayloadJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("hitl", "Hitl")]
    [InlineData(" HITL ", "Hitl")]
    [InlineData("mirror", "Mirror")]
    [InlineData(" MIRROR ", "Mirror")]
    [InlineData(null, "Mirror")]
    [InlineData("", "Mirror")]
    [InlineData("full", "Mirror")]
    [InlineData("none", "Mirror")]
    [InlineData("single", "Mirror")]
    [InlineData("unsupported", "Mirror")]
    public void TetherModeParserAcceptsOnlyHitlAndMirror(string? mode, string expected)
        => Assert.Equal(expected, ChannelHostHitlTetherModeParser.Parse(mode).ToString());

    [Fact]
    public void ApprovalRequestedBuildsSemanticApprovalRequest()
    {
        ChannelRuntimeEventTranslator translator = new();
        ChannelRuntimeEventAction requested = Base(
            "tool.approval.requested",
            """{"requestId":"apr-1","toolName":"roll_dice","toolCallId":"tc1"}""",
            "run-d",
            interactionId: "ix-1");
        IReadOnlyList<ChannelTranslatedOutboundAction> actions = translator.Translate(hitlSettings, requested);
        Assert.Single(actions);
        Assert.Equal(ChannelOutboundActionTypes.SendSemanticMessage, actions[0].ActionType);
        Assert.Contains("approval_request", actions[0].PayloadJson, StringComparison.Ordinal);
        // Typical interaction ids exceed Telegram's 64-byte callback_data budget for JSON.
        Assert.Contains("hitl:ix-1:approve", actions[0].PayloadJson, StringComparison.Ordinal);
        Assert.Contains("hitl:ix-1:deny", actions[0].PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public void ApprovalRequestedPrefersGatewayPromptContext()
    {
        ChannelRuntimeEventTranslator translator = new();
        ChannelRuntimeEventAction requested = Base(
            "tool.approval.requested",
            """{"requestId":"apr-1","toolName":"invoke_agent_roller","prompt":"roll a d995\n\nAllow the agent to run `invoke_agent_roller`?","userMessage":"roll a d995"}""",
            "run-prompt",
            interactionId: "ix-prompt");
        IReadOnlyList<ChannelTranslatedOutboundAction> actions = translator.Translate(hitlSettings, requested);
        Assert.Single(actions);
        Assert.Contains("roll a d995", actions[0].PayloadJson, StringComparison.Ordinal);
        Assert.Contains("invoke_agent_roller", actions[0].PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public void BothModesEmitUserMessageAsOneShotSemanticMessage()
    {
        ChannelRuntimeEventTranslator translator = new();
        ChannelRuntimeEventAction userMessage = Base(
            "user.message.recorded",
            """{"message":"roll a d995","role":"user","messageId":"u1"}""",
            "run-user");

        foreach (JsonElement settings in new[] { mirrorSettings, hitlSettings })
        {
            IReadOnlyList<ChannelTranslatedOutboundAction> actions = translator.Translate(settings, userMessage);
            Assert.Single(actions);
            Assert.Equal(ChannelOutboundActionTypes.SendSemanticMessage, actions[0].ActionType);
            Assert.Contains("user_message", actions[0].PayloadJson, StringComparison.Ordinal);
            Assert.Contains("roll a d995", actions[0].PayloadJson, StringComparison.Ordinal);
            Assert.DoesNotContain("stream_message", actions[0].ActionType, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ApprovalCallbackTokenUsesStructuredJsonWhenItFitsTelegramLimit()
    {
        // Full JSON shape only fits tiny ids; typical hitl-{guid} tokens use the legacy form.
        string token = ChannelRuntimeEventTranslator.BuildApprovalCallbackToken("x", "a");
        Assert.Contains("approval.resolve", token, StringComparison.Ordinal);
        Assert.Contains("\"interactionId\":\"x\"", token, StringComparison.Ordinal);
        Assert.DoesNotContain("hitl:", token, StringComparison.Ordinal);
    }

    [Fact]
    public void ApprovalCallbackTokenFallsBackToHitlWhenStructuredExceedsTelegramLimit()
    {
        string longId = "hitl-" + new string('a', 32);
        string token = ChannelRuntimeEventTranslator.BuildApprovalCallbackToken(longId, "approve");
        Assert.StartsWith("hitl:", token, StringComparison.Ordinal);
        Assert.Contains(longId, token, StringComparison.Ordinal);
    }

    [Fact]
    public void ApprovalResolvedFallsBackToStatusMessageWithoutTransportId()
    {
        ChannelRuntimeEventTranslator translator = new();
        ChannelRuntimeEventAction resolved = Base(
            "tool.approval.resolved",
            """{"approvalRequestId":"apr-1","decision":"approved","toolName":"roll_dice","resolutionSource":"chat"}""",
            "run-e");
        IReadOnlyList<ChannelTranslatedOutboundAction> actions = translator.Translate(hitlSettings, resolved);
        Assert.Single(actions);
        Assert.Equal(ChannelOutboundActionTypes.SendSemanticMessage, actions[0].ActionType);
        Assert.Contains("approval_resolution", actions[0].PayloadJson, StringComparison.Ordinal);
        Assert.Contains("approved", actions[0].PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public void ApprovalResolvedEditsWhenTransportIdInPayload()
    {
        ChannelRuntimeEventTranslator translator = new();
        ChannelRuntimeEventAction resolved = Base(
            "tool.approval.resolved",
            """{"approvalRequestId":"apr-2","decision":"denied","toolName":"shell","transportMessageId":"99"}""",
            "run-f2");
        IReadOnlyList<ChannelTranslatedOutboundAction> actions = translator.Translate(hitlSettings, resolved);
        Assert.Single(actions);
        Assert.Equal(ChannelOutboundActionTypes.EditMessageText, actions[0].ActionType);
        Assert.Contains("\"messageId\":\"99\"", actions[0].PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public void ApprovalResolvedEditsWhenTransportIdProvided()
    {
        ChannelRuntimeEventTranslator translator = new();
        ChannelRuntimeEventAction resolved = Base(
            "tool.approval.resolved",
            """{"approvalRequestId":"apr-2","decision":"denied","toolName":"shell"}""",
            "run-f");
        IReadOnlyList<ChannelTranslatedOutboundAction> actions = translator.Translate(hitlSettings, resolved, approvalTransportMessageId: "42");
        Assert.Single(actions);
        Assert.Equal(ChannelOutboundActionTypes.EditMessageText, actions[0].ActionType);
        Assert.Contains("\"messageId\":\"42\"", actions[0].PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownEventTypesAreFiltered()
    {
        ChannelRuntimeEventTranslator translator = new();
        ChannelRuntimeEventAction evt = Base("tool.call.started", """{"toolCallId":"tc"}""", "run-g");
        Assert.Empty(translator.Translate(mirrorSettings, evt));
    }

    [Fact]
    public void MissingConversationIdSkipsDelivery()
    {
        ChannelRuntimeEventTranslator translator = new();
        ChannelRuntimeEventAction evt = Base(
            "tool.approval.requested",
            """{"requestId":"apr","toolName":"x"}""",
            "run-h",
            conversationId: null);
        Assert.Empty(translator.Translate(hitlSettings, evt));
    }

    [Fact]
    public void RuntimeEventActionTypeConstantIsStable()
        => Assert.Equal("runtime_event", ChannelOutboundActionTypes.RuntimeEvent);

    [Theory]
    [InlineData("hitl")]
    [InlineData("mirror")]
    [InlineData("unsupported")]
    public void NoTetherModeEverEmitsStreamMessage(string mode)
    {
        JsonElement settings = JsonDocument.Parse(
            "{\"hitl_tether_mode\":\"" + mode + "\"}").RootElement.Clone();
        ChannelRuntimeEventTranslator translator = new();
        string runId = "run-no-stream-" + mode;
        Assert.Empty(translator.Translate(
            settings,
            Base("assistant.text.delta", """{"messageId":"m-ns","delta":"a"}""", runId)));
        Assert.Empty(translator.Translate(
            settings,
            Base("assistant.text.delta", """{"messageId":"m-ns","delta":"b"}""", runId)));

        IReadOnlyList<ChannelTranslatedOutboundAction> completed = translator.Translate(
            settings,
            Base("assistant.message.completed", """{"messageId":"m-ns"}""", runId));
        Assert.DoesNotContain(
            completed,
            action => string.Equals(action.ActionType, ChannelOutboundActionTypes.StreamMessage, StringComparison.Ordinal));

        IReadOnlyList<ChannelTranslatedOutboundAction> finished = translator.Translate(
            settings,
            Base("run.finished", "{}", runId));
        Assert.DoesNotContain(
            finished,
            action => string.Equals(action.ActionType, ChannelOutboundActionTypes.StreamMessage, StringComparison.Ordinal));
    }

    [Fact]
    public void MessageCompletedAndRunFinishedEmitSingleFinalForSameRoot()
    {
        ChannelRuntimeEventTranslator translator = new();
        IReadOnlyList<ChannelTranslatedOutboundAction> completed = translator.Translate(
            hitlSettings,
            Base(
                "assistant.message.completed",
                """{"messageId":"m-final","content":"The result of the d45 roll is 4."}""",
                "run-dedupe-final"));
        Assert.Empty(completed);

        IReadOnlyList<ChannelTranslatedOutboundAction> finished = translator.Translate(
            hitlSettings,
            Base(
                "run.finished",
                """{"response":"The result of the d45 roll is 4."}""",
                "run-dedupe-final"));
        Assert.Single(finished);
    }

    [Fact]
    public void RunFinishedThenMessageCompletedEmitsSingleFinalForSameRoot()
    {
        ChannelRuntimeEventTranslator translator = new();
        IReadOnlyList<ChannelTranslatedOutboundAction> finished = translator.Translate(
            hitlSettings,
            Base(
                "run.finished",
                """{"response":"done"}""",
                "run-dedupe-order"));
        Assert.Single(finished);

        IReadOnlyList<ChannelTranslatedOutboundAction> completed = translator.Translate(
            hitlSettings,
            Base(
                "assistant.message.completed",
                """{"messageId":"m2","content":"done"}""",
                "run-dedupe-order"));
        Assert.Empty(completed);
    }

    [Fact]
    public void SameRunFinalIsEmittedOncePerLinkedRegistration()
    {
        ChannelRuntimeEventTranslator translator = new();
        ChannelRuntimeEventAction finished = Base("run.finished", """{"response":"done"}""", "run-linked-final");

        Assert.Single(translator.Translate(hitlSettings, finished));
        Assert.Single(translator.Translate(hitlSettings, finished with { RegistrationId = "reg-2" }));
        Assert.Empty(translator.Translate(hitlSettings, finished));
    }

    [Fact]
    public void UserMessageAttributesSurfaceAgentDisplayName()
    {
        ChannelRuntimeEventTranslator translator = new();
        IReadOnlyList<ChannelTranslatedOutboundAction> actions = translator.Translate(
            hitlSettings,
            Base(
                "user.message.recorded",
                """{"message":"roll a d45","messageId":"u1"}""",
                "run-attr"));
        Assert.Single(actions);
        Assert.Contains("Assistant", actions[0].PayloadJson, StringComparison.Ordinal);
        Assert.Contains("roll a d45", actions[0].PayloadJson, StringComparison.Ordinal);
    }

    private static ChannelRuntimeEventAction Base(
        string eventType,
        string payloadJson,
        string runId,
        string? conversationId = "chat-1",
        string? interactionId = null)
        => new(
            EventId: Guid.NewGuid().ToString("N"),
            EventType: eventType,
            PayloadJson: payloadJson,
            OwnerUserId: "user-1",
            RegistrationId: "reg-1",
            SessionId: "session-1",
            RootRunId: runId,
            RunId: runId,
            ConversationId: conversationId,
            ConversationThreadId: null,
            AgentId: "agent-1",
            InteractionId: interactionId,
            AgentDisplayName: "Assistant");

    [Fact]
    public void SenderAttributionUsesDisplayNameNeverAgentId()
    {
        ChannelRuntimeEventTranslator translator = new();
        ChannelRuntimeEventAction finished = new(
            EventId: "evt-name",
            EventType: "run.finished",
            PayloadJson: """{"response":"done"}""",
            OwnerUserId: "user-1",
            RegistrationId: "reg-1",
            SessionId: "session-1",
            RootRunId: "run-name",
            RunId: "run-name",
            ConversationId: "chat-1",
            ConversationThreadId: null,
            AgentId: "agent-1784294095907",
            InteractionId: null,
            AgentDisplayName: "Assistant");

        IReadOnlyList<ChannelTranslatedOutboundAction> actions = translator.Translate(hitlSettings, finished);
        Assert.Single(actions);
        Assert.Contains("Assistant", actions[0].PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("agent-1784294095907", actions[0].PayloadJson, StringComparison.Ordinal);
    }
}
