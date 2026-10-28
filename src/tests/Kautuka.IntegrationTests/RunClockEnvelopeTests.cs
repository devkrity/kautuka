using Kautuka.Gateway.Domain.Runtime;

namespace Kautuka.IntegrationTests;

public sealed class RunClockEnvelopeTests
{
    [Fact]
    public void WrapThenUnwrapRestoresElapsedAndBehavior()
    {
        const string behavior = """{"providerId":"ollama","modelId":"qwen"}""";
        TimeSpan elapsed = TimeSpan.FromSeconds(42);

        (string restoredBehavior, TimeSpan restoredElapsed) = RunClockEnvelope.Unwrap(
            RunClockEnvelope.Wrap(behavior, elapsed));

        Assert.Equal(42000, restoredElapsed.TotalMilliseconds);
        Assert.Contains("ollama", restoredBehavior, StringComparison.Ordinal);
        Assert.Contains("qwen", restoredBehavior, StringComparison.Ordinal);
        Assert.DoesNotContain("kautukaActiveElapsedMs", restoredBehavior, StringComparison.Ordinal);
    }

    [Fact]
    public void WrapUpdatesElapsedWithoutNestingEnvelopes()
    {
        string first = RunClockEnvelope.Wrap("{\"modelId\":\"a\"}", TimeSpan.FromMilliseconds(10));
        string second = RunClockEnvelope.Wrap(first, TimeSpan.FromMilliseconds(25));

        Assert.True(RunClockEnvelope.TryReadElapsed(second, out TimeSpan elapsed));
        Assert.Equal(25, elapsed.TotalMilliseconds);
        (string behavior, _) = RunClockEnvelope.Unwrap(second);
        Assert.DoesNotContain("effectiveBehavior", behavior, StringComparison.Ordinal);
        Assert.Contains("modelId", behavior, StringComparison.Ordinal);
    }

    [Fact]
    public void TryReadElapsedIsFalseForRawBehaviorJson()
    {
        Assert.False(RunClockEnvelope.TryReadElapsed("{\"providerId\":\"ollama\"}", out TimeSpan elapsed));
        Assert.Equal(TimeSpan.Zero, elapsed);
    }

    [Fact]
    public void TryReadElapsedIsTrueForExplicitZeroCheckpoint()
    {
        string stored = RunClockEnvelope.Wrap("{}", TimeSpan.Zero);
        Assert.True(RunClockEnvelope.TryReadElapsed(stored, out TimeSpan elapsed));
        Assert.Equal(TimeSpan.Zero, elapsed);
    }

    [Fact]
    public void WrapPersistsPackedApprovalRequestIds()
    {
        string stored = RunClockEnvelope.Wrap("{\"modelId\":\"a\"}", TimeSpan.FromMilliseconds(10), ["apr-1", "apr-2"]);

        Assert.True(RunClockEnvelope.TryReadPackedApprovalRequestIds(stored, out IReadOnlyList<string> packedIds));
        Assert.Equal(["apr-1", "apr-2"], packedIds);
        (string behavior, TimeSpan elapsed) = RunClockEnvelope.Unwrap(stored);
        Assert.Equal(10, elapsed.TotalMilliseconds);
        Assert.Contains("modelId", behavior, StringComparison.Ordinal);
        Assert.DoesNotContain("kautukaPackedApprovalRequestIds", behavior, StringComparison.Ordinal);
    }

    [Fact]
    public void WrapPreservesPackedIdsWhenUpdatingElapsedOnly()
    {
        string first = RunClockEnvelope.Wrap("{\"modelId\":\"a\"}", TimeSpan.FromMilliseconds(10), ["apr-1", "apr-2"]);
        string second = RunClockEnvelope.Wrap(first, TimeSpan.FromMilliseconds(25));

        Assert.True(RunClockEnvelope.TryReadPackedApprovalRequestIds(second, out IReadOnlyList<string> packedIds));
        Assert.Equal(["apr-1", "apr-2"], packedIds);
        Assert.True(RunClockEnvelope.TryReadElapsed(second, out TimeSpan elapsed));
        Assert.Equal(25, elapsed.TotalMilliseconds);
    }

    [Fact]
    public void PutPackedToolResultPreservesPackedIds()
    {
        string first = RunClockEnvelope.Wrap("{\"modelId\":\"a\"}", TimeSpan.FromMilliseconds(10), ["apr-1", "apr-2"]);
        string stored = RunClockEnvelope.PutPackedToolResult(first, "apr-1", """{"result":"ok"}""", journaledStart: true);

        Assert.True(RunClockEnvelope.TryReadPackedApprovalRequestIds(stored, out IReadOnlyList<string> packedIds));
        Assert.Equal(["apr-1", "apr-2"], packedIds);
        Assert.True(RunClockEnvelope.TryReadPackedToolResults(stored, out IReadOnlyDictionary<string, PackedToolResult> results));
        Assert.Equal("""{"result":"ok"}""", results["apr-1"].ResultJson);
        Assert.True(results["apr-1"].JournaledStart);
        (string behavior, TimeSpan elapsed) = RunClockEnvelope.Unwrap(stored);
        Assert.Equal(10, elapsed.TotalMilliseconds);
        Assert.Contains("modelId", behavior, StringComparison.Ordinal);
        Assert.DoesNotContain("kautukaPackedToolResults", behavior, StringComparison.Ordinal);
    }

    [Fact]
    public void PutPackedToolResultMergesAdditionalRequestWithoutDroppingIds()
    {
        string first = RunClockEnvelope.Wrap("{\"modelId\":\"a\"}", TimeSpan.FromMilliseconds(10), ["apr-1", "apr-2"]);
        string afterFirst = RunClockEnvelope.PutPackedToolResult(first, "apr-1", """{"outcome":"denied"}""", journaledStart: false);
        string stored = RunClockEnvelope.PutPackedToolResult(afterFirst, "apr-2", """{"outcome":"denied"}""", journaledStart: false);

        Assert.True(RunClockEnvelope.TryReadPackedApprovalRequestIds(stored, out IReadOnlyList<string> packedIds));
        Assert.Equal(["apr-1", "apr-2"], packedIds);
        Assert.True(RunClockEnvelope.TryReadPackedToolResults(stored, out IReadOnlyDictionary<string, PackedToolResult> results));
        Assert.Equal("""{"outcome":"denied"}""", results["apr-1"].ResultJson);
        Assert.Equal("""{"outcome":"denied"}""", results["apr-2"].ResultJson);
        Assert.False(results["apr-1"].JournaledStart);
        Assert.False(results["apr-2"].JournaledStart);
    }

    [Fact]
    public void WrapPreservesPackedResultsWhenUpdatingElapsedOnly()
    {
        string first = RunClockEnvelope.PutPackedToolResult(
            RunClockEnvelope.Wrap("{}", TimeSpan.FromMilliseconds(10), ["apr-1"]),
            "apr-1",
            """{"result":"ok"}""",
            journaledStart: false);
        string second = RunClockEnvelope.Wrap(first, TimeSpan.FromMilliseconds(25));

        Assert.True(RunClockEnvelope.TryReadPackedToolResults(second, out IReadOnlyDictionary<string, PackedToolResult> results));
        Assert.Equal("""{"result":"ok"}""", results["apr-1"].ResultJson);
        Assert.True(RunClockEnvelope.TryReadElapsed(second, out TimeSpan elapsed));
        Assert.Equal(25, elapsed.TotalMilliseconds);
    }

    [Fact]
    public void UnwrapReturnsRawStringWhenNotAnEnvelope()
    {
        (string behavior, TimeSpan elapsed) = RunClockEnvelope.Unwrap("not-json");
        Assert.Equal("not-json", behavior);
        Assert.Equal(TimeSpan.Zero, elapsed);
    }
}
