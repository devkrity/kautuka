using Kautuka.Gateway.Infrastructure.AI;

namespace Kautuka.ContractTests;

public sealed class GeminiParallelToolCallGuidanceTests
{
    [Fact]
    public void BuildAddsGuidanceForGeminiWhenMultipleToolCallsAreAllowed()
    {
        Assert.Equal(
            GeminiParallelToolCallGuidance.Text,
            GeminiParallelToolCallGuidance.Build("gemini", allowMultipleToolCalls: true));
        Assert.Equal(
            GeminiParallelToolCallGuidance.Text,
            GeminiParallelToolCallGuidance.Build("Gemini", allowMultipleToolCalls: true));
    }

    [Fact]
    public void BuildIsEmptyWhenMultipleToolCallsAreOff()
        => Assert.Equal(string.Empty, GeminiParallelToolCallGuidance.Build("gemini", allowMultipleToolCalls: false));

    [Fact]
    public void BuildIsEmptyForNonGeminiProviders()
        => Assert.Equal(string.Empty, GeminiParallelToolCallGuidance.Build("ollama", allowMultipleToolCalls: true));

    [Fact]
    public void AppendLeavesContextUnchangedWithoutGuidance()
        => Assert.Equal("base", GeminiParallelToolCallGuidance.Append("base", guidance: string.Empty));

    [Fact]
    public void AppendJoinsGuidanceAfterExistingContext()
        => Assert.Equal(
            "base\n\n" + GeminiParallelToolCallGuidance.Text,
            GeminiParallelToolCallGuidance.Append("base", GeminiParallelToolCallGuidance.Text));
}
