using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Kautuka.IntegrationTests;

public sealed class RemoteMcpSdkTransportTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("2025-11-25")]
    public async Task SdkInitializeDiscoveryInvocationAndTerminationUsePrivatePerSendInjectionAndScreening(string? protocolVersion)
    {
        const string secret = "synthetic-sdk-credential";
        var requests = new ConcurrentQueue<(string Method, string? Authorization)>();
        var cookies = new ConcurrentQueue<string>();
        string? initializedVersion = null;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using WebApplication app = builder.Build();
        app.MapMethods("/mcp", ["GET", "POST", "DELETE"], async (HttpContext http) =>
        {
            requests.Enqueue((http.Request.Method, http.Request.Headers.Authorization));
            cookies.Enqueue(http.Request.Headers.Cookie.ToString());
            if (http.Request.Method == "DELETE") { http.Response.StatusCode = 204; return; }
            if (http.Request.Method == "GET") { http.Response.StatusCode = 405; return; }
            using JsonDocument document = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("id", out JsonElement id)) { http.Response.StatusCode = 202; return; }
            string method = root.GetProperty("method").GetString()!;
            string result;
            switch (method)
            {
                case "server/discover":
                    // The SDK probes newer discovery before falling back to initialize.
                    http.Response.ContentType = "application/json";
                    await http.Response.WriteAsync($$$"""{"jsonrpc":"2.0","id":{{{id.GetRawText()}}},"error":{"code":-32601,"message":"Method not found"}}""", http.RequestAborted);
                    return;
                case "initialize":
                    string version = root.GetProperty("params").GetProperty("protocolVersion").GetRawText();
                    initializedVersion = root.GetProperty("params").GetProperty("protocolVersion").GetString();
                    http.Response.Headers["Mcp-Session-Id"] = "synthetic-session";
                    http.Response.Headers.SetCookie = "echo=" + secret + "; Path=/";
                    result = $$$"""{"protocolVersion":{{{version}}},"capabilities":{"tools":{}},"serverInfo":{"name":"fixture","version":"1"},"instructions":"{{{secret}}}"}""";
                    break;
                case "tools/list":
                    result = $$$$"""{"tools":[{"name":"search","description":"{{{{secret}}}}","inputSchema":{"type":"object","properties":{}}}]}""";
                    break;
                case "tools/call":
                    result = $$$"""{"content":[{"type":"text","text":"{{{secret}}}"}],"isError":false}""";
                    break;
                case "ping":
                    result = "{}";
                    break;
                default: throw new InvalidOperationException("Unexpected fixture method: " + method);
            }
            http.Response.ContentType = "application/json";
            await http.Response.WriteAsync($$$"""{"jsonrpc":"2.0","id":{{{id.GetRawText()}}},"result":{{{result}}}}""", http.RequestAborted);
        });
        await app.StartAsync(TestContext.Current.CancellationToken);
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var endpoint = new Uri(address + "/mcp");
        int resolutions = 0;
        var context = new RemoteMcpRequestContext(endpoint, "fixture", "owner", false,
            [new RemoteMcpCredential("key", "API key", true)],
            [new RemoteMcpHeader("Authorization", CredentialKey: "key", Prefix: "Bearer ")],
            (keys, _) =>
            {
                Interlocked.Increment(ref resolutions);
                return Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { [Assert.Single(keys)] = secret });
            }, CredentialNamespace: "fixture-incarnation", ProtocolVersion: protocolVersion);
        await using (McpClient client = await new McpHttpClientFactory(new AllowAllTestChecker()).CreateAsync(new HttpClientTransportOptions { Endpoint = endpoint }, true, context, TestContext.Current.CancellationToken))
        {
            IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("[redacted]", Assert.Single(tools).Description);
            CallToolResult result = await client.CallToolAsync("search", new Dictionary<string, object?>(), cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("[redacted]", Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
        }
        Assert.True(requests.Count >= 4);
        Assert.Contains(requests, request => request.Method == "DELETE");
        Assert.All(requests, request => Assert.Equal("Bearer " + secret, request.Authorization));
        Assert.All(cookies, cookie => Assert.Empty(cookie));
        Assert.Equal(requests.Count, resolutions);
        if (protocolVersion is not null) Assert.Equal(protocolVersion, initializedVersion);
        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    private sealed class AllowAllTestChecker : Kautuka.NetworkAccess.Client.INetworkAccessChecker
    {
        public Task<Kautuka.NetworkAccess.Client.NetworkAccessCheckResult> CheckAsync(
            Kautuka.NetworkAccess.Client.OutboundTarget target,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Kautuka.NetworkAccess.Client.NetworkAccessCheckResult.CreateAllowed("test_allowed"));
    }
}
