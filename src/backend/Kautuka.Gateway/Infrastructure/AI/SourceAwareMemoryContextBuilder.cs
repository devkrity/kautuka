using System.Globalization;
using System.Text;

namespace Kautuka.Gateway.Infrastructure.AI;

internal sealed class SourceAwareMemoryContextBuilder(IMemoryFactFormatter formatter) : IInjectedMemoryContextBuilder
{
    private const int maxFacts = 40;

    public string Build(IReadOnlyList<InjectedMemoryFact> facts)
    {
        if (facts.Count == 0)
        {
            return string.Empty;
        }

        var normalized = facts
            .Where(static fact => !string.IsNullOrWhiteSpace(fact.SourceScope)
                && !string.IsNullOrWhiteSpace(fact.SourceId)
                && !string.IsNullOrWhiteSpace(fact.Content))
            .Take(maxFacts)
            .ToList();

        if (normalized.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine("## Remembered context");

        AppendScopeSection(sb, normalized, "agent", "Agent Memory", groupBySource: true);

        return sb.ToString().TrimEnd();
    }

    private void AppendScopeSection(StringBuilder sb, IReadOnlyList<InjectedMemoryFact> facts, string scope, string sectionTitle, bool groupBySource)
    {
        List<InjectedMemoryFact> scoped = facts
            .Where(fact => string.Equals(fact.SourceScope, scope, StringComparison.OrdinalIgnoreCase))
            .OrderBy(fact => fact.SourceId, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(GetSortTimestamp)
            .ToList();

        if (scoped.Count == 0)
        {
            return;
        }

        if (!groupBySource)
        {
            string sourceId = scoped[0].SourceId;
            sb.AppendLine();
            sb.Append(CultureInfo.InvariantCulture, $"## {sectionTitle} ({sourceId})");
            sb.AppendLine();
            foreach (InjectedMemoryFact fact in scoped)
            {
                sb.AppendLine(formatter.Format(fact));
            }
            return;
        }

        foreach (IGrouping<string, InjectedMemoryFact> group in scoped.GroupBy(fact => fact.SourceId, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine();
            sb.Append(CultureInfo.InvariantCulture, $"## {sectionTitle} ({group.Key})");
            sb.AppendLine();
            foreach (InjectedMemoryFact fact in group)
            {
                sb.AppendLine(formatter.Format(fact));
            }
        }
    }

    private static DateTimeOffset GetSortTimestamp(InjectedMemoryFact fact)
        => DateTimeOffset.TryParse(fact.UpdatedAt, out DateTimeOffset parsed)
            ? parsed
            : DateTimeOffset.MinValue;
}
