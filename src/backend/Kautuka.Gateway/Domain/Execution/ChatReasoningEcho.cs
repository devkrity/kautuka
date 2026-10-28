using System.Text;

using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Domain.Execution;

/// <summary>
/// Ollama/MEAI often follows native <see cref="TextReasoningContent"/> tokens with one
/// <see cref="TextContent"/> that repeats the same thinking, tokens joined by newlines.
/// That echo must not become <c>message.text.delta</c> (web chat body + Telegram "final").
/// </summary>
internal static class ChatReasoningEcho
{
    internal static bool IsNativeReasoningEcho(string text, string thinking)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(thinking))
        {
            return false;
        }

        return CollapseWhitespace(text) == CollapseWhitespace(thinking);
    }

    private static string CollapseWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char ch in value)
        {
            if (!char.IsWhiteSpace(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }
}
