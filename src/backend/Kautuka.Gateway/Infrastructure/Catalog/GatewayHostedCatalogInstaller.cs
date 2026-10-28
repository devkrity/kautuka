using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Connectors;
using Kautuka.Gateway.Domain.Skills;
using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.Gateway.Infrastructure.Catalog;

internal sealed class GatewayHostedCatalogInstaller(
    IGatewayHostedCatalogProvider catalogProvider,
    IGatewayCatalogStore catalogStore,
    IHttpClientFactory httpClientFactory,
    IMcpbPackageService mcpbPackageService,
    IMcpConnectorTestService mcpConnectorTestService,
    ISkillPackageService skillPackageService) : IGatewayHostedCatalogInstaller
{
    private const long maxArtifactBytes = 64L * 1024L * 1024L;

    public async Task<GatewayHostedCatalogInstallResult> InstallAsync(
        string kind,
        string id,
        GatewayUser user,
        string? requestedScope,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(user);

        GatewayHostedCatalogItem? item = await catalogProvider
            .GetItemAsync(kind, id, user, cancellationToken)
            .ConfigureAwait(false);

        if (item is null)
        {
            throw new KeyNotFoundException("Catalog item not found.");
        }

        if (string.Equals(item.Kind, "connector", StringComparison.OrdinalIgnoreCase))
        {
            EnsureCanImportConnectors(user);
        }
        else if (string.Equals(item.Kind, "skill", StringComparison.OrdinalIgnoreCase))
        {
            EnsureCanImportSkills(user);
        }
        else
        {
            throw new InvalidOperationException($"Unsupported catalog item kind '{item.Kind}'.");
        }

        DownloadedCatalogArtifact artifact = await DownloadArtifactAsync(item, cancellationToken).ConfigureAwait(false);
        string scope = GatewayOwnershipAccess.ResolveCatalogScope(user, requestedScope);
        string? ownerUserId = GatewayOwnershipAccess.ResolveCatalogOwnerUserId(scope, user);

        return string.Equals(item.Kind, "connector", StringComparison.OrdinalIgnoreCase)
            ? await InstallConnectorAsync(item, artifact, user, scope, ownerUserId, cancellationToken).ConfigureAwait(false)
            : await InstallSkillAsync(item, artifact, user, scope, ownerUserId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GatewayHostedCatalogInstallResult> InstallConnectorAsync(
        GatewayHostedCatalogItem item,
        DownloadedCatalogArtifact artifact,
        GatewayUser user,
        string scope,
        string? ownerUserId,
        CancellationToken cancellationToken)
    {
        bool hasExisting = catalogStore.TryGetMcpServer(item.Id, out GatewayMcpServer? existing) && existing is not null;
        if (hasExisting && !GatewayOwnershipAccess.CanManageConnector(user, existing))
        {
            throw new UnauthorizedAccessException("Forbidden.");
        }

        using MemoryStream archiveStream = new(artifact.Content, writable: false);
        McpbConnectorInspection inspection = await mcpbPackageService
            .InspectAsync(archiveStream, artifact.FileName, cancellationToken)
            .ConfigureAwait(false);

        if (!string.Equals(inspection.ServerId, item.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Catalog artifact id mismatch. Expected '{item.Id}' but bundle declared '{inspection.ServerId}'.");
        }

        McpConnectorTestResult validation = await mcpConnectorTestService
            .TestAsync(BuildImportValidationRequest(inspection.ServerId, inspection.Connector), cancellationToken)
            .ConfigureAwait(false);

        if (!validation.Success)
        {
            throw new InvalidOperationException(validation.Message);
        }

        _ = await catalogStore.SaveMcpServerAsync(
            inspection.ServerId,
            BuildActivatedImportRequest(inspection.Connector, validation),
            scope,
            ownerUserId,
            cancellationToken).ConfigureAwait(false);

        return new GatewayHostedCatalogInstallResult("connector", inspection.ServerId, hasExisting);
    }

    private async Task<GatewayHostedCatalogInstallResult> InstallSkillAsync(
        GatewayHostedCatalogItem item,
        DownloadedCatalogArtifact artifact,
        GatewayUser user,
        string scope,
        string? ownerUserId,
        CancellationToken cancellationToken)
    {
        bool hasExisting = catalogStore.TryGetSkill(item.Id, out GatewaySkill? existing) && existing is not null;
        if (hasExisting && !GatewayOwnershipAccess.CanManageSkill(user, existing))
        {
            throw new UnauthorizedAccessException("Forbidden.");
        }

        using MemoryStream archiveStream = new(artifact.Content, writable: false);
        SkillPackageImportOutcome outcome = await skillPackageService
            .ImportOutcomeAsync(
                archiveStream,
                new SkillPackageImportRequest(artifact.FileName, BuildSourceMetadata(item)),
                cancellationToken)
            .ConfigureAwait(false);

        SkillPackageInspection? inspection = outcome.Skills.SingleOrDefault(candidate =>
            string.Equals(candidate.Definition.SkillId, item.Id, StringComparison.OrdinalIgnoreCase));

        if (inspection is null)
        {
            throw new InvalidOperationException($"Catalog artifact did not contain the expected skill '{item.Id}'.");
        }

        JsonObject payload = new()
        {
            ["name"] = inspection.Definition.Name,
            ["description"] = inspection.Definition.Description,
            ["packageHash"] = inspection.Definition.PackageHash,
            ["status"] = inspection.Definition.Status,
            ["packagePath"] = inspection.Definition.PackagePath,
            ["hasResources"] = inspection.Definition.HasResources,
            ["hasScripts"] = inspection.Definition.HasScripts,
            ["enabled"] = inspection.Definition.Enabled,
            ["importTimestamp"] = inspection.Definition.ImportTimestamp.ToString("O"),
            ["sourceMetadata"] = JsonObject.Create(JsonSerializer.SerializeToElement(
                inspection.Definition.SourceMetadata,
                KautukaJsonContext.Default.DictionaryStringString)),
        };

        _ = catalogStore.SaveSkill(
            inspection.Definition.SkillId,
            JsonSerializer.SerializeToElement(payload, KautukaJsonContext.Default.JsonObject),
            scope,
            ownerUserId);

        return new GatewayHostedCatalogInstallResult("skill", inspection.Definition.SkillId, hasExisting);
    }

    private async Task<DownloadedCatalogArtifact> DownloadArtifactAsync(
        GatewayHostedCatalogItem item,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.ArtifactUrl))
        {
            throw new InvalidOperationException("Catalog item does not currently have an installable artifact.");
        }

        using HttpClient client = httpClientFactory.CreateClient();
        Uri artifactUri = new(item.ArtifactUrl, UriKind.Absolute);
        using HttpResponseMessage response = await client.GetAsync(
            artifactUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

#pragma warning disable CA2007 // await using on Stream — no SynchronizationContext in ASP.NET Core
        await using Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore CA2007
        MemoryStream buffer = new();
        await CopyWithLimitAsync(responseStream, buffer, maxArtifactBytes, cancellationToken).ConfigureAwait(false);

        byte[] content = buffer.ToArray();
        VerifyArtifactDigest(item, content);

        return new DownloadedCatalogArtifact(content, ResolveFileName(item));
    }

    private static void VerifyArtifactDigest(GatewayHostedCatalogItem item, byte[] content)
    {
        string? expectedDigest = NormalizeSha256(item.ArtifactSha256);
        bool requiresDigest = string.Equals(item.TrustKind, "verified_by_kautuka", StringComparison.OrdinalIgnoreCase);

        if (requiresDigest && string.IsNullOrWhiteSpace(expectedDigest))
        {
            throw new InvalidOperationException("Verified catalog item is missing an artifact digest.");
        }

        if (string.IsNullOrWhiteSpace(expectedDigest))
        {
            return;
        }

        byte[] digest = SHA256.HashData(content);
        string actualDigest = Convert.ToHexString(digest).ToUpperInvariant();
        if (!string.Equals(actualDigest, expectedDigest, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Catalog artifact digest verification failed.");
        }
    }

    private static string? NormalizeSha256(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            return null;
        }

        string normalized = digest.Trim();
        if (normalized.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[7..];
        }

        return normalized.ToUpperInvariant();
    }

    private static async Task CopyWithLimitAsync(Stream source, Stream destination, long maxBytes, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[81920];
        long total = 0;

        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maxBytes)
            {
                throw new InvalidOperationException($"Catalog artifact exceeds the {maxBytes} byte limit.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static string ResolveFileName(GatewayHostedCatalogItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.ArtifactUrl)
            && Uri.TryCreate(item.ArtifactUrl, UriKind.Absolute, out Uri? artifactUri))
        {
            string fileName = Path.GetFileName(artifactUri.LocalPath);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                return fileName;
            }
        }

        return string.Equals(item.Kind, "connector", StringComparison.OrdinalIgnoreCase)
            ? $"{item.Id}.mcpb"
            : $"{item.Id}.zip";
    }

    private static Dictionary<string, string> BuildSourceMetadata(GatewayHostedCatalogItem item)
        => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["source"] = "hosted_catalog",
            ["catalogId"] = item.Id,
            ["catalogKind"] = item.Kind,
            ["catalogVersion"] = item.Version,
            ["catalogPublisher"] = item.Publisher,
            ["catalogTrustKind"] = item.TrustKind,
            ["catalogArtifactUrl"] = item.ArtifactUrl ?? string.Empty,
        };

    private static GatewayConnectorTestRequest BuildImportValidationRequest(string serverId, GatewayConnectorUpsertRequest connector)
        => new()
        {
            Id = serverId,
            Transport = connector.Transport ?? "container",
            ContainerImage = connector.ContainerImage,
            ContainerCommand = connector.ContainerCommand,
            ContainerArgs = connector.ContainerArgs,
            BundlePath = connector.BundlePath,
            ServerType = connector.ServerType,
            ReadinessTimeoutSeconds = connector.ReadinessTimeoutSeconds,
            JitCooldownSeconds = connector.JitCooldownSeconds,
        };

    private static GatewayConnectorUpsertRequest BuildActivatedImportRequest(
        GatewayConnectorUpsertRequest connector,
        McpConnectorTestResult validation)
        => new()
        {
            Scope = connector.Scope,
            Name = connector.Name,
            Description = connector.Description,
            IconUrl = connector.IconUrl,
            PresetId = connector.PresetId,
            Transport = connector.Transport,
            HttpUrl = connector.HttpUrl,
            LocalCommand = connector.LocalCommand,
            LocalArgs = connector.LocalArgs,
            ContainerImage = connector.ContainerImage,
            ContainerCommand = connector.ContainerCommand,
            ContainerArgs = connector.ContainerArgs,
            ContainerSidecarName = connector.ContainerSidecarName,
            BundlePath = connector.BundlePath,
            ServerType = connector.ServerType,
            RuntimeArtifactType = connector.RuntimeArtifactType,
            RuntimeArtifactSourceDigest = connector.RuntimeArtifactSourceDigest,
            RuntimeArtifactBuildStatus = "ready",
            ReadinessTimeoutSeconds = connector.ReadinessTimeoutSeconds,
            JitCooldownSeconds = connector.JitCooldownSeconds,
            HealthcheckUrl = connector.HealthcheckUrl,
            HealthcheckIntervalMilliseconds = connector.HealthcheckIntervalMilliseconds,
            EnvKeys = connector.EnvKeys,
            Enabled = connector.Enabled,
            ToolCount = validation.ToolCount,
            ToolNames = validation.ToolNames?.ToArray(),
        };

    private static void EnsureCanImportConnectors(GatewayUser user)
    {
        if (!string.Equals(user.Role, "admin", StringComparison.OrdinalIgnoreCase)
            && !user.CanImportConnectors)
        {
            throw new UnauthorizedAccessException("Forbidden.");
        }
    }

    private static void EnsureCanImportSkills(GatewayUser user)
    {
        if (!string.Equals(user.Role, "admin", StringComparison.OrdinalIgnoreCase)
            && !user.CanImportSkills)
        {
            throw new UnauthorizedAccessException("Forbidden.");
        }
    }

    private sealed record DownloadedCatalogArtifact(byte[] Content, string FileName);
}
