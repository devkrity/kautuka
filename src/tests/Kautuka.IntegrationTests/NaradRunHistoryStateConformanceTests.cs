using System.Text.Json;

using Kautuka.Gateway.Domain.Execution;

namespace Kautuka.IntegrationTests;

public sealed class NaradRunHistoryStateConformanceTests
{
    [Fact]
    public void InterruptPauseTraceOpensThenClearsLifecycleSubset()
    {
        NaradPersistedEvent[] history = LoadAuthority("interrupt-pause.jsonl");

        NaradRunOpenState afterPause = NaradRunHistoryState.Analyze(TakeThrough(history, NaradEventTypes.RunPaused));
        Assert.True(afterPause.RootPaused);
        Assert.Equal("interrupt-1", Assert.Single(afterPause.OpenInterrupts).InterruptId);
        Assert.True(afterPause.HasBlockingState);

        NaradRunOpenState afterResolve = NaradRunHistoryState.Analyze(TakeThrough(history, NaradEventTypes.InterruptResolved));
        Assert.Empty(afterResolve.OpenInterrupts);

        NaradRunOpenState afterFinished = NaradRunHistoryState.Analyze(history);
        Assert.False(afterFinished.RootPaused);
        Assert.Empty(afterFinished.OpenInterrupts);
        Assert.False(afterFinished.HasBlockingState);
    }

    [Fact]
    public void ClientToolTraceTracksOpenThenFinishedTool()
    {
        NaradPersistedEvent[] history = LoadAuthority("client-tool.jsonl");

        NaradRunOpenState afterStart = NaradRunHistoryState.Analyze(TakeThrough(history, NaradEventTypes.ToolCallStarted));
        Assert.Equal("tool-2", Assert.Single(afterStart.OpenToolCallIds));
        Assert.True(afterStart.HasBlockingState);

        NaradRunOpenState afterFinish = NaradRunHistoryState.Analyze(history);
        Assert.Empty(afterFinish.OpenToolCallIds);
        Assert.False(afterFinish.HasBlockingState);
    }

    [Theory]
    [MemberData(nameof(PositiveTraceFiles))]
    public void PositiveConformanceTracesFoldWithoutThrowing(string fileName)
    {
        NaradPersistedEvent[] history = LoadAuthority(fileName);
        NaradRunOpenState state = NaradRunHistoryState.Analyze(history);
        Assert.NotNull(state);
    }

    public static TheoryData<string> PositiveTraceFiles()
    {
        string[] names =
        [
            "authority-replay.jsonl",
            "client-tool.jsonl",
            "client-tool-delivery-failure.jsonl",
            "client-tool-stop.jsonl",
            "independent-run.jsonl",
            "interrupt-pause.jsonl",
            "progress-replay.jsonl",
            "progress-while-paused.jsonl",
            "reconnect-revision.jsonl",
            "run-graph.jsonl",
            "snapshot-replay.jsonl",
            "terminal-usage.jsonl",
            "workflow.jsonl",
            "workspace-watch.jsonl",
        ];
        var data = new TheoryData<string>();
        foreach (string name in names)
        {
            data.Add(name);
        }

        return data;
    }

    private static NaradPersistedEvent[] LoadAuthority(string fileName)
    {
        string path = Path.Combine(ConformanceDirectory(), fileName);
        return [.. File.ReadLines(path)
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .Select(FromWire)
            .Where(static item => item is not null)
            .Cast<NaradPersistedEvent>()];
    }

    private static NaradPersistedEvent[] TakeThrough(NaradPersistedEvent[] history, string eventType)
    {
        int last = -1;
        for (int index = 0; index < history.Length; index++)
        {
            if (string.Equals(history[index].EventType, eventType, StringComparison.Ordinal))
            {
                last = index;
            }
        }

        Assert.True(last >= 0, $"Trace did not contain {eventType}.");
        return [.. history.Take(last + 1)];
    }

    private static NaradPersistedEvent? FromWire(string line)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("eventId", out JsonElement eventIdElement)
            || eventIdElement.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("sessionSequence", out JsonElement sessionSequenceElement)
            || sessionSequenceElement.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        string eventType = root.GetProperty("type").GetString() ?? string.Empty;
        return new NaradPersistedEvent(
            EventId: eventIdElement.GetString() ?? string.Empty,
            ProtocolVersion: root.GetProperty("protocolVersion").GetString() ?? NaradProtocolVersions.V1,
            EventType: eventType,
            SessionId: root.GetProperty("sessionId").GetString() ?? string.Empty,
            SessionSequence: sessionSequenceElement.GetInt64(),
            RecordClass: NaradRecordClasses.Authority,
            EventJson: root.GetRawText(),
            OccurredAt: root.TryGetProperty("timestamp", out JsonElement timestamp)
                ? timestamp.GetString() ?? "2026-08-05T00:00:00Z"
                : "2026-08-05T00:00:00Z",
            RootRunId: ReadString(root, "rootRunId"),
            RunId: ReadString(root, "runId"),
            Sequence: ReadInt64(root, "sequence"),
            WorkflowId: ReadString(root, "workflowId"),
            WorkflowSequence: ReadInt64(root, "workflowSequence"));
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? ReadInt64(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : null;

    private static string ConformanceDirectory()
    {
        string installed = Path.Combine(FindRepoRoot(), "node_modules", "@devkrity", "narad-spec", "conformance");
        if (!Directory.Exists(installed))
        {
            throw new DirectoryNotFoundException(
                "Narad traces live in @devkrity/narad-spec. Run pnpm install at the repository root.");
        }

        return installed;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "Kautuka.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }
}
