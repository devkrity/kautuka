using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using Kautuka.Gateway.Domain.Execution;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

using OllamaSharp;

namespace Kautuka.IntegrationTests;

public sealed class PackedToolCallAttributionTests
{
    private const string ollamaBaseUrl = "http://127.0.0.1:11434/";
    private const string liveModel = "qwen3.8:latest";
    private const string packPrompt =
        "roll a d20 and fetch bbc.com and cnn.com. " +
        "These are independent. Call every required tool in this single response. " +
        "Do not wait for any tool result before emitting the other calls.";

    [Fact]
    public async Task NativeOllamaChatPacksIndependentToolCalls()
    {
        if (!await OllamaIsReachableAsync().ConfigureAwait(true))
        {
            Assert.Skip("Ollama is not running at http://127.0.0.1:11434.");
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        using var request = new HttpRequestMessage(HttpMethod.Post, ollamaBaseUrl + "api/chat")
        {
            Content = JsonContent(BuildNativeChatBody(think: true)),
        };

        string body = await SendLiveChatRequestAsync(http, request).ConfigureAwait(true);
        NativeToolCallCapture.Append("native-http", body);

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement[] toolCalls = ReadToolCalls(document.RootElement);
        string[] names = [.. toolCalls.Select(ReadToolName)];

        Assert.Equal(3, toolCalls.Length);
        Assert.Equal(
            ["invoke_agent_roller", "kautuka_web_fetch", "kautuka_web_fetch"],
            names);
    }

    [Fact]
    public async Task OllamaSharpMapsEveryNativeToolCallToFunctionCallContent()
    {
        const string packedBody = """
            {"model":"qwen3.8:latest","created_at":"2026-01-01T00:00:00Z","message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"invoke_agent_roller","arguments":{"message":"Roll a d20"}}},{"function":{"name":"kautuka_web_fetch","arguments":{"url":"https://www.bbc.com"}}},{"function":{"name":"kautuka_web_fetch","arguments":{"url":"https://www.cnn.com"}}}]},"done":true,"done_reason":"stop"}
            """;
        var handler = new StaticChatHandler(packedBody);
#pragma warning disable CA2000
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://ollama.test/") };
#pragma warning restore CA2000
        var client = new OllamaApiClient(http, "qwen3.8:latest");

        List<FunctionCallContent> mapped = [];
        await foreach (ChatResponseUpdate update in ((IChatClient)client).GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, packPrompt)],
            new ChatOptions { AllowMultipleToolCalls = true },
            TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            mapped.AddRange(update.Contents.OfType<FunctionCallContent>());
        }

        string[] names = [.. mapped.Select(static call => call.Name)];
        Assert.Equal(
            ["invoke_agent_roller", "kautuka_web_fetch", "kautuka_web_fetch"],
            names);
    }

    [Fact]
    public async Task OllamaSharpMapsOneToolCallPerStreamingChunk()
    {
        const string streamedBody = """
            {"model":"qwen3.8:latest","message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"invoke_agent_roller","arguments":{"message":"Roll a d20"}}}]},"done":false}
            {"model":"qwen3.8:latest","message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"kautuka_web_fetch","arguments":{"url":"https://www.bbc.com"}}}]},"done":false}
            {"model":"qwen3.8:latest","message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"kautuka_web_fetch","arguments":{"url":"https://www.cnn.com"}}}]},"done":false}
            {"model":"qwen3.8:latest","message":{"role":"assistant","content":""},"done":true,"done_reason":"stop"}
            """;
        var handler = new StaticChatHandler(streamedBody);
#pragma warning disable CA2000
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://ollama.test/") };
#pragma warning restore CA2000
        var client = new OllamaApiClient(http, "qwen3.8:latest");

        List<FunctionCallContent> mapped = [];
        await foreach (ChatResponseUpdate update in ((IChatClient)client).GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, packPrompt)],
            new ChatOptions { AllowMultipleToolCalls = true },
            TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            mapped.AddRange(update.Contents.OfType<FunctionCallContent>());
        }

        Assert.Equal(
            ["invoke_agent_roller", "kautuka_web_fetch", "kautuka_web_fetch"],
            mapped.Select(static call => call.Name).ToArray());
    }

    [Fact]
    public async Task OllamaSharpRequestDoesNotSendParallelToolCallsFlag()
    {
        var handler = new StaticChatHandler(
            """{"model":"qwen3.8:latest","created_at":"2026-01-01T00:00:00Z","message":{"role":"assistant","content":"ok"},"done":true}""");
#pragma warning disable CA2000
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://ollama.test/") };
#pragma warning restore CA2000
        var client = new OllamaApiClient(http, "qwen3.8:latest");

        _ = await ((IChatClient)client).GetResponseAsync(
            [new ChatMessage(ChatRole.User, packPrompt)],
            new ChatOptions
            {
                AllowMultipleToolCalls = true,
                Tools = [CreateTool("invoke_agent_roller"), CreateTool("kautuka_web_fetch")],
                ToolMode = ChatToolMode.Auto,
            },
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(string.IsNullOrWhiteSpace(handler.LastRequestBody));
        Assert.DoesNotContain("parallel_tool_calls", handler.LastRequestBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("allowMultipleToolCalls", handler.LastRequestBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"tools\"", handler.LastRequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MafApprovalRequiredAgentEmitsTarcForEveryPackedFunctionCall()
    {
        using var inner = new ScriptedPackChatClient(
        [
            new FunctionCallContent("1", "do_a", new Dictionary<string, object?>()),
            new FunctionCallContent("2", "do_b", new Dictionary<string, object?>()),
            new FunctionCallContent("3", "do_c", new Dictionary<string, object?>()),
        ]);

        ChatClientAgent agent = inner.AsAIAgent(new ChatClientAgentOptions
        {
            Id = "pack-test",
            ChatOptions = new ChatOptions
            {
                Tools =
                [
                    new ApprovalRequiredAIFunction(CreateTool("do_a")),
                    new ApprovalRequiredAIFunction(CreateTool("do_b")),
                    new ApprovalRequiredAIFunction(CreateTool("do_c")),
                ],
                ToolMode = ChatToolMode.Auto,
                AllowMultipleToolCalls = true,
            },
            AllowConcurrentInvocation = true,
        });

        List<ToolApprovalRequestContent> tarcs = [];
        await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(
            "do all three",
            cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            tarcs.AddRange(update.Contents.OfType<ToolApprovalRequestContent>());
        }

        string[] names =
        [
            .. tarcs.Select(static tarc => tarc.ToolCall is FunctionCallContent functionCall
                ? functionCall.Name
                : tarc.RequestId),
        ];
        Assert.Equal(["do_a", "do_b", "do_c"], names);
    }

    [Fact]
    public async Task KautukaPackedTarcCollectionProjectsEveryPackedItem()
    {
        async IAsyncEnumerable<string> packedApprovals(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return "tarc-roller";
            yield return "tarc-bbc";
            yield return "tarc-cnn";
            await Task.CompletedTask.ConfigureAwait(false);
        }

        bool boundaryReached = false;
        List<string> projected = [];
        await foreach (string item in NativeApprovalBoundaryStreamDrainer.DrainAfterBoundaryAsync(
            packedApprovals(TestContext.Current.CancellationToken),
            () => boundaryReached,
            TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            projected.Add(item);
        }

        Assert.Equal(["tarc-roller", "tarc-bbc", "tarc-cnn"], projected);
    }

    [Fact]
    public async Task NestedHitlPauseDoesNotDrainSiblingPackedInvokes()
    {
        async IAsyncEnumerable<string> packedInvokes(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return "nested-hitl-pause";
            yield return "sibling-fetch-bbc";
            yield return "sibling-fetch-cnn";
            await Task.CompletedTask.ConfigureAwait(false);
        }

        bool stopAfterAgentToolApprovalBoundary = false;
        List<string> projected = [];
        await foreach (string item in NativeApprovalBoundaryStreamDrainer.DrainAfterBoundaryAsync(
            packedInvokes(TestContext.Current.CancellationToken),
            () => stopAfterAgentToolApprovalBoundary,
            TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            projected.Add(item);
            // Nested HITL pauses the child wait without flipping the packed-turn drain flag.
        }

        Assert.Equal(["nested-hitl-pause", "sibling-fetch-bbc", "sibling-fetch-cnn"], projected);
    }

    [Fact]
    public async Task FiccAllHitlPackInvokesEveryToolAfterPackedResponses()
    {
        var invokes = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        using var inner = new ScriptedPackChatClient(
        [
            new FunctionCallContent("1", "do_a", new Dictionary<string, object?>()),
            new FunctionCallContent("2", "do_b", new Dictionary<string, object?>()),
            new FunctionCallContent("3", "do_c", new Dictionary<string, object?>()),
        ]);

        ChatClientAgent agent = inner.AsAIAgent(new ChatClientAgentOptions
        {
            Id = "pack-all-hitl",
            ChatOptions = new ChatOptions
            {
                Tools =
                [
                    new ApprovalRequiredAIFunction(CreateCountingTool("do_a", invokes)),
                    new ApprovalRequiredAIFunction(CreateCountingTool("do_b", invokes)),
                    new ApprovalRequiredAIFunction(CreateCountingTool("do_c", invokes)),
                ],
                ToolMode = ChatToolMode.Auto,
                AllowMultipleToolCalls = true,
            },
            AllowConcurrentInvocation = true,
        });

        AgentSession session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        List<ToolApprovalRequestContent> tarcs = [];
        await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(
            "do all three",
            session,
            cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            tarcs.AddRange(update.Contents.OfType<ToolApprovalRequestContent>());
        }

        Assert.Equal(3, tarcs.Count);
        Assert.Empty(invokes);

        ChatMessage packedResponses = new(
            ChatRole.Tool,
            [.. tarcs.Select(static tarc => (AIContent)new ToolApprovalResponseContent(
                tarc.RequestId,
                approved: true,
                tarc.ToolCall))]);
        await foreach (AgentResponseUpdate _ in agent.RunStreamingAsync(
            [packedResponses],
            session,
            cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
        }

        Assert.Equal(1, invokes.GetValueOrDefault("do_a"));
        Assert.Equal(1, invokes.GetValueOrDefault("do_b"));
        Assert.Equal(1, invokes.GetValueOrDefault("do_c"));
        Assert.Equal(1, inner.PackedCallGenerations);
    }

    [Fact]
    public async Task FiccMixedPackInvokesUnwrappedSiblingsAndHitlAfterOneApproval()
    {
        var invokes = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        using var inner = new ScriptedPackChatClient(
        [
            new FunctionCallContent("1", "invoke_agent_roller", new Dictionary<string, object?>()),
            new FunctionCallContent("2", "kautuka_web_fetch", new Dictionary<string, object?> { ["url"] = "https://www.bbc.com" }),
            new FunctionCallContent("3", "kautuka_web_fetch", new Dictionary<string, object?> { ["url"] = "https://www.cnn.com" }),
        ]);

        ChatClientAgent agent = inner.AsAIAgent(new ChatClientAgentOptions
        {
            Id = "pack-mixed",
            ChatOptions = new ChatOptions
            {
                Tools =
                [
                    new ApprovalRequiredAIFunction(CreateCountingTool("invoke_agent_roller", invokes)),
                    CreateCountingTool("kautuka_web_fetch", invokes),
                ],
                ToolMode = ChatToolMode.Auto,
                AllowMultipleToolCalls = true,
            },
            AllowConcurrentInvocation = true,
            DisableApprovalNotRequiredFunctionBypassing = false,
        });

        AgentSession session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        List<ToolApprovalRequestContent> tarcs = [];
        await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(
            packPrompt,
            session,
            cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            tarcs.AddRange(update.Contents.OfType<ToolApprovalRequestContent>());
        }

        ToolApprovalRequestContent rollerTarc = Assert.Single(
            tarcs,
            static tarc => tarc.ToolCall is FunctionCallContent call
                && string.Equals(call.Name, "invoke_agent_roller", StringComparison.Ordinal));

        ChatMessage packedResponses = new(
            ChatRole.Tool,
            [new ToolApprovalResponseContent(rollerTarc.RequestId, approved: true, rollerTarc.ToolCall)]);
        await foreach (AgentResponseUpdate _ in agent.RunStreamingAsync(
            [packedResponses],
            session,
            cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
        }

        Assert.Equal(1, invokes.GetValueOrDefault("invoke_agent_roller"));
        Assert.Equal(2, invokes.GetValueOrDefault("kautuka_web_fetch"));
        Assert.Equal(1, inner.PackedCallGenerations);
    }

    [Fact]
    public async Task FiccMixedPackDoesNotExecuteUnwrappedSiblingsBeforeHitlWhenBypassDisabled()
    {
        var invokes = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        using var inner = new ScriptedPackChatClient(
        [
            new FunctionCallContent("1", "invoke_agent_roller", new Dictionary<string, object?>()),
            new FunctionCallContent("2", "kautuka_web_fetch", new Dictionary<string, object?> { ["url"] = "https://www.bbc.com" }),
            new FunctionCallContent("3", "kautuka_web_fetch", new Dictionary<string, object?> { ["url"] = "https://www.cnn.com" }),
        ]);

        ChatClientAgent agent = inner.AsAIAgent(new ChatClientAgentOptions
        {
            Id = "pack-mixed-no-bypass",
            ChatOptions = new ChatOptions
            {
                Tools =
                [
                    new ApprovalRequiredAIFunction(CreateCountingTool("invoke_agent_roller", invokes)),
                    CreateCountingTool("kautuka_web_fetch", invokes),
                ],
                ToolMode = ChatToolMode.Auto,
                AllowMultipleToolCalls = true,
            },
            AllowConcurrentInvocation = true,
            DisableApprovalNotRequiredFunctionBypassing = true,
        });

        AgentSession session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        List<ToolApprovalRequestContent> tarcs = [];
        await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(
            packPrompt,
            session,
            cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            tarcs.AddRange(update.Contents.OfType<ToolApprovalRequestContent>());
        }

        Assert.Equal(0, invokes.GetValueOrDefault("kautuka_web_fetch"));
        Assert.Contains(
            tarcs,
            static tarc => tarc.ToolCall is FunctionCallContent call
                && string.Equals(call.Name, "kautuka_web_fetch", StringComparison.Ordinal));
        Assert.Contains(
            tarcs,
            static tarc => tarc.ToolCall is FunctionCallContent call
                && string.Equals(call.Name, "invoke_agent_roller", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LiveOllamaSharpUniqueToolCallsMatchNativeResponse()
    {
        if (!await OllamaIsReachableAsync().ConfigureAwait(true))
        {
            Assert.Skip("Ollama is not running at http://127.0.0.1:11434.");
        }

        using var http = new HttpClient { BaseAddress = new Uri(ollamaBaseUrl), Timeout = TimeSpan.FromMinutes(3) };
        var client = new OllamaApiClient(http, liveModel);

        List<string> mapped = [];
        int streamed = 0;
        try
        {
            await foreach (ChatResponseUpdate update in ((IChatClient)client).GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, packPrompt)],
                new ChatOptions
                {
                    AllowMultipleToolCalls = true,
                    Tools = [CreateTool("invoke_agent_roller"), CreateTool("kautuka_web_fetch")],
                    ToolMode = ChatToolMode.Auto,
                },
                TestContext.Current.CancellationToken).ConfigureAwait(true))
            {
                foreach (FunctionCallContent call in update.Contents.OfType<FunctionCallContent>())
                {
                    streamed++;
                    string args = string.Join(",", call.Arguments?.OrderBy(static pair => pair.Key).Select(static pair => pair.Key + "=" + pair.Value) ?? []);
                    mapped.Add($"{call.Name} {args} id={call.CallId}");
                }
            }
        }
        catch (HttpRequestException ex) when (ex.InnerException is SocketException)
        {
            Assert.Skip($"Ollama refused the live streaming request: {ex.Message}");
        }

        NativeToolCallCapture.Append(
            "ollamasharp-live",
            $"streamed={streamed}; mapped={string.Join(" || ", mapped)}");

        if (mapped.Count < 3)
        {
            Assert.Skip("OllamaSharp mapped fewer than the native packed 3 tools: " + string.Join(" || ", mapped));
        }
    }

    private static AIFunction CreateTool(string name)
        => AIFunctionFactory.Create(static () => "ok", name);

    private static AIFunction CreateCountingTool(string name, ConcurrentDictionary<string, int> invokes)
        => AIFunctionFactory.Create(
            () =>
            {
                invokes.AddOrUpdate(name, 1, static (_, count) => count + 1);
                return "ok";
            },
            name);

    private static StringContent JsonContent(string json)
        => new(json, Encoding.UTF8, "application/json");

    private static string BuildNativeChatBody(bool think)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = liveModel,
            ["stream"] = false,
            ["think"] = think,
            ["messages"] = new object[] { new Dictionary<string, string> { ["role"] = "user", ["content"] = packPrompt } },
            ["tools"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object?>
                    {
                        ["name"] = "invoke_agent_roller",
                        ["description"] = "Ask the Roller agent to roll dice.",
                        ["parameters"] = new Dictionary<string, object?>
                        {
                            ["type"] = "object",
                            ["properties"] = new Dictionary<string, object?>
                            {
                                ["message"] = new Dictionary<string, string> { ["type"] = "string" },
                            },
                            ["required"] = new[] { "message" },
                        },
                    },
                },
                new Dictionary<string, object?>
                {
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object?>
                    {
                        ["name"] = "kautuka_web_fetch",
                        ["description"] = "Fetch a web page.",
                        ["parameters"] = new Dictionary<string, object?>
                        {
                            ["type"] = "object",
                            ["properties"] = new Dictionary<string, object?>
                            {
                                ["url"] = new Dictionary<string, string> { ["type"] = "string" },
                            },
                            ["required"] = new[] { "url" },
                        },
                    },
                },
            },
        };
        return JsonSerializer.Serialize(payload);
    }

    private static JsonElement[] ReadToolCalls(JsonElement root)
    {
        if (!root.TryGetProperty("message", out JsonElement message)
            || !message.TryGetProperty("tool_calls", out JsonElement toolCalls)
            || toolCalls.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. toolCalls.EnumerateArray()];
    }

    private static string ReadToolName(JsonElement toolCall)
    {
        if (toolCall.TryGetProperty("function", out JsonElement function)
            && function.TryGetProperty("name", out JsonElement name))
        {
            return name.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static async Task<string> SendLiveChatRequestAsync(HttpClient http, HttpRequestMessage request)
    {
        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
        }
        catch (HttpRequestException ex) when (ex.InnerException is SocketException)
        {
            Assert.Skip($"Ollama refused the live generation request: {ex.Message}");
            throw;
        }
    }

    private static async Task<bool> OllamaIsReachableAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using HttpResponseMessage response = await http.GetAsync(new Uri(ollamaBaseUrl + "api/tags")).ConfigureAwait(true);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    private static string Truncate(string value)
        => value.Length <= 800 ? value : value[..800];

    private sealed class StaticChatHandler(string responseBody) : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true);
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/x-ndjson"),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");
            return response;
        }
    }

    private sealed class ScriptedPackChatClient(IReadOnlyList<FunctionCallContent> packedCalls) : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("scripted-pack", null, null);

        public int PackedCallGenerations { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ChatMessage assistant = CreateAssistantMessage(messages);
            return Task.FromResult(new ChatResponse([assistant]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            ChatMessage assistant = CreateAssistantMessage(messages);
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [.. assistant.Contents],
            };
        }

        public object? GetService(Type serviceType, object? key = null) => null;

        public void Dispose()
        {
        }

        private ChatMessage CreateAssistantMessage(IEnumerable<ChatMessage> messages)
        {
            bool resumeOrToolResults = messages
                .SelectMany(static message => message.Contents)
                .Any(static content => content is ToolApprovalResponseContent or FunctionResultContent);
            if (resumeOrToolResults)
            {
                return new ChatMessage(ChatRole.Assistant, "done");
            }

            PackedCallGenerations++;
            return new ChatMessage(ChatRole.Assistant, [.. packedCalls]);
        }
    }
}

internal static class NativeToolCallCapture
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Proof capture must not fail the test.")]
    public static void Append(string label, string payload)
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".kautuka",
                "runtime");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "ollama-pack-proof.jsonl"),
                JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, label, payload = Truncate(payload) })
                    + Environment.NewLine);
        }
        catch (Exception)
        {
        }
    }

    private static string Truncate(string value)
        => value.Length <= 4000 ? value : value[..4000];
}
