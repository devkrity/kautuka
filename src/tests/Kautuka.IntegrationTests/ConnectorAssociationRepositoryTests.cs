using Grpc.Core;

using Kautuka.CredentialsHost.Database;
using Kautuka.CredentialsHost.Security;

using Microsoft.Data.Sqlite;

namespace Kautuka.IntegrationTests;

public sealed class ConnectorAssociationRepositoryTests
{
    [Fact]
    public async Task StagingPreservesScopesAndDestinationsAndSurvivesExpiryAndRotation()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-associations", Guid.NewGuid().ToString("N"));
        try
        {
            var db = new CredentialsDatabase(root); CredentialsSchemaInitializer.EnsureReady(db);
            using var ring = new CredentialKeyRing(root); var vault = new CredentialsVaultService(ring); var clock = new TestClock();
            var audit = new CredentialAuditStore(db); var credentials = new CredentialRepository(db, vault, audit);
            const string ns = "11111111111111111111111111111111";
            string key = "__connector_remote__:" + ns + ":destination-a:key";
            string other = "__connector_remote__:" + ns + ":destination-b:key";
            async Task SetAsync(string scope, string subject, string value)
            {
                await credentials.MutateAsync(new CredentialMutation(Guid.NewGuid().ToString("N"), "admin", scope, subject,
                    new Dictionary<string, string> { [key] = value }, null,
                    new CredentialAuditEvent("test", "admin", "set", scope, subject, "batch", "success")), TestContext.Current.CancellationToken);
            }
            await SetAsync("user", "alice", "alice-old");
            var drafts = new ConnectorDraftRepository(db, vault, clock); var associations = new ConnectorAssociationRepository(db, vault, clock);
            string Seed(string actor, string scope, string value, string source)
            {
                string seed = Guid.NewGuid().ToString("N");
                drafts.Create(seed, actor, "connector", scope, "{}", "v1", "https://example.com", "https://example.com", true, ns, [key], new Dictionary<string, string> { [key] = value }, [], source);
                return associations.Stage(seed, actor, source);
            }
            string source = Seed("admin", "system", "system-old", "");
            Assert.Empty(associations.Resolve(source, "connector", ns, "alice", [key], false));
            source = Seed("bob", "user", "bob-old", source);
            source = Seed("alice", "user", "alice-association-old", source);
            string draft = Guid.NewGuid().ToString("N");
            drafts.Create(draft, "alice", "connector", "user", "{}", "v1", "https://example.com", "https://example.com", true, ns, [key], new Dictionary<string, string> { [key] = "alice-new" }, [], source);
            Assert.Throws<RpcException>(() => associations.Stage(draft, "alice", ""));
            string id = associations.Stage(draft, "alice", source);
            Assert.Equal(id, associations.Stage(draft, "alice", source));
            Assert.Throws<RpcException>(() => associations.Stage(draft, "bob", source));
            Assert.Throws<RpcException>(() => associations.Stage(draft, "alice", "different"));
            Assert.Equal("alice-new", associations.Resolve(id, "connector", ns, "alice", [key], true)[key]);
            Assert.Equal("bob-old", associations.Resolve(id, "connector", ns, "bob", [key], true)[key]);
            Assert.Equal("system-old", associations.Resolve(id, "connector", ns, "charlie", [key], true)[key]);
            Assert.Empty(associations.Resolve(id, "connector", ns, "charlie", [key, other], false));
            Assert.Empty(associations.Resolve(id, "connector", ns, "alice", [other], true));
            (List<string> presentUser, List<string> presentSystem) = associations.GetPresence(id, "connector", ns, "alice", [key, other], true);
            Assert.Equal([key], presentUser); Assert.Equal([key], presentSystem);
            Assert.Empty(associations.GetPresence(id, "connector", ns, "charlie", [key], false).SystemKeys);
            Assert.Throws<RpcException>(() => associations.GetPresence(id, "other-connector", ns, "alice", [key], true));
            Assert.Equal(StatusCode.FailedPrecondition, Assert.Throws<RpcException>(() => associations.Resolve(Guid.NewGuid().ToString("N"), "connector", ns, "alice", [key], true)).StatusCode);
            Assert.Equal(StatusCode.FailedPrecondition, Assert.Throws<RpcException>(() => associations.Resolve(id, "other-connector", ns, "alice", [key], true)).StatusCode);
            Assert.Equal(StatusCode.FailedPrecondition, Assert.Throws<RpcException>(() => associations.Resolve(id, "connector", "22222222222222222222222222222222", "alice", [key], true)).StatusCode);
            string nextDraft = Guid.NewGuid().ToString("N");
            drafts.Create(nextDraft, "alice", "connector", "user", "{}", "v2", "https://example.com", "https://example.com", true, ns, [key], new Dictionary<string, string>(), [key], id);
            string next = associations.Stage(nextDraft, "alice", id);
            await using (SqliteConnection auditConnection = await db.OpenConnectionAsync(TestContext.Current.CancellationToken))
            await using (var auditCommand = new SqliteCommand("SELECT COUNT(*) FROM credential_audit WHERE correlation_id=$id AND action='credential-remove' AND outcome='success'", auditConnection))
            {
                auditCommand.Parameters.AddWithValue("$id", nextDraft);
                Assert.Equal(1L, await auditCommand.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            }
            await using (SqliteConnection auditConnection = await db.OpenConnectionAsync(TestContext.Current.CancellationToken))
            await using (var auditCommand = new SqliteCommand("SELECT action,key_id,outcome FROM credential_audit WHERE correlation_id=$id AND action LIKE 'credential-%'", auditConnection))
            {
                auditCommand.Parameters.AddWithValue("$id", draft);
                await using SqliteDataReader auditReader = await auditCommand.ExecuteReaderAsync(TestContext.Current.CancellationToken);
                Assert.True(await auditReader.ReadAsync(TestContext.Current.CancellationToken));
                Assert.Equal("credential-replace", auditReader.GetString(0)); Assert.Equal(key, auditReader.GetString(1)); Assert.Equal("success", auditReader.GetString(2));
                Assert.False(await auditReader.ReadAsync(TestContext.Current.CancellationToken));
            }
            Assert.Equal("system-old", associations.Resolve(next, "connector", ns, "alice", [key], true)[key]);
            Assert.Empty(associations.Resolve(next, "connector", ns, "alice", [key], false));
            Assert.Equal("alice-new", associations.Resolve(id, "connector", ns, "alice", [key], true)[key]);
            Assert.Equal("bob-old", associations.Resolve(next, "connector", ns, "bob", [key], true)[key]);
            string overrideKey = "__connector_http_url__:" + ns + ":connector";
            string mutation = Guid.NewGuid().ToString("N");
            var updates = new Dictionary<string, string> { [key] = "alice-direct", [overrideKey] = "https://personal.example/mcp" };
            string changed = associations.StageMutation(mutation, "alice", "connector", ns, id, "user", updates, []);
            Assert.Equal(changed, associations.StageMutation(mutation, "alice", "connector", ns, id, "user", updates, []));
            Assert.Throws<RpcException>(() => associations.StageMutation(mutation, "alice", "connector", ns, id, "system", updates, []));
            Assert.Equal("alice-direct", associations.Resolve(changed, "connector", ns, "alice", [key], false)[key]);
            Assert.Equal("https://personal.example/mcp", associations.Resolve(changed, "connector", ns, "alice", [overrideKey], false)[overrideKey]);
            Assert.Empty(associations.Resolve(changed, "connector", ns, "bob", [overrideKey], true));
            Assert.Equal("bob-old", associations.Resolve(changed, "connector", ns, "bob", [key], true)[key]);
            Assert.Equal("system-old", associations.Resolve(changed, "connector", ns, "charlie", [key], true)[key]);
            Assert.Equal("alice-new", associations.Resolve(id, "connector", ns, "alice", [key], false)[key]);
            await using (SqliteConnection connection = await db.OpenConnectionAsync(TestContext.Current.CancellationToken))
            using (var command = new SqliteCommand("SELECT encrypted_value FROM connector_credential_association_entries", connection))
            await using (SqliteDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken))
                while (await reader.ReadAsync(TestContext.Current.CancellationToken)) Assert.DoesNotContain("-old", reader.GetString(0), StringComparison.Ordinal);
            clock.Now = clock.Now.AddMinutes(16); drafts.CleanupExpired();
            Assert.Equal(id, associations.Stage(draft, "alice", source));
            ConnectorDraftRecord retained = associations.GetStagedDraft(draft, "alice");
            Assert.Equal("{}", retained.Candidate);
            Assert.Equal("v1", retained.BaseUpdatedAt);
            Assert.Equal(ns, retained.CredentialNamespace);
            Assert.Equal(source, retained.SourceAssociationId);
            Assert.Throws<RpcException>(() => associations.GetStagedDraft(draft, "bob"));
            await using (SqliteConnection afterExpiry = await db.OpenConnectionAsync(TestContext.Current.CancellationToken))
            await using (var remaining = new SqliteCommand("SELECT COUNT(*) FROM connector_credential_draft_entries WHERE draft_id=$id", afterExpiry))
            {
                remaining.Parameters.AddWithValue("$id", draft);
                Assert.Equal(0L, await remaining.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            }
            var rotation = new CredentialKeyRotationService(db, ring, vault, audit);
            _ = await rotation.RunToCompletionAsync(TestContext.Current.CancellationToken);
            Assert.Equal("alice-new", associations.Resolve(id, "connector", ns, "alice", [key], true)[key]);
            Assert.Equal("bob-old", associations.Resolve(next, "connector", ns, "bob", [key], true)[key]);
            (IReadOnlyList<StoredCredential> rows, _) = await credentials.ResolveAsync("alice", System.Text.Json.JsonSerializer.Serialize(new[] { key }), TestContext.Current.CancellationToken);
            Assert.Equal("alice-old", vault.Decrypt(Assert.Single(rows).EncryptedValue, new CredentialEnvelopeIdentity("user", "alice", key)));
            Assert.True(drafts.Cancel(draft, "alice"));
            Assert.Throws<RpcException>(() => associations.GetStagedDraft(draft, "alice"));
            Assert.Throws<RpcException>(() => associations.Stage(draft, "alice", source));
            Assert.Equal("alice-new", associations.Resolve(id, "connector", ns, "alice", [key], false)[key]);
        }
        finally { SqliteTestPoolCleanup.ClearPoolsUnderDirectory(root); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void CorruptDraftLeavesNoStagedAssociation()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-associations", Guid.NewGuid().ToString("N"));
        try
        {
            var db = new CredentialsDatabase(root); CredentialsSchemaInitializer.EnsureReady(db);
            using var ring = new CredentialKeyRing(root); var vault = new CredentialsVaultService(ring); var clock = new TestClock();
            var drafts = new ConnectorDraftRepository(db, vault, clock); var associations = new ConnectorAssociationRepository(db, vault, clock);
            const string ns = "11111111111111111111111111111111"; string key = "__connector_remote__:" + ns + ":destination:key";
            string draft = Guid.NewGuid().ToString("N"); drafts.Create(draft, "alice", "connector", "user", "{}", "", "https://example.com", "", false, ns, [key], new Dictionary<string, string> { [key] = "synthetic" }, []);
            using (SqliteConnection connection = db.OpenConnection()) using (var command = new SqliteCommand("UPDATE connector_credential_draft_entries SET encrypted_value='invalid'", connection)) command.ExecuteNonQuery();
            Assert.Throws<CredentialIntegrityException>(() => associations.Stage(draft, "alice", ""));
            using SqliteConnection verify = db.OpenConnection(); using var count = new SqliteCommand("SELECT COUNT(*) FROM connector_credential_associations", verify);
            Assert.Equal(0L, count.ExecuteScalar());
        }
        finally { SqliteTestPoolCleanup.ClearPoolsUnderDirectory(root); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-11-05T00:00:00Z", global::System.Globalization.CultureInfo.InvariantCulture);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
