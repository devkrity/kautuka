using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Auth;
using Kautuka.Gateway.Endpoints.Runtime;
using Kautuka.Gateway.RuntimePlane.Projection;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

using static Kautuka.IntegrationTests.NaradTestSupport;

namespace Kautuka.IntegrationTests;

public sealed class NaradSecurityTests
{
    [Fact]
    public async Task UnauthenticatedCapabilitiesReturnsUnauthorized()
    {
        await using WebApplication app = await BuildAppAsync(authGuard: new UnauthenticatedAuthGuard()).ConfigureAwait(true);
        HttpClient client = app.GetTestClient();

        using HttpResponseMessage response = await client
            .GetAsync(new Uri("/api/narad/v1/capabilities?agentId=agent-1&sessionId=session-1", UriKind.Relative), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CrossSessionWatchReturnsForbidden()
    {
        await using WebApplication app = await BuildAppAsync(authGuard: new DenyCrossSessionAuthGuard()).ConfigureAwait(true);
        HttpClient client = app.GetTestClient();

        using HttpResponseMessage response = await client
            .GetAsync(new Uri("/api/narad/v1/sessions/session-1/watch?agentId=agent-1", UriKind.Relative), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CrossSessionStreamReturnsForbidden()
    {
        await using WebApplication app = await BuildAppAsync(authGuard: new DenyCrossSessionAuthGuard()).ConfigureAwait(true);
        HttpClient client = app.GetTestClient();

        using HttpResponseMessage response = await client
            .GetAsync(new Uri("/api/narad/v1/sessions/session-1/stream?agentId=agent-1&afterSessionSequence=0", UriKind.Relative), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CrossSessionMessageIntentReturnsForbidden()
    {
        await using WebApplication app = await BuildAppAsync(authGuard: new DenyCrossSessionAuthGuard()).ConfigureAwait(true);
        HttpClient client = app.GetTestClient();

        using HttpResponseMessage response = await client
            .PostAsJsonAsync(
                "/api/narad/v1/agents/agent-1/sessions/session-1/messages",
                new { message = "hello" },
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task HistoryLimitAboveMaxIsClamped()
    {
        NaradPersistedEvent[] seed =
        [
            AuthorityEvent("evt-1", NaradEventTypes.RunStarted, "session-1", 1, "run-1", "run-1", 1),
        ];

        await using WebApplication app = await BuildAppAsync(seed).ConfigureAwait(true);
        HttpClient client = app.GetTestClient();

        using HttpResponseMessage response = await client
            .GetAsync(
                new Uri("/api/narad/v1/sessions/session-1/events?agentId=agent-1&limit=99999", UriKind.Relative),
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.True(body.GetProperty("events").GetArrayLength() <= NaradProfileDiscovery.MaxHistoryLimit);
    }

    [Fact]
    public async Task ClientAttachFailsClosedWhenExecutorUnsupported()
    {
        await using WebApplication app = await BuildAppAsync().ConfigureAwait(true);
        HttpClient client = app.GetTestClient();

        using HttpResponseMessage response = await client
            .PostAsJsonAsync("/api/narad/v1/client/attach", new { clientExecutorId = "desktop-1" }, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task StreamClampsNegativeAfterSessionSequenceToZero()
    {
        NaradPersistedEvent[] seed =
        [
            AuthorityEvent("evt-1", NaradEventTypes.RunStarted, "session-1", 1, "run-1", "run-1", 1),
        ];

        await using WebApplication app = await BuildAppAsync(seed).ConfigureAwait(true);
        HttpClient client = app.GetTestClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        using HttpResponseMessage response = await client
            .GetAsync(
                new Uri("/api/narad/v1/sessions/session-1/stream?agentId=agent-1&afterSessionSequence=-5&hydrate=false", UriKind.Relative),
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token)
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        List<SseFrame> frames = await ReadSseFramesAsync(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(true), 4, timeout.Token).ConfigureAwait(true);
        Assert.Contains(frames, frame => frame.Data.Contains("\"eventId\":\"evt-1\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OversizeMessageBodyIsRejectedBeforeMutation()
    {
        await using WebApplication app = await BuildAppAsync().ConfigureAwait(true);
        HttpClient client = app.GetTestClient();
        string huge = new('x', NaradRequestParsing.MaxMessageLength + 1);

        using HttpResponseMessage response = await client
            .PostAsJsonAsync(
                "/api/narad/v1/agents/agent-1/sessions/session-1/messages",
                new { message = huge },
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task MalformedJsonIntentIsRejected()
    {
        await using WebApplication app = await BuildAppAsync().ConfigureAwait(true);
        HttpClient client = app.GetTestClient();
        using var content = new StringContent("{", Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await client
            .PostAsync(new Uri("/api/narad/v1/agents/agent-1/sessions/session-1/messages", UriKind.Relative), content, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.True(
            response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnsupportedMediaType,
            $"Unexpected status {response.StatusCode}.");
    }

    [Fact]
    public void PublicFilterDeliversNegotiatedProfileEventsAndRedactsIdentityFields()
    {
        NaradPersistedEvent toolStarted = AuthorityEvent(
            "evt-tool",
            NaradEventTypes.ToolCallStarted,
            "session-1",
            2,
            "run-1",
            "run-1",
            2,
            new JsonObject
            {
                ["toolCallId"] = "tool-1",
                ["toolName"] = "search",
                ["actorId"] = "operator-1",
            });
        NaradPersistedEvent publicProgress = AuthorityEvent(
            "evt-progress-public",
            NaradEventTypes.ProgressRecorded,
            "session-1",
            3,
            "run-1",
            "run-1",
            3,
            new JsonObject
            {
                ["progressId"] = "phase-plan",
                ["category"] = "phase",
                ["label"] = "Planning",
                ["status"] = "active",
                ["privacy"] = "public",
                ["actorId"] = "operator-1",
            });
        NaradPersistedEvent reasoningDelta = AuthorityEvent(
            "evt-reason",
            NaradEventTypes.ReasoningDelta,
            "session-1",
            4,
            "run-1",
            "run-1",
            4,
            new JsonObject
            {
                ["messageId"] = "reason-1",
                ["delta"] = "thinking",
                ["ownerUserId"] = "operator-1",
            });

        Assert.True(NaradPublicAuthorityFilter.TryBuildPublicPayload(toolStarted, out JsonObject toolPayload));
        Assert.False(toolPayload.ContainsKey("actorId"));
        Assert.Equal("tool-1", toolPayload["toolCallId"]!.GetValue<string>());

        Assert.True(NaradPublicAuthorityFilter.TryBuildPublicPayload(publicProgress, out JsonObject progressPayload));
        Assert.Equal("public", progressPayload["privacy"]!.GetValue<string>());
        Assert.False(progressPayload.ContainsKey("actorId"));

        Assert.True(NaradPublicAuthorityFilter.TryBuildPublicPayload(reasoningDelta, out JsonObject reasoningPayload));
        Assert.False(reasoningPayload.ContainsKey("ownerUserId"));
        Assert.Equal("thinking", reasoningPayload["delta"]!.GetValue<string>());
    }

    [Fact]
    public void PublicFilterOmitsInternalProgressTraceAndSessionRevision()
    {
        NaradPersistedEvent internalProgress = AuthorityEvent(
            "evt-progress-internal",
            NaradEventTypes.ProgressRecorded,
            "session-1",
            2,
            "run-1",
            "run-1",
            2,
            new JsonObject
            {
                ["progressId"] = "connector-search",
                ["category"] = "connector",
                ["label"] = "Search",
                ["status"] = "active",
                ["privacy"] = "internal",
            });
        NaradPersistedEvent trace = AuthorityEvent(
            "evt-trace",
            NaradEventTypes.TraceEvent,
            "session-1",
            3,
            body: new JsonObject { ["name"] = "credential.host.check" });
        NaradPersistedEvent sessionRevision = AuthorityEvent(
            "evt-revision",
            NaradEventTypes.SessionRevision,
            "session-1",
            4,
            body: new JsonObject { ["revision"] = 4 });

        Assert.False(NaradPublicAuthorityFilter.TryBuildPublicPayload(internalProgress, out _));
        Assert.False(NaradPublicAuthorityFilter.TryBuildPublicPayload(trace, out _));
        Assert.False(NaradPublicAuthorityFilter.TryBuildPublicPayload(sessionRevision, out _));
    }

    [Fact]
    public void PublicFilterDoesNotMutateStoredEventJson()
    {
        NaradPersistedEvent persistedEvent = AuthorityEvent(
            "evt-progress",
            NaradEventTypes.ProgressRecorded,
            "session-1",
            2,
            "run-1",
            "run-1",
            2,
            new JsonObject
            {
                ["progressId"] = "phase-plan",
                ["category"] = "phase",
                ["label"] = "Planning",
                ["status"] = "active",
                ["privacy"] = "public",
                ["actorId"] = "operator-1",
            });
        string originalJson = persistedEvent.EventJson;

        Assert.True(NaradPublicAuthorityFilter.TryBuildPublicPayload(persistedEvent, out JsonObject _));
        Assert.Equal(originalJson, persistedEvent.EventJson);
        Assert.Contains("\"actorId\":\"operator-1\"", persistedEvent.EventJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublicHistoryAndStreamDeliverPublicProgressToolsAndReasoning()
    {
        NaradPersistedEvent[] seed =
        [
            AuthorityEvent("evt-1", NaradEventTypes.RunStarted, "session-1", 1, "run-1", "run-1", 1),
            AuthorityEvent(
                "evt-2",
                NaradEventTypes.ProgressRecorded,
                "session-1",
                2,
                "run-1",
                "run-1",
                2,
                new JsonObject
                {
                    ["progressId"] = "phase-plan",
                    ["category"] = "phase",
                    ["label"] = "Planning",
                    ["status"] = "active",
                    ["privacy"] = "public",
                }),
            AuthorityEvent(
                "evt-3",
                NaradEventTypes.ProgressRecorded,
                "session-1",
                3,
                "run-1",
                "run-1",
                3,
                new JsonObject
                {
                    ["progressId"] = "connector-search",
                    ["category"] = "connector",
                    ["label"] = "Search",
                    ["status"] = "active",
                    ["privacy"] = "internal",
                }),
            AuthorityEvent(
                "evt-4",
                NaradEventTypes.ToolCallStarted,
                "session-1",
                4,
                "run-1",
                "run-1",
                4,
                new JsonObject { ["toolCallId"] = "tool-1", ["toolName"] = "search", ["actorId"] = "operator-1" }),
            AuthorityEvent(
                "evt-5",
                NaradEventTypes.ReasoningDelta,
                "session-1",
                5,
                "run-1",
                "run-1",
                5,
                new JsonObject { ["messageId"] = "reason-1", ["delta"] = "thinking", ["ownerUserId"] = "operator-1" }),
            AuthorityEvent(
                "evt-6",
                NaradEventTypes.TraceEvent,
                "session-1",
                6,
                body: new JsonObject { ["name"] = "credential.host.check" }),
            AuthorityEvent(
                "evt-7",
                NaradEventTypes.SessionRevision,
                "session-1",
                7,
                body: new JsonObject { ["revision"] = 7 }),
        ];

        await using WebApplication app = await BuildAppAsync(seed).ConfigureAwait(true);
        HttpClient client = app.GetTestClient();

        using HttpResponseMessage historyResponse = await client
            .GetAsync(new Uri("/api/narad/v1/sessions/session-1/events?agentId=agent-1&limit=100", UriKind.Relative), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
        JsonElement historyBody = await historyResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken).ConfigureAwait(true);
        string[] historyTypes = historyBody.GetProperty("events").EnumerateArray().Select(item => item.GetProperty("type").GetString()!).ToArray();
        Assert.Contains("progress.recorded", historyTypes);
        Assert.Contains("tool.call.started", historyTypes);
        Assert.Contains("reasoning.delta", historyTypes);
        Assert.DoesNotContain("trace.event", historyTypes);
        Assert.DoesNotContain("session.revision", historyTypes);
        Assert.DoesNotContain(
            historyBody.GetProperty("events").EnumerateArray().First(item => item.GetProperty("type").GetString() == "progress.recorded").GetRawText(),
            "internal",
            StringComparison.Ordinal);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using HttpResponseMessage streamResponse = await client
            .GetAsync(
                new Uri("/api/narad/v1/sessions/session-1/stream?agentId=agent-1&afterSessionSequence=0&hydrate=false", UriKind.Relative),
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token)
            .ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, streamResponse.StatusCode);
        List<SseFrame> frames = await ReadSseFramesAsync(await streamResponse.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(true), 12, timeout.Token).ConfigureAwait(true);
        string joined = string.Join('\n', frames.Select(frame => frame.Data));
        Assert.Contains("\"type\":\"tool.call.started\"", joined, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"reasoning.delta\"", joined, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"progress.recorded\"", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("\"type\":\"trace.event\"", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("\"type\":\"session.revision\"", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("\"actorId\"", joined, StringComparison.Ordinal);
    }

    private sealed class UnauthenticatedAuthGuard : IGatewayAuthGuard
    {
        public bool TryAuthorize(HttpContext context, out GatewayUser? user)
        {
            user = null;
            return false;
        }

        public IResult? RequireAuthenticated(HttpContext context, out GatewayUser? user)
        {
            user = null;
            return Results.Unauthorized();
        }

        public IResult? RequireAdmin(HttpContext context, out GatewayUser? user)
        {
            user = null;
            return Results.Unauthorized();
        }
    }
}
