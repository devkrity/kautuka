using System.Data;
using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

using Kautuka.Gateway.Domain.Auth;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Hosting;
using Kautuka.Gateway.Infrastructure.Configuration;
using Kautuka.Gateway.Infrastructure.Database;
using Kautuka.Hosting.Hosting;
using Kautuka.Security;

using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Kautuka.SecurityTests;

public sealed class GatewaySecurityBoundaryTests
{
    [Fact]
    public void PasswordHasherNeverStoresPlaintextAndRejectsLegacyPlaintext()
    {
        string hash = GatewayPasswordHasher.Hash("Correct-Horse-1");

        Assert.DoesNotContain("Correct-Horse-1", hash, StringComparison.Ordinal);
        Assert.True(GatewayPasswordHasher.Verify(hash, "Correct-Horse-1"));
        Assert.False(GatewayPasswordHasher.Verify(hash, "wrong-password"));
        Assert.False(GatewayPasswordHasher.Verify("Correct-Horse-1", "Correct-Horse-1"));
        Assert.False(GatewayPasswordHasher.VerifyOrEqualize(null, "Correct-Horse-1"));
        Assert.True(GatewayPasswordHasher.VerifyOrEqualize(hash, "Correct-Horse-1"));
    }

    [Fact]
    public void AccessTokensSurviveStoreRestartExpireAndCanBeRevoked()
    {
        using var fixture = new TokenStoreFixture();
        var first = fixture.CreateStore();
        string token = first.IssueAccessToken("user-1");

        var restarted = fixture.CreateStore();
        Assert.True(restarted.TryResolveAccessToken(token, out string? userId));
        Assert.Equal("user-1", userId);

        restarted.RevokeUserSessions("user-1");
        Assert.False(restarted.TryResolveAccessToken(token, out _));

        string replacementInSameSecond = restarted.IssueAccessToken("user-1");
        Assert.True(restarted.TryResolveAccessToken(replacementInSameSecond, out userId));
        Assert.Equal("user-1", userId);

        string expiring = restarted.IssueAccessToken("user-2");
        fixture.Clock.Advance(GatewayTokenStore.AccessTokenLifetime + TimeSpan.FromSeconds(1));
        Assert.False(restarted.TryResolveAccessToken(expiring, out _));
    }

    [Fact]
    public void ShortAccessTokenSignatureIsRejected()
    {
        using var fixture = new TokenStoreFixture();
        GatewayTokenStore store = fixture.CreateStore();
        string token = store.IssueAccessToken("user-1");
        string truncated = string.Join('.', token.Split('.').Take(2)) + ".YQ";

        Assert.False(store.TryResolveAccessToken(truncated, out _));
    }

    [Fact]
    public void RefreshTokensAreDurableRotatedAndReplayProtected()
    {
        using var fixture = new TokenStoreFixture();
        string original = fixture.CreateStore().IssueRefreshToken("user-1");

        var restarted = fixture.CreateStore();
        Assert.True(restarted.TryRotateRefreshToken(original, out string? userId, out string? replacement));
        Assert.Equal("user-1", userId);
        Assert.NotNull(replacement);
        Assert.False(restarted.TryRotateRefreshToken(original, out _, out _));
        Assert.True(restarted.TryResolveRefreshToken(replacement!, out _));

        restarted.RevokeRefreshToken(replacement!);
        Assert.False(restarted.TryResolveRefreshToken(replacement!, out _));
    }

    [Fact]
    public void AccessTokenSigningKeyIsRestrictedToTheProcessOwner()
    {
        using var fixture = new TokenStoreFixture();
        byte[] key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(fixture.SigningKeyPath, key);
        if (OperatingSystem.IsWindows())
        {
            var info = new FileInfo(fixture.SigningKeyPath);
            FileSecurity security = info.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.Read,
                AccessControlType.Allow));
            info.SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(
                fixture.SigningKeyPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        GatewayTokenStore store = fixture.CreateStore();
        string token = store.IssueAccessToken("user-1");
        Assert.True(store.TryResolveAccessToken(token, out string? userId));
        Assert.Equal("user-1", userId);
        Assert.Equal(key, File.ReadAllBytes(fixture.SigningKeyPath));

        if (OperatingSystem.IsWindows())
        {
            FileSecurity security = new FileInfo(fixture.SigningKeyPath).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected);
            AuthorizationRuleCollection rules = security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier));
            Assert.NotEmpty(rules);
            foreach (FileSystemAccessRule rule in rules)
            {
                Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
                Assert.Equal(WindowsIdentity.GetCurrent().User, (SecurityIdentifier)rule.IdentityReference);
            }
        }
        else
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(fixture.SigningKeyPath));
        }
    }

    [Fact]
    public void NewAccessTokenSigningKeyIsCreatedOwnerOnly()
    {
        using var fixture = new TokenStoreFixture();
        GatewayTokenStore store = fixture.CreateStore();
        string token = store.IssueAccessToken("user-1");
        Assert.True(store.TryResolveAccessToken(token, out string? userId));
        Assert.Equal("user-1", userId);

        if (OperatingSystem.IsWindows())
        {
            FileSecurity security = new FileInfo(fixture.SigningKeyPath).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected);
            AuthorizationRuleCollection rules = security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier));
            Assert.NotEmpty(rules);
            foreach (FileSystemAccessRule rule in rules)
            {
                Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
                Assert.Equal(WindowsIdentity.GetCurrent().User, (SecurityIdentifier)rule.IdentityReference);
            }
        }
        else
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(fixture.SigningKeyPath));
        }
    }

    [Fact]
    public void MachineKeyDropsAWorldReadRuleAndStillDecrypts()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-machine-key", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string keyPath = Path.Combine(root, "kautuka.key");
        try
        {
            byte[] key = RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(keyPath, key);
            if (OperatingSystem.IsWindows())
            {
                var info = new FileInfo(keyPath);
                FileSecurity security = info.GetAccessControl();
                security.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                    FileSystemRights.Read,
                    AccessControlType.Allow));
                info.SetAccessControl(security);
            }
            else
            {
                File.SetUnixFileMode(
                    keyPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }

            var provider = new MachineKeyProvider(root);
            Assert.Equal(key, provider.Key.ToArray());

            if (OperatingSystem.IsWindows())
            {
                FileSecurity security = new FileInfo(keyPath).GetAccessControl();
                Assert.True(security.AreAccessRulesProtected);
                AuthorizationRuleCollection rules = security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier));
                Assert.NotEmpty(rules);
                foreach (FileSystemAccessRule rule in rules)
                {
                    Assert.Equal(WindowsIdentity.GetCurrent().User, (SecurityIdentifier)rule.IdentityReference);
                }
            }
            else
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(keyPath));
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void GatewayDatabaseDropsAWorldReadRuleAndStillOpens()
    {
        using var fixture = new TokenStoreFixture();
        GrantWorldRead(fixture.DatabasePath);
        new KautukaDatabase(fixture.Configuration).EnsureReady();
        AssertOwnerOnly(fixture.DatabasePath);

        using var connection = new SqliteConnection(new KautukaDatabase(fixture.Configuration).ConnectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' LIMIT 1;";
        Assert.False(string.IsNullOrWhiteSpace(command.ExecuteScalar() as string));
    }

    [Fact]
    public void AgentJournalDropsAWorldReadRuleAndStillOpens()
    {
        using var fixture = new TokenStoreFixture();
        var store = new SqliteAgentStoreDatabase(fixture.Configuration);
        using (SqliteConnection created = store.OpenCurrentConnection("owner-journal"))
        {
            Assert.Equal(ConnectionState.Open, created.State);
        }

        string journalPath = Path.Combine(
            KautukaHostPathPolicy.ResolveSharedStateRoot(fixture.Configuration),
            "agents",
            "owner-journal",
            "agent.db");
        GrantWorldRead(journalPath);
        using (SqliteConnection reopened = store.OpenCurrentConnection("owner-journal"))
        {
            using SqliteCommand command = reopened.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' LIMIT 1;";
            _ = command.ExecuteScalar();
        }

        AssertOwnerOnly(journalPath);
    }

    private static void GrantWorldRead(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var info = new FileInfo(path);
            FileSecurity security = info.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.Read,
                AccessControlType.Allow));
            info.SetAccessControl(security);
            return;
        }

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    private static void AssertOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            FileSecurity security = new FileInfo(path).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected);
            AuthorizationRuleCollection rules = security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier));
            Assert.NotEmpty(rules);
            foreach (FileSystemAccessRule rule in rules)
            {
                Assert.Equal(WindowsIdentity.GetCurrent().User, (SecurityIdentifier)rule.IdentityReference);
            }

            return;
        }

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void RemoteListenerWithoutTlsIsRejected()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            GatewayAppBootstrap.ConfigureProductionServer(builder, IPAddress.Any));

        Assert.Contains("requires", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CertificatePath", error.Message, StringComparison.Ordinal);
    }

    private sealed class TokenStoreFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "kautuka-security-tests", Guid.NewGuid().ToString("N"));
        private readonly KautukaDatabase database;

        public TokenStoreFixture()
        {
            Directory.CreateDirectory(root);
            Configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Kautuka:ContainerStateRoot"] = root })
                .Build();
            database = new KautukaDatabase(Configuration);
            DatabasePath = KautukaDatabase.ResolvePath(Configuration);
            database.EnsureReady();
        }

        public IConfiguration Configuration { get; }

        public string DatabasePath { get; }

        public MutableTimeProvider Clock { get; } = new(DateTimeOffset.Parse("2026-07-14T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

        public string SigningKeyPath => Path.Combine(database.DataDirectory, "gateway-auth.key");

        public GatewayTokenStore CreateStore() => new(database, Clock);

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan duration) => current += duration;
    }
}
