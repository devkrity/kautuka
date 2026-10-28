using System.Text.Json;
using System.Text.Json.Serialization;

using Kautuka.Manager.RuntimeContainer.Configuration;

namespace Kautuka.Manager.Hosting;

internal sealed class WorkspaceSidecarAssetStagingService(
    WorkspaceRuntimePaths runtimePaths,
    ILogger<WorkspaceSidecarAssetStagingService> logger) : IHostedService
{
    private static readonly EnumerationOptions enumerationOptions = new()
    {
        IgnoreInaccessible = false,
        MatchCasing = MatchCasing.CaseInsensitive,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    public Task StartAsync(CancellationToken cancellationToken)
    {
        string sourceRoot = Path.Combine(AppContext.BaseDirectory, "sidecar-assets");
        if (!Directory.Exists(sourceRoot))
        {
            logger.LogWarning("Sidecar asset source directory '{SourceRoot}' was not found. Skipping config staging.", sourceRoot);
            return Task.CompletedTask;
        }

        string destinationRoot = Path.Combine(runtimePaths.SharedDataRoot, "sidecars");
        Directory.CreateDirectory(destinationRoot);

        foreach (string sourceDirectory in Directory.EnumerateDirectories(sourceRoot, "*", enumerationOptions))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string sidecarName = Path.GetFileName(sourceDirectory);
            string destinationDirectory = Path.Combine(destinationRoot, sidecarName, "config");

            CopyDirectory(sourceDirectory, destinationDirectory);
            Directory.CreateDirectory(Path.Combine(destinationRoot, sidecarName, "data"));
        }

        logger.LogInformation("Staged sidecar assets from {SourceRoot} into {DestinationRoot}.", sourceRoot, destinationRoot);

        int sidecarCount = Directory.GetDirectories(destinationRoot, "*", SearchOption.TopDirectoryOnly).Length;
        string stampPath = Path.Combine(destinationRoot, "last-staged.json");
        WriteStagingStamp(stampPath, sidecarCount);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    private static void WriteStagingStamp(string path, int sidecarCount)
    {
        var stamp = new SidecarStagingStamp(DateTimeOffset.UtcNow, sidecarCount);
        string json = JsonSerializer.Serialize(stamp, SidecarStagingStampJsonContext.Default.SidecarStagingStamp);
        string tempPath = path + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (string directory in Directory.EnumerateDirectories(sourceDirectory, "*", enumerationOptions))
        {
            string nestedDestination = Path.Combine(destinationDirectory, Path.GetFileName(directory));
            CopyDirectory(directory, nestedDestination);
        }

        foreach (string file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            string destinationFile = Path.Combine(destinationDirectory, Path.GetFileName(file));
            File.Copy(file, destinationFile, overwrite: true);
        }
    }
}

internal sealed record SidecarStagingStamp(DateTimeOffset At, int SidecarCount);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SidecarStagingStamp))]
internal sealed partial class SidecarStagingStampJsonContext : JsonSerializerContext
{
}
