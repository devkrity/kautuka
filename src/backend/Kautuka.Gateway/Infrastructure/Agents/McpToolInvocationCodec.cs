using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Infrastructure.Agents;

internal static class McpToolInvocationCodec
{
    public static JsonElement ParseToolJsonSchema(string? jsonSchema)
    {
        if (string.IsNullOrWhiteSpace(jsonSchema))
        {
            using JsonDocument fallback = JsonDocument.Parse("{}");
            return fallback.RootElement.Clone();
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(jsonSchema);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            using JsonDocument fallback = JsonDocument.Parse("{}");
            return fallback.RootElement.Clone();
        }
    }

    public static string SerializeToolArguments(AIFunctionArguments arguments)
    {
        if (arguments.Count == 0)
        {
            return "{}";
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach ((string key, object? value) in arguments)
            {
                writer.WritePropertyName(key);
                WriteToolArgumentValue(writer, value);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static Dictionary<string, object?> ParseInput(string toolInputJson)
    {
        if (string.IsNullOrWhiteSpace(toolInputJson))
        {
            return [];
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(toolInputJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? ToDictionary(document.RootElement)
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static object? ToInvokeResult(JsonElement? structuredContent, bool? isError, IReadOnlyList<string> textBlocks)
    {
        if (structuredContent is JsonElement structured
            && structured.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
        {
            return JsonNode.Parse(structured.GetRawText());
        }

        string text = string.Join('\n', textBlocks.Where(static block => !string.IsNullOrWhiteSpace(block)));
        if (isError == true)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(text) ? "MCP tool returned an error." : text);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return new JsonObject { ["result"] = text };
        }
    }

    private static Dictionary<string, object?> ToDictionary(JsonElement element)
    {
        var dictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            dictionary[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.Object or JsonValueKind.Array => property.Value.Clone(),
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.TryGetInt64(out long integer) ? integer : property.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => null,
            };
        }

        return dictionary;
    }

    private static void WriteToolArgumentValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                return;
            case JsonElement element:
                element.WriteTo(writer);
                return;
            case JsonNode node:
                node.WriteTo(writer);
                return;
            case string text:
                writer.WriteStringValue(text);
                return;
            case char character:
                writer.WriteStringValue(character.ToString());
                return;
            case bool boolean:
                writer.WriteBooleanValue(boolean);
                return;
            case Guid guid:
                writer.WriteStringValue(guid);
                return;
            case DateTime dateTime:
                writer.WriteStringValue(dateTime);
                return;
            case DateTimeOffset dateTimeOffset:
                writer.WriteStringValue(dateTimeOffset);
                return;
            case TimeSpan timeSpan:
                writer.WriteStringValue(timeSpan.ToString("c", CultureInfo.InvariantCulture));
                return;
            case byte number:
                writer.WriteNumberValue(number);
                return;
            case sbyte number:
                writer.WriteNumberValue(number);
                return;
            case short number:
                writer.WriteNumberValue(number);
                return;
            case ushort number:
                writer.WriteNumberValue(number);
                return;
            case int number:
                writer.WriteNumberValue(number);
                return;
            case uint number:
                writer.WriteNumberValue(number);
                return;
            case long number:
                writer.WriteNumberValue(number);
                return;
            case ulong number:
                writer.WriteNumberValue(number);
                return;
            case float number:
                writer.WriteNumberValue(number);
                return;
            case double number:
                writer.WriteNumberValue(number);
                return;
            case decimal number:
                writer.WriteNumberValue(number);
                return;
            case Enum enumValue:
                WriteEnumValue(writer, enumValue);
                return;
            case IReadOnlyDictionary<string, object?> objectMap:
                writer.WriteStartObject();
                foreach ((string key, object? nestedValue) in objectMap)
                {
                    writer.WritePropertyName(key);
                    WriteToolArgumentValue(writer, nestedValue);
                }

                writer.WriteEndObject();
                return;
            case IDictionary<string, object?> objectMap:
                writer.WriteStartObject();
                foreach ((string key, object? nestedValue) in objectMap)
                {
                    writer.WritePropertyName(key);
                    WriteToolArgumentValue(writer, nestedValue);
                }

                writer.WriteEndObject();
                return;
            case IEnumerable<object?> sequence:
                writer.WriteStartArray();
                foreach (object? item in sequence)
                {
                    WriteToolArgumentValue(writer, item);
                }

                writer.WriteEndArray();
                return;
            case IEnumerable sequence:
                writer.WriteStartArray();
                foreach (object? item in sequence)
                {
                    WriteToolArgumentValue(writer, item);
                }

                writer.WriteEndArray();
                return;
            default:
                throw new InvalidOperationException($"Unsupported MCP tool argument type '{value.GetType().FullName}'.");
        }
    }

    private static void WriteEnumValue(Utf8JsonWriter writer, Enum enumValue)
    {
        Type enumType = enumValue.GetType();
        Type underlyingType = Enum.GetUnderlyingType(enumType);

        if (underlyingType == typeof(byte))
        {
            writer.WriteNumberValue((byte)(object)enumValue);
            return;
        }

        if (underlyingType == typeof(sbyte))
        {
            writer.WriteNumberValue((sbyte)(object)enumValue);
            return;
        }

        if (underlyingType == typeof(short))
        {
            writer.WriteNumberValue((short)(object)enumValue);
            return;
        }

        if (underlyingType == typeof(ushort))
        {
            writer.WriteNumberValue((ushort)(object)enumValue);
            return;
        }

        if (underlyingType == typeof(int))
        {
            writer.WriteNumberValue((int)(object)enumValue);
            return;
        }

        if (underlyingType == typeof(uint))
        {
            writer.WriteNumberValue((uint)(object)enumValue);
            return;
        }

        if (underlyingType == typeof(long))
        {
            writer.WriteNumberValue((long)(object)enumValue);
            return;
        }

        if (underlyingType == typeof(ulong))
        {
            writer.WriteNumberValue((ulong)(object)enumValue);
            return;
        }

        writer.WriteStringValue(enumValue.ToString());
    }
}
