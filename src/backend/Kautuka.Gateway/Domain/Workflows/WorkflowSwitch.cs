using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kautuka.Gateway.Domain.Workflows;

internal sealed record GatewayWorkflowSwitchCase(string Pointer, string Op, string? Value, string GoTo);

internal static class WorkflowSwitchOps
{
    public const string Eq = "eq";
    public const string Ne = "ne";
    public const string Gt = "gt";
    public const string Gte = "gte";
    public const string Lt = "lt";
    public const string Lte = "lte";
    public const string In = "in";
    public const string Exists = "exists";

    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        Eq, Ne, Gt, Gte, Lt, Lte, In, Exists,
    };
}

internal static class WorkflowSwitch
{
    public static bool Matches(JsonObject envelope, GatewayWorkflowSwitchCase rule)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(rule);
        JsonNode? node = WorkflowJsonPointer.Get(envelope, rule.Pointer);
        string op = rule.Op.Trim();
        if (op.Equals(WorkflowSwitchOps.Exists, StringComparison.OrdinalIgnoreCase))
        {
            return node is not null && node.GetValueKind() != JsonValueKind.Null;
        }

        if (node is null)
        {
            return op.Equals(WorkflowSwitchOps.Ne, StringComparison.OrdinalIgnoreCase);
        }

        string actual = Unwrap(node);
        string expected = rule.Value ?? string.Empty;
        if (op.Equals(WorkflowSwitchOps.Eq, StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(actual, expected, StringComparison.Ordinal);
        }

        if (op.Equals(WorkflowSwitchOps.Ne, StringComparison.OrdinalIgnoreCase))
        {
            return !string.Equals(actual, expected, StringComparison.Ordinal);
        }

        if (op.Equals(WorkflowSwitchOps.In, StringComparison.OrdinalIgnoreCase))
        {
            return expected.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Contains(actual, StringComparer.Ordinal);
        }

        if (!TryCompareNumbers(actual, expected, out int compared))
        {
            compared = string.CompareOrdinal(actual, expected);
        }

        return op.ToUpperInvariant() switch
        {
            "GT" => compared > 0,
            "GTE" => compared >= 0,
            "LT" => compared < 0,
            "LTE" => compared <= 0,
            _ => false,
        };
    }

    public static Func<string, bool> Predicate(GatewayWorkflowSwitchCase rule)
        => payload =>
        {
            JsonObject? envelope = ParseEnvelope(payload);
            return envelope is not null && Matches(envelope, rule);
        };

    public static JsonObject? ParseEnvelope(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(payload)?.AsObject();
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string Unwrap(JsonNode node)
        => node is JsonValue jsonValue && jsonValue.TryGetValue(out string? text)
            ? text ?? string.Empty
            : node is JsonValue number && number.TryGetValue(out double numeric)
                ? numeric.ToString(CultureInfo.InvariantCulture)
                : node.ToJsonString();

    private static bool TryCompareNumbers(string left, string right, out int compared)
    {
        compared = 0;
        if (!double.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out double leftNumber)
            || !double.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out double rightNumber))
        {
            return false;
        }

        compared = leftNumber.CompareTo(rightNumber);
        return true;
    }
}
