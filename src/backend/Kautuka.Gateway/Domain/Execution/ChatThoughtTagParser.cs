using System.Text;
using System.Text.RegularExpressions;

using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Domain.Execution;

/// <summary>Parses provider-emitted thought tags from assistant text streams.</summary>
internal static class ChatThoughtTagParser
{
    private static readonly string[] openingThoughtTags = ["<thought>", "<thinking>", "<think>"];
    private static readonly string[] closingThoughtTags = ["</thought>", "</thinking>", "</think>"];

    internal static string ExtractNativeThinking(IEnumerable<ChatMessage> messages)
        => string.Join(
            '\n',
            messages
                .SelectMany(static message => message.Contents)
                .OfType<TextReasoningContent>()
                .Select(static reasoning => reasoning.Text)
                .Where(static text => !string.IsNullOrWhiteSpace(text))!);

    internal static (string ResponseText, string ThinkingText) ExtractTaggedThinking(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return (text, string.Empty);
        }

        var thinking = new StringBuilder();
        string result = Regex.Replace(
            text,
            @"<(think|thinking|thought)>(.*?)<\/\1>",
            m =>
            {
                if (thinking.Length > 0)
                {
                    thinking.Append('\n');
                }

                thinking.Append(m.Groups[2].Value.Trim());
                return string.Empty;
            },
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        // Strip any orphan closing tags that lacked a matching opener.
        result = Regex.Replace(result, @"<\/(think|thinking|thought)>", string.Empty, RegexOptions.IgnoreCase);
        return (result.Trim(), thinking.ToString());
    }

    internal static (string TextOut, string ThinkOut, string Pending, bool InThought)
        RouteTaggedChunk(string pending, string chunk, bool inThought)
    {
        string text = pending + chunk;
        var textOut = new StringBuilder();
        var thinkOut = new StringBuilder();

        while (text.Length > 0)
        {
            string[] searchTags = inThought ? closingThoughtTags : openingThoughtTags;
            int tagStart = FindTag(text, searchTags, out int tagLength);
            if (tagStart < 0)
            {
                return FlushRemainingChunk(text, inThought, searchTags, textOut, thinkOut);
            }

            AppendChunk(inThought, textOut, thinkOut, text[..tagStart]);
            text = text[(tagStart + tagLength)..];
            inThought = !inThought;
        }

        return (textOut.ToString(), thinkOut.ToString(), string.Empty, inThought);
    }

    private static (string TextOut, string ThinkOut, string Pending, bool InThought) FlushRemainingChunk(
        string text,
        bool inThought,
        string[] searchTags,
        StringBuilder textOut,
        StringBuilder thinkOut)
    {
        int partialLength = FindTrailingPartial(text, searchTags);
        string safeText = partialLength > 0 ? text[..^partialLength] : text;
        AppendChunk(inThought, textOut, thinkOut, safeText);
        return (
            textOut.ToString(),
            thinkOut.ToString(),
            partialLength > 0 ? text[^partialLength..] : string.Empty,
            inThought);
    }

    private static void AppendChunk(bool inThought, StringBuilder textOut, StringBuilder thinkOut, string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return;
        }

        if (inThought)
        {
            thinkOut.Append(content);
            return;
        }

        textOut.Append(content);
    }

    private static int FindTag(string text, string[] tags, out int tagLength)
    {
        int bestIndex = -1;
        tagLength = 0;

        foreach (string tag in tags)
        {
            int index = text.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && (bestIndex < 0 || index < bestIndex))
            {
                bestIndex = index;
                tagLength = tag.Length;
            }
        }

        return bestIndex;
    }

    private static int FindTrailingPartial(string text, string[] tags)
    {
        int maxLength = 0;
        foreach (string tag in tags)
        {
            int candidateLength = Math.Min(text.Length, tag.Length - 1);
            for (int length = candidateLength; length > 0; length--)
            {
                if (!text.EndsWith(tag[..length], StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                maxLength = Math.Max(maxLength, length);
                break;
            }
        }

        return maxLength;
    }
}
