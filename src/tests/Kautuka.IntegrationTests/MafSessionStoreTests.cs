using System.Text.Json;

using Microsoft.Extensions.Configuration;

namespace Kautuka.IntegrationTests;

public sealed class MafSessionStoreTests
{
    [Fact]
    public async Task SaveAndLoadStripApprovalContentFromChatHistoryOnly()
    {
        using TemporaryDatabase temp = TemporaryDatabase.Create();
        temp.EnsureReady();

        var database = new KautukaDatabase(temp.Configuration);
        var store = new MafSessionStore(database);

        using JsonDocument state = JsonDocument.Parse("""
            {
              "history": [
                {
                  "content": [
                    { "$type": "text", "text": "top-level-keep" },
                    { "$type": "toolApprovalRequest", "requestId": "apr-history-root" }
                  ]
                }
              ],
              "stateBag": {
                "InMemoryChatHistoryProvider": {
                  "messages": [
                    {
                      "role": "assistant",
                      "contents": [
                        { "$type": "text", "text": "keep" },
                        { "$type": "toolApprovalRequest", "requestId": "apr-history" },
                        { "$type": "toolApprovalResponse", "approved": true }
                      ]
                    }
                  ]
                },
                "FunctionInvokingChatClient.approvalBypass": {
                  "$type": "toolApprovalRequest",
                  "requestId": "apr-bypass"
                }
              }
            }
            """);

        await store.SaveAsync(
            "session-1",
            "agent-1",
            "agent",
            state.RootElement,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        JsonElement cleaned = Assert.IsType<JsonElement>(
            await store.LoadAsync("session-1", TestContext.Current.CancellationToken).ConfigureAwait(true));

        JsonElement historyContents = cleaned
            .GetProperty("stateBag")
            .GetProperty("InMemoryChatHistoryProvider")
            .GetProperty("messages")[0]
            .GetProperty("contents");
        Assert.Equal(1, historyContents.GetArrayLength());
        Assert.Equal("text", historyContents[0].GetProperty("$type").GetString());
        Assert.Equal("keep", historyContents[0].GetProperty("text").GetString());

        JsonElement bypass = cleaned
            .GetProperty("stateBag")
            .GetProperty("FunctionInvokingChatClient.approvalBypass");
        Assert.Equal("toolApprovalRequest", bypass.GetProperty("$type").GetString());
        Assert.Equal("apr-bypass", bypass.GetProperty("requestId").GetString());

        JsonElement topLevelHistory = cleaned.GetProperty("history")[0].GetProperty("content");
        Assert.Equal(2, topLevelHistory.GetArrayLength());
        Assert.Equal("apr-history-root", topLevelHistory[1].GetProperty("requestId").GetString());
    }

    [Fact]
    public async Task LoadCleanedForApprovalResumeAsyncMatchesLoadAsync()
    {
        using TemporaryDatabase temp = TemporaryDatabase.Create();
        temp.EnsureReady();

        var database = new KautukaDatabase(temp.Configuration);
        var store = new MafSessionStore(database);

        using JsonDocument state = JsonDocument.Parse("""
            {
              "stateBag": {
                "InMemoryChatHistoryProvider": {
                  "messages": [
                    {
                      "contents": [
                        { "$type": "text", "text": "keep" },
                        { "$type": "toolApprovalRequest", "requestId": "apr-1" }
                      ]
                    }
                  ]
                },
                "FunctionInvokingChatClient.approvalBypass": {
                  "$type": "toolApprovalRequest",
                  "requestId": "apr-bypass"
                }
              }
            }
            """);

        await store.SaveAsync(
            "session-1",
            "agent-1",
            "agent",
            state.RootElement,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        JsonElement loaded = Assert.IsType<JsonElement>(
            await store.LoadAsync("session-1", TestContext.Current.CancellationToken).ConfigureAwait(true));
        JsonElement cleaned = Assert.IsType<JsonElement>(
            await store.LoadCleanedForApprovalResumeAsync("session-1", TestContext.Current.CancellationToken).ConfigureAwait(true));

        Assert.DoesNotContain("apr-1", loaded.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("apr-bypass", loaded.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(loaded.GetRawText(), cleaned.GetRawText());
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string directoryPath;

        private TemporaryDatabase(string directoryPath, IConfiguration configuration)
        {
            this.directoryPath = directoryPath;
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        public static TemporaryDatabase Create()
        {
            string directoryPath = Path.Combine(Path.GetTempPath(), "kautuka-maf-session-store-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directoryPath);
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Kautuka:ContainerStateRoot"] = directoryPath,
                })
                .Build();

            return new TemporaryDatabase(directoryPath, configuration);
        }

        public void EnsureReady()
        {
            var database = new KautukaDatabase(Configuration);
            database.EnsureReady();
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(directoryPath, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
