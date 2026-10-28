using System.Diagnostics.CodeAnalysis;
using System.Security.AccessControl;
using System.Security.Principal;

using Microsoft.Data.Sqlite;

namespace Kautuka.ChannelHost;

[SuppressMessage("Performance", "CA1812", Justification = "Constructed through dependency injection in Program.cs")]
internal sealed class ChannelHostDatabase
{
    private const string dbFileName = "channelhost.db";
    internal const int CurrentSchemaVersion = 1;
    internal const string CurrentContractVersion = "kautuka-channelhost/v1";

    public ChannelHostDatabase(IConfiguration configuration)
    {
        string databasePath = KautukaHostPathPolicy.ResolveServiceFilePath(configuration, "channelHost", dbFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = databasePath,
            Cache = SqliteCacheMode.Shared,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        };

        ConnectionString = builder.ToString();
        DatabasePath = databasePath;
    }

    public string ConnectionString { get; }
    public string DatabasePath { get; }

    public void EnsureReady()
    {
        using SqliteConnection connection = OpenConnection();
        long schemaVersion;
        using (SqliteCommand version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version;";
            schemaVersion = (long)(version.ExecuteScalar() ?? 0L);
        }
        if (schemaVersion > CurrentSchemaVersion)
            throw new InvalidOperationException($"ChannelHost schema v{schemaVersion} is newer than supported v{CurrentSchemaVersion}; refusing mixed-version startup.");

        using (SqliteCommand integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            string result = Convert.ToString(integrity.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("ChannelHost state integrity check failed; restore from a verified backup before startup.");
        }

        if (schemaVersion < CurrentSchemaVersion)
        {
            CreateMigrationBackup(connection, schemaVersion);
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand migration = connection.CreateCommand();
            migration.Transaction = transaction;
            migration.CommandText = """
                CREATE TABLE IF NOT EXISTS channel_schema_metadata(
                    singleton INTEGER PRIMARY KEY CHECK(singleton = 1),
                    schema_version INTEGER NOT NULL,
                    contract_version TEXT NOT NULL,
                    migrated_at_utc TEXT NOT NULL);
                INSERT INTO channel_schema_metadata(singleton,schema_version,contract_version,migrated_at_utc)
                VALUES(1,$schemaVersion,$contractVersion,$now)
                ON CONFLICT(singleton) DO UPDATE SET
                    schema_version=excluded.schema_version,
                    contract_version=excluded.contract_version,
                    migrated_at_utc=excluded.migrated_at_utc;
                PRAGMA user_version = 1;
                """;
            migration.Parameters.AddWithValue("$schemaVersion", CurrentSchemaVersion);
            migration.Parameters.AddWithValue("$contractVersion", CurrentContractVersion);
            migration.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            _ = migration.ExecuteNonQuery();
            transaction.Commit();
        }

        ValidateSchemaMetadata(connection);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
        _ = command.ExecuteNonQuery();
        RestrictStoreFile(DatabasePath);
        RestrictStoreFile(DatabasePath + "-wal");
        RestrictStoreFile(DatabasePath + "-shm");
    }

    private void CreateMigrationBackup(SqliteConnection source, long oldVersion)
    {
        using SqliteCommand tableCount = source.CreateCommand();
        tableCount.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        if ((long)(tableCount.ExecuteScalar() ?? 0L) == 0) return;

        string backupPath = DatabasePath + $".pre-v{CurrentSchemaVersion}-from-v{oldVersion}.bak";
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        destination.Open();
        source.BackupDatabase(destination);

        using SqliteCommand integrity = destination.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check;";
        string result = Convert.ToString(integrity.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("ChannelHost migration backup verification failed; refusing schema migration.");

        using SqliteCommand version = destination.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        long backupVersion = (long)(version.ExecuteScalar() ?? -1L);
        if (backupVersion != oldVersion)
            throw new InvalidOperationException("ChannelHost migration backup schema version is inconsistent; refusing schema migration.");
        RestrictStoreFile(backupPath);
    }

    private static void RestrictStoreFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                WindowsIdentity.GetCurrent().User!,
                FileSystemRights.Read | FileSystemRights.Write,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void ValidateSchemaMetadata(SqliteConnection connection)
    {
        using SqliteCommand table = connection.CreateCommand();
        table.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='channel_schema_metadata';";
        if ((long)(table.ExecuteScalar() ?? 0L) != 1)
            throw new InvalidOperationException("ChannelHost schema metadata is missing; refusing startup.");

        using SqliteCommand metadata = connection.CreateCommand();
        metadata.CommandText = "SELECT singleton,schema_version,contract_version FROM channel_schema_metadata;";
        using SqliteDataReader reader = metadata.ExecuteReader();
        if (!reader.Read()
            || reader.GetInt64(0) != 1
            || reader.GetInt64(1) != CurrentSchemaVersion
            || !string.Equals(reader.GetString(2), CurrentContractVersion, StringComparison.Ordinal)
            || reader.Read())
        {
            throw new InvalidOperationException("ChannelHost schema metadata is incompatible with this runtime; refusing startup.");
        }
    }

    public SqliteConnection OpenConnection()
    {
        SqliteConnection connection = new(ConnectionString);
        connection.Open();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA foreign_keys = ON;";
        _ = command.ExecuteNonQuery();

        return connection;
    }
}
