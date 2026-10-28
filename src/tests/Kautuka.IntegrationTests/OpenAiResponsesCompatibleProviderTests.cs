using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.AI;

namespace Kautuka.IntegrationTests;

public sealed class OpenAiResponsesCompatibleProviderTests
{
    [Fact]
    public async Task TryCreatePostsToResponsesWithStoreDisabled()
    {
        using var handler = new RecordingHandler();
        var provider = new OpenAiResponsesCompatibleProvider(
            defaultBaseUrl: "http://127.0.0.1/v1",
            transportHandler: handler);
        IChatClient? client = provider.TryCreate(new JsonObject(), "gpt-4.1-mini", "sk-test");
        Assert.NotNull(client);

        ChatResponse response = await client.GetResponseAsync("hi", cancellationToken: TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal("ok", response.Text);
        Assert.NotNull(handler.LastRequestUri);
        Assert.EndsWith("/responses", handler.LastRequestUri.AbsolutePath, StringComparison.OrdinalIgnoreCase);
        using JsonDocument body = JsonDocument.Parse(handler.LastRequestBody);
        Assert.False(body.RootElement.GetProperty("store").GetBoolean());
        Assert.Contains(
            "reasoning.encrypted_content",
            body.RootElement.GetProperty("include").EnumerateArray().Select(static item => item.GetString()),
            StringComparer.Ordinal);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        public string LastRequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            if (request.Content is not null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            const string payload =
                """
                {
                  "id": "resp_test",
                  "object": "response",
                  "created_at": 1710000000,
                  "status": "completed",
                  "model": "gpt-4.1-mini",
                  "store": false,
                  "output": [
                    {
                      "id": "msg_test",
                      "type": "message",
                      "role": "assistant",
                      "status": "completed",
                      "content": [
                        {
                          "type": "output_text",
                          "text": "ok",
                          "annotations": []
                        }
                      ]
                    }
                  ],
                  "usage": {
                    "input_tokens": 1,
                    "output_tokens": 1,
                    "total_tokens": 2
                  }
                }
                """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
        }
    }
}
