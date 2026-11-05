using System.Text.Json;

using Kautuka.Gateway.Domain.Connectors;

namespace Kautuka.IntegrationTests;

public sealed class RemoteMcpConfigurationTests
{
    [Theory]
    [InlineData("https://user:synthetic@example.com/mcp")]
    [InlineData("https://example.com/mcp?api_key=synthetic")]
    [InlineData("https://example.com/mcp?%74oken=synthetic")]
    [InlineData("https://example.com/mcp?tavilyApiKey=synthetic")]
    [InlineData("https://example.com/mcp#synthetic")]
    [InlineData("file:///tmp/mcp")]
    public void RejectsCredentialBearingEndpointWithoutEchoingIt(string input)
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => RemoteMcpConfigurationValidator.ValidateEndpoint(new Uri(input)));
        Assert.DoesNotContain("synthetic", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AllowsOrdinaryProviderConfigurationQueries()
        => RemoteMcpConfigurationValidator.ValidateEndpoint(new Uri("https://example.com/mcp?tools=search&login"));

    [Fact]
    public void SourceGeneratedJsonRequiresExplicitRequiredAndRoundTrips()
    {
        const string json = """{"credentials":[{"key":"key","label":"Key","required":false}],"httpHeaders":[{"name":"Authorization","credentialKey":"key","prefix":"Bearer "}]}""";
        GatewayConnectorUpsertRequest parsed = JsonSerializer.Deserialize(json, KautukaJsonContext.Default.GatewayConnectorUpsertRequest)!;
        Assert.False(Assert.Single(parsed.Credentials!).Required);
        Assert.Equal("Bearer ", Assert.Single(parsed.HttpHeaders!).Prefix);
        string roundtrip = JsonSerializer.Serialize(parsed, KautukaJsonContext.Default.GatewayConnectorUpsertRequest);
        Assert.Equal(parsed.Credentials, JsonSerializer.Deserialize(roundtrip, KautukaJsonContext.Default.GatewayConnectorUpsertRequest)!.Credentials);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""{"credentials":[{"key":"key","label":"Key"}]}""", KautukaJsonContext.Default.GatewayConnectorUpsertRequest));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""{"credentials":[{"key":"key","label":"Key","required":null}]}""", KautukaJsonContext.Default.GatewayConnectorUpsertRequest));
        GatewayConnectorUpsertRequest nulls = JsonSerializer.Deserialize("""{"credentials":null,"httpHeaders":null}""", KautukaJsonContext.Default.GatewayConnectorUpsertRequest)!;
        Assert.Null(nulls.Credentials);
        Assert.Null(nulls.HttpHeaders);
    }

    [Fact]
    public void RejectsInvalidCredentialDeclarations()
    {
        Assert.Throws<InvalidOperationException>(() => RemoteMcpConfigurationValidator.Validate("http", [new("key", "Key", true), new("key", "Other", false)], []));
        Assert.Throws<InvalidOperationException>(() => RemoteMcpConfigurationValidator.Validate("http", [new(" ", "Key", true)], []));
        Assert.Throws<InvalidOperationException>(() => RemoteMcpConfigurationValidator.Validate("http", [new("key", " ", true)], []));
    }

    [Fact]
    public void RejectsInvalidHeadersWithoutExposingValues()
    {
        RemoteMcpCredential[] credentials = [new("key", "Key", true)];
        RemoteMcpHeader[][] bad = [
            [new("X-Test", "a"), new("x-test", "b")],
            [new("X-Test", "sensitive-example", "key")],
            [new("X-Test", CredentialKey: "missing")],
            [new("X-Test", "sensitive-example\r\nother")],
            [new("Authorization", "sensitive-example")],
            [new("X-Auth-Token", "sensitive-example")],
            [new("X-Service-ApiKey", "sensitive-example")],
            [new("X-Service-Api_Key", "sensitive-example")],
            [new("Proxy-Authorization", CredentialKey: "key")],
            [new("Host", "example")],
            [new("MCP-Session-Id", "example")],
            [new("X-Test", CredentialKey: "key", Prefix: "sensitive-example\r\n")],
            Enumerable.Range(0, 5).Select(i => new RemoteMcpHeader($"X-{i}", new string('x', 8192))).ToArray(),
            [new(new string('n', 257), "value")],
            [new("bad name", "value")],
            [new("X-Test", new string('x', 8193))],
            Enumerable.Range(0, 21).Select(i => new RemoteMcpHeader($"X-{i}", "v")).ToArray()
        ];
        foreach (RemoteMcpHeader[] headers in bad)
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => RemoteMcpConfigurationValidator.Validate("http", credentials, headers));
            Assert.DoesNotContain("sensitive-example", ex.Message, StringComparison.Ordinal);
        }
        RemoteMcpConfigurationValidator.Validate("http", credentials, [new("X-Mode", "free"), new("Authorization", CredentialKey: "key", Prefix: "Bearer ")]);
        Assert.Throws<InvalidOperationException>(() => RemoteMcpConfigurationValidator.Validate("container", credentials, []));
    }
}
