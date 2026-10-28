namespace Kautuka.Gateway.Infrastructure.AI;

/// <summary>
/// Gemini has no FunctionCallingConfig flag for parallel tool emission.
/// <see cref="Microsoft.Extensions.AI.ChatOptions.AllowMultipleToolCalls"/> is set by
/// Gateway but Google.GenAI's <c>AsIChatClient</c> only maps tool mode to AUTO/ANY/NONE.
/// Google's own parallel-calling samples coax the model in the prompt.
/// </summary>
internal static class GeminiParallelToolCallGuidance
{
    internal const string Text =
        "When several independent tool calls would help, emit them together in one turn rather than one after another.";

    internal static string Build(string? providerType, bool allowMultipleToolCalls)
    {
        if (!allowMultipleToolCalls)
        {
            return string.Empty;
        }

        string normalized = ProviderTypeCatalogSettings.NormalizeType(providerType ?? string.Empty);
        return string.Equals(normalized, "gemini", StringComparison.Ordinal) ? Text : string.Empty;
    }

    internal static string Append(string combinedSystemContext, string guidance)
    {
        if (string.IsNullOrWhiteSpace(guidance))
        {
            return combinedSystemContext;
        }

        if (string.IsNullOrWhiteSpace(combinedSystemContext))
        {
            return guidance;
        }

        return combinedSystemContext + "\n\n" + guidance;
    }
}
