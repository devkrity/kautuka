using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Kautuka.Gateway.RuntimePlane.Projection;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;

using static Kautuka.IntegrationTests.NaradTestSupport;

namespace Kautuka.IntegrationTests;

public sealed class NaradPerformanceTests
{
    private const int paginationCorpusSize = 600;
    private const int paginationPageSize = 100;
    private const int projectionCorpusSize = 500;
    private static readonly TimeSpan projectionBudget = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task SessionHistoryPaginationIsBoundedAndComplete()
    {
        NaradPersistedEvent[] seed = BuildProgressSeed("session-1", paginationCorpusSize);
        await using WebApplication app = await BuildAppAsync(seed).ConfigureAwait(true);
        HttpClient client = app.GetTestClient();

        var collected = new List<JsonElement>();
        long cursor = 0;
        int pages = 0;
        while (pages < 20)
        {
            pages += 1;
            using HttpResponseMessage response = await client
                .GetAsync(
                    new Uri(
                        $"/api/narad/v1/sessions/session-1/events?agentId=agent-1&afterSessionSequence={cursor}&limit={paginationPageSize}",
                        UriKind.Relative),
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken).ConfigureAwait(true);
            JsonElement.ArrayEnumerator events = body.GetProperty("events").EnumerateArray();
            int pageCount = events.Count();
            Assert.InRange(pageCount, 0, paginationPageSize);
            collected.AddRange(events);

            if (!body.GetProperty("hasMore").GetBoolean())
            {
                break;
            }

            cursor = body.GetProperty("nextSessionSequence").GetInt64();
        }

        Assert.Equal(paginationCorpusSize, collected.Count);
        Assert.Equal(6, pages);
    }

    [Fact]
    public void SessionProjectionRebuildStaysWithinBudget()
    {
        NaradPersistedEvent[] seed = BuildProgressSeed("session-proj", projectionCorpusSize);
        Stopwatch stopwatch = Stopwatch.StartNew();
        NaradSessionProjection projection = NaradSessionProjection.Fold("session-proj", seed);
        stopwatch.Stop();

        Assert.Equal(projectionCorpusSize, projection.LastSessionSequence);
        Assert.True(stopwatch.Elapsed <= projectionBudget, $"Projection took {stopwatch.Elapsed.TotalMilliseconds} ms.");
    }

    private static NaradPersistedEvent[] BuildProgressSeed(string sessionId, int count)
    {
        var events = new NaradPersistedEvent[count];
        events[0] = AuthorityEvent("evt-1", NaradEventTypes.RunStarted, sessionId, 1, "run-page", "run-page", 1);
        for (int index = 2; index <= count; index++)
        {
            events[index - 1] = AuthorityEvent(
                $"evt-{index}",
                NaradEventTypes.MessageUserRecorded,
                sessionId,
                index,
                body: new System.Text.Json.Nodes.JsonObject
                {
                    ["messageId"] = $"message-{index}",
                    ["content"] = $"message {index}",
                });
        }

        return events;
    }
}
