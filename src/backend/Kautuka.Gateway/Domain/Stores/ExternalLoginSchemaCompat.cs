using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Domain.Stores;

/// <summary>
/// Additive external-login columns for databases created before tenant and invite metadata.
/// Lives outside Infrastructure/Database because that folder cannot contain ALTER TABLE.
/// </summary>
internal static class ExternalLoginSchemaCompat
{
    public static void EnsureIdentityColumns(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (!TableExists(connection, "external_logins"))
        {
            return;
        }

        EnsureColumn(connection, "issuer", "TEXT");
        EnsureColumn(connection, "tenant_id", "TEXT");
        EnsureColumn(connection, "object_id", "TEXT");
    }

    public static void EnsureInviteColumns(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!TableExists(connection, "external_login_invites")) return;
        using SqliteTransaction transaction = connection.BeginTransaction();
        bool hasEmail = HasInviteColumn(connection, "email", transaction);
        bool hasExpiry = HasInviteColumn(connection, "expires_at", transaction);
        if (!hasEmail || !hasExpiry)
        {
            using var invalidateLegacy = connection.CreateCommand();
            invalidateLegacy.Transaction = transaction;
            invalidateLegacy.CommandText = "DELETE FROM external_login_invites;";
            invalidateLegacy.ExecuteNonQuery();
        }
        if (!hasEmail) EnsureInviteColumn(connection, transaction, "email");
        if (!hasExpiry) EnsureInviteColumn(connection, transaction, "expires_at");
        transaction.Commit();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Callers use only the closed email and expires_at schema column names.")]
    private static void EnsureInviteColumn(SqliteConnection connection, SqliteTransaction transaction, string columnName)
    {
        if (HasInviteColumn(connection, columnName, transaction)) return;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"ALTER TABLE external_login_invites ADD COLUMN {columnName} TEXT NOT NULL";
        command.ExecuteNonQuery();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Column name and SQL type are closed identifiers.")]
    private static void EnsureColumn(SqliteConnection connection, string columnName, string columnSql)
    {
        if (HasColumn(connection, columnName))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE external_logins ADD COLUMN {columnName} {columnSql}";
        command.ExecuteNonQuery();
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1L;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Table name is a closed identifier.")]
    private static bool HasColumn(SqliteConnection connection, string columnName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(external_logins)";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The table name is a closed identifier.")]
    private static bool HasInviteColumn(SqliteConnection connection, string columnName, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA table_info(external_login_invites)";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
