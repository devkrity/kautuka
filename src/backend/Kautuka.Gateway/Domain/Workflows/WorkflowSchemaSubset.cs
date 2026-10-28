using System.Text.Json.Nodes;

namespace Kautuka.Gateway.Domain.Workflows;

/// <summary>
/// Restricted JSON Schema subset for step outputs and working state:
/// an object whose properties are string, number, integer, boolean, or arrays of those.
/// No $ref, oneOf, allOf, anyOf, or nested objects.
/// </summary>
internal static class WorkflowSchemaSubset
{
    private static readonly HashSet<string> allowedLeafTypes =
        new(StringComparer.Ordinal) { "string", "number", "integer", "boolean" };

    public static void ValidateSchema(JsonObject schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        RejectKeywords(schema);
        if (!IsType(schema, "object"))
        {
            throw new WorkflowSchemaException("Schema root must have type 'object'.");
        }

        JsonObject properties = ReadProperties(schema);
        HashSet<string> required = ReadRequired(schema);
        foreach (string name in required)
        {
            if (!properties.ContainsKey(name))
            {
                throw new WorkflowSchemaException($"Required property '{name}' is not declared.");
            }
        }

        foreach ((string name, JsonNode? node) in properties)
        {
            if (node is not JsonObject propertySchema)
            {
                throw new WorkflowSchemaException($"Property '{name}' must be a schema object.");
            }

            ValidateLeafOrArray(name, propertySchema);
        }
    }

    public static void ValidateInstance(JsonObject schema, JsonNode? instance)
    {
        ValidateSchema(schema);
        if (instance is not JsonObject obj)
        {
            throw new WorkflowSchemaException("Instance must be a JSON object.");
        }

        HashSet<string> required = ReadRequired(schema);
        foreach (string name in required)
        {
            if (!obj.ContainsKey(name) || obj[name] is null)
            {
                throw new WorkflowSchemaException($"Required property '{name}' is missing.");
            }
        }

        JsonObject properties = ReadProperties(schema);
        foreach ((string name, JsonNode? value) in obj)
        {
            if (!properties.TryGetPropertyValue(name, out JsonNode? propertySchemaNode)
                || propertySchemaNode is not JsonObject propertySchema)
            {
                throw new WorkflowSchemaException($"Property '{name}' is not declared.");
            }

            ValidateValue(name, propertySchema, value);
        }
    }

    public static bool DeclaresProperty(JsonObject schema, string name)
        => schema["properties"] is JsonObject properties && properties.ContainsKey(name);

    public static bool PointerResolvesToDeclaredProperty(JsonObject schema, string pointer)
    {
        ValidateSchema(schema);
        IReadOnlyList<string> tokens = WorkflowJsonPointer.Parse(pointer);
        if (tokens.Count == 0)
        {
            return true;
        }

        JsonObject properties = ReadProperties(schema);
        if (!properties.ContainsKey(tokens[0]))
        {
            return false;
        }

        return tokens.Count == 1;
    }

    private static void ValidateLeafOrArray(string name, JsonObject schema)
    {
        RejectKeywords(schema);
        if (IsType(schema, "array"))
        {
            if (schema["items"] is not JsonObject items)
            {
                throw new WorkflowSchemaException($"Array property '{name}' must declare items.");
            }

            RejectKeywords(items);
            if (!TryGetSingleType(items, out string itemType) || !allowedLeafTypes.Contains(itemType))
            {
                throw new WorkflowSchemaException($"Array property '{name}' items must be a scalar type.");
            }

            return;
        }

        if (!TryGetSingleType(schema, out string type) || !allowedLeafTypes.Contains(type))
        {
            throw new WorkflowSchemaException($"Property '{name}' must be a scalar or array of scalars.");
        }
    }

    private static void ValidateValue(string name, JsonObject schema, JsonNode? value)
    {
        if (IsType(schema, "array"))
        {
            if (value is not JsonArray array)
            {
                throw new WorkflowSchemaException($"Property '{name}' must be an array.");
            }

            JsonObject items = (JsonObject)schema["items"]!;
            string itemType = ReadSingleType(items);
            foreach (JsonNode? item in array)
            {
                ValidateScalar($"{name}[]", itemType, item);
            }

            return;
        }

        ValidateScalar(name, ReadSingleType(schema), value);
    }

    private static void ValidateScalar(string name, string type, JsonNode? value)
    {
        bool ok = type switch
        {
            "string" => value is JsonValue v && v.TryGetValue(out string? _),
            "boolean" => value is JsonValue v && v.TryGetValue(out bool _),
            "integer" => value is JsonValue v && v.TryGetValue(out long _),
            "number" => value is JsonValue v && (v.TryGetValue(out double _) || v.TryGetValue(out long _)),
            _ => false,
        };
        if (!ok)
        {
            throw new WorkflowSchemaException($"Property '{name}' does not match type '{type}'.");
        }
    }

    private static JsonObject ReadProperties(JsonObject schema)
    {
        if (schema["properties"] is JsonObject properties)
        {
            return properties;
        }

        return [];
    }

    private static HashSet<string> ReadRequired(JsonObject schema)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (schema["required"] is not JsonArray required)
        {
            return names;
        }

        foreach (JsonNode? node in required)
        {
            if (node is not JsonValue value || !value.TryGetValue(out string? name) || string.IsNullOrWhiteSpace(name))
            {
                throw new WorkflowSchemaException("Schema required[] must contain property names.");
            }

            names.Add(name);
        }

        return names;
    }

    private static bool IsType(JsonObject schema, string type) =>
        TryGetSingleType(schema, out string actual) && actual == type;

    private static string ReadSingleType(JsonObject schema)
    {
        if (!TryGetSingleType(schema, out string type))
        {
            throw new WorkflowSchemaException("Schema type must be a single string.");
        }

        return type;
    }

    private static bool TryGetSingleType(JsonObject schema, out string type)
    {
        type = string.Empty;
        if (schema["type"] is not JsonValue value || !value.TryGetValue(out string? text) || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        type = text;
        return true;
    }

    private static readonly string[] forbiddenKeywords =
        ["$ref", "oneOf", "allOf", "anyOf", "not", "$defs", "definitions"];

    private static void RejectKeywords(JsonObject schema)
    {
        foreach (string keyword in forbiddenKeywords)
        {
            if (schema.ContainsKey(keyword))
            {
                throw new WorkflowSchemaException($"Schema keyword '{keyword}' is not allowed.");
            }
        }
    }
}

internal sealed class WorkflowSchemaException : InvalidOperationException
{
    public WorkflowSchemaException()
    {
    }

    public WorkflowSchemaException(string message)
        : base(message)
    {
    }

    public WorkflowSchemaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
