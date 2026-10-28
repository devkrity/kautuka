using System.Runtime.CompilerServices;

using Microsoft.Extensions.AI;

namespace Kautuka.IntegrationTests;

/// <summary>
/// Deterministic IChatClient that replays pre-recorded response turns.
/// Seeded from real AAAT parent-session shapes (e.g. roll a dN → invoke_agent_roller),
/// not live Ollama. Use this instead of sampling so journal maps stay stable.
/// </summary>
internal sealed class ScriptedOllamaChatClient : IChatClient
{
    private readonly Queue<IReadOnlyList<ChatResponseUpdate>> turns;
    private int completedTurns;

    public ScriptedOllamaChatClient(params IReadOnlyList<ChatResponseUpdate>[] scriptedTurns)
    {
        turns = new Queue<IReadOnlyList<ChatResponseUpdate>>(scriptedTurns);
    }

    public int CompletedTurns => completedTurns;

    public int RemainingTurns => turns.Count;

    public ChatClientMetadata Metadata { get; } = new("ollama-scripted", new Uri("http://127.0.0.1:11434"), "scripted");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Use GetStreamingResponseAsync for scripted Ollama mocks.");

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _ = chatMessages;
        _ = options;
        cancellationToken.ThrowIfCancellationRequested();

        if (turns.Count == 0)
        {
            throw new InvalidOperationException(
                "ScriptedOllamaChatClient has no remaining turns. " +
                "Seed the client with one turn per model call (function-call then post-tool text, etc.).");
        }

        IReadOnlyList<ChatResponseUpdate> turn = turns.Dequeue();
        completedTurns++;
        foreach (ChatResponseUpdate update in turn)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
            await Task.Yield();
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    /// <summary>
    /// Grounded in captured parent-session flow for user text like "roll a d44":
    /// model calls invoke_agent_roller, then after tool result, emits a short answer.
    /// </summary>
    public static ScriptedOllamaChatClient CreateRollerParentScript(string userDice = "d44", string toolCallId = "call_s1toof1p")
    {
        IReadOnlyList<ChatResponseUpdate> invokeTurn =
        [
            new ChatResponseUpdate
            {
                Contents =
                [
                    new FunctionCallContent(
                        toolCallId,
                        "invoke_agent_roller",
                        new Dictionary<string, object?> { ["message"] = userDice }),
                ],
            },
        ];

        IReadOnlyList<ChatResponseUpdate> answerTurn =
        [
            new ChatResponseUpdate { Contents = [new TextContent("You rolled ")] },
            new ChatResponseUpdate { Contents = [new TextContent("**17**")] },
            new ChatResponseUpdate { Contents = [new TextContent($" on a {userDice}.")] },
        ];

        return new ScriptedOllamaChatClient(invokeTurn, answerTurn);
    }

    /// <summary>
    /// Nested roller tool path seed: load_skill then request approval-shaped tool call args
    /// for run_skill_script (HITL on child), matching nested AAAT journal shape.
    /// </summary>
    public static ScriptedOllamaChatClient CreateRollerChildScript(
        string loadSkillCallId = "call_oacowgfm",
        string runScriptCallId = "call_x5yiobtr")
    {
        IReadOnlyList<ChatResponseUpdate> loadSkillTurn =
        [
            new ChatResponseUpdate
            {
                Contents =
                [
                    new FunctionCallContent(
                        loadSkillCallId,
                        "load_skill",
                        new Dictionary<string, object?> { ["skill"] = "roller" }),
                ],
            },
        ];

        IReadOnlyList<ChatResponseUpdate> runScriptTurn =
        [
            new ChatResponseUpdate
            {
                Contents =
                [
                    new FunctionCallContent(
                        runScriptCallId,
                        "run_skill_script",
                        new Dictionary<string, object?>
                        {
                            ["skill"] = "roller",
                            ["script"] = "roll",
                            ["args"] = new Dictionary<string, object?> { ["sides"] = 44 },
                        }),
                ],
            },
        ];

        IReadOnlyList<ChatResponseUpdate> childAnswerTurn =
        [
            new ChatResponseUpdate { Contents = [new TextContent("17")] },
        ];

        return new ScriptedOllamaChatClient(loadSkillTurn, runScriptTurn, childAnswerTurn);
    }
}
