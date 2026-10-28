using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Dapper;

using Kautuka.Security;

using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Domain.Stores;

internal sealed class GatewayTokenStore : IGatewayTokenStore
{
    internal static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(14);

    private readonly KautukaDatabase database;
    private readonly TimeProvider timeProvider;
    private readonly byte[] signingKey;

    public GatewayTokenStore(KautukaDatabase database, TimeProvider timeProvider)
    {
        this.database = database;
        this.timeProvider = timeProvider;
        signingKey = LoadOrCreateSigningKey(database.DataDirectory);
    }

    public string IssueAccessToken(string userId)
    {
        using var connection = OpenConnection();
        long sessionGeneration = GetSessionGeneration(connection, userId);
        long issuedAt = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        long expiresAt = timeProvider.GetUtcNow().Add(AccessTokenLifetime).ToUnixTimeSeconds();
        string payload = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes($"{userId}\n{sessionGeneration}\n{issuedAt}\n{expiresAt}\n{Guid.NewGuid():N}"));
        string signature = WebEncoders.Base64UrlEncode(HMACSHA256.HashData(signingKey, Encoding.ASCII.GetBytes(payload)));
        return $"k1.{payload}.{signature}";
    }

    public string IssueRefreshToken(string userId)
    {
        CleanupExpiredSessions();
        string token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(48));
        using var connection = OpenConnection();
        connection.Execute(
            "INSERT INTO auth_refresh_sessions (token_hash,user_id,created_at,expires_at) VALUES (@hash,@userId,@created,@expires)",
            new
            {
                hash = HashToken(token),
                userId,
                created = Timestamp(timeProvider.GetUtcNow()),
                expires = Timestamp(timeProvider.GetUtcNow().Add(RefreshTokenLifetime)),
            });
        return token;
    }

    public bool TryResolveAccessToken(string token, out string? userId)
    {
        userId = null;
        string[] parts = token.Split('.');
        if (parts.Length != 3 || !string.Equals(parts[0], "k1", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            byte[] expected = HMACSHA256.HashData(signingKey, Encoding.ASCII.GetBytes(parts[1]));
            byte[] supplied = WebEncoders.Base64UrlDecode(parts[2]);
            if (supplied.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(expected, supplied))
            {
                return false;
            }

            string[] fields = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(parts[1])).Split('\n');
            if (fields.Length != 5
                || !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out long tokenGeneration)
                || !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out _)
                || !long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out long expiresAt)
                || expiresAt <= timeProvider.GetUtcNow().ToUnixTimeSeconds())
            {
                return false;
            }

            using var connection = OpenConnection();
            if (tokenGeneration != GetSessionGeneration(connection, fields[0]))
            {
                return false;
            }

            userId = fields[0];
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public bool TryResolveRefreshToken(string token, out string? userId)
    {
        using var connection = OpenConnection();
        userId = connection.QueryFirstOrDefault<string>(
            "SELECT user_id FROM auth_refresh_sessions WHERE token_hash=@hash AND revoked_at IS NULL AND expires_at>@now",
            new { hash = HashToken(token), now = Timestamp(timeProvider.GetUtcNow()) });
        return userId is not null;
    }

    public bool TryRotateRefreshToken(string token, out string? userId, out string? replacementToken)
    {
        userId = null;
        replacementToken = null;
        string hash = HashToken(token);
        string now = Timestamp(timeProvider.GetUtcNow());
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        userId = connection.QueryFirstOrDefault<string>(
            "SELECT user_id FROM auth_refresh_sessions WHERE token_hash=@hash AND revoked_at IS NULL AND expires_at>@now",
            new { hash, now }, transaction);
        if (userId is null || connection.Execute(
                "UPDATE auth_refresh_sessions SET revoked_at=@now WHERE token_hash=@hash AND revoked_at IS NULL",
                new { hash, now }, transaction) != 1)
        {
            transaction.Rollback();
            userId = null;
            return false;
        }

        replacementToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(48));
        connection.Execute(
            "INSERT INTO auth_refresh_sessions (token_hash,user_id,created_at,expires_at,rotated_from_hash) VALUES (@newHash,@userId,@now,@expires,@hash)",
            new
            {
                newHash = HashToken(replacementToken),
                userId,
                now,
                expires = Timestamp(timeProvider.GetUtcNow().Add(RefreshTokenLifetime)),
                hash,
            }, transaction);
        transaction.Commit();
        return true;
    }

    public void RevokeRefreshToken(string token)
    {
        using var connection = OpenConnection();
        connection.Execute(
            "UPDATE auth_refresh_sessions SET revoked_at=@now WHERE token_hash=@hash AND revoked_at IS NULL",
            new { hash = HashToken(token), now = Timestamp(timeProvider.GetUtcNow()) });
    }

    public void RevokeUserSessions(string userId)
    {
        string now = Timestamp(timeProvider.GetUtcNow());
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        connection.Execute("UPDATE auth_refresh_sessions SET revoked_at=@now WHERE user_id=@userId AND revoked_at IS NULL", new { now, userId }, transaction);
        connection.Execute(
            "INSERT INTO auth_user_session_generations (user_id,generation) VALUES (@userId,1) ON CONFLICT(user_id) DO UPDATE SET generation=generation+1",
            new { userId }, transaction);
        transaction.Commit();
    }

    private void CleanupExpiredSessions()
    {
        using var connection = OpenConnection();
        connection.Execute(
            "DELETE FROM auth_refresh_sessions WHERE expires_at<=@now OR (revoked_at IS NOT NULL AND revoked_at<=@old)",
            new { now = Timestamp(timeProvider.GetUtcNow()), old = Timestamp(timeProvider.GetUtcNow().AddDays(-1)) });
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        connection.Execute("PRAGMA busy_timeout=5000;");
        return connection;
    }

    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static string Timestamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static long GetSessionGeneration(SqliteConnection connection, string userId)
        => connection.QueryFirstOrDefault<long?>(
            "SELECT generation FROM auth_user_session_generations WHERE user_id=@userId",
            new { userId }) ?? 0;

    private static byte[] LoadOrCreateSigningKey(string dataDirectory)
    {
        string path = Path.Combine(dataDirectory, "gateway-auth.key");
        if (File.Exists(path))
        {
            SetupPossession.RestrictToOwner(path);
            byte[] existing = File.ReadAllBytes(path);
            return existing.Length == 32 ? existing : throw new InvalidOperationException("The Gateway authentication signing key is invalid.");
        }

        byte[] key = RandomNumberGenerator.GetBytes(32);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        OwnerOnlySecretFile.WriteAllBytes(temporary, key);
        try
        {
            File.Move(temporary, path, overwrite: false);
            SetupPossession.RestrictToOwner(path);
            return key;
        }
        catch (IOException) when (File.Exists(path))
        {
            File.Delete(temporary);
            SetupPossession.RestrictToOwner(path);
            byte[] existing = File.ReadAllBytes(path);
            return existing.Length == 32
                ? existing
                : throw new InvalidOperationException("The Gateway authentication signing key is invalid.");
        }
    }
}
