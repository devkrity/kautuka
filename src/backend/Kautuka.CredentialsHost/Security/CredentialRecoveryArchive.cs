using System.Security.Cryptography;
using System.Text;

using Kautuka.CredentialsHost.Database;

using Microsoft.Data.Sqlite;

namespace Kautuka.CredentialsHost.Security;

internal sealed class CredentialRecoveryArchive(
    CredentialStorageLayout layout,
    CredentialsDatabase database)
{
    private const int schemaVersion = 1;
    private const long maximumArchiveBytes = 512L * 1024 * 1024;
    private static readonly byte[] outerMagic = "KAUTUKA-CREDENTIAL-RECOVERY\0"u8.ToArray();
    private static readonly byte[] payloadMagic = "KAUTUKA-CREDENTIAL-VAULT\0"u8.ToArray();

    public async Task CreateAsync(string archivePath, string recoveryKeyPath, CancellationToken cancellationToken)
    {
        layout.ValidateExternalRecoveryKeyPath(recoveryKeyPath);
        byte[] recoveryKey = LoadRecoveryKey(recoveryKeyPath);
        string snapshotDirectory = CreateOwnerOnlyDirectory(Path.Combine(Path.GetTempPath(), $"kautuka-credentials-{Guid.NewGuid():N}"));
        string snapshotPath = Path.Combine(snapshotDirectory, "snapshot.db");
        string outputPath = Path.GetFullPath(archivePath);
        string temporaryOutputPath = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await database.CreateOnlineSnapshotAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
            byte[] plaintext = BuildPayload(snapshotPath, Path.Combine(layout.VaultRoot, "vault-keys"));
            try
            {
                byte[] nonce = RandomNumberGenerator.GetBytes(12);
                byte[] ciphertext = new byte[plaintext.Length];
                byte[] tag = new byte[16];
                using (var aes = new AesGcm(recoveryKey, tag.Length))
                {
                    aes.Encrypt(nonce, plaintext, ciphertext, tag, outerMagic);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                await using (var stream = OpenOwnerOnly(temporaryOutputPath))
                await using (var writer = new BinaryWriterStream(stream))
                {
                    await writer.WriteAsync(outerMagic, cancellationToken).ConfigureAwait(false);
                    await writer.WriteInt32Async(schemaVersion, cancellationToken).ConfigureAwait(false);
                    await writer.WriteAsync(nonce, cancellationToken).ConfigureAwait(false);
                    await writer.WriteAsync(tag, cancellationToken).ConfigureAwait(false);
                    await writer.WriteInt32Async(ciphertext.Length, cancellationToken).ConfigureAwait(false);
                    await writer.WriteAsync(ciphertext, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                SecureFile(temporaryOutputPath);
                File.Move(temporaryOutputPath, outputPath, overwrite: true);
                SecureFile(outputPath);
                CryptographicOperations.ZeroMemory(ciphertext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(recoveryKey);
            if (Directory.Exists(snapshotDirectory)) Directory.Delete(snapshotDirectory, recursive: true);
            if (File.Exists(temporaryOutputPath)) File.Delete(temporaryOutputPath);
        }
    }

    public async Task RestoreBeforeStartupAsync(string archivePath, string recoveryKeyPath, CancellationToken cancellationToken)
    {
        layout.ValidateExternalRecoveryKeyPath(recoveryKeyPath);
        byte[] recoveryKey = LoadRecoveryKey(recoveryKeyPath);
        string stageRoot = CreateOwnerOnlyDirectory(Path.Combine(Path.GetTempPath(), $"kautuka-credential-restore-{Guid.NewGuid():N}"));
        try
        {
            byte[] plaintext = await DecryptArchiveAsync(archivePath, recoveryKey, cancellationToken).ConfigureAwait(false);
            try
            {
                ExtractPayload(plaintext, stageRoot);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }

            ValidateStagedVault(stageRoot);
            ReplaceVault(stageRoot);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(recoveryKey);
            if (Directory.Exists(stageRoot)) Directory.Delete(stageRoot, recursive: true);
        }
    }

    private static byte[] BuildPayload(string snapshotPath, string keyRingPath)
    {
        using var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(payloadMagic);
            writer.Write(schemaVersion);
            WriteBoundedFile(writer, "kautuka.credentials.db", snapshotPath);
            string[] keyFiles = Directory.EnumerateFiles(keyRingPath)
                .OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal)
                .ToArray();
            writer.Write(keyFiles.Length);
            foreach (string keyFile in keyFiles)
            {
                WriteBoundedFile(writer, Path.GetFileName(keyFile), keyFile);
            }
        }

        byte[] bodyBytes = body.ToArray();
        byte[] checksum = SHA256.HashData(bodyBytes);
        using var payload = new MemoryStream(bodyBytes.Length + checksum.Length + sizeof(int));
        payload.Write(bodyBytes);
        using (var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(checksum.Length);
            writer.Write(checksum);
        }
        return payload.ToArray();
    }

    private static void WriteBoundedFile(BinaryWriter writer, string name, string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.LongLength > maximumArchiveBytes) throw new InvalidOperationException("Credential recovery input exceeds the supported bound.");
        writer.Write(name);
        writer.Write(bytes.Length);
        writer.Write(bytes);
        CryptographicOperations.ZeroMemory(bytes);
    }

    private static async Task<byte[]> DecryptArchiveAsync(string archivePath, byte[] recoveryKey, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > maximumArchiveBytes) throw new InvalidOperationException("Credential recovery archive exceeds the supported bound.");
        byte[] bytes = new byte[stream.Length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        using var reader = new BinaryReader(new MemoryStream(bytes, writable: false), Encoding.UTF8);
        if (!reader.ReadBytes(outerMagic.Length).AsSpan().SequenceEqual(outerMagic) || reader.ReadInt32() != schemaVersion)
            throw new CredentialIntegrityException("Credential recovery archive header is invalid.");
        byte[] nonce = reader.ReadBytes(12);
        byte[] tag = reader.ReadBytes(16);
        int length = reader.ReadInt32();
        if (length < 0 || length != bytes.Length - reader.BaseStream.Position)
            throw new CredentialIntegrityException("Credential recovery archive length is invalid.");
        byte[] ciphertext = reader.ReadBytes(length);
        byte[] plaintext = new byte[length];
        try
        {
            using var aes = new AesGcm(recoveryKey, tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, outerMagic);
            return plaintext;
        }
        catch (AuthenticationTagMismatchException exception)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new CredentialIntegrityException("Credential recovery archive authentication failed.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    private static void ExtractPayload(byte[] payload, string stageRoot)
    {
        if (payload.Length < 36) throw new CredentialIntegrityException("Credential recovery payload is truncated.");
        int checksumLengthOffset = payload.Length - 36;
        int checksumLength = BitConverter.ToInt32(payload, checksumLengthOffset);
        if (checksumLength != 32) throw new CredentialIntegrityException("Credential recovery checksum is invalid.");
        ReadOnlySpan<byte> body = payload.AsSpan(0, checksumLengthOffset);
        ReadOnlySpan<byte> expectedChecksum = payload.AsSpan(checksumLengthOffset + sizeof(int), checksumLength);
        byte[] actualChecksum = SHA256.HashData(body);
        if (!CryptographicOperations.FixedTimeEquals(actualChecksum, expectedChecksum))
            throw new CredentialIntegrityException("Credential recovery checksum mismatch.");

        using var reader = new BinaryReader(new MemoryStream(body.ToArray(), writable: false), Encoding.UTF8);
        if (!reader.ReadBytes(payloadMagic.Length).AsSpan().SequenceEqual(payloadMagic) || reader.ReadInt32() != schemaVersion)
            throw new CredentialIntegrityException("Credential recovery payload header is invalid.");
        ReadFile(reader, stageRoot, expectedName: "kautuka.credentials.db");
        int keyCount = reader.ReadInt32();
        if (keyCount is < 2 or > 64) throw new CredentialIntegrityException("Credential recovery key set is invalid.");
        string keyRoot = Path.Combine(stageRoot, "vault-keys");
        Directory.CreateDirectory(keyRoot);
        for (int i = 0; i < keyCount; i++) ReadFile(reader, keyRoot, expectedName: null);
        if (reader.BaseStream.Position != reader.BaseStream.Length)
            throw new CredentialIntegrityException("Credential recovery payload has trailing data.");
    }

    private static void ReadFile(BinaryReader reader, string root, string? expectedName)
    {
        string name = reader.ReadString();
        if ((expectedName is not null && !string.Equals(name, expectedName, StringComparison.Ordinal))
            || name != Path.GetFileName(name)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new CredentialIntegrityException("Credential recovery entry name is invalid.");
        int length = reader.ReadInt32();
        if (length < 0 || length > maximumArchiveBytes || length > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new CredentialIntegrityException("Credential recovery entry length is invalid.");
        File.WriteAllBytes(Path.Combine(root, name), reader.ReadBytes(length));
    }

    private static void ValidateStagedVault(string stageRoot)
    {
        string databasePath = Path.Combine(stageRoot, "kautuka.credentials.db");
        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        if (!string.Equals(command.ExecuteScalar() as string, "ok", StringComparison.Ordinal))
            throw new CredentialIntegrityException("Recovered credential database failed integrity validation.");
        string keyRoot = Path.Combine(stageRoot, "vault-keys");
        string active = File.ReadAllText(Path.Combine(keyRoot, "active")).Trim();
        if (active.Length == 0 || !File.Exists(Path.Combine(keyRoot, active + ".key")))
            throw new CredentialIntegrityException("Recovered credential key ring is incomplete.");
    }

    private void ReplaceVault(string stageRoot)
    {
        string rollbackRoot = layout.VaultRoot + ".rollback." + Guid.NewGuid().ToString("N");
        Directory.Move(layout.VaultRoot, rollbackRoot);
        try
        {
            Directory.Move(stageRoot, layout.VaultRoot);
            Directory.Delete(rollbackRoot, recursive: true);
        }
        catch
        {
            if (Directory.Exists(layout.VaultRoot)) Directory.Delete(layout.VaultRoot, recursive: true);
            Directory.Move(rollbackRoot, layout.VaultRoot);
            throw;
        }
    }

    private static byte[] LoadRecoveryKey(string path)
    {
        byte[] key = File.ReadAllBytes(path);
        if (key.Length != 32) throw new InvalidOperationException("The external credential recovery key must contain exactly 32 bytes.");
        OwnerOnlyFile.RequireRecoveryKeyOwnerOnly(path);
        return key;
    }

    private static string CreateOwnerOnlyDirectory(string path)
    {
        Directory.CreateDirectory(path);
        OwnerOnlyFile.RestrictDirectory(path);
        return path;
    }

    private static FileStream OpenOwnerOnly(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 81920,
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        var stream = new FileStream(path, options);
        OwnerOnlyFile.RestrictFile(path);
        return stream;
    }

    private static void SecureFile(string path) => OwnerOnlyFile.RestrictFile(path);

    private sealed class BinaryWriterStream(Stream stream) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task WriteAsync(byte[] value, CancellationToken cancellationToken) => stream.WriteAsync(value, cancellationToken).AsTask();
        public async Task WriteInt32Async(int value, CancellationToken cancellationToken)
            => await stream.WriteAsync(BitConverter.GetBytes(value), cancellationToken).ConfigureAwait(false);
    }
}
