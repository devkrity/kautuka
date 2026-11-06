using Grpc.Core;

using Kautuka.CredentialsHost.Database;
using Kautuka.CredentialsHost.Security;

namespace Kautuka.IntegrationTests;

public sealed class ConnectorDraftRepositoryTests
{
    [Fact]
    public void QuotaAndExpiryAreAuthoritative()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-draft-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var database = new CredentialsDatabase(root); CredentialsSchemaInitializer.EnsureReady(database);
            using var ring = new CredentialKeyRing(root); var clock = new MutableClock();
            var drafts = new ConnectorDraftRepository(database, new CredentialsVaultService(ring), clock);
            string[] ids = [.. Enumerable.Range(0, 5).Select(_ => Guid.NewGuid().ToString("N"))];
            foreach (string id in ids) drafts.Create(id, "alice", "connector", "user", "{}", "", "https://example.com/mcp", "https://example.com/mcp", true, "11111111111111111111111111111111", [], new Dictionary<string, string>(), []);
            RpcException quota = Assert.Throws<RpcException>(() => drafts.Create(Guid.NewGuid().ToString("N"), "alice", "connector", "user", "{}", "", "https://example.com/mcp", "https://example.com/mcp", true, "11111111111111111111111111111111", [], new Dictionary<string, string>(), []));
            Assert.Equal(StatusCode.ResourceExhausted, quota.StatusCode);
            clock.Now = clock.Now.AddMinutes(15);
            Assert.Equal(StatusCode.NotFound, Assert.Throws<RpcException>(() => drafts.Get(ids[0], "alice")).StatusCode);
            drafts.CleanupExpired();
            _ = drafts.Create(Guid.NewGuid().ToString("N"), "alice", "connector", "user", "{}", "", "https://example.com/mcp", "https://example.com/mcp", true, "11111111111111111111111111111111", [], new Dictionary<string, string>(), []);
        }
        finally
        {
            SqliteTestPoolCleanup.ClearPoolsUnderDirectory(root);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-11-05T00:00:00Z", global::System.Globalization.CultureInfo.InvariantCulture);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
