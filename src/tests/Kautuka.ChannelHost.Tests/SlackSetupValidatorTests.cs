using System.Net;

using Kautuka.ChannelContracts.Channels;
using Kautuka.ChannelHost.Providers.Slack;

namespace Kautuka.ChannelHost.Tests;

public sealed class SlackSetupValidatorTests
{
    [Fact]
    public async Task ValidationRequiresEveryBotScopeFromTheGeneratedManifest()
    {
        string[] scopes = SlackSetupManifest.RequiredBotScopes.Where(static scope => scope != "users:read").ToArray();
        using SlackApiHandler handler = new(scopes);
        SlackSetupValidator.Result result = await SlackSetupValidator.ValidateAsync(
            new TestHttpClientFactory(handler),
            (_, _, _) => Task.FromResult<string?>(null),
            "slack-registration",
            "xoxb-valid-token",
            "xapp-valid-token",
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("This Slack app doesn't match Kautuka's private setup. Recreate it from the manifest link.", result.Detail);
        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData("channels:history")]
    [InlineData("groups:history")]
    [InlineData("mpim:history")]
    [InlineData("app_mentions:read")]
    public async Task ValidationRejectsAnySharedConversationScope(string forbiddenScope)
    {
        using SlackApiHandler handler = new([.. SlackSetupManifest.RequiredBotScopes, forbiddenScope]);
        SlackSetupValidator.Result result = await SlackSetupValidator.ValidateAsync(
            new TestHttpClientFactory(handler), (_, _, _) => Task.FromResult<string?>(null),
            "slack-registration", "xoxb-valid-token", "xapp-valid-token", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("This Slack app doesn't match Kautuka's private setup. Recreate it from the manifest link.", result.Detail);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task ValidationChecksBotIdentityScopesAndSocketModeAccess()
    {
        using SlackApiHandler handler = new(SlackSetupManifest.RequiredBotScopes);
        bool identitySynchronized = false;
        SlackSetupValidator.Result result = await SlackSetupValidator.ValidateAsync(
            new TestHttpClientFactory(handler),
            (_, token, _) =>
            {
                identitySynchronized = token == "xoxb-valid-token";
                return Task.FromResult<string?>(null);
            },
            "slack-registration",
            "xoxb-valid-token",
            "xapp-valid-token",
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(identitySynchronized);
        Assert.Equal(2, handler.RequestCount);
        Assert.Contains("bot_identity_verified", result.Detail, StringComparison.Ordinal);
        Assert.Contains("socket_token_verified", result.Detail, StringComparison.Ordinal);
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class SlackApiHandler(IEnumerable<string> scopes) : HttpMessageHandler
    {
        private readonly string[] scopes = [.. scopes];
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri?.AbsolutePath == "/api/apps.connections.open"
                    ? "{\"ok\":true,\"url\":\"wss://wss.slack.com/link/?ticket=test&app_id=A123\"}"
                    : "{\"ok\":true,\"team_id\":\"T123\",\"user_id\":\"U123\"}"),
            };
            if (request.RequestUri?.AbsolutePath == "/api/auth.test")
            {
                response.Headers.TryAddWithoutValidation("x-oauth-scopes", string.Join(',', scopes));
            }
            return Task.FromResult(response);
        }
    }
}
