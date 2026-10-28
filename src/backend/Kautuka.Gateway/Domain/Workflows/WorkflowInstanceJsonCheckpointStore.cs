using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;

namespace Kautuka.Gateway.Domain.Workflows;

internal sealed class WorkflowInstanceJsonCheckpointStore(Func<string?> load, Action<string> save) : ICheckpointStore<JsonElement>
{
    private readonly Lock gate = new();

    public ValueTask<CheckpointInfo> CreateCheckpointAsync(string sessionId, JsonElement value, CheckpointInfo? parent = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (gate)
        {
            CheckpointDocument document = Load();
            var info = new CheckpointInfo(sessionId, Guid.NewGuid().ToString("N"));
            document.Index.Add(new CheckpointIndexEntry(info.SessionId, info.CheckpointId, parent?.CheckpointId));
            document.Values[info.CheckpointId] = value.GetRawText();
            Save(document);
            return new ValueTask<CheckpointInfo>(info);
        }
    }

    public ValueTask<JsonElement> RetrieveCheckpointAsync(string sessionId, CheckpointInfo key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (gate)
        {
            CheckpointDocument document = Load();
            if (!document.Values.TryGetValue(key.CheckpointId, out string? json))
            {
                throw new KeyNotFoundException($"Checkpoint '{key.CheckpointId}' was not found.");
            }

            return new ValueTask<JsonElement>(JsonSerializer.Deserialize(json, Kautuka.Gateway.Serialization.KautukaJsonContext.Default.JsonElement));
        }
    }

    public ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(string sessionId, CheckpointInfo? withParent = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (gate)
        {
            IEnumerable<CheckpointInfo> matches = Load().Index
                .Where(entry => string.Equals(entry.SessionId, sessionId, StringComparison.Ordinal))
                .Where(entry => withParent is null
                    || string.Equals(entry.ParentCheckpointId, withParent.CheckpointId, StringComparison.Ordinal))
                .Select(static entry => new CheckpointInfo(entry.SessionId, entry.CheckpointId));
            return new ValueTask<IEnumerable<CheckpointInfo>>(matches.ToArray());
        }
    }

    private CheckpointDocument Load()
    {
        string? json = load();
        if (string.IsNullOrWhiteSpace(json))
        {
            return new CheckpointDocument();
        }

        JsonObject root = JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
        var document = new CheckpointDocument();
        if (root["index"] is JsonArray index)
        {
            foreach (JsonNode? item in index)
            {
                if (item is not JsonObject obj)
                {
                    continue;
                }

                string checkpointId = obj["checkpointId"]?.GetValue<string>() ?? string.Empty;
                string storedSessionId = obj["sessionId"]?.GetValue<string>() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(checkpointId) || string.IsNullOrWhiteSpace(storedSessionId))
                {
                    continue;
                }

                document.Index.Add(new CheckpointIndexEntry(storedSessionId, checkpointId, obj["parentCheckpointId"]?.GetValue<string>()));
            }
        }

        if (root["values"] is JsonObject values)
        {
            foreach (KeyValuePair<string, JsonNode?> pair in values)
            {
                if (pair.Value is not null)
                {
                    document.Values[pair.Key] = pair.Value.ToJsonString();
                }
            }
        }

        return document;
    }

    private void Save(CheckpointDocument document)
    {
        var index = new JsonArray();
        foreach (CheckpointIndexEntry entry in document.Index)
        {
            var item = new JsonObject
            {
                ["sessionId"] = entry.SessionId,
                ["checkpointId"] = entry.CheckpointId,
            };
            if (!string.IsNullOrWhiteSpace(entry.ParentCheckpointId))
            {
                item["parentCheckpointId"] = entry.ParentCheckpointId;
            }

            index.Add((JsonNode)item);
        }

        var values = new JsonObject();
        foreach (KeyValuePair<string, string> pair in document.Values)
        {
            values[pair.Key] = JsonNode.Parse(pair.Value);
        }

        save(new JsonObject { ["index"] = index, ["values"] = values }.ToJsonString());
    }

    private sealed class CheckpointDocument
    {
        public List<CheckpointIndexEntry> Index { get; } = [];

        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
    }

    private sealed record CheckpointIndexEntry(string SessionId, string CheckpointId, string? ParentCheckpointId);
}
