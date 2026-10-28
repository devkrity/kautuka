using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Execution;

internal interface IModelTurnOrdinalCounter
{
    int Next(string runId);
}

/// <summary>
/// Process-wide ordinals so HITL continuation <c>Begin()</c> does not reuse
/// <c>agent-turn:{runId}:1</c> and overwrite the first invoke in the live reducer.
/// </summary>
internal sealed class ModelTurnOrdinalCounter : IModelTurnOrdinalCounter
{
    private readonly ConcurrentDictionary<string, int> ordinals = new(StringComparer.Ordinal);

    public int Next(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        return ordinals.AddOrUpdate(runId, 1, static (_, current) => current + 1);
    }
}

/// <summary>
/// Ambient publisher so the long-lived chat client can emit Narad
/// <c>progress.recorded</c> facts for each provider generation without taking a
/// run-scoped writer in <see cref="AgentRuntimeFactory"/>.
/// </summary>
internal static class ModelTurnProgressContext
{
    private static readonly AsyncLocal<Scope?> current = new();

    public static IDisposable Begin(
        IRuntimeEventPublisher publisher,
        NaradEventContext context,
        ChannelWriter<NaradPersistedEvent>? streamWriter = null,
        IModelTurnOrdinalCounter? ordinalCounter = null)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(context);
        Scope? prior = current.Value;
        current.Value = new Scope(publisher, context, streamWriter, ordinalCounter);
        return new Restore(prior);
    }

    public static bool IsActive => current.Value is not null;

    public static async Task RecordTurnStartedAsync(CancellationToken cancellationToken)
    {
        Scope? scope = current.Value;
        if (scope is null)
        {
            return;
        }

        int ordinal = scope.NextOrdinal();
        string progressId = BuildProgressId(scope.Context.RunId, ordinal);
        scope.ActiveProgressId = progressId;
        scope.ActiveOrdinal = ordinal;
        await scope.PublishAsync(
            progressId,
            BuildLabel(scope.Context, ordinal),
            "active",
            detail: null,
            usage: null,
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task RecordTurnFinishedAsync(
        string status,
        ModelTurnToolCallSnapshot toolCalls,
        int tokenIn = 0,
        int tokenOut = 0,
        CancellationToken cancellationToken = default)
    {
        Scope? scope = current.Value;
        string? progressId = scope?.ActiveProgressId;
        int ordinal = scope?.ActiveOrdinal ?? 0;
        if (scope is null || string.IsNullOrWhiteSpace(progressId) || ordinal <= 0)
        {
            return;
        }

        scope.ActiveProgressId = null;
        scope.ActiveOrdinal = 0;
        JsonElement? usage = tokenIn > 0 || tokenOut > 0
            ? NaradEventBodies.Usage(tokenIn, tokenOut)
            : null;
        await scope.PublishAsync(
            progressId,
            BuildLabel(scope.Context, ordinal),
            status,
            BuildDetail(toolCalls),
            usage,
            cancellationToken).ConfigureAwait(false);
    }

    internal static string BuildProgressId(string runId, int ordinal)
        => string.Create(CultureInfo.InvariantCulture, $"agent-turn:{runId}:{ordinal}");

    private static string BuildLabel(NaradEventContext context, int ordinal)
    {
        string prefix = string.IsNullOrWhiteSpace(context.ParentRunId) ? "Agent turn" : "Nested agent turn";
        return string.Create(CultureInfo.InvariantCulture, $"{prefix} {ordinal}");
    }

    internal static string BuildDetail(ModelTurnToolCallSnapshot toolCalls)
    {
        if (toolCalls.UniqueToolSummaries.Count == 0)
        {
            return "no tool calls";
        }

        string names = string.Join(", ", toolCalls.UniqueToolSummaries);
        string unique = toolCalls.UniqueToolSummaries.Count == 1
            ? "1 tool call"
            : string.Create(CultureInfo.InvariantCulture, $"{toolCalls.UniqueToolSummaries.Count} tool calls");
        if (toolCalls.StreamedCount <= toolCalls.UniqueToolSummaries.Count)
        {
            return unique + ": " + names;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{unique}: {names} [streamed {toolCalls.StreamedCount}]");
    }

    private sealed class Scope(
        IRuntimeEventPublisher publisher,
        NaradEventContext context,
        ChannelWriter<NaradPersistedEvent>? streamWriter,
        IModelTurnOrdinalCounter? ordinalCounter)
    {
        public NaradEventContext Context { get; } = context;

        public int Ordinal;

        public string? ActiveProgressId { get; set; }

        public int ActiveOrdinal { get; set; }

        public int NextOrdinal()
            => ordinalCounter is not null
                ? ordinalCounter.Next(Context.RunId)
                : Interlocked.Increment(ref Ordinal);

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Design",
            "CA1031:Do not catch general exception types",
            Justification = "Turn progress is observational; a journal failure must not fail the model request.")]
        public async Task PublishAsync(
            string progressId,
            string label,
            string status,
            string? detail,
            JsonElement? usage,
            CancellationToken cancellationToken)
        {
            try
            {
                NaradAuthorityEventDraft draft = NaradAuthorityEventDraftFactory.CreateNew(
                    NaradEventTypes.ProgressRecorded,
                    NaradEventBodies.ProgressRecorded(
                        progressId,
                        NaradProgressCategories.AgentTurn,
                        label,
                        status,
                        "public",
                        detail,
                        usage),
                    Context);
                NaradPersistedEvent persisted = await publisher.PublishAsync(draft, Context, cancellationToken)
                    .ConfigureAwait(false);
                if (streamWriter is not null)
                {
                    await streamWriter.WriteAsync(persisted, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _ = ex;
            }
        }
    }

    private sealed class Restore(Scope? prior) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            current.Value = prior;
        }
    }
}

internal readonly record struct ModelTurnToolCallSnapshot(
    int StreamedCount,
    IReadOnlyList<string> UniqueToolSummaries);
