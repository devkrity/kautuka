namespace Kautuka.Gateway.Domain.Connectors;

internal static class RemoteMcpPresets
{
    public static bool Contains(string id) => id is "tavily" or "exa" or "firecrawl";

    public static (string Id, GatewayConnectorUpsertRequest Definition)[] Create() =>
    [
        ("tavily", Definition("Tavily", "tavily", "https://mcp.tavily.com/mcp/",
            "Search and extract web pages. Free access · Usage limits apply. Add your API key for your account's limits.",
            [new("X-Tavily-Access-Mode", "keyless"), new("Authorization", CredentialKey: "api-key", Prefix: "Bearer ")])),
        ("exa", Definition("Exa", "exa", "https://mcp.exa.ai/mcp",
            "Web search and page fetching. Free access · Usage limits apply. Keyless access requires direct IPv4. If access is denied, add your API key.",
            [new("x-api-key", CredentialKey: "api-key")])),
        ("firecrawl", Definition("Firecrawl", "firecrawl", "https://mcp.firecrawl.dev/v2/mcp",
            "Search, scrape and parse pages. Free access · Usage limits apply. An API key enables your account's access and limits.",
            [new("Authorization", CredentialKey: "api-key", Prefix: "Bearer ")])),
    ];

    private static GatewayConnectorUpsertRequest Definition(string name, string presetId, string endpoint,
        string description, RemoteMcpHeader[] headers) => new()
        {
            Name = name,
            PresetId = presetId,
            Description = description,
            Transport = "http",
            HttpUrl = endpoint,
            Enabled = false,
            Credentials = [new("api-key", "API key", false)],
            HttpHeaders = headers,
        };
}
