using System.Text;
using System.Text.RegularExpressions;

namespace Kautuka.Gateway.Domain.Workflows;

internal static partial class WorkflowDiagramText
{
    public static string Condition(GatewayWorkflowSwitchCase rule)
    {
        string field = FieldName(rule.Pointer);
        string value = rule.Value?.Trim() ?? string.Empty;
        return rule.Op switch
        {
            "eq" => $"{field} is {value}",
            "ne" => $"{field} is not {value}",
            "gt" => $"{field} is above {value}",
            "gte" => $"{field} is at least {value}",
            "lt" => $"{field} is below {value}",
            "lte" => $"{field} is at most {value}",
            "in" => $"{field} is one of {value}",
            "exists" => $"{field} is set",
            _ => string.IsNullOrWhiteSpace(value) ? $"{field} {rule.Op}".Trim() : $"{field} {rule.Op} {value}",
        };
    }

    public static string Present(
        string mermaid,
        GatewayWorkflowNode[] nodes,
        IReadOnlyDictionary<string, string>? agentNames)
    {
        ArgumentNullException.ThrowIfNull(mermaid);
        ArgumentNullException.ThrowIfNull(nodes);
        if (!TryRead(mermaid, out List<DiagramNode> diagramNodes, out List<DiagramEdge> edges))
        {
            return mermaid;
        }

        Dictionary<string, string> edgeLabels = EdgeLabels(nodes);
        Dictionary<string, int> displayNumbers = DisplayNumbers(nodes);
        var drop = new HashSet<string>(StringComparer.Ordinal);
        var joined = new List<DiagramEdge>();
        foreach (DiagramNode node in diagramNodes)
        {
            if (!edgeLabels.ContainsKey(node.RawId))
            {
                continue;
            }

            List<DiagramEdge> incoming = edges.Where(edge => edge.To == node.Alias).ToList();
            List<DiagramEdge> outgoing = edges.Where(edge => edge.From == node.Alias).ToList();
            if (incoming.Count != 1 || outgoing.Count != 1)
            {
                continue;
            }

            drop.Add(node.Alias);
            joined.Add(new DiagramEdge(incoming[0].From, outgoing[0].To, edgeLabels[node.RawId], node.RawId));
        }

        if (drop.Count > 0)
        {
            edges = edges
                .Where(edge => !drop.Contains(edge.From) && !drop.Contains(edge.To))
                .Concat(joined)
                .ToList();
        }

        var lines = new List<string> { "flowchart TD" };
        var steps = new List<string>();
        var pauses = new List<string>();
        foreach (DiagramNode node in diagramNodes)
        {
            if (drop.Contains(node.Alias))
            {
                continue;
            }

            string label = Display(node, nodes, displayNumbers, agentNames);
            lines.Add($"  {node.Alias}[\"{Escape(label)}\"]");
            if (node.RawId.StartsWith("review-", StringComparison.Ordinal))
            {
                pauses.Add(node.Alias);
            }
            else
            {
                steps.Add(node.Alias);
            }
        }

        for (int index = 0; index < edges.Count; index++)
        {
            DiagramEdge edge = edges[index];
            lines.Add(string.IsNullOrWhiteSpace(edge.Label)
                ? $"  {edge.From} --> {edge.To}"
                : $"  {edge.From} -->|{Escape(edge.Label)}| {edge.To}");
            if (!string.IsNullOrWhiteSpace(edge.ChoiceId) && edge.ChoiceId.IndexOfAny([' ', '\n', '\r']) < 0)
            {
                lines.Add($"%% choice {index} {edge.ChoiceId}");
            }
        }

        if (steps.Count > 0)
        {
            lines.Add("  classDef step fill:#eef3ff,stroke:#315fad,color:#142033");
            lines.Add($"  class {string.Join(',', steps)} step");
        }

        if (pauses.Count > 0)
        {
            lines.Add("  classDef pause fill:#fff7e8,stroke:#a87412,color:#3a2a08");
            lines.Add($"  class {string.Join(',', pauses)} pause");
        }

        return string.Join('\n', lines);
    }

    private static Dictionary<string, string> EdgeLabels(GatewayWorkflowNode[] nodes)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (GatewayWorkflowNode node in nodes)
        {
            for (int index = 0; index < node.EffectiveSwitchCases.Length; index++)
            {
                GatewayWorkflowSwitchCase rule = node.EffectiveSwitchCases[index];
                labels[$"edge-{node.NodeId}-{rule.GoTo}-{index}"] = Condition(rule);
            }

            if (!string.IsNullOrWhiteSpace(node.DefaultGoTo))
            {
                labels[$"edge-{node.NodeId}-{node.DefaultGoTo}-{node.EffectiveSwitchCases.Length}"] =
                    node.EffectiveSwitchCases.Length == 0 ? string.Empty : "Otherwise";
            }
        }

        return labels;
    }

    private static string Display(
        DiagramNode node,
        GatewayWorkflowNode[] nodes,
        Dictionary<string, int> displayNumbers,
        IReadOnlyDictionary<string, string>? agentNames)
    {
        if (node.RawId.StartsWith("review-", StringComparison.Ordinal))
        {
            string stepId = node.RawId["review-".Length..];
            return displayNumbers.TryGetValue(stepId, out int reviewNumber)
                ? $"Pause after step {reviewNumber}"
                : "Pause for your review";
        }

        if (!displayNumbers.TryGetValue(node.RawId, out int stepNumber))
        {
            return node.RawId;
        }

        string title = $"Step {stepNumber}";
        GatewayWorkflowNode step = nodes.First(candidate => string.Equals(candidate.NodeId, node.RawId, StringComparison.Ordinal));
        if (agentNames is not null
            && agentNames.TryGetValue(step.AgentId, out string? agentName)
            && !string.IsNullOrWhiteSpace(agentName))
        {
            title = $"{title} · {agentName.Trim()}";
        }

        return node.IsStart ? $"{title} (starts here)" : title;
    }

    private static Dictionary<string, int> DisplayNumbers(GatewayWorkflowNode[] nodes)
    {
        var outgoing = nodes.ToDictionary(
            node => node.NodeId,
            _ => new List<string>(),
            StringComparer.Ordinal);
        var explicitTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (GatewayWorkflowNode node in nodes)
        {
            foreach (GatewayWorkflowSwitchCase rule in node.EffectiveSwitchCases)
            {
                explicitTargets.Add(rule.GoTo);
            }

            if (!string.IsNullOrWhiteSpace(node.DefaultGoTo))
            {
                explicitTargets.Add(node.DefaultGoTo);
            }
        }

        for (int index = 0; index < nodes.Length; index++)
        {
            GatewayWorkflowNode node = nodes[index];
            List<string> targets = outgoing[node.NodeId];
            targets.AddRange(node.EffectiveSwitchCases.Select(rule => rule.GoTo));
            if (!string.IsNullOrWhiteSpace(node.DefaultGoTo))
            {
                targets.Add(node.DefaultGoTo);
            }
            else if (node.EffectiveSwitchCases.Length == 0 && index + 1 < nodes.Length
                && !explicitTargets.Contains(nodes[index + 1].NodeId))
            {
                targets.Add(nodes[index + 1].NodeId);
            }
        }

        var order = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        if (nodes.Length > 0)
        {
            queue.Enqueue(nodes[0].NodeId);
        }

        while (queue.TryDequeue(out string? nodeId))
        {
            if (!visited.Add(nodeId))
            {
                continue;
            }

            order.Add(nodeId);
            foreach (string target in outgoing[nodeId])
            {
                if (!visited.Contains(target))
                {
                    queue.Enqueue(target);
                }
            }
        }

        foreach (GatewayWorkflowNode node in nodes)
        {
            if (visited.Add(node.NodeId))
            {
                order.Add(node.NodeId);
            }
        }

        return order.Select((nodeId, index) => (nodeId, number: index + 1))
            .ToDictionary(item => item.nodeId, item => item.number, StringComparer.Ordinal);
    }

    private static bool TryRead(string mermaid, out List<DiagramNode> nodes, out List<DiagramEdge> edges)
    {
        nodes = [];
        edges = [];
        foreach (string rawLine in mermaid.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("flowchart", StringComparison.Ordinal) || line.StartsWith("class", StringComparison.Ordinal))
            {
                continue;
            }

            Match nodeMatch = NodePattern().Match(line);
            if (nodeMatch.Success)
            {
                string label = Unescape(nodeMatch.Groups["label"].Value);
                bool start = label.EndsWith(" (Start)", StringComparison.Ordinal);
                nodes.Add(new DiagramNode(
                    nodeMatch.Groups["id"].Value,
                    start ? label[..^" (Start)".Length] : label,
                    start));
                continue;
            }

            Match dotted = DottedEdgePattern().Match(line);
            if (dotted.Success)
            {
                edges.Add(new DiagramEdge(dotted.Groups["from"].Value, dotted.Groups["to"].Value, null));
                continue;
            }

            Match labeled = LabeledEdgePattern().Match(line);
            if (labeled.Success)
            {
                edges.Add(new DiagramEdge(
                    labeled.Groups["from"].Value,
                    labeled.Groups["to"].Value,
                    Unescape(labeled.Groups["label"].Value)));
                continue;
            }

            Match plain = PlainEdgePattern().Match(line);
            if (plain.Success)
            {
                edges.Add(new DiagramEdge(plain.Groups["from"].Value, plain.Groups["to"].Value, null));
            }
        }

        return nodes.Count > 0;
    }

    private static string FieldName(string pointer)
    {
        string trimmed = pointer.Trim();
        return trimmed.StartsWith('/') ? trimmed[1..] : trimmed;
    }

    private static string Unescape(string value)
        => value
            .Replace("&quot;", "\"", StringComparison.Ordinal)
            .Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal)
            .Replace("&amp;", "&", StringComparison.Ordinal);

    private static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char ch in value)
        {
            switch (ch)
            {
                case '&':
                    builder.Append("&amp;");
                    break;
                case '"':
                    builder.Append("&quot;");
                    break;
                case '<':
                    builder.Append("&lt;");
                    break;
                case '>':
                    builder.Append("&gt;");
                    break;
                case '|':
                    builder.Append('/');
                    break;
                case '\n':
                case '\r':
                    builder.Append(' ');
                    break;
                default:
                    builder.Append(ch);
                    break;
            }
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"^(?<id>[A-Za-z_][A-Za-z0-9_]*)\[""(?<label>.*)""\];?$")]
    private static partial Regex NodePattern();

    [GeneratedRegex(@"^(?<from>[A-Za-z_][A-Za-z0-9_]*)\s+-\.\s+.*?\s+\.->\s+(?<to>[A-Za-z_][A-Za-z0-9_]*);?$")]
    private static partial Regex DottedEdgePattern();

    [GeneratedRegex(@"^(?<from>[A-Za-z_][A-Za-z0-9_]*)\s+-->\|(?<label>.*)\|\s*(?<to>[A-Za-z_][A-Za-z0-9_]*);?$")]
    private static partial Regex LabeledEdgePattern();

    [GeneratedRegex(@"^(?<from>[A-Za-z_][A-Za-z0-9_]*)\s+-->\s+(?<to>[A-Za-z_][A-Za-z0-9_]*);?$")]
    private static partial Regex PlainEdgePattern();

    private sealed record DiagramNode(string Alias, string RawId, bool IsStart);

    private sealed record DiagramEdge(string From, string To, string? Label, string? ChoiceId = null);
}
