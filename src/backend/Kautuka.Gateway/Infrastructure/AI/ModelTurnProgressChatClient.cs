using System.Globalization;
using System.Runtime.CompilerServices;

using Kautuka.Gateway.Domain.Execution;

using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Infrastructure.AI;

/// <summary>
/// Emits one Narad agent-turn progress fact per inner <see cref="IChatClient"/>
/// request. Must sit inside MAF function-invocation middleware so HITL conversion
/// happens after the generation completes.
/// </summary>
internal sealed class ModelTurnProgressChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    private const string captureEnvName = "KAUTUKA_CAPTURE_MODEL_TOOL_CALLS";

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!ModelTurnProgressContext.IsActive)
        {
            return await base.GetResponseAsync(chatMessages, options, cancellationToken).ConfigureAwait(false);
        }

        await ModelTurnProgressContext.RecordTurnStartedAsync(cancellationToken).ConfigureAwait(false);
        string status = "failed";
        var stats = new ToolCallStats();
        try
        {
            ChatResponse response = await base.GetResponseAsync(chatMessages, options, cancellationToken)
                .ConfigureAwait(false);
            stats.Observe(response.Messages.SelectMany(static message => message.Contents));
            stats.ObserveUsage(response.Usage);
            status = "completed";
            return response;
        }
        catch (OperationCanceledException)
        {
            status = "cancelled";
            throw;
        }
        finally
        {
            CaptureIfEnabled(stats, status);
            await ModelTurnProgressContext.RecordTurnFinishedAsync(
                    status,
                    stats.ToSnapshot(),
                    stats.TokenIn,
                    stats.TokenOut,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!ModelTurnProgressContext.IsActive)
        {
            await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(
                chatMessages, options, cancellationToken).ConfigureAwait(false))
            {
                yield return update;
            }

            yield break;
        }

        await ModelTurnProgressContext.RecordTurnStartedAsync(cancellationToken).ConfigureAwait(false);
        string status = "failed";
        var stats = new ToolCallStats();
        try
        {
            await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(
                chatMessages, options, cancellationToken).ConfigureAwait(false))
            {
                stats.Observe(update.Contents);
                yield return update;
            }

            status = "completed";
        }
        finally
        {
            if (status != "completed")
            {
                status = cancellationToken.IsCancellationRequested ? "cancelled" : "failed";
            }

            CaptureIfEnabled(stats, status);
            await ModelTurnProgressContext.RecordTurnFinishedAsync(
                    status,
                    stats.ToSnapshot(),
                    stats.TokenIn,
                    stats.TokenOut,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Optional diagnostic capture must not fail the model turn.")]
    private static void CaptureIfEnabled(ToolCallStats stats, string status)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(captureEnvName), "1", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".kautuka",
                "runtime");
            Directory.CreateDirectory(directory);
            ModelTurnToolCallSnapshot snapshot = stats.ToSnapshot();
            string unique = string.Join(" | ", snapshot.UniqueToolSummaries);
            string line = string.Create(
                CultureInfo.InvariantCulture,
                $"{DateTimeOffset.UtcNow:O}\t{status}\tstreamed={snapshot.StreamedCount}\tunique={unique}");
            File.AppendAllText(
                Path.Combine(directory, "model-tool-calls.log"),
                line + Environment.NewLine);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
        }
    }

    private sealed class ToolCallStats
    {
        private readonly List<string> uniqueSummaries = [];
        private readonly HashSet<string> uniqueKeys = new(StringComparer.Ordinal);

        public int StreamedCount { get; private set; }

        public int TokenIn { get; private set; }

        public int TokenOut { get; private set; }

        public void Observe(IEnumerable<AIContent> contents)
        {
            foreach (AIContent content in contents)
            {
                if (content is UsageContent usage)
                {
                    ObserveUsage(usage.Details);
                    continue;
                }

                if (content is not FunctionCallContent functionCall)
                {
                    continue;
                }

                StreamedCount++;
                string args = SerializeArgs(functionCall.Arguments);
                string key = functionCall.Name + "\n" + args;
                if (uniqueKeys.Add(key))
                {
                    uniqueSummaries.Add(string.IsNullOrWhiteSpace(args) || args == "{}"
                        ? functionCall.Name
                        : functionCall.Name + " " + args);
                }
            }
        }

        public void ObserveUsage(UsageDetails? usage)
        {
            if (usage is null)
            {
                return;
            }

            if (usage.InputTokenCount is long input)
            {
                TokenIn = (int)input;
            }

            if (usage.OutputTokenCount is long output)
            {
                TokenOut = (int)output;
            }
        }

        public ModelTurnToolCallSnapshot ToSnapshot()
            => new(StreamedCount, uniqueSummaries.ToArray());

        private static string SerializeArgs(IDictionary<string, object?>? arguments)
        {
            if (arguments is null || arguments.Count == 0)
            {
                return "{}";
            }

            return string.Join(
                ",",
                arguments
                    .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                    .Select(static pair => pair.Key + "=" + Convert.ToString(pair.Value, CultureInfo.InvariantCulture)));
        }
    }
}
