using System.Text.Json.Serialization;

namespace Kautuka.NetworkAccess.Acceptance;

public sealed record AcceptanceObservation(
    [property: JsonPropertyName("testId")] string TestId,
    [property: JsonPropertyName("observedValue")] string ObservedValue,
    [property: JsonPropertyName("expectedValue")] string ExpectedValue,
    [property: JsonPropertyName("ncObservedValue")] string NcObservedValue,
    [property: JsonPropertyName("ncExpectedValue")] string NcExpectedValue,
    [property: JsonPropertyName("ncFlipped")] bool NcFlipped,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("timestampUtc")] DateTimeOffset TimestampUtc);

[JsonSerializable(typeof(AcceptanceObservation))]
[JsonSerializable(typeof(List<AcceptanceObservation>))]
internal sealed partial class AcceptanceJsonContext : JsonSerializerContext;

public static class AcceptanceHarness
{
    private static readonly List<AcceptanceObservation> observations = [];

    /// <summary>
    /// Records an acceptance test result adhering strictly to Ground Rule G9 (Section 7.0):
    /// 1. Compares observed values read during execution (never hard-coded literals).
    /// 2. Asserts negative control (NC) flipped the result.
    /// 3. Rejects any attempt to record PASS when NC did not flip or observed values are absent.
    /// </summary>
    public static AcceptanceObservation Record(
        string testId,
        string observedValue,
        string expectedValue,
        string ncObservedValue,
        string ncExpectedValue,
        bool ncFlipped)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(testId);
        ArgumentException.ThrowIfNullOrWhiteSpace(observedValue);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedValue);

        // Ground Rule G9: A test whose NC does not flip is reported FAIL (cannot fail), never PASS!
        if (!ncFlipped)
        {
            var failRecord = new AcceptanceObservation(
                testId,
                observedValue,
                expectedValue,
                ncObservedValue,
                ncExpectedValue,
                false,
                "FAIL (cannot fail: NC did not flip)",
                DateTimeOffset.UtcNow);
            observations.Add(failRecord);
            return failRecord;
        }

        bool valuesMatch = string.Equals(observedValue.Trim(), expectedValue.Trim(), StringComparison.Ordinal);
        string outcome = valuesMatch ? "PASS" : "FAIL (mismatch)";

        var record = new AcceptanceObservation(
            testId,
            observedValue,
            expectedValue,
            ncObservedValue,
            ncExpectedValue,
            true,
            outcome,
            DateTimeOffset.UtcNow);

        observations.Add(record);
        return record;
    }

    public static IReadOnlyList<AcceptanceObservation> GetAll() => observations.AsReadOnly();
}

public static class Program
{
    public static int Main()
    {
        Console.WriteLine("Kautuka NAC v7 Acceptance Suite Scaffold");
        Console.WriteLine("Enforcing Ground Rule G9: observed values and verified negative control flips only.");

        // Harness self-test (Rule 3)
        Console.WriteLine("Running harness self-test...");
        var badNc = AcceptanceHarness.Record("SELFTEST_BAD_NC", "42", "42", "42", "0", false);
        if (badNc.Outcome != "FAIL (cannot fail: NC did not flip)")
        {
            Console.Error.WriteLine("CRITICAL: Harness self-test failed to reject invalid NC!");
            return 1;
        }

        Console.WriteLine("Harness self-test verified G9 enforcement.");
        return 0;
    }
}
