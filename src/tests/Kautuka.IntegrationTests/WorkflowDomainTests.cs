using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Workflows;

using Microsoft.Agents.AI.Workflows;

namespace Kautuka.IntegrationTests;

public sealed class WorkflowDomainTests
{
    [Fact]
    public void JsonPointerGetAndSetCopyMappedFields()
    {
        JsonObject source = JsonNode.Parse("""{"findings":{"items":["a"],"summary":"no"}}""")!.AsObject();
        JsonObject working = new() { ["kept"] = "yes" };

        JsonObject next = WorkflowMapApplier.Apply(
            working,
            source,
            [new WorkflowStateMap("/findings/items", "/items")]);

        Assert.Equal("yes", next["kept"]!.GetValue<string>());
        Assert.Equal("a", next["items"]!.AsArray()[0]!.GetValue<string>());
        Assert.Null(next["summary"]);
        Assert.Equal("no", source["findings"]!["summary"]!.GetValue<string>());
    }

    [Fact]
    public void JsonPointerRejectsArrayAppendToken()
    {
        JsonObject root = new() { ["items"] = new JsonArray() };
        Assert.Throws<WorkflowMapException>(() => WorkflowJsonPointer.Set(root, "/items/-", "x"));
    }

    [Fact]
    public void SchemaSubsetRejectsNestedObjectsAndRefs()
    {
        JsonObject nested = JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "findings": { "type": "object", "properties": { "n": { "type": "integer" } } }
              }
            }
            """)!.AsObject();
        Assert.Throws<WorkflowSchemaException>(() => WorkflowSchemaSubset.ValidateSchema(nested));

        JsonObject withRef = JsonNode.Parse("""
            { "type": "object", "properties": { "id": { "$ref": "#/$defs/id" } } }
            """)!.AsObject();
        Assert.Throws<WorkflowSchemaException>(() => WorkflowSchemaSubset.ValidateSchema(withRef));
    }

    [Fact]
    public void SchemaSubsetValidatesRequiredScalars()
    {
        JsonObject schema = ScalarObjectSchema();
        WorkflowSchemaSubset.ValidateInstance(schema, JsonNode.Parse("""{"title":"ok","count":2}"""));
        Assert.Throws<WorkflowSchemaException>(
            () => WorkflowSchemaSubset.ValidateInstance(schema, JsonNode.Parse("""{"title":"ok"}""")));
        Assert.Throws<WorkflowSchemaException>(
            () => WorkflowSchemaSubset.ValidateInstance(schema, JsonNode.Parse("""{"title":"ok","count":2,"extra":true}""")));
    }

    [Fact]
    public void EnvelopeHashIsOrderIndependentAndDetectsSecrets()
    {
        JsonObject a = JsonNode.Parse("""{"b":1,"a":"x"}""")!.AsObject();
        JsonObject b = JsonNode.Parse("""{"a":"x","b":1}""")!.AsObject();
        Assert.Equal(WorkflowEnvelopeCanonical.Sha256Hex(a), WorkflowEnvelopeCanonical.Sha256Hex(b));

        JsonObject dirty = JsonNode.Parse("""{"token":"sk-live-secret"}""")!.AsObject();
        Assert.True(WorkflowEnvelopeCanonical.ContainsSecret(dirty));
        Assert.False(WorkflowEnvelopeCanonical.ContainsSecret(a));

        JsonObject oversized = new() { ["title"] = new string('x', WorkflowEnvelopeCanonical.MaxUtf8Bytes) };
        Assert.Throws<WorkflowSchemaException>(() => WorkflowEnvelopeCanonical.EnsureSafe(oversized));
    }

    [Fact]
    public void GovernedDefinitionRejectsTextOutputAndUngatedExternalEffects()
    {
        JsonObject schema = ScalarObjectSchema();
        WorkflowNodeDefinition writer = new(
            "write",
            "agent-writer",
            "Write {{input.title}}",
            WorkflowOutputKind.Schema,
            schema,
            [new WorkflowStateMap("/title", "/title")],
            WorkflowSideEffects.External,
            MemoryWrites: false,
            RequiresReview: false,
            MaxRetries: 1);

        WorkflowDefinitionDraft draft = new(
            "wf-1",
            "Draft",
            Governed: true,
            schema,
            schema,
            schema,
            [writer]);

        Assert.Throws<WorkflowSchemaException>(() => WorkflowDefinitionValidator.Validate(draft));

        WorkflowNodeDefinition review = writer with { NodeId = "review", RequiresReview = true, SideEffects = WorkflowSideEffects.None };
        WorkflowNodeDefinition external = writer with { NodeId = "apply" };
        WorkflowDefinitionValidator.Validate(draft with { Nodes = [review, external] });
    }

    [Fact]
    public void GovernedDefinitionRejectsTextOutputKind()
    {
        JsonObject schema = ScalarObjectSchema();
        WorkflowNodeDefinition node = new(
            "n1",
            "agent-1",
            "Go",
            WorkflowOutputKind.Text,
            null,
            [],
            WorkflowSideEffects.None,
            false,
            false,
            0);
        WorkflowDefinitionDraft draft = new("wf-1", "Draft", true, schema, schema, schema, [node]);
        Assert.Throws<WorkflowSchemaException>(() => WorkflowDefinitionValidator.Validate(draft));
    }

    [Fact]
    public void PromptRendererFillsInputPlaceholdersFromEnvelope()
    {
        JsonObject envelope = new() { ["title"] = "Draft" };
        Assert.Equal("Review Draft", WorkflowPromptRenderer.Render("Review {{input.title}}", envelope));
    }

    [Fact]
    public void PromptRendererStepAddsWorkingDataAndJsonContract()
    {
        JsonObject envelope = new() { ["title"] = "Draft" };
        var node = new GatewayWorkflowNode(
            "review",
            "agent-a",
            "Tighten {{title}}",
            "schema",
            ScalarObjectSchema(),
            [],
            "none",
            false,
            false,
            0);

        string prompt = WorkflowPromptRenderer.RenderStep(node, envelope);

        Assert.Contains("Tighten Draft", prompt, StringComparison.Ordinal);
        Assert.Contains("Current working data:", prompt, StringComparison.Ordinal);
        Assert.Contains("""{"title":"Draft"}""", prompt, StringComparison.Ordinal);
        Assert.Contains("Reply with a JSON object only", prompt, StringComparison.Ordinal);
        Assert.Contains("\"count\"", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void PromptRendererStepIncludesPickListAnswers()
    {
        var node = new GatewayWorkflowNode(
            "assess",
            "agent-a",
            "Choose a decision.",
            "schema",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["decision"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray("proceed", "refer", "decline"),
                    },
                },
            },
            [],
            "none",
            false,
            false,
            0);

        string prompt = WorkflowPromptRenderer.RenderStep(node, new JsonObject());

        Assert.Contains("proceed", prompt, StringComparison.Ordinal);
        Assert.Contains("refer", prompt, StringComparison.Ordinal);
        Assert.Contains("decline", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void StepOutputParserReadsJsonObjectForSchemaKind()
    {
        var node = new GatewayWorkflowNode(
            "review",
            "agent-a",
            "Review",
            "schema",
            ScalarObjectSchema(),
            [],
            "none",
            false,
            false,
            0);
        JsonObject parsed = WorkflowStepOutputParser.Parse("""{"title":"ok","count":1}""", node);
        Assert.Equal("ok", parsed["title"]!.GetValue<string>());
    }

    [Fact]
    public void StepOutputParserExtractsJsonFromProseAndDropsExtraKeys()
    {
        var node = new GatewayWorkflowNode(
            "review",
            "agent-a",
            "Review",
            "schema",
            ScalarObjectSchema(),
            [],
            "none",
            false,
            false,
            0);

        JsonObject parsed = WorkflowStepOutputParser.Parse(
            """Sure — here you go: {"title":"ok","count":2,"extra":true} thanks!""",
            node);

        Assert.Equal("ok", parsed["title"]!.GetValue<string>());
        Assert.Equal(2, parsed["count"]!.GetValue<int>());
        Assert.False(parsed.ContainsKey("extra"));
    }

    [Fact]
    public void StepOutputParserRejectsNonJsonSchemaOutput()
    {
        var node = new GatewayWorkflowNode(
            "review",
            "agent-a",
            "Review",
            "schema",
            ScalarObjectSchema(),
            [],
            "none",
            false,
            false,
            0);

        Assert.Throws<WorkflowSchemaException>(() => WorkflowStepOutputParser.Parse("just a title", node));
    }

    [Fact]
    public void GovernedParserRejectsProseScrape()
    {
        var node = new GatewayWorkflowNode(
            "review",
            "agent-a",
            "Review",
            "schema",
            ScalarObjectSchema(),
            [],
            "none",
            false,
            false,
            0);

        Assert.Throws<WorkflowSchemaException>(
            () => WorkflowStepOutputParser.Parse(
                """Sure — {"title":"ok","count":2} thanks!""",
                node,
                governed: true,
                out _));
    }

    [Fact]
    public void UngovernedParserJournalsDroppedKeys()
    {
        var node = new GatewayWorkflowNode(
            "review",
            "agent-a",
            "Review",
            "schema",
            ScalarObjectSchema(),
            [],
            "none",
            false,
            false,
            0);

        JsonObject parsed = WorkflowStepOutputParser.Parse(
            """{"title":"ok","count":2,"extra":true}""",
            node,
            governed: false,
            out IReadOnlyList<string> dropped);
        Assert.Equal("ok", parsed["title"]!.GetValue<string>());
        Assert.Contains("extra", dropped);
    }

    [Fact]
    public void GovernedPromptOmitsUndeclaredEnvelopeFields()
    {
        JsonObject envelope = new() { ["title"] = "Draft", ["ssn"] = "000-00-0000" };
        var node = new GatewayWorkflowNode(
            "review",
            "agent-a",
            "Tighten {{title}}",
            "schema",
            ScalarObjectSchema(),
            [new GatewayWorkflowMap("/title", "/title")],
            "none",
            false,
            false,
            0);

        string prompt = WorkflowPromptRenderer.RenderStep(node, envelope, governed: true);

        Assert.Contains("Tighten Draft", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("000-00-0000", prompt, StringComparison.Ordinal);
        Assert.Contains("""{"title":"Draft"}""", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void SwitchMatchesFirstEqAndDefault()
    {
        JsonObject proceed = new() { ["decision"] = "proceed" };
        JsonObject other = new() { ["decision"] = "hold" };
        GatewayWorkflowSwitchCase rule = new("/decision", "eq", "proceed", "proceed");
        Assert.True(WorkflowSwitch.Matches(proceed, rule));
        Assert.False(WorkflowSwitch.Matches(other, rule));
        Assert.True(WorkflowSwitch.Matches(proceed, new("/decision", "exists", null, "ok")));
    }

    [Fact]
    public async Task SwitchUsesTheFirstMatchingRuleInDefinitionOrder()
    {
        GatewayWorkflowSwitchCase[] rules =
        [
            new("/count", "gt", "100", "ask"),
            new("/count", "gt", "500", "auto"),
        ];
        var router = new FixedExecutor("router", """{"count":900}""");
        var ask = new FixedExecutor("ask", "ask");
        var auto = new FixedExecutor("auto", "auto");
        var builder = new WorkflowBuilder(router);
        builder.AddSwitch(router, switchBuilder =>
        {
            for (int index = 0; index < rules.Length; index++)
            {
                GatewayWorkflowSwitchCase rule = rules[index];
                var edge = new FixedExecutor($"edge-{index}", "edge");
                ExecutorBinding target = rule.GoTo == "ask" ? ask : auto;
                builder.AddEdge(edge, target, WorkflowDiagramText.Condition(rule), idempotent: false);
                switchBuilder.AddCase<string>(
                    payload => payload is not null && WorkflowSwitch.Predicate(rule)(payload),
                    [edge]);
            }
        });
        Workflow workflow = builder.WithOutputFrom([ask, auto]).Build();

        string? output = null;
        await using StreamingRun run = await InProcessExecution.Default.RunStreamingAsync(
            workflow,
            "ignored",
            $"switch-order-{Guid.NewGuid():N}",
            TestContext.Current.CancellationToken);
        await foreach (WorkflowEvent evt in run.WatchStreamAsync(
            blockOnPendingRequest: false,
            TestContext.Current.CancellationToken))
        {
            if (evt is WorkflowOutputEvent yielded && yielded.Is<string>(out string? text))
            {
                output = text;
            }
        }

        Assert.Equal("ask", output);
    }

    [Fact]
    public void DefinitionRejectsBackJumpCyclesUsingComposeTransitions()
    {
        WorkflowNodeDefinition start = DefinitionStep("start");
        WorkflowNodeDefinition back = DefinitionStep("back", defaultGoTo: "start");
        WorkflowDefinitionDraft cyclic = Definition(start, back);

        WorkflowSchemaException error = Assert.Throws<WorkflowSchemaException>(
            () => WorkflowDefinitionValidator.Validate(cyclic));

        Assert.Equal(
            "Workflow paths can't loop; step 'back' leads back into a step that comes before it on the same path.",
            error.Message);
    }

    [Fact]
    public void DefinitionDoesNotTreatCutFallthroughAsACycle()
    {
        WorkflowDefinitionDraft fixture = Definition(
            DefinitionStep(
                "review",
                [new GatewayWorkflowSwitchCase("/count", "gt", "500", "ask")],
                defaultGoTo: "file"),
            DefinitionStep("file"),
            DefinitionStep("ask", defaultGoTo: "file"));

        WorkflowDefinitionValidator.Validate(fixture);
    }

    [Fact]
    public void SwitchLintRequiresDefaultAndKnownOp()
    {
        JsonObject schema = ScalarObjectSchema();
        WorkflowNodeDefinition assess = new(
            "assess",
            "agent-a",
            "Score",
            WorkflowOutputKind.Schema,
            schema,
            [new WorkflowStateMap("/title", "/title")],
            WorkflowSideEffects.None,
            false,
            false,
            0,
            [new GatewayWorkflowSwitchCase("/title", "eq", "x", "done")],
            DefaultGoTo: null);
        WorkflowNodeDefinition done = assess with { NodeId = "done", SwitchCases = null, DefaultGoTo = null };
        WorkflowDefinitionDraft draft = new("wf-1", "Switch", false, schema, schema, schema, [assess, done]);
        Assert.Throws<WorkflowSchemaException>(() => WorkflowDefinitionValidator.Validate(draft));
    }

    [Fact]
    public void StructuredOutputRetrySkipsExternalAndHonoursMaxRetries()
    {
        Assert.True(WorkflowRetryPolicy.CanRetryStructuredOutput("none", 1, 1));
        Assert.False(WorkflowRetryPolicy.CanRetryStructuredOutput("none", 2, 1));
        Assert.False(WorkflowRetryPolicy.CanRetryStructuredOutput("none", 1, 0));
        Assert.False(WorkflowRetryPolicy.CanRetryStructuredOutput("external", 1, 3));
        Assert.True(WorkflowRetryPolicy.CanRetryStructuredOutput("idempotent", 1, 1));
    }

    [Fact]
    public void ChangeRequestsAndStructuredOutputRetriesHaveSeparateAllowances()
    {
        GatewayWorkflowStepAttempt Attempt(int number, string status) => new(
            $"attempt-{number}",
            "instance-1",
            "node-1",
            number,
            $"run-{number}",
            null,
            status,
            null,
            null,
            null,
            "2026-10-25T00:00:00Z");

        // A malformed first reply can retry, then finish. That auto-retry does not use an owner request.
        GatewayWorkflowStepAttempt[] autoRetriedAndFinished =
            [Attempt(1, WorkflowStepAttemptStatuses.Failed), Attempt(2, WorkflowStepAttemptStatuses.Finished)];
        Assert.Equal(0, WorkflowRetryPolicy.CountFailuresSinceLastFinish(autoRetriedAndFinished));
        Assert.True(WorkflowRetryPolicy.CanRequestChanges(
            autoRetriedAndFinished.Count(step => step.Status == WorkflowStepAttemptStatuses.Finished)));

        // After an owner request, the next malformed reply still gets one automatic retry.
        GatewayWorkflowStepAttempt[] changeThenMalformed =
        [
            Attempt(1, WorkflowStepAttemptStatuses.Finished),
            Attempt(2, WorkflowStepAttemptStatuses.Failed),
        ];
        int failuresAfterReview = WorkflowRetryPolicy.CountFailuresSinceLastFinish(changeThenMalformed);
        Assert.Equal(1, failuresAfterReview);
        Assert.True(WorkflowRetryPolicy.CanRetryStructuredOutput("none", failuresAfterReview, maxRetries: 1));

        // The retry's next malformed reply uses that same automatic allowance, not another owner allowance.
        changeThenMalformed =
        [
            Attempt(1, WorkflowStepAttemptStatuses.Finished),
            Attempt(2, WorkflowStepAttemptStatuses.Failed),
            Attempt(3, WorkflowStepAttemptStatuses.Failed),
        ];
        failuresAfterReview = WorkflowRetryPolicy.CountFailuresSinceLastFinish(changeThenMalformed);
        Assert.Equal(2, failuresAfterReview);
        Assert.False(WorkflowRetryPolicy.CanRetryStructuredOutput("none", failuresAfterReview, maxRetries: 1));
        Assert.True(WorkflowRetryPolicy.CanRequestChanges(finishedAttempts: 1));
        Assert.True(WorkflowRetryPolicy.CanRequestChanges(finishedAttempts: 2));
        Assert.Equal(2, WorkflowRetryPolicy.RemainingChangeRequests(finishedAttempts: 1));
        Assert.Equal(1, WorkflowRetryPolicy.RemainingChangeRequests(finishedAttempts: 2));
        Assert.Equal(0, WorkflowRetryPolicy.RemainingChangeRequests(finishedAttempts: 3));

        Assert.False(WorkflowRetryPolicy.CanRequestChanges(finishedAttempts: 3));
        Assert.Equal(
            "You've asked for changes twice. Approve or stop this run.",
            WorkflowRetryPolicy.ReviewFailureMessage(WorkflowFailureCodes.GateChangesExhausted));
    }

    [Fact]
    public void DiagramNamesStepsPausesAndThePlaceARunStarts()
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["agent-1"] = "Roller",
            ["agent-2"] = "My Assistant",
        };
        string diagram = WorkflowMafGraphFactory.Diagram(
            [
                Step("prepare", "agent-1", review: true),
                Step("lodge", "agent-2", review: true),
            ],
            names);

        Assert.Contains("Step 1 · Roller (starts here)", diagram, StringComparison.Ordinal);
        Assert.Contains("Pause after step 1", diagram, StringComparison.Ordinal);
        Assert.Contains("Step 2 · My Assistant", diagram, StringComparison.Ordinal);
        Assert.Contains("Pause after step 2", diagram, StringComparison.Ordinal);
        Assert.DoesNotContain("edge-", diagram, StringComparison.Ordinal);
        Assert.DoesNotContain("conditional", diagram, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagramWritesTheChoiceOnTheArrow()
    {
        string diagram = WorkflowMafGraphFactory.Diagram(
            [
                Step(
                    "assess",
                    "agent-1",
                    review: false,
                    cases: [new("/decision", "eq", "proceed", "proceed"), new("/decision", "eq", "refer", "refer")],
                    defaultGoTo: "decline"),
                Step("proceed", "agent-1", review: true),
                Step("refer", "agent-1", review: true),
                Step("decline", "agent-1", review: false),
            ],
            agentNames: null);

        Assert.Contains("Step 1 (starts here)", diagram, StringComparison.Ordinal);
        Assert.Contains("decision is proceed", diagram, StringComparison.Ordinal);
        Assert.Contains("decision is refer", diagram, StringComparison.Ordinal);
        Assert.Contains("Otherwise", diagram, StringComparison.Ordinal);
        Assert.Contains("Pause after step 2", diagram, StringComparison.Ordinal);
        Assert.Contains("Pause after step 3", diagram, StringComparison.Ordinal);
        Assert.Contains("%% choice ", diagram, StringComparison.Ordinal);
        Assert.Contains("edge-assess-decline-2", diagram, StringComparison.Ordinal);
        foreach (string line in diagram.Split('\n'))
        {
            if (line.TrimStart().StartsWith("%%", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.DoesNotContain("edge-", line, StringComparison.Ordinal);
            Assert.DoesNotContain("edge_", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DiagramNumbersStepsInFlowOrderWhenSavedOrderDiffers()
    {
        string diagram = WorkflowMafGraphFactory.Diagram(
            [
                Step(
                    "root",
                    "agent-1",
                    review: false,
                    cases: [new("/decision", "eq", "first", "branch")],
                    defaultGoTo: "otherwise"),
                Step("leaf", "agent-1", review: false),
                Step("otherwise", "agent-1", review: false, defaultGoTo: "leaf"),
                Step("branch", "agent-1", review: false, defaultGoTo: "leaf"),
            ],
            agentNames: null);

        Assert.Contains("Step 1 (starts here)", diagram, StringComparison.Ordinal);
        Assert.Contains("Step 2\"]", diagram, StringComparison.Ordinal);
        Assert.Contains("Step 3\"]", diagram, StringComparison.Ordinal);
        Assert.Contains("Step 4\"]", diagram, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagramShowsChoiceBranchesJoiningThroughContinuations()
    {
        string diagram = WorkflowMafGraphFactory.Diagram(
            [
                Step(
                    "review",
                    "agent-1",
                    review: false,
                    cases: [new("/amount", "gt", "500", "ask")],
                    defaultGoTo: "auto"),
                Step("ask", "agent-1", review: false, defaultGoTo: "file"),
                Step("auto", "agent-1", review: false, defaultGoTo: "file"),
                Step("file", "agent-1", review: false),
            ],
            agentNames: null);

        string[] lines = diagram.Split('\n');
        string fileBox = Assert.Single(
            lines,
            line => line.Contains("[\"Step 4\"],", StringComparison.Ordinal)
                || line.Contains("[\"Step 4\"]", StringComparison.Ordinal));
        string fileAlias = fileBox.Trim().Split('[', 2)[0];
        string[] incoming = lines
            .Where(line => line.Contains(" --> ", StringComparison.Ordinal) && line.TrimEnd().EndsWith($"--> {fileAlias}", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(2, incoming.Length);
        Assert.All(incoming, line => Assert.DoesNotContain("Otherwise", line, StringComparison.Ordinal));
        Assert.DoesNotContain(
            lines.Where(line => !line.TrimStart().StartsWith("%%", StringComparison.Ordinal)),
            line => line.Contains("edge-", StringComparison.Ordinal));
    }

    [Fact]
    public void DiagramMatchesSharedComposeTransitionParityFixtures()
    {
        string fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "workflow-transition-parity.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(fixturePath));
        foreach (JsonElement fixture in document.RootElement.EnumerateArray())
        {
            JsonElement fixtureNodes = fixture.GetProperty("nodes");
            GatewayWorkflowNode[] nodes = fixtureNodes.EnumerateArray()
                .Select(node => Step(
                    node.GetProperty("nodeId").GetString()!,
                    "agent-1",
                    review: false,
                    node.TryGetProperty("switchCases", out JsonElement cases)
                        ? cases.EnumerateArray().Select(rule => new GatewayWorkflowSwitchCase(
                            rule.GetProperty("pointer").GetString()!,
                            rule.GetProperty("op").GetString()!,
                            rule.GetProperty("value").ValueKind == JsonValueKind.Null ? null : rule.GetProperty("value").GetString(),
                            rule.GetProperty("goTo").GetString()!)).ToArray()
                        : null,
                    node.TryGetProperty("defaultGoTo", out JsonElement defaultGoTo) ? defaultGoTo.GetString() : null))
                .ToArray();
            string diagram = WorkflowMafGraphFactory.Diagram(nodes, agentNames: null);
            var actual = diagram.Split('\n')
                .Select(line => System.Text.RegularExpressions.Regex.Match(
                    line,
                    @"^\s*(?<from>[A-Za-z0-9_-]+)\s+-->\s*(?:\|[^|]*\|\s*)?(?<to>[A-Za-z0-9_-]+)\s*$"))
                .Where(match => match.Success)
                .Select(match => (Source: match.Groups["from"].Value, Target: match.Groups["to"].Value))
                .ToHashSet();
            var expected = fixture.GetProperty("transitions").EnumerateArray()
                .Select(item => (Source: item.GetProperty("source").GetString()!, Target: item.GetProperty("target").GetString()!))
                .ToHashSet();

            Assert.True(expected.SetEquals(actual), $"Parity fixture '{fixture.GetProperty("name").GetString()}' differed. Diagram:\n{diagram}");
        }
    }

    private static GatewayWorkflowNode Step(
        string nodeId,
        string agentId,
        bool review,
        GatewayWorkflowSwitchCase[]? cases = null,
        string? defaultGoTo = null)
        => new(nodeId, agentId, "do the step", "schema", null, [], "none", false, review, 0, cases, defaultGoTo);

    private static WorkflowDefinitionDraft Definition(params WorkflowNodeDefinition[] nodes)
    {
        JsonObject schema = ScalarObjectSchema();
        return new WorkflowDefinitionDraft("wf-1", "Workflow", false, schema, schema, schema, nodes);
    }

    private static WorkflowNodeDefinition DefinitionStep(
        string nodeId,
        GatewayWorkflowSwitchCase[]? cases = null,
        string? defaultGoTo = null)
        => new(
            nodeId,
            "agent-1",
            "Do the step",
            WorkflowOutputKind.Schema,
            ScalarObjectSchema(),
            [],
            WorkflowSideEffects.None,
            MemoryWrites: false,
            RequiresReview: false,
            MaxRetries: 0,
            cases,
            defaultGoTo);

    private sealed class FixedExecutor : Executor<string, string>
    {
        private readonly string output;

        public FixedExecutor(string id, string output)
            : base(id, options: null, declareCrossRunShareable: true)
        {
            this.output = output;
        }

        public override ValueTask<string> HandleAsync(
            string message,
            IWorkflowContext context,
            CancellationToken cancellationToken)
        {
            _ = message;
            _ = context;
            _ = cancellationToken;
            return ValueTask.FromResult(output);
        }
    }

    private static JsonObject ScalarObjectSchema() => JsonNode.Parse("""
        {
          "type": "object",
          "required": ["title", "count"],
          "properties": {
            "title": { "type": "string" },
            "count": { "type": "integer" }
          }
        }
        """)!.AsObject();
}
