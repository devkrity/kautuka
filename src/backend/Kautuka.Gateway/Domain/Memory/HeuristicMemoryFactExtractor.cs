using System.Text.RegularExpressions;

namespace Kautuka.Gateway.Domain.Memory;

internal sealed class HeuristicMemoryFactExtractor
{
    private static readonly Regex heuristicFactRegex = new(
        "(?i)\\bmy\\s+([a-z][a-z0-9_\\- ]{1,30})\\s+is\\s+([^\\.!?]{2,120})",
        RegexOptions.Compiled);

    private readonly ILogger<HeuristicMemoryFactExtractor> logger;

    public HeuristicMemoryFactExtractor(ILogger<HeuristicMemoryFactExtractor> logger)
    {
        this.logger = logger;
    }

    public IReadOnlyList<GatewayMemoryFact> Extract(
        string targetType,
        string targetId,
        string ownerUserId,
        string userMessage,
        DateTimeOffset timestamp)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
        {
            return [];
        }

        Match match = heuristicFactRegex.Match(userMessage);
        if (!match.Success)
        {
            HeuristicMemoryFactExtractorLog.NoMatch(logger, userMessage.Length);
            return [];
        }

        string attribute = match.Groups[1].Value.Trim();
        string value = match.Groups[2].Value.Trim();

        return
        [
            new GatewayMemoryFact(
                Id: $"fact-{Guid.NewGuid():N}",
                TargetType: targetType,
                TargetId: targetId,
                OwnerUserId: ownerUserId,
                Title: $"{attribute} preference",
                Content: $"My {attribute} is {value}",
                Category: "profile",
                Source: "heuristic",
                Confidence: 0.65D,
                CreatedAt: timestamp.ToString("O"),
                UpdatedAt: timestamp.ToString("O")),
        ];
    }
}

internal static partial class HeuristicMemoryFactExtractorLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "FactExtract heuristic: no match in message (len={Len})")]
    public static partial void NoMatch(ILogger logger, int len);
}
