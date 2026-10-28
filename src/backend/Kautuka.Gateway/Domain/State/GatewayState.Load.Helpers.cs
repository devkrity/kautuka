using System.Text.Json;
using System.Text.Json.Nodes;

using Dapper;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Domain.State;

internal sealed partial class GatewayState
{
    private static string? ReadSetting(SqliteConnection connection, string key)
    {
        string? raw = connection.QueryFirstOrDefault<string>(
            "SELECT value_json FROM system_settings WHERE key = @key", new { key });
        if (raw is null) return null;
        return raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"' ? raw[1..^1] : raw;
    }

    private static JsonElement DeserializeJsonElement(string? json) =>
        string.IsNullOrEmpty(json) ? default : JsonSerializer.Deserialize(json, KautukaJsonContext.Default.JsonElement);

    private static JsonObject SafeCreateJsonObject(JsonElement element)
    {
        // JsonObject.Create() requires the element to be of type Object.
        // If the stored JSON is malformed or not an object, safely default to an empty object.
        if (element.ValueKind != JsonValueKind.Object)
        {
            return new JsonObject();
        }

        return JsonObject.Create(element) ?? new JsonObject();
    }

    private static string[] DeserializeStringArray(string json) =>
        JsonSerializer.Deserialize(json, KautukaJsonContext.Default.StringArray) ?? [];

    private static List<string>? DeserializeStringList(string? json) =>
        json is null ? null : JsonSerializer.Deserialize(json, KautukaJsonContext.Default.ListString);

    private static Dictionary<string, string[]> DeserializePermissionMap(string json) =>
        JsonSerializer.Deserialize(json, KautukaJsonContext.Default.DictionaryStringStringArray) ?? new(StringComparer.Ordinal);

    private static GatewayMessage[] DeserializeMessages(string json) =>
        JsonSerializer.Deserialize(json, KautukaJsonContext.Default.GatewayMessageArray) ?? [];

    private static GatewayRun? DeserializeRun(string json) =>
        JsonSerializer.Deserialize(json, KautukaJsonContext.Default.GatewayRun);

    private static GatewayChannelInteractionOption[] DeserializeInteractionOptions(string json) =>
        JsonSerializer.Deserialize(json, KautukaJsonContext.Default.GatewayChannelInteractionOptionArray) ?? [];
}
