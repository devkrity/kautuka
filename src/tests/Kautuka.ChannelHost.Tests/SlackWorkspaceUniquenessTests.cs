using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.ChannelHost.Hosting;
using Kautuka.Protos;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Kautuka.ChannelHost.Tests;

public sealed class SlackWorkspaceUniquenessTests
{
    [Fact]
    public void SlackIdentityClaimsArePreservedAndCoverEveryLiveState()
    {
        string root = Path.Combine(Path.GetTempPath(), $"kautuka-slack-workspace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kautuka:ContainerStateRoot"] = root,
            }).Build();
            ChannelHostDatabase database = new(configuration);
            database.EnsureReady();
            ChannelAttachmentStore store = new(database);
            Upsert(store, "winner", "user-1");
            Assert.True(store.TryClaimSlackIdentity("winner", Identity("T1", "A1", "B1", "BOT1"), out string? firstConflict));
            Assert.Null(firstConflict);

            // A Gateway upsert that omits ChannelHost-owned identity must retain the claim.
            Upsert(store, "winner", "user-1");
            using (JsonDocument settings = JsonDocument.Parse(store.TryGetActiveAttachment("winner", out ChannelAttachmentRecord? row)
                       ? row!.Settings.GetRawText() : "{}"))
                Assert.Equal("T1", settings.RootElement.GetProperty("team_id").GetString());

            Upsert(store, "same-team", "user-2");
            Assert.False(store.TryClaimSlackIdentity("same-team", Identity("T1", "A2", "B2", "BOT2"), out string? teamConflict));
            Assert.Equal("slack_workspace_in_use", teamConflict);

            Upsert(store, "same-app", "user-3");
            Assert.False(store.TryClaimSlackIdentity("same-app", Identity("T2", "A1", "B3", "BOT3"), out string? appConflict));
            Assert.Equal("slack_workspace_in_use", appConflict);

            Assert.True(store.DeactivateAttachment("winner"));
            Assert.True(store.TryClaimSlackIdentity("same-team", Identity("T1", "A2", "B2", "BOT2"), out _));
        }
        finally
        {
            ChannelHostSqliteTestCleanup.ReleaseAndDelete(root);
        }
    }

    [Fact]
    public void RecoveryRequiredSlackAttachmentContinuesToOwnItsWorkspace()
    {
        string root = Path.Combine(Path.GetTempPath(), $"kautuka-slack-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kautuka:ContainerStateRoot"] = root,
            }).Build();
            ChannelHostDatabase database = new(configuration);
            database.EnsureReady();
            ChannelAttachmentStore store = new(database);
            Upsert(store, "winner", "user-1");
            Assert.True(store.TryClaimSlackIdentity("winner", Identity("T1", "A1", "B1", "BOT1"), out _));
            Assert.True(store.MarkProvisioningFailed("winner", "test-recovery", "test-recovery"));
            Upsert(store, "loser", "user-2");

            Assert.False(store.TryClaimSlackIdentity("loser", Identity("T1", "A2", "B2", "BOT2"), out string? conflict));
            Assert.Equal("slack_workspace_in_use", conflict);
        }
        finally
        {
            ChannelHostSqliteTestCleanup.ReleaseAndDelete(root);
        }
    }

    [Fact]
    public void SettingsPatchPersistsBeforeUpdatingInMemoryWhenSlackClaimConflicts()
    {
        string root = Path.Combine(Path.GetTempPath(), $"kautuka-slack-patch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kautuka:ContainerStateRoot"] = root,
            }).Build();
            ChannelHostDatabase database = new(configuration);
            database.EnsureReady();
            ChannelAttachmentStore store = new(database);
            Upsert(store, "winner", "user-1");
            Upsert(store, "loser", "user-2");
            Assert.True(store.TryClaimSlackIdentity("winner", Identity("T1", "A1", "B1", "BOT1"), out _));
            Assert.True(store.TryClaimSlackIdentity("loser", Identity("T2", "A2", "B2", "BOT2"), out _));

            Assert.Throws<SqliteException>(() => store.ApplySettingsPatch("loser", new JsonObject { ["team_id"] = "T1" }));

            Assert.True(store.TryGetActiveAttachment("loser", out ChannelAttachmentRecord? actual));
            Assert.Equal("T2", actual!.Settings.GetProperty("team_id").GetString());
        }
        finally
        {
            ChannelHostSqliteTestCleanup.ReleaseAndDelete(root);
        }
    }

    private static JsonObject Identity(string teamId, string appId, string botId, string botUserId) => new()
    {
        ["team_id"] = teamId,
        ["app_id"] = appId,
        ["bot_id"] = botId,
        ["bot_user_id"] = botUserId,
    };

    private static void Upsert(ChannelAttachmentStore store, string id, string userId)
    {
        UpsertAttachmentRequest request = new()
        {
            Context = new ScopedRequestContext { ActorUserId = userId, TenantId = "tenant-1" },
            AttachmentId = id,
            ProviderKey = "slack",
            ExpectedSchemaVersion = "slack/v1",
        };
        request.Settings.Add(new AttachmentSettingValue { FieldKey = "owner_user_id", StringValue = userId });
        request.Settings.Add(new AttachmentSettingValue { FieldKey = "display_name", StringValue = "Slack" });
        Assert.Equal(ValidationStatus.Accepted, store.UpsertAttachment(request).ValidationStatus);
    }
}
