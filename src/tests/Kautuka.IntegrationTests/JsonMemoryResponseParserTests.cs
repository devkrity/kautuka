using System.Text.Json;

using Kautuka.Gateway.Domain.Memory;

using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class JsonMemoryResponseParserTests
{
    private readonly JsonMemoryResponseParser parser = new(NullLogger<JsonMemoryResponseParser>.Instance);

    [Fact]
    public void TryGetMemoriesArrayParsesPlainJsonPayload()
    {
        bool ok = parser.TryGetMemoriesArray(
            "{\"memories\":[{\"title\":\"name\",\"content\":\"My name is Sam\"}]}",
            "agent",
            "DevAgent",
            out JsonElement memories);

        Assert.True(ok);
        Assert.Equal(JsonValueKind.Array, memories.ValueKind);
        Assert.Single(memories.EnumerateArray());
    }

    [Fact]
    public void TryGetMemoriesArrayParsesFencedJsonPayload()
    {
        const string responseText = "```json\n{\"memories\":[{\"content\":\"Use aspire for dotnet services\"}]}\n```";

        bool ok = parser.TryGetMemoriesArray(responseText, "agent", "DevAgent", out JsonElement memories);

        Assert.True(ok);
        Assert.Single(memories.EnumerateArray());
    }

    [Fact]
    public void TryGetMemoriesArrayParsesJsonAfterLeadingProse()
    {
        const string responseText = "Here is the extracted memory:\n{\"memories\":[{\"content\":\"Prefers concise answers\"}]}";

        bool ok = parser.TryGetMemoriesArray(responseText, "agent", "DevAgent", out JsonElement memories);

        Assert.True(ok);
        Assert.Equal("Prefers concise answers", memories[0].GetProperty("content").GetString());
    }

    [Fact]
    public void TryGetMemoriesArrayHandlesEscapedQuotesInsideString()
    {
        const string responseText = "prefix {\"memories\":[{\"content\":\"Call me \\\"Sam\\\"\"}]} suffix";

        bool ok = parser.TryGetMemoriesArray(responseText, "agent", "DevAgent", out JsonElement memories);

        Assert.True(ok);
        Assert.Equal("Call me \"Sam\"", memories[0].GetProperty("content").GetString());
    }

    [Fact]
    public void TryGetMemoriesArrayReturnsFalseForTruncatedJson()
    {
        const string responseText = "{\"memories\":[{\"content\":\"Incomplete\"}]";

        bool ok = parser.TryGetMemoriesArray(responseText, "agent", "DevAgent", out JsonElement memories);

        Assert.False(ok);
        Assert.Equal(JsonValueKind.Undefined, memories.ValueKind);
    }
}
