using Grpc.Core;

using Kautuka.ChannelHost.Grpc;
using Kautuka.Protos;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Kautuka.ChannelHost.Tests;

public sealed class ChannelHostDatabaseCompatibilityTests
{
    [Fact]
    public void ConcurrentWorkBackpressureIsIsolatedByRegistration()
    {
        var governor = new ChannelResourceGovernor(TimeProvider.System);
        using IDisposable first = governor.AcquireRegistrationOperation("slack:owner:first", "send", limit: 1);
        RpcException denied = Assert.Throws<RpcException>(() =>
            governor.AcquireRegistrationOperation("slack:owner:first", "send", limit: 1));
        Assert.Equal("registration_concurrent_work_limit_exceeded", denied.Status.Detail);

        using IDisposable healthy = governor.AcquireRegistrationOperation("slack:owner:second", "send", limit: 1);
    }

    [Fact]
    public void ProviderRateLimitDoesNotBackpressureAnotherProvider()
    {
        var governor = new ChannelResourceGovernor(TimeProvider.System);
        var context = new ScopedRequestContext { ActorUserId = "owner", TenantId = "tenant" };
        for (int index = 0; index < 480; index++)
        {
            governor.Enforce(context, "GetAttachment",
                new GetAttachmentRequest { AttachmentId = $"slack:owner:{index % 3}" }, "gateway");
        }

        RpcException denied = Assert.Throws<RpcException>(() => governor.Enforce(context, "GetAttachment",
            new GetAttachmentRequest { AttachmentId = "slack:owner:fourth" }, "gateway"));
        Assert.Equal("channel_rate_limit_exceeded", denied.Status.Detail);

        governor.Enforce(context, "GetAttachment",
            new GetAttachmentRequest { AttachmentId = "telegram:owner:healthy" }, "gateway");
    }

    [Fact]
    public void ContractMetadataMismatchFailsClosed()
    {
        using var state = new TemporaryState();
        ChannelHostDatabase database = state.CreateDatabase();
        database.EnsureReady();

        using (SqliteConnection connection = database.OpenConnection())
        using (SqliteCommand tamper = connection.CreateCommand())
        {
            tamper.CommandText = "UPDATE channel_schema_metadata SET contract_version='incompatible/v99' WHERE singleton=1;";
            _ = tamper.ExecuteNonQuery();
        }

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(database.EnsureReady);
        Assert.Contains("metadata is incompatible", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingMetadataForCurrentSchemaFailsClosed()
    {
        using var state = new TemporaryState();
        ChannelHostDatabase database = state.CreateDatabase();
        using (SqliteConnection connection = database.OpenConnection())
        using (SqliteCommand schema = connection.CreateCommand())
        {
            schema.CommandText = "PRAGMA user_version=1;";
            _ = schema.ExecuteNonQuery();
        }

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(database.EnsureReady);
        Assert.Contains("metadata is missing", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationBackupIsVerifiedAndCanRestoreLegacyState()
    {
        using var state = new TemporaryState();
        ChannelHostDatabase database = state.CreateDatabase();
        using (SqliteConnection connection = database.OpenConnection())
        using (SqliteCommand legacy = connection.CreateCommand())
        {
            legacy.CommandText = "CREATE TABLE legacy_state(id INTEGER PRIMARY KEY,value TEXT NOT NULL); INSERT INTO legacy_state(value) VALUES('preserved'); PRAGMA user_version=0;";
            _ = legacy.ExecuteNonQuery();
        }

        database.EnsureReady();

        string backupPath = database.DatabasePath + $".pre-v{ChannelHostDatabase.CurrentSchemaVersion}-from-v0.bak";
        Assert.True(File.Exists(backupPath));
        string restorePath = Path.Combine(state.Root, "restored.db");
        using (var backup = Open(backupPath, SqliteOpenMode.ReadOnly))
        {
            using SqliteCommand integrity = backup.CreateCommand();
            integrity.CommandText = "PRAGMA integrity_check;";
            Assert.Equal("ok", integrity.ExecuteScalar());

            using SqliteConnection restored = Open(restorePath, SqliteOpenMode.ReadWriteCreate);
            backup.BackupDatabase(restored);
        }

        using (SqliteConnection restored = Open(restorePath, SqliteOpenMode.ReadOnly))
        using (SqliteCommand verify = restored.CreateCommand())
        {
            verify.CommandText = "SELECT value FROM legacy_state WHERE id=1;";
            Assert.Equal("preserved", verify.ExecuteScalar());
            verify.CommandText = "PRAGMA user_version;";
            Assert.Equal(0L, verify.ExecuteScalar());
        }
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private sealed class TemporaryState : IDisposable
    {
        public TemporaryState()
        {
            Root = Path.Combine(Path.GetTempPath(), "kautuka-channel-schema-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public ChannelHostDatabase CreateDatabase()
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Kautuka:ContainerStateRoot"] = Root })
                .Build();
            return new ChannelHostDatabase(configuration);
        }

        public void Dispose()
        {
            ChannelHostSqliteTestCleanup.ReleaseAndDelete(Root);
        }
    }
}
