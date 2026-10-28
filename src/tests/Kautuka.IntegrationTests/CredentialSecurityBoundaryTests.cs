using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

using Grpc.Core;

using Kautuka.CredentialsHost.Database;
using Kautuka.CredentialsHost.Grpc;
using Kautuka.CredentialsHost.Hosting;
using Kautuka.CredentialsHost.Security;
using Kautuka.Protos;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
namespace Kautuka.IntegrationTests;

public sealed class CredentialSecurityBoundaryTests
{
    [Fact]
    public void MissingAccessContextIsUnauthenticated()
    {
        RpcException exception = Assert.Throws<RpcException>(
            () => CredentialRequestAuthorizer.AuthorizeResolve(null, "user-a"));

        Assert.Equal(StatusCode.Unauthenticated, exception.StatusCode);
    }

    [Fact]
    public void OwnUserCapabilityCannotReadAnotherUser()
    {
        var access = new CredentialAccessContext
        {
            ActorId = "user-a",
            Capability = CredentialCapability.OwnUserAdmin
        };

        RpcException exception = Assert.Throws<RpcException>(
            () => CredentialRequestAuthorizer.AuthorizeUserAdministration(access, "user-b"));

        Assert.Equal(StatusCode.PermissionDenied, exception.StatusCode);
    }

    [Fact]
    public void ResolveOnlyCapabilityCannotAdministerSystemCredentials()
    {
        var access = new CredentialAccessContext
        {
            ActorId = "gateway-runtime",
            Capability = CredentialCapability.RuntimeResolve
        };

        RpcException exception = Assert.Throws<RpcException>(
            () => CredentialRequestAuthorizer.AuthorizeSystemAdministration(access));

        Assert.Equal(StatusCode.PermissionDenied, exception.StatusCode);
    }

    [Fact]
    public void CrossUserCapabilityCanAdministerAnotherUser()
    {
        var access = new CredentialAccessContext
        {
            ActorId = "admin-a",
            Capability = CredentialCapability.CrossUserAdmin
        };

        CredentialRequestAuthorizer.AuthorizeUserAdministration(access, "user-b");
    }

    [Fact]
    public void CorruptEnvelopeFailsIntegrityInsteadOfAppearingAbsent()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-credential-boundary", Guid.NewGuid().ToString("N"));
        try
        {
            using var keyRing = new CredentialKeyRing(root);
            var vault = new CredentialsVaultService(keyRing);
            Assert.Throws<CredentialIntegrityException>(() => vault.Decrypt(
                "not-an-envelope",
                new CredentialEnvelopeIdentity("user", "user-a", "api-key")));
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
    public void CiphertextIsBoundToScopeSubjectAndKey()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-credential-boundary", Guid.NewGuid().ToString("N"));
        try
        {
            using var keyRing = new CredentialKeyRing(root);
            var vault = new CredentialsVaultService(keyRing);
            var identity = new CredentialEnvelopeIdentity("user", "user-a", "api-key");
            string envelope = vault.Encrypt("secret", identity);

            Assert.Equal("secret", vault.Decrypt(envelope, identity));
            Assert.Throws<CredentialIntegrityException>(() => vault.Decrypt(
                envelope,
                new CredentialEnvelopeIdentity("user", "user-b", "api-key")));
            Assert.Throws<CredentialIntegrityException>(() => vault.Decrypt(
                envelope,
                new CredentialEnvelopeIdentity("system", string.Empty, "api-key")));
            Assert.Throws<CredentialIntegrityException>(() => vault.Decrypt(
                envelope,
                new CredentialEnvelopeIdentity("user", "user-a", "different-key")));
        }
        finally
        {
            SqliteTestPoolCleanup.ClearPoolsUnderDirectory(root);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void KeyRotationKeepsOldEnvelopesReadableUntilRetirement()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-credential-boundary", Guid.NewGuid().ToString("N"));
        try
        {
            using var keyRing = new CredentialKeyRing(root);
            var vault = new CredentialsVaultService(keyRing);
            var identity = new CredentialEnvelopeIdentity("system", string.Empty, "token");
            string oldEnvelope = vault.Encrypt("old", identity);

            string active = keyRing.Rotate();
            string newEnvelope = vault.Encrypt("new", identity);

            Assert.Equal("old", vault.Decrypt(oldEnvelope, identity));
            Assert.Equal("new", vault.Decrypt(newEnvelope, identity));
            keyRing.RetireAllExcept(active);
            Assert.Throws<CredentialIntegrityException>(() => vault.Decrypt(oldEnvelope, identity));
            Assert.Equal("new", vault.Decrypt(newEnvelope, identity));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RotationStateSurvivesKeyRingRestart()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-credential-boundary", Guid.NewGuid().ToString("N"));
        try
        {
            var identity = new CredentialEnvelopeIdentity("user", "user-a", "token");
            string oldEnvelope;
            string newEnvelope;
            using (var initial = new CredentialKeyRing(root))
            {
                var vault = new CredentialsVaultService(initial);
                oldEnvelope = vault.Encrypt("old", identity);
                initial.Rotate();
                newEnvelope = vault.Encrypt("new", identity);
            }

            using var restarted = new CredentialKeyRing(root);
            var restartedVault = new CredentialsVaultService(restarted);
            Assert.Equal("old", restartedVault.Decrypt(oldEnvelope, identity));
            Assert.Equal("new", restartedVault.Decrypt(newEnvelope, identity));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DuplicateMutationOperationIsAppliedExactlyOnce()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-credential-boundary", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var database = new CredentialsDatabase(root);
            CredentialsSchemaInitializer.EnsureReady(database);
            using var keyRing = new CredentialKeyRing(root);
            var service = new CredentialsGrpcService(
                database,
                new CredentialRepository(database, new CredentialsVaultService(keyRing), new CredentialAuditStore(database)),
                new CredentialsVaultService(keyRing),
                new CredentialAuditStore(database),
                new CredentialClientIdentityState("test-thumbprint", DateTimeOffset.UtcNow.AddDays(30)),
                NullLogger<CredentialsGrpcService>.Instance);
            var request = new SetSystemCredentialsRequest
            {
                Access = new CredentialAccessContext { ActorId = "admin", Capability = CredentialCapability.SystemAdmin },
                OperationId = Guid.NewGuid().ToString("N")
            };
            request.Entries.Add("token", "first");

            SetCredentialsResponse first = await service.SetSystemCredentials(request, TestCallContext.Instance);
            request.Entries["token"] = "must-not-overwrite";
            SetCredentialsResponse replay = await service.SetSystemCredentials(request, TestCallContext.Instance);

            Assert.Equal(1, first.UpsertedCount);
            Assert.Equal(1, replay.UpsertedCount);
            CredentialKeyListResponse listed = await service.ListSystemCredentialKeys(
                new ListSystemCredentialKeysRequest
                {
                    Access = new CredentialAccessContext
                    {
                        ActorId = "admin",
                        Capability = CredentialCapability.SystemAdmin
                    }
                },
                TestCallContext.Instance);
            Assert.Equal(["token"], listed.Keys);
            var resolve = new GetResolvedCredentialsRequest
            {
                Access = new CredentialAccessContext { ActorId = "runtime", Capability = CredentialCapability.RuntimeResolve }
            };
            resolve.RequiredKeys.Add("token");
            CredentialMapResponse values = await service.GetResolvedCredentials(resolve, TestCallContext.Instance);
            Assert.Equal("first", values.Entries["token"]);

            var held = new SetSystemCredentialsRequest
            {
                Access = new CredentialAccessContext { ActorId = "admin", Capability = CredentialCapability.SystemAdmin },
                OperationId = Guid.NewGuid().ToString("N")
            };
            held.Entries.Add("MCP_REFRESH_TOKEN", "held-secret");
            await service.SetSystemCredentials(held, TestCallContext.Instance);
            var named = new GetResolvedCredentialsRequest
            {
                Access = new CredentialAccessContext { ActorId = "member", Capability = CredentialCapability.RuntimeResolve },
                UserId = "member",
            };
            named.RequiredKeys.Add("token");
            named.RequiredKeys.Add("MCP_REFRESH_TOKEN");
            CredentialMapResponse hidden = await service.GetResolvedCredentials(named, TestCallContext.Instance);
            Assert.Empty(hidden.Entries);
            named.IncludeSystemScope = true;
            CredentialMapResponse systemOnly = await service.GetResolvedCredentials(named, TestCallContext.Instance);
            Assert.Equal("first", systemOnly.Entries["token"]);
            Assert.False(systemOnly.Entries.ContainsKey("MCP_REFRESH_TOKEN"));
            named.IncludeHeldSecrets = true;
            CredentialMapResponse heldVisible = await service.GetResolvedCredentials(named, TestCallContext.Instance);
            Assert.Equal("held-secret", heldVisible.Entries["MCP_REFRESH_TOKEN"]);
            await using SqliteConnection auditConnection = await database.OpenConnectionAsync(TestContext.Current.CancellationToken);
            await using var auditCommand = new SqliteCommand(
                "SELECT group_concat(actor_id || ':' || action || ':' || scope || ':' || key_id || ':' || outcome, '|') FROM credential_audit ORDER BY sequence",
                auditConnection);
            string auditText = (string?)await auditCommand.ExecuteScalarAsync(TestContext.Current.CancellationToken) ?? string.Empty;
            Assert.Contains("admin:set:system:batch:success", auditText, StringComparison.Ordinal);
            Assert.DoesNotContain("first", auditText, StringComparison.Ordinal);
            Assert.DoesNotContain("must-not-overwrite", auditText, StringComparison.Ordinal);
        }
        finally
        {
            SqliteTestPoolCleanup.ClearPoolsUnderDirectory(root);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CredentialsHostPreservesOpaqueConnectorRefreshEnvelopeExactly()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-credential-boundary", Guid.NewGuid().ToString("N"));
        const string key = "KAUTUKA_GOOGLE_MAIL_REFRESH_TOKEN";
        const string envelope = "krt1.key-2026.AAAAAAAAAAAAAAAA.AAAAAAAAAAAAAAAAAAAAAA";
        try
        {
            Directory.CreateDirectory(root);
            var database = new CredentialsDatabase(root);
            CredentialsSchemaInitializer.EnsureReady(database);
            using var keyRing = new CredentialKeyRing(root);
            var vault = new CredentialsVaultService(keyRing);
            var audit = new CredentialAuditStore(database);
            var service = new CredentialsGrpcService(
                database,
                new CredentialRepository(database, vault, audit),
                vault,
                audit,
                new CredentialClientIdentityState("test-thumbprint", DateTimeOffset.UtcNow.AddDays(30)),
                NullLogger<CredentialsGrpcService>.Instance);

            SetCredentialsResponse stored = await service.SetUserCredentials(
                new SetUserCredentialsRequest
                {
                    UserId = "owner",
                    Access = new CredentialAccessContext { ActorId = "owner", Capability = CredentialCapability.OwnUserAdmin },
                    OperationId = Guid.NewGuid().ToString("N"),
                    Entries = { [key] = envelope },
                },
                TestCallContext.Instance).ConfigureAwait(true);
            Assert.Equal(1, stored.UpsertedCount);

            CredentialMapResponse resolved = await service.GetResolvedCredentials(
                new GetResolvedCredentialsRequest
                {
                    UserId = "owner",
                    Access = new CredentialAccessContext { ActorId = "owner", Capability = CredentialCapability.RuntimeResolve },
                    IncludeHeldSecrets = true,
                    RequiredKeys = { key },
                },
                TestCallContext.Instance).ConfigureAwait(true);

            Assert.Equal(envelope, resolved.Entries[key]);
        }
        finally
        {
            SqliteTestPoolCleanup.ClearPoolsUnderDirectory(root);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CredentialVaultDirectoryIsRestrictedToTheProcessOwner()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-credential-boundary", Guid.NewGuid().ToString("N"));
        try
        {
            var layout = new CredentialStorageLayout(root);
            if (OperatingSystem.IsWindows())
            {
                DirectorySecurity security = new DirectoryInfo(layout.VaultRoot).GetAccessControl();
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
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    new DirectoryInfo(layout.VaultRoot).UnixFileMode);
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
    public void CredentialDatabaseFileIsRestrictedToTheProcessOwner()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-credential-boundary", Guid.NewGuid().ToString("N"));
        try
        {
            var database = new CredentialsDatabase(root);
            using SqliteConnection connection = database.OpenConnection();
            AssertOwnerOnlyFile(database.DatabasePath);
        }
        finally
        {
            SqliteTestPoolCleanup.ClearPoolsUnderDirectory(root);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void VaultKeyFileDropsAWorldReadRuleAndStillDecrypts()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-credential-boundary", Guid.NewGuid().ToString("N"));
        try
        {
            string keyDirectory = Path.Combine(root, "vault-keys");
            Directory.CreateDirectory(keyDirectory);
            const string keyId = "ABCDEF0123456789ABCDEF01";
            string keyPath = Path.Combine(keyDirectory, keyId + ".key");
            byte[] key = RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(keyPath, key);
            File.WriteAllText(Path.Combine(keyDirectory, "active"), keyId);
            GrantWorldRead(keyPath);

            using var keyRing = new CredentialKeyRing(root);
            var vault = new CredentialsVaultService(keyRing);
            var identity = new CredentialEnvelopeIdentity("user", "user-a", "api-key");
            string envelope = vault.Encrypt("secret", identity);
            Assert.Equal("secret", vault.Decrypt(envelope, identity));
            Assert.Equal(key, File.ReadAllBytes(keyPath));
            AssertOwnerOnlyFile(keyPath);
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
    public async Task WorldReadableRecoveryKeyIsRejected()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-credential-recovery", Guid.NewGuid().ToString("N"));
        string externalRoot = Path.Combine(Path.GetTempPath(), "kautuka-recovery-owner", Guid.NewGuid().ToString("N"));
        try
        {
            var layout = new CredentialStorageLayout(root);
            var database = new CredentialsDatabase(layout.VaultRoot);
            CredentialsSchemaInitializer.EnsureReady(database);
            Directory.CreateDirectory(externalRoot);
            string recoveryKey = Path.Combine(externalRoot, "recovery.key");
            await File.WriteAllBytesAsync(recoveryKey, RandomNumberGenerator.GetBytes(32), TestContext.Current.CancellationToken).ConfigureAwait(true);
            GrantWorldRead(recoveryKey);
            var recovery = new CredentialRecoveryArchive(layout, database);

            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => recovery.CreateAsync(Path.Combine(externalRoot, "credentials.kcr"), recoveryKey, TestContext.Current.CancellationToken)).ConfigureAwait(true);

            Assert.Contains("owner-only", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            SqliteTestPoolCleanup.ClearPoolsUnderDirectory(root);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(externalRoot)) Directory.Delete(externalRoot, recursive: true);
        }
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

    private static void AssertOwnerOnlyFile(string path)
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
    public async Task RecoveryArchiveRoundTripsVaultAndRejectsTampering()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-credential-recovery", Guid.NewGuid().ToString("N"));
        string externalRoot = Path.Combine(Path.GetTempPath(), "kautuka-recovery-owner", Guid.NewGuid().ToString("N"));
        try
        {
            var layout = new CredentialStorageLayout(root);
            var database = new CredentialsDatabase(layout.VaultRoot);
            CredentialsSchemaInitializer.EnsureReady(database);
            using var keyRing = new CredentialKeyRing(layout.VaultRoot);
            var vault = new CredentialsVaultService(keyRing);
            string envelope = vault.Encrypt("recover-me", new CredentialEnvelopeIdentity("system", string.Empty, "token"));
            await using (SqliteConnection connection = await database.OpenConnectionAsync(TestContext.Current.CancellationToken))
            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO system_credentials(key,encrypted_value,created_at,updated_at) VALUES('token',$value,'now','now')";
                command.Parameters.AddWithValue("$value", envelope);
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            Directory.CreateDirectory(externalRoot);
            string recoveryKey = Path.Combine(externalRoot, "recovery.key");
            await File.WriteAllBytesAsync(recoveryKey, RandomNumberGenerator.GetBytes(32), TestContext.Current.CancellationToken);
            OwnerOnlyFile.RestrictFile(recoveryKey);
            string archive = Path.Combine(externalRoot, "credentials.kcr");
            var recovery = new CredentialRecoveryArchive(layout, database);
            await recovery.CreateAsync(archive, recoveryKey, TestContext.Current.CancellationToken);

            byte[] tampered = await File.ReadAllBytesAsync(archive, TestContext.Current.CancellationToken);
            tampered[^1] ^= 0x40;
            string tamperedPath = Path.Combine(externalRoot, "tampered.kcr");
            await File.WriteAllBytesAsync(tamperedPath, tampered, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<CredentialIntegrityException>(
                () => recovery.RestoreBeforeStartupAsync(tamperedPath, recoveryKey, TestContext.Current.CancellationToken));

            SqliteTestPoolCleanup.ClearPoolsUnderDirectory(layout.VaultRoot);
            Directory.Delete(layout.VaultRoot, recursive: true);
            Directory.CreateDirectory(layout.VaultRoot);
            await recovery.RestoreBeforeStartupAsync(archive, recoveryKey, TestContext.Current.CancellationToken);
            using var restoredKeys = new CredentialKeyRing(layout.VaultRoot);
            var restoredVault = new CredentialsVaultService(restoredKeys);
            var restoredDatabase = new CredentialsDatabase(layout.VaultRoot);
            await using SqliteConnection restoredConnection = await restoredDatabase.OpenConnectionAsync(TestContext.Current.CancellationToken);
            await using var restoredCommand = new SqliteCommand("SELECT encrypted_value FROM system_credentials WHERE key='token'", restoredConnection);
            string restoredEnvelope = Assert.IsType<string>(await restoredCommand.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            Assert.Equal("recover-me", restoredVault.Decrypt(restoredEnvelope, new CredentialEnvelopeIdentity("system", string.Empty, "token")));
        }
        finally
        {
            SqliteTestPoolCleanup.ClearPoolsUnderDirectory(root);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(externalRoot)) Directory.Delete(externalRoot, recursive: true);
        }
    }

    [Fact]
    public async Task MutationQuotaFailureRollsBackWholeBatch()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-credential-quota", Guid.NewGuid().ToString("N"));
        try
        {
            var database = new CredentialsDatabase(root);
            CredentialsSchemaInitializer.EnsureReady(database);
            using var keyRing = new CredentialKeyRing(root);
            var vault = new CredentialsVaultService(keyRing);
            var audit = new CredentialAuditStore(database);
            var repository = new CredentialRepository(database, vault, audit);
            await using (SqliteConnection connection = await database.OpenConnectionAsync(TestContext.Current.CancellationToken))
            {
                for (int i = 0; i < 256; i++)
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = "INSERT INTO user_credentials(user_id,key,encrypted_value,created_at,updated_at) VALUES('user-a',$key,'value','now','now')";
                    command.Parameters.AddWithValue("$key", $"key-{i}");
                    await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
                }
            }

            RpcException exception = await Assert.ThrowsAsync<RpcException>(() => repository.MutateAsync(
                new CredentialMutation(Guid.NewGuid().ToString("N"), "user-a", "user", "user-a",
                    new Dictionary<string, string> { ["overflow"] = "secret" }, null,
                    new CredentialAuditEvent("correlation", "user-a", "set", "user", "user-a", "batch", "success")),
                TestContext.Current.CancellationToken));
            Assert.Equal(StatusCode.ResourceExhausted, exception.StatusCode);
            await using SqliteConnection verify = await database.OpenConnectionAsync(TestContext.Current.CancellationToken);
            await using var verifyCommand = new SqliteCommand("SELECT COUNT(*) FROM user_credentials WHERE user_id='user-a' AND key='overflow'", verify);
            Assert.Equal(0L, await verifyCommand.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            SqliteTestPoolCleanup.ClearPoolsUnderDirectory(root);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task KeyRotationResumesAfterRestartAndRetiresOldKeyOnlyAfterVerification()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-credential-rotation", Guid.NewGuid().ToString("N"));
        try
        {
            var database = new CredentialsDatabase(root);
            CredentialsSchemaInitializer.EnsureReady(database);
            string oldKeyId;
            string targetKeyId;
            using (var initialKeys = new CredentialKeyRing(root))
            {
                oldKeyId = initialKeys.ActiveKeyId;
                var initialVault = new CredentialsVaultService(initialKeys);
                await using SqliteConnection connection = await database.OpenConnectionAsync(TestContext.Current.CancellationToken);
                for (int i = 0; i < 70; i++)
                {
                    string key = $"key-{i:D3}";
                    string encrypted = initialVault.Encrypt($"value-{i}", new CredentialEnvelopeIdentity("system", string.Empty, key));
                    await using SqliteCommand insert = connection.CreateCommand();
                    insert.CommandText = "INSERT INTO system_credentials(key,encrypted_value,created_at,updated_at) VALUES($key,$value,'now','now')";
                    insert.Parameters.AddWithValue("$key", key);
                    insert.Parameters.AddWithValue("$value", encrypted);
                    await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
                }
                var firstProcess = new CredentialKeyRotationService(database, initialKeys, initialVault, new CredentialAuditStore(database));
                Assert.True(await firstProcess.ProcessNextBatchAsync(TestContext.Current.CancellationToken));
                CredentialKeyRotationProgress interrupted = await firstProcess.BeginOrResumeAsync(TestContext.Current.CancellationToken);
                Assert.Equal("running", interrupted.Status);
                Assert.Equal(64, interrupted.ProcessedCount);
                targetKeyId = interrupted.TargetKeyId;
            }

            using var restartedKeys = new CredentialKeyRing(root);
            var restartedVault = new CredentialsVaultService(restartedKeys);
            var resumed = new CredentialKeyRotationService(database, restartedKeys, restartedVault, new CredentialAuditStore(database));
            CredentialKeyRotationProgress completed = await resumed.RunToCompletionAsync(TestContext.Current.CancellationToken);
            Assert.Equal("completed", completed.Status);
            Assert.Equal(targetKeyId, completed.TargetKeyId);
            Assert.Throws<CredentialIntegrityException>(() => restartedKeys.GetKey(oldKeyId));
            await using SqliteConnection verify = await database.OpenConnectionAsync(TestContext.Current.CancellationToken);
            await using var command = new SqliteCommand("SELECT encrypted_value FROM system_credentials ORDER BY key", verify);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            int count = 0;
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                Assert.True(AesGcmEnvelope.TryDeserialize(reader.GetString(0), out SecretEnvelope? envelope));
                Assert.Equal(targetKeyId, envelope!.KeyId);
                count++;
            }
            Assert.Equal(70, count);
        }
        finally
        {
            SqliteTestPoolCleanup.ClearPoolsUnderDirectory(root);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task KeyRotationIntegrityFailureKeepsOldKeyAndCheckpointUncommitted()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-credential-rotation-failure", Guid.NewGuid().ToString("N"));
        try
        {
            var database = new CredentialsDatabase(root);
            CredentialsSchemaInitializer.EnsureReady(database);
            using var keys = new CredentialKeyRing(root);
            string oldKeyId = keys.ActiveKeyId;
            await using (SqliteConnection connection = await database.OpenConnectionAsync(TestContext.Current.CancellationToken))
            await using (SqliteCommand insert = connection.CreateCommand())
            {
                insert.CommandText = "INSERT INTO system_credentials(key,encrypted_value,created_at,updated_at) VALUES('broken','not-an-envelope','now','now')";
                await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            var rotation = new CredentialKeyRotationService(database, keys, new CredentialsVaultService(keys), new CredentialAuditStore(database));
            await Assert.ThrowsAsync<CredentialIntegrityException>(() => rotation.ProcessNextBatchAsync(TestContext.Current.CancellationToken));
            CredentialKeyRotationProgress progress = await rotation.BeginOrResumeAsync(TestContext.Current.CancellationToken);
            Assert.Equal("running", progress.Status);
            Assert.Equal(0, progress.ProcessedCount);
            byte[] retained = keys.GetKey(oldKeyId);
            CryptographicOperations.ZeroMemory(retained);
        }
        finally
        {
            SqliteTestPoolCleanup.ClearPoolsUnderDirectory(root);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class TestCallContext : ServerCallContext
    {
        public static readonly TestCallContext Instance = new();
        private readonly Dictionary<object, object> state = [];
        private Metadata trailers = [];
        private Status status;
        private WriteOptions? options;
        protected override string MethodCore => "Credentials";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "local";
        protected override DateTime DeadlineCore => DateTime.MaxValue;
        protected override Metadata RequestHeadersCore => [];
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;
        protected override Metadata ResponseTrailersCore => trailers;
        protected override IDictionary<object, object> UserStateCore => state;
        protected override Status StatusCore { get => status; set => status = value; }
        protected override WriteOptions? WriteOptionsCore { get => options; set => options = value; }
        protected override AuthContext AuthContextCore => new(string.Empty, []);
        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => throw new NotSupportedException();
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }
}
