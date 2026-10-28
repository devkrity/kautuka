using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.Gateway.Domain.Skills;

internal sealed partial class BuiltInSkillPackageBootstrapService(
    IWebHostEnvironment environment,
    IGatewayCatalogStore catalog,
    ISkillPackageService skillPackageService,
    ILogger<BuiltInSkillPackageBootstrapService> logger) : IHostedService
{
    private const string skillsDirectoryName = "skills";

    [LoggerMessage(EventId = 7100, Level = LogLevel.Warning,
        Message = "Built-in skills directory was not found at {SkillsRoot}")]
    private static partial void LogSkillsDirectoryMissing(ILogger logger, string skillsRoot);

    [LoggerMessage(EventId = 7101, Level = LogLevel.Error,
        Message = "Failed to import built-in skill package from {SkillDirectory}")]
    private static partial void LogBuiltInImportFailed(ILogger logger, Exception exception, string skillDirectory);

    [LoggerMessage(EventId = 7102, Level = LogLevel.Information,
        Message = "Built-in skill bootstrap start. skillsRoot={SkillsRoot}")]
    private static partial void LogBootstrapStart(ILogger logger, string skillsRoot);

    [LoggerMessage(EventId = 7103, Level = LogLevel.Debug,
        Message = "Built-in skill import begin. skillDirectory={SkillDirectory}")]
    private static partial void LogBuiltInImportBegin(ILogger logger, string skillDirectory);

    [LoggerMessage(EventId = 7104, Level = LogLevel.Debug,
        Message = "Built-in skill import completed. skillDirectory={SkillDirectory}")]
    private static partial void LogBuiltInImportCompleted(ILogger logger, string skillDirectory);

    [LoggerMessage(EventId = 7105, Level = LogLevel.Information,
        Message = "Built-in skill bootstrap completed")]
    private static partial void LogBootstrapCompleted(ILogger logger);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        string skillsRoot = ResolveSkillsRoot();
        LogBootstrapStart(logger, skillsRoot);
        if (!Directory.Exists(skillsRoot))
        {
            LogSkillsDirectoryMissing(logger, skillsRoot);
            return;
        }

        foreach (string skillDirectory in Directory.EnumerateDirectories(skillsRoot).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(Path.Combine(skillDirectory, "SKILL.md")))
            {
                continue;
            }

            try
            {
                LogBuiltInImportBegin(logger, skillDirectory);
                await ImportBuiltInSkillAsync(skillDirectory, cancellationToken).ConfigureAwait(false);
                LogBuiltInImportCompleted(logger, skillDirectory);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                LogBuiltInImportFailed(logger, ex, skillDirectory);
                if (ex is UnauthorizedAccessException)
                {
                    continue;
                }

                throw;
            }
        }

        LogBootstrapCompleted(logger);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private string ResolveSkillsRoot()
    {
        string outputRoot = Path.Combine(AppContext.BaseDirectory, skillsDirectoryName);
        if (Directory.Exists(outputRoot))
        {
            return outputRoot;
        }

        return Path.Combine(environment.ContentRootPath, skillsDirectoryName);
    }

    private async Task ImportBuiltInSkillAsync(string skillDirectory, CancellationToken cancellationToken)
    {
        string packageName = Path.GetFileName(skillDirectory);
        if (string.IsNullOrWhiteSpace(packageName))
        {
            return;
        }

        using var archiveStream = CreateArchiveStream(skillDirectory);
        SkillPackageImportRequest request = new(
            $"{packageName}.zip",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["publisher"] = "platform",
                ["origin"] = "built-in",
                ["presetId"] = packageName,
            });
        SkillPackageImportOutcome validated = await skillPackageService
            .ValidateImportOutcomeAsync(archiveStream, request, cancellationToken)
            .ConfigureAwait(false);
        SkillPackageInspection validatedInspection = validated.Skill ?? validated.Skills[0];
        if (catalog.TryGetSkill(validatedInspection.Definition.SkillId, out GatewaySkill? existing)
            && existing is not null
            && string.Equals(existing.PackageHash, validatedInspection.Definition.PackageHash, StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(existing.PackagePath, "SKILL.md")))
        {
            return;
        }

        archiveStream.Position = 0;
        SkillPackageImportOutcome outcome = await skillPackageService.ImportOutcomeAsync(
            archiveStream,
            request,
            cancellationToken).ConfigureAwait(false);

        SkillPackageInspection inspection = outcome.Skill ?? outcome.Skills[0];
        bool keepEnabled = catalog.TryGetSkill(inspection.Definition.SkillId, out GatewaySkill? current) && current is not null
            ? current.Enabled
            : true;

        JsonObject payload = new()
        {
            ["name"] = inspection.Definition.Name,
            ["description"] = inspection.Definition.Description,
            ["packageHash"] = inspection.Definition.PackageHash,
            ["status"] = inspection.Definition.Status,
            ["packagePath"] = inspection.Definition.PackagePath,
            ["hasResources"] = inspection.Definition.HasResources,
            ["hasScripts"] = inspection.Definition.HasScripts,
            ["enabled"] = keepEnabled,
            ["importTimestamp"] = inspection.Definition.ImportTimestamp.ToString("O"),
            ["sourceMetadata"] = JsonObject.Create(JsonSerializer.SerializeToElement(
                inspection.Definition.SourceMetadata,
                KautukaJsonContext.Default.DictionaryStringString)),
        };

        catalog.SaveSkill(
            inspection.Definition.SkillId,
            JsonSerializer.SerializeToElement(payload, KautukaJsonContext.Default.JsonObject),
            Kautuka.Gateway.Domain.Models.GatewayOwnershipScopes.System,
            ownerUserId: null);
    }

    private static MemoryStream CreateArchiveStream(string skillDirectory)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (string filePath in Directory.EnumerateFiles(skillDirectory, "*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(skillDirectory, filePath).Replace(Path.DirectorySeparatorChar, '/');
                ZipArchiveEntry entry = archive.CreateEntry(relativePath, CompressionLevel.SmallestSize);
                using Stream entryStream = entry.Open();
                using FileStream source = File.OpenRead(filePath);
                source.CopyTo(entryStream);
            }
        }

        stream.Position = 0;
        return stream;
    }
}
