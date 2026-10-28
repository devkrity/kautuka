using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Endpoints.Runtime;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using static Kautuka.IntegrationTests.NaradTestSupport;

namespace Kautuka.IntegrationTests;

public sealed class NaradEndpointTests
{
    [Fact]
    public async Task CapabilitiesReturnsNaradProfileNegotiation()
    {
        await using WebApplication app = await BuildAppAsync().ConfigureAwait(true);
        HttpClient client = app.GetTestClient();

        using HttpResponseMessage response = await client
            .GetAsync(new Uri("/api/narad/v1/capabilities?agentId=agent-1&sessionId=session-1", UriKind.Relative), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(NaradProtocolVersions.V1, body.GetProperty("protocolVersion").GetString());
        Assert.Equal("narad.graph/v1", body.GetProperty("graphSchemaVersion").GetString());
        Assert.False(body.GetProperty("features").GetProperty("workflow").GetBoolean());
        string?[] profiles = [.. body.GetProperty("activeProfiles").EnumerateArray().Select(static item => item.GetString())];
        Assert.Contains("narad.ui/v1", profiles);
        Assert.DoesNotContain("narad.workflow/v1", profiles);
    }

    [Fact]
    public async Task SessionEventsReturnsBoundedHistoryAfterCursor()
    {
        NaradPersistedEvent[] seed =
        [
            AuthorityEvent("evt-1", NaradEventTypes.RunStarted, "session-1", 1, "run-1", "run-1", 1),
            AuthorityEvent("evt-2", NaradEventTypes.MessageUserRecorded, "session-1", 2, body: new JsonObject
            {
                ["messageId"] = "user-1",
                ["content"] = "hello",
            }),
            AuthorityEvent("evt-3", NaradEventTypes.RunFinished, "session-1", 3, "run-1", "run-1", 2),
        ];

        await using WebApplication app = await BuildAppAsync(seed).ConfigureAwait(true);
        HttpClient client = app.GetTestClient();

        using HttpResponseMessage response = await client
            .GetAsync(new Uri("/api/narad/v1/sessions/session-1/events?agentId=agent-1&afterSessionSequence=1&limit=1", UriKind.Relative), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(1, body.GetProperty("events").GetArrayLength());
        Assert.Equal("evt-2", body.GetProperty("events")[0].GetProperty("eventId").GetString());
        Assert.Equal(2, body.GetProperty("nextSessionSequence").GetInt64());
    }

    [Fact]
    public async Task SessionEventsWithoutLimitReturnsOkAtDefaultPeek()
    {
        NaradPersistedEvent[] seed =
        [
            AuthorityEvent("evt-1", NaradEventTypes.RunStarted, "session-1", 1, "run-1", "run-1", 1),
            AuthorityEvent("evt-2", NaradEventTypes.RunFinished, "session-1", 2, "run-1", "run-1", 2),
        ];

        await using WebApplication app = await BuildAppAsync(seed).ConfigureAwait(true);
        HttpClient client = app.GetTestClient();

        using HttpResponseMessage response = await client
            .GetAsync(new Uri("/api/narad/v1/sessions/session-1/events?agentId=agent-1", UriKind.Relative), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(2, body.GetProperty("events").GetArrayLength());
        Assert.False(body.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task StreamAttachEmitsCapabilitiesHydrateAndAuthorityWithSseIds()
    {
        NaradPersistedEvent[] seed =
        [
            AuthorityEvent("evt-1", NaradEventTypes.RunStarted, "session-1", 1, "run-graph", "run-graph", 1, new JsonObject { ["trigger"] = "chat" }),
            AuthorityEvent("evt-2", NaradEventTypes.MessageUserRecorded, "session-1", 2, body: new JsonObject
            {
                ["messageId"] = "user-1",
                ["content"] = "search",
            }),
            AuthorityEvent("evt-3", NaradEventTypes.ToolCallStarted, "session-1", 3, "run-graph", "run-graph", 2, new JsonObject
            {
                ["toolCallId"] = "tool-1",
                ["toolName"] = "search",
                ["executor"] = "backend",
            }),
        ];

        await using WebApplication app = await BuildAppAsync(seed).ConfigureAwait(true);
        HttpClient client = app.GetTestClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        using HttpResponseMessage response = await client
            .GetAsync(new Uri("/api/narad/v1/sessions/session-1/stream?agentId=agent-1&afterSessionSequence=0&hydrate=true", UriKind.Relative), HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        List<SseFrame> frames = await ReadSseFramesAsync(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(true), 8, timeout.Token).ConfigureAwait(true);

        JsonObject[] payloads = [.. frames.Select(frame => JsonNode.Parse(frame.Data)!.AsObject())];
        Assert.Contains(payloads, payload => payload["protocolVersion"]?.GetValue<string>() == NaradProtocolVersions.V1
            && payload["activeProfiles"] is not null);
        Assert.Contains(payloads, payload => payload["type"]?.GetValue<string>() == "messages.snapshot");
        Assert.Contains(payloads, payload => payload["type"]?.GetValue<string>() == NaradEventTypes.GraphSnapshot);

        SseFrame? firstAuthority = frames.FirstOrDefault(frame =>
            JsonNode.Parse(frame.Data)!["eventId"]?.GetValue<string>() == "evt-1");
        Assert.NotNull(firstAuthority);
        Assert.Equal("evt-1", firstAuthority.Id);
    }

    [Fact]
    public async Task StreamDedupesRepeatedEventIdsOnLiveReplay()
    {
        NaradPersistedEvent started = AuthorityEvent("evt-live", NaradEventTypes.RunStarted, "session-1", 1, "run-live", "run-live", 1);
        await using WebApplication app = await BuildAppAsync([started]).ConfigureAwait(true);
        RuntimeEventStreamHub hub = (RuntimeEventStreamHub)app.Services.GetRequiredService<IRuntimeEventStreamHub>();
        hub.Publish(started);

        HttpClient client = app.GetTestClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        using HttpResponseMessage response = await client
            .GetAsync(new Uri("/api/narad/v1/sessions/session-1/stream?agentId=agent-1&afterSessionSequence=0&hydrate=false", UriKind.Relative), HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(true);

        List<SseFrame> frames = await ReadSseFramesAsync(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(true), 10, timeout.Token).ConfigureAwait(true);
        int authorityCount = frames.Count(frame =>
            string.Equals(JsonNode.Parse(frame.Data)!["eventId"]?.GetValue<string>(), "evt-live", StringComparison.Ordinal));
        Assert.Equal(1, authorityCount);
    }

    [Fact]
    public async Task CrossSessionAccessReturnsForbidden()
    {
        await using WebApplication app = await BuildAppAsync(authGuard: new DenyCrossSessionAuthGuard()).ConfigureAwait(true);
        HttpClient client = app.GetTestClient();

        using HttpResponseMessage response = await client
            .GetAsync(new Uri("/api/narad/v1/sessions/session-1/events?agentId=agent-1", UriKind.Relative), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ClientAttachIsGatedWhenExecutorUnsupported()
    {
        await using WebApplication app = await BuildAppAsync().ConfigureAwait(true);
        HttpClient client = app.GetTestClient();

        using HttpResponseMessage attach = await client
            .PostAsJsonAsync("/api/narad/v1/client/attach", new { clientExecutorId = "client-1" }, TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Conflict, attach.StatusCode);
    }

    [Fact]
    public async Task SendMessageIntentAcceptsWithoutResumeArray()
    {
        await using WebApplication app = await BuildAppAsync().ConfigureAwait(true);
        HttpClient client = app.GetTestClient();

        using HttpResponseMessage response = await client
            .PostAsJsonAsync(
                "/api/narad/v1/agents/agent-1/sessions/session-1/messages",
                new { message = "hello", expectedRevision = 0 },
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal("accepted", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task OversizeMessageIsRejected()
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
    public async Task SessionWatchEmitsSessionRevisionCursor()
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
            .GetAsync(new Uri("/api/narad/v1/sessions/session-1/watch?agentId=agent-1", UriKind.Relative), HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(true);

        List<SseFrame> frames = await ReadSseFramesAsync(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(true), 1, timeout.Token).ConfigureAwait(true);
        JsonObject payload = JsonNode.Parse(frames[0].Data)!.AsObject();
        Assert.Equal(NaradEventTypes.SessionRevision, payload["type"]!.GetValue<string>());
        Assert.Equal(1, payload["revision"]!.GetValue<long>());
        Assert.Equal(1, payload["cursor"]!["sessionSequence"]!.GetValue<long>());
        Assert.Equal("session-revision:session-1:1", frames[0].Id);
    }

    [Fact]
    public async Task CapabilitiesReturnsForbiddenForInaccessibleSession()
    {
        await using WebApplication app = await BuildAppAsync(authGuard: new DenyCrossSessionAuthGuard()).ConfigureAwait(true);
        HttpClient client = app.GetTestClient();

        using HttpResponseMessage response = await client
            .GetAsync(new Uri("/api/narad/v1/capabilities?agentId=agent-1&sessionId=session-1", UriKind.Relative), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
