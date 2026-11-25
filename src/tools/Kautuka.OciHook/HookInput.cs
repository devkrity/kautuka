using System.Text.Json;

namespace Kautuka.OciHook;

/// <summary>Parses untrusted hook inputs without reflection or permissive coercion.</summary>
internal static class HookInput
{
    internal static HookPolicy.State ParseState(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        RejectDuplicateProperties(root);
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("pid", out JsonElement pid) ||
            pid.ValueKind != JsonValueKind.Number || !pid.TryGetInt32(out int processId) || processId <= 0)
            throw new InvalidDataException("Invalid OCI state PID.");
        string id = ReadString(root, "id") ?? string.Empty;
        string? role = null;
        string? image = null;
        if (root.TryGetProperty("annotations", out JsonElement annotations) && annotations.ValueKind != JsonValueKind.Null)
        {
            if (annotations.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Invalid OCI annotations.");
            role = ReadString(annotations, "io.kautuka.role");
            image = ReadString(annotations, "io.kautuka.image-id");
        }
        return new(id, processId, role, image);
    }

    internal static Dictionary<string, HookPolicy.Infrastructure> ParseAllowlist(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        RejectDuplicateProperties(root);
        var entries = new Dictionary<string, HookPolicy.Infrastructure>(StringComparer.Ordinal);
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("entries", out JsonElement array) && array.ValueKind == JsonValueKind.Array)
            ReadEntries(array, entries);
        else if (root.ValueKind == JsonValueKind.Array)
            ReadEntries(root, entries);
        else if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in root.EnumerateObject())
                Add(entries, property.Name, ParseInfrastructure(property.Value));
        }
        else throw new InvalidDataException("Invalid infrastructure allowlist root.");
        return entries;
    }

    private static void ReadEntries(JsonElement array, Dictionary<string, HookPolicy.Infrastructure> entries)
    {
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid infrastructure entry.");
            string id = ReadString(item, "id") ?? throw new InvalidDataException("Missing infrastructure ID.");
            Add(entries, id, ParseInfrastructure(item));
        }
    }

    private static void Add(Dictionary<string, HookPolicy.Infrastructure> entries, string id, HookPolicy.Infrastructure entry)
    {
        if (!entries.TryAdd(id, entry)) throw new InvalidDataException("Duplicate infrastructure ID.");
    }

    private static HookPolicy.Infrastructure ParseInfrastructure(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.Array) return new(null, ReadStrings(item));
        if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid infrastructure entry.");
        IReadOnlyList<string> expected = item.TryGetProperty("expected_ipv4", out JsonElement ips) ? ReadStrings(ips) : [];
        IReadOnlyList<IReadOnlyList<string>>? alternates = null;
        if (item.TryGetProperty("expected_ipv4_alternates", out JsonElement alternateValue))
        {
            if (alternateValue.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid alternate addresses.");
            var parsed = new List<IReadOnlyList<string>>();
            foreach (JsonElement alternate in alternateValue.EnumerateArray()) parsed.Add(ReadStrings(alternate));
            alternates = parsed;
        }
        return new(ReadString(item, "role"), expected, alternates, ReadString(item, "image_digest"));
    }

    private static List<string> ReadStrings(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Expected address array.");
        var values = new List<string>();
        foreach (JsonElement item in array.EnumerateArray())
            values.Add(item.ValueKind == JsonValueKind.String ? item.GetString()! : throw new InvalidDataException("Invalid address."));
        return values;
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate JSON property.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in value.EnumerateArray()) RejectDuplicateProperties(item);
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : throw new InvalidDataException("Invalid string property.");
    }
}
