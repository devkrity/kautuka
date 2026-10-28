using Kautuka.Gateway.Domain.Stores;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Infrastructure.Configuration;

internal sealed class SqliteGatewayUserCommandRepository(KautukaDatabase database) : IGatewayUserCommandRepository
{
    public bool TryCreateFirstAdmin(GatewayUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var readSetup = connection.CreateCommand();
        readSetup.Transaction = transaction;
        readSetup.CommandText = "SELECT value_json FROM system_settings WHERE key = 'setup.completed';";
        string? persistedValue = readSetup.ExecuteScalar() as string;
        if (string.Equals(persistedValue?.Trim('"'), "true", StringComparison.OrdinalIgnoreCase))
        {
            transaction.Rollback();
            return false;
        }

        Upsert(connection, transaction, user);
        using var completeSetup = connection.CreateCommand();
        completeSetup.Transaction = transaction;
        completeSetup.CommandText =
            "INSERT INTO system_settings (key, value_json, updated_at) VALUES ('setup.completed', '\"true\"', $now) " +
            "ON CONFLICT(key) DO UPDATE SET value_json=excluded.value_json, updated_at=excluded.updated_at;";
        completeSetup.Parameters.AddWithValue("$now", GatewayState.Timestamp());
        completeSetup.ExecuteNonQuery();
        transaction.Commit();
        return true;
    }

    public bool TryCreateSocialAdmin(GatewayUser user, ExternalLoginLink link)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(link);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var readSetup = connection.CreateCommand();
        readSetup.Transaction = transaction;
        readSetup.CommandText = "SELECT value_json FROM system_settings WHERE key = 'setup.completed';";
        string? persistedValue = readSetup.ExecuteScalar() as string;
        if (string.Equals(persistedValue?.Trim('"'), "true", StringComparison.OrdinalIgnoreCase))
        {
            transaction.Rollback();
            return false;
        }

        Upsert(connection, transaction, user);
        InsertExternalLogin(connection, transaction, link);
        SetPasswordless(connection, transaction, user.Id, passwordless: true);
        using var completeSetup = connection.CreateCommand();
        completeSetup.Transaction = transaction;
        completeSetup.CommandText =
            "INSERT INTO system_settings (key, value_json, updated_at) VALUES ('setup.completed', '\"true\"', $now) " +
            "ON CONFLICT(key) DO UPDATE SET value_json=excluded.value_json, updated_at=excluded.updated_at;";
        completeSetup.Parameters.AddWithValue("$now", GatewayState.Timestamp());
        completeSetup.ExecuteNonQuery();
        transaction.Commit();
        return true;
    }

    public bool TryInsertExternalLogin(ExternalLoginLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        try
        {
            InsertExternalLogin(connection, transaction, link);
            transaction.Commit();
            return true;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            transaction.Rollback();
            return false;
        }
    }

    public void DeleteExternalLogin(string provider, string subject)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM external_logins WHERE provider = $provider AND subject = $subject;";
        command.Parameters.AddWithValue("$provider", provider);
        command.Parameters.AddWithValue("$subject", subject);
        command.ExecuteNonQuery();
    }

    public void SetPasswordless(string userId, bool passwordless)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        SetPasswordless(connection, transaction, userId, passwordless);
        transaction.Commit();
    }

    public void Upsert(GatewayUser user, bool passwordless = false)
    {
        ArgumentNullException.ThrowIfNull(user);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        Upsert(connection, transaction, user);
        if (passwordless)
        {
            SetPasswordless(connection, transaction, user.Id, passwordless: true);
        }

        transaction.Commit();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void Upsert(SqliteConnection connection, SqliteTransaction transaction, GatewayUser user)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO runtime_users (
                username, user_id, role, password, is_active, email, display_name,
                can_import_connectors, can_import_skills, agent_ids_json,
                agent_permissions_json, created_at, updated_at)
            VALUES (
                $username, $userId, $role, $password, $isActive, $email, $displayName,
                $canImportConnectors, $canImportSkills, '[]', '{}', $now, $now)
            ON CONFLICT(username) DO UPDATE SET
                user_id=excluded.user_id,
                role=excluded.role,
                password=excluded.password,
                is_active=excluded.is_active,
                email=excluded.email,
                display_name=excluded.display_name,
                can_import_connectors=excluded.can_import_connectors,
                can_import_skills=excluded.can_import_skills,
                updated_at=excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$username", user.Username);
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$role", user.Role);
        command.Parameters.AddWithValue("$password", user.Password);
        command.Parameters.AddWithValue("$isActive", user.IsActive ? 1 : 0);
        command.Parameters.AddWithValue("$email", (object?)user.Email ?? DBNull.Value);
        command.Parameters.AddWithValue("$displayName", (object?)user.DisplayName ?? DBNull.Value);
        command.Parameters.AddWithValue("$canImportConnectors", user.CanImportConnectors ? 1 : 0);
        command.Parameters.AddWithValue("$canImportSkills", user.CanImportSkills ? 1 : 0);
        command.Parameters.AddWithValue("$now", GatewayState.Timestamp());
        command.ExecuteNonQuery();
    }

    private static void InsertExternalLogin(SqliteConnection connection, SqliteTransaction transaction, ExternalLoginLink link)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO external_logins (provider, subject, user_id, email, created_at, issuer, tenant_id, object_id)
            VALUES ($provider, $subject, $userId, $email, $now, $issuer, $tenantId, $objectId);
            """;
        command.Parameters.AddWithValue("$provider", link.Provider);
        command.Parameters.AddWithValue("$subject", link.Subject);
        command.Parameters.AddWithValue("$userId", link.UserId);
        command.Parameters.AddWithValue("$email", link.Email);
        command.Parameters.AddWithValue("$now", GatewayState.Timestamp());
        command.Parameters.AddWithValue("$issuer", (object?)link.Issuer ?? DBNull.Value);
        command.Parameters.AddWithValue("$tenantId", (object?)link.TenantId ?? DBNull.Value);
        command.Parameters.AddWithValue("$objectId", (object?)link.ObjectId ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static void SetPasswordless(SqliteConnection connection, SqliteTransaction transaction, string userId, bool passwordless)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (passwordless)
        {
            command.CommandText =
                """
                INSERT INTO runtime_user_password_flags (user_id, has_password)
                VALUES ($userId, 0)
                ON CONFLICT(user_id) DO UPDATE SET has_password = 0;
                """;
        }
        else
        {
            command.CommandText = "DELETE FROM runtime_user_password_flags WHERE user_id = $userId;";
        }

        command.Parameters.AddWithValue("$userId", userId);
        command.ExecuteNonQuery();
    }

    public void InsertInviteCode(string codeHash, string userId, string canonicalEmail, DateTimeOffset expiresAt)
    {
        using SqliteConnection connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO external_login_invites (code_hash, user_id, created_at, email, expires_at)
            VALUES ($hash, $userId, $now, $email, $expiresAt);
            """;
        command.Parameters.AddWithValue("$hash", codeHash);
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$now", GatewayState.Timestamp());
        command.Parameters.AddWithValue("$email", canonicalEmail);
        command.Parameters.AddWithValue("$expiresAt", expiresAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public bool TryConsumeInviteAndInsertExternalLogin(string codeHash, string canonicalEmail, ExternalLoginLink link, DateTimeOffset now, out string? userId)
    {
        userId = null;
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using var consume = connection.CreateCommand();
        consume.Transaction = transaction;
        consume.CommandText = "DELETE FROM external_login_invites WHERE code_hash=$hash AND email=$email AND expires_at>$now AND user_id=$userId;";
        consume.Parameters.AddWithValue("$hash", codeHash);
        consume.Parameters.AddWithValue("$email", canonicalEmail);
        consume.Parameters.AddWithValue("$now", now.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        consume.Parameters.AddWithValue("$userId", link.UserId);
        if (consume.ExecuteNonQuery() != 1) { transaction.Rollback(); return false; }
        try
        {
            InsertExternalLogin(connection, transaction, link);
            transaction.Commit();
            userId = link.UserId;
            return true;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            transaction.Rollback();
            return false;
        }
    }

    public bool TryGetInviteTarget(string codeHash, string canonicalEmail, DateTimeOffset now, out string? userId)
    {
        using SqliteConnection connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT user_id FROM external_login_invites WHERE code_hash=$hash AND email=$email AND expires_at>$now;";
        command.Parameters.AddWithValue("$hash", codeHash);
        command.Parameters.AddWithValue("$email", canonicalEmail);
        command.Parameters.AddWithValue("$now", now.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        userId = command.ExecuteScalar() as string;
        return userId is not null;
    }

    public void ConsumeInviteCode(string codeHash)
    {
        using SqliteConnection connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM external_login_invites WHERE code_hash = $hash;";
        command.Parameters.AddWithValue("$hash", codeHash);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<(string Hash, string UserId)> ListOpenInviteCodes()
    {
        using SqliteConnection connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT code_hash, user_id FROM external_login_invites;";
        using SqliteDataReader reader = command.ExecuteReader();
        var codes = new List<(string, string)>();
        while (reader.Read())
        {
            codes.Add((reader.GetString(0), reader.GetString(1)));
        }

        return codes;
    }
}
