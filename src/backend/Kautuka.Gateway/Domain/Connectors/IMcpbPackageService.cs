using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

using Kautuka.Gateway.Domain.Packages;

namespace Kautuka.Gateway.Domain.Connectors;

internal sealed record McpbConnectorInspection(
    string ServerId,
    GatewayConnectorUpsertRequest Connector,
    string ManifestVersion,
    string ServerType,
    string BundleName);

internal interface IMcpbPackageService
{
    Task<McpbConnectorInspection> InspectAsync(Stream archiveStream, string fileName, CancellationToken cancellationToken);
}

internal sealed class McpbPackageService(KautukaDatabase database) : IMcpbPackageService
{
    private const int maxManifestBytes = 1 * 1024 * 1024;
    private const string bundleDirectoryName = "mcpb-bundles";
    private static readonly ArchiveIngestionLimits archiveLimits = new(
        MaxCompressedBytes: 50L * 1024 * 1024,
        MaxEntries: 512,
        MaxEntryBytes: 32L * 1024 * 1024,
        MaxExpandedBytes: 200L * 1024 * 1024,
        MaxCompressionRatio: 100,
        MaxProcessingTime: TimeSpan.FromSeconds(30));

    private static readonly HashSet<string> supportedManifestVersions = new(StringComparer.Ordinal)
    {
        "0.3",
        "0.4"
    };

    private static readonly HashSet<string> allowedRootFields = new(StringComparer.Ordinal)
    {
        "$schema",
        "manifest_version",
        "name",
        "display_name",
        "version",
        "description",
        "long_description",
        "author",
        "repository",
        "homepage",
        "documentation",
        "support",
        "icon",
        "icons",
        "screenshots",
        "localization",
        "server",
        "tools",
        "tools_generated",
        "prompts",
        "prompts_generated",
        "keywords",
        "license",
        "privacy_policies",
        "compatibility",
        "user_config",
        "_meta"
    };

    private static readonly HashSet<string> allowedAuthorFields = new(StringComparer.Ordinal)
    {
        "name",
        "email",
        "url"
    };

    private static readonly HashSet<string> allowedServerFields = new(StringComparer.Ordinal)
    {
        "type",
        "entry_point",
        "mcp_config"
    };

    private static readonly HashSet<string> allowedMcpConfigFields = new(StringComparer.Ordinal)
    {
        "command",
        "args",
        "env",
        "platform_overrides"
    };

    public async Task<McpbConnectorInspection> InspectAsync(Stream archiveStream, string fileName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(archiveStream);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        if (!fileName.EndsWith(".mcpb", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only .mcpb bundle uploads are supported.");
        }

        string tempDirectory = Path.Combine(Path.GetTempPath(), $"kautuka-mcpb-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        string archivePath = Path.Combine(tempDirectory, "bundle.mcpb");
        using var ingestion = new BoundedZipArchive(archiveLimits, cancellationToken);

        try
        {
#pragma warning disable CA2007 // await using on Stream — no SynchronizationContext in ASP.NET Core
            await using (FileStream output = File.Create(archivePath))
            {
                await ingestion.CopyUploadAsync(archiveStream, output).ConfigureAwait(false);
            }
#pragma warning restore CA2007

#pragma warning disable CA1849 // ZipFile.OpenRead has no async overload
            using ZipArchive archive = ZipFile.OpenRead(archivePath);
#pragma warning restore CA1849

            IReadOnlyList<ZipArchiveEntry> fileEntries = ingestion.Validate(archive, "MCPB bundle");

            ZipArchiveEntry? manifestEntry = fileEntries.FirstOrDefault(static entry =>
                string.Equals(NormalizeEntryPath(entry.FullName), "manifest.json", StringComparison.OrdinalIgnoreCase)
                || NormalizeEntryPath(entry.FullName).EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase));

            if (manifestEntry is null)
            {
                throw new InvalidOperationException("manifest.json is required in the bundle archive.");
            }

            if (manifestEntry.Length > maxManifestBytes)
            {
                throw new InvalidOperationException($"manifest.json exceeds the {maxManifestBytes} byte limit.");
            }

            string manifestJson;
#pragma warning disable CA2007 // await using on Stream — no SynchronizationContext in ASP.NET Core
            await using (var manifestStream = new MemoryStream((int)manifestEntry.Length))
            {
                await ingestion.CopyEntryAsync(manifestEntry, manifestStream, "MCPB bundle").ConfigureAwait(false);
                manifestStream.Position = 0;
                using var reader = new StreamReader(manifestStream);
                manifestJson = await reader.ReadToEndAsync(ingestion.CancellationToken).ConfigureAwait(false);
            }
#pragma warning restore CA2007

            using JsonDocument document = JsonDocument.Parse(manifestJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("manifest.json must contain a JSON object.");
            }

            JsonElement root = document.RootElement;
            ValidateAllowedFields(root, allowedRootFields, "manifest");

            string manifestVersion = ReadManifestVersion(root);
            string bundleName = ReadRequiredString(root, "name");
            string displayName = ReadOptionalString(root, "display_name") ?? bundleName;
            string description = ReadRequiredString(root, "description");

            JsonElement author = ReadRequiredObject(root, "author");
            ValidateAllowedFields(author, allowedAuthorFields, "manifest.author");
            _ = ReadRequiredString(author, "name");

            JsonElement server = ReadRequiredObject(root, "server");
            ValidateAllowedFields(server, allowedServerFields, "manifest.server");

            string serverType = ReadRequiredString(server, "type");
            if (!string.Equals(serverType, "node", StringComparison.Ordinal)
                && !string.Equals(serverType, "python", StringComparison.Ordinal)
                && !string.Equals(serverType, "uv", StringComparison.Ordinal)
                && !string.Equals(serverType, "binary", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unsupported server.type '{serverType}'.");
            }

            _ = ReadRequiredString(server, "entry_point");

            JsonElement mcpConfig = ReadRequiredObject(server, "mcp_config");
            ValidateAllowedFields(mcpConfig, allowedMcpConfigFields, "manifest.server.mcp_config");

            string command = ReadRequiredString(mcpConfig, "command");
            string[] args = ReadStringArray(mcpConfig, "args");
            List<string> envValues = ReadEnvValues(mcpConfig);
            string connectorId = NormalizeConnectorId(bundleName);
            string normalizedServerType = serverType.Trim();
            ValidateServerLaunchConfiguration(normalizedServerType, command, args, envValues);
            string[] envKeys = ExtractUserConfigReferences(root, command, args, envValues);
            PersistedBundleArchive persistedBundle = PersistBundleArchive(archivePath, connectorId);

            return new McpbConnectorInspection(
                connectorId,
                new GatewayConnectorUpsertRequest
                {
                    Name = displayName,
                    Description = description,
                    Transport = "container",
                    ContainerImage = ResolveRuntimeImage(normalizedServerType),
                    ContainerCommand = command,
                    ContainerArgs = args,
                    BundlePath = persistedBundle.Path,
                    ServerType = normalizedServerType,
                    RuntimeArtifactType = "oci",
                    RuntimeArtifactSourceDigest = persistedBundle.SourceDigest,
                    RuntimeArtifactBuildStatus = "pending",
                    EnvKeys = envKeys.Length == 0 ? null : envKeys,
                    Enabled = true,
                    ReadinessTimeoutSeconds = 60,
                    JitCooldownSeconds = 600,
                },
                manifestVersion,
                normalizedServerType,
                bundleName);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"manifest.json is not valid JSON: {ex.Message}");
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private PersistedBundleArchive PersistBundleArchive(string sourceArchivePath, string connectorId)
    {
        string bundleRoot = Path.Combine(database.DataDirectory, bundleDirectoryName);
        Directory.CreateDirectory(bundleRoot);

        string connectorDirectory = Path.Combine(bundleRoot, connectorId);
        Directory.CreateDirectory(connectorDirectory);

        string digest = ComputeSha256Hex(sourceArchivePath);
        string destinationPath = Path.Combine(connectorDirectory, $"{digest}.mcpb");

        // Only copy the archive file if not already persisted
        if (!File.Exists(destinationPath))
        {
            File.Copy(sourceArchivePath, destinationPath, overwrite: false);
        }

        ApplyManagerReadableUnixMode(bundleRoot, connectorDirectory, destinationPath);

        return new PersistedBundleArchive(destinationPath, digest);
    }

    private sealed record PersistedBundleArchive(string Path, string SourceDigest);

    private static void ApplyManagerReadableUnixMode(
        string bundleRoot,
        string connectorDirectory,
        string destinationPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        UnixFileMode directoryMode = UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead
            | UnixFileMode.GroupExecute
            | UnixFileMode.SetGroup;
        UnixFileMode fileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        File.SetUnixFileMode(bundleRoot, directoryMode);
        File.SetUnixFileMode(connectorDirectory, directoryMode);
        File.SetUnixFileMode(destinationPath, fileMode);
    }

    private static string ComputeSha256Hex(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexStringLower(hash);
    }

    private static string ResolveRuntimeImage(string serverType)
        => serverType switch
        {
            "node" => McpCommandTransportMapper.NodeImage,
            "python" => McpCommandTransportMapper.PythonImage,
            "uv" => McpCommandTransportMapper.UvImage,
            "binary" => McpCommandTransportMapper.BinaryImage,
            _ => throw new InvalidOperationException($"Unsupported server.type '{serverType}'.")
        };

    private static void ValidateServerLaunchConfiguration(
        string serverType,
        string command,
        IReadOnlyList<string> args,
        IEnumerable<string> envValues)
    {
        if (!string.Equals(serverType, "uv", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (ContainsBundleDirectoryPlaceholder(command)
            || args.Any(ContainsBundleDirectoryPlaceholder)
            || envValues.Any(ContainsBundleDirectoryPlaceholder))
        {
            throw new InvalidOperationException("manifest.server.mcp_config for server.type 'uv' cannot reference ${__dirname}; uv bundles must run from the mounted bundle working directory.");
        }
    }

    private static bool ContainsBundleDirectoryPlaceholder(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.Contains("${__dirname}", StringComparison.Ordinal);

    private static string ReadManifestVersion(JsonElement root)
    {
        string? manifestVersion = ReadOptionalString(root, "manifest_version");
        if (string.IsNullOrWhiteSpace(manifestVersion))
        {
            throw new InvalidOperationException("manifest_version is required.");
        }

        if (!supportedManifestVersions.Contains(manifestVersion))
        {
            throw new InvalidOperationException($"Unsupported manifest version '{manifestVersion}'. Supported versions: 0.3, 0.4.");
        }

        return manifestVersion;
    }

    private static void ValidateAllowedFields(JsonElement element, HashSet<string> allowedFields, string path)
    {
        List<string> unsupported = [.. element.EnumerateObject().Select(static property => property.Name).Where(name => !allowedFields.Contains(name))];
        if (unsupported.Count > 0)
        {
            throw new InvalidOperationException($"{path} contains unsupported fields/extensions: {string.Join(", ", unsupported)}");
        }
    }

    private static JsonElement ReadRequiredObject(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"manifest.{name} is required and must be an object.");
        }

        return value;
    }

    private static string ReadRequiredString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"manifest.{name} is required and must be a string.");
        }

        string result = value.GetString()?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(result))
        {
            throw new InvalidOperationException($"manifest.{name} must not be empty.");
        }

        return result;
    }

    private static string? ReadOptionalString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"manifest.{name} must be a string when present.");
        }

        string? result = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static string[] ReadStringArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"manifest.server.mcp_config.{name} must be an array of strings when present.");
        }

        var items = new List<string>();
        foreach (JsonElement element in value.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException($"manifest.server.mcp_config.{name} entries must be strings.");
            }

            string item = element.GetString()?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(item))
            {
                items.Add(item);
            }
        }

        return [.. items];
    }

    private static List<string> ReadEnvValues(JsonElement mcpConfig)
    {
        if (!mcpConfig.TryGetProperty("env", out JsonElement env) || env.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (env.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("manifest.server.mcp_config.env must be an object when present.");
        }

        var values = new List<string>();
        foreach (JsonProperty property in env.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException($"manifest.server.mcp_config.env.{property.Name} must be a string.");
            }

            string value = property.Value.GetString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(value))
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static string[] ExtractUserConfigReferences(JsonElement manifest, string command, IReadOnlyList<string> args, IEnumerable<string> envValues)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (string token in EnumerateUserConfigTokens(command))
        {
            keys.Add(token);
        }

        foreach (string arg in args)
        {
            foreach (string token in EnumerateUserConfigTokens(arg))
            {
                keys.Add(token);
            }
        }

        foreach (string envValue in envValues)
        {
            foreach (string token in EnumerateUserConfigTokens(envValue))
            {
                keys.Add(token);
            }
        }

        if (manifest.TryGetProperty("user_config", out JsonElement userConfig)
            && userConfig.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in userConfig.EnumerateObject())
            {
                keys.Add(property.Name);
            }
        }

        return [.. keys.OrderBy(static key => key, StringComparer.OrdinalIgnoreCase)];
    }

    private static IEnumerable<string> EnumerateUserConfigTokens(string value)
    {
        const string prefix = "${user_config.";
        if (string.IsNullOrEmpty(value))
        {
            yield break;
        }

        int index = 0;
        while (index < value.Length)
        {
            int start = value.IndexOf(prefix, index, StringComparison.Ordinal);
            if (start < 0)
            {
                yield break;
            }

            int contentStart = start + prefix.Length;
            int end = value.IndexOf('}', contentStart);
            if (end < 0)
            {
                yield break;
            }

            string token = value[contentStart..end].Trim();
            if (!string.IsNullOrWhiteSpace(token))
            {
                yield return token;
            }

            index = end + 1;
        }
    }

    private static string NormalizeConnectorId(string candidate)
    {
        string trimmed = candidate.Trim();
        var builder = new System.Text.StringBuilder(trimmed.Length);
        foreach (char character in trimmed)
        {
            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-');
        }

        string normalized = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? $"mcpb-{Guid.NewGuid():N}" : normalized;
    }

    private static string NormalizeEntryPath(string fullName)
        => BoundedZipArchive.NormalizeEntryPath(fullName);
}
