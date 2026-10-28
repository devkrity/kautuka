using System.Runtime.CompilerServices;

using Kautuka.CredentialsHost.Security;

using Microsoft.Data.Sqlite;

namespace Kautuka.CredentialsHost.Database;

/// <summary>
/// Opens connections to the CredentialsHost SQLite database.
/// Completely independent of KautukaDatabase from Kautuka.Platform.
/// The database file path is derived from the canonical credentials data root.
/// </summary>
internal sealed class CredentialsDatabase(string dataRoot)
{
    private const string dbFileName = "kautuka.credentials.db";

    private readonly string connectionString = BuildConnectionString(dataRoot);
    private bool permissionsApplied;

    public string DatabasePath { get; } = Path.GetFullPath(Path.Combine(dataRoot, dbFileName));

    public async Task CreateOnlineSnapshotAsync(string destinationPath, CancellationToken cancellationToken)
    {
        await using SqliteConnection source = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
        source.BackupDatabase(destination);
    }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();

        if (!permissionsApplied)
        {
            ApplyFilePermissions(dataRoot);
            permissionsApplied = true;
        }

        return connection;
    }

    public async ValueTask<SqliteConnection> OpenConnectionAsync(CancellationToken ct = default)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        if (!permissionsApplied)
        {
            ApplyFilePermissions(dataRoot);
            permissionsApplied = true;
        }

        return connection;
    }

    private static string BuildConnectionString(string dataRoot)
    {
        Directory.CreateDirectory(dataRoot);
        string dbPath = Path.Combine(dataRoot, dbFileName);
        return new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 5,
        }.ToString();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ApplyFilePermissions(string dataRoot)
    {
        string dbPath = Path.Combine(dataRoot, dbFileName);
        if (OperatingSystem.IsWindows())
        {
            if (File.Exists(dbPath)) OwnerOnlyFile.RestrictFile(dbPath);
            if (File.Exists(dbPath + "-wal")) OwnerOnlyFile.RestrictFile(dbPath + "-wal");
            if (File.Exists(dbPath + "-shm")) OwnerOnlyFile.RestrictFile(dbPath + "-shm");
            return;
        }

        string walPath = dbPath + "-wal";
        string shmPath = dbPath + "-shm";

        UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        if (File.Exists(dbPath)) File.SetUnixFileMode(dbPath, ownerOnly);
        if (File.Exists(walPath)) File.SetUnixFileMode(walPath, ownerOnly);
        if (File.Exists(shmPath)) File.SetUnixFileMode(shmPath, ownerOnly);
    }
}
