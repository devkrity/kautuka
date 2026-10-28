using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

namespace Kautuka.Manager.RuntimeContainer.Configuration;

public sealed record WorkspaceSidecarCatalog(
    IReadOnlyList<WorkspaceSidecarDefinition> Sidecars)
{
    public static WorkspaceSidecarCatalog Empty { get; } = new([]);
}

public sealed record WorkspaceSidecarDefinition
{
    [JsonConstructor]
    public WorkspaceSidecarDefinition(
        string Name,
        string Image,
        IReadOnlyList<WorkspaceSidecarPortBinding> Ports,
        IReadOnlyList<WorkspaceSidecarVolumeBinding>? Volumes,
        IReadOnlyDictionary<string, string> Environment,
        IReadOnlyList<string> Command,
        string? BuildContext,
        string? Dockerfile,
        WorkspaceSidecarHealthCheck? HealthCheck,
        bool RequiredForReady = true,
        bool ManagerInfrastructure = false,
        string? Policy = null)
    {
        this.Name = Name;
        this.Image = Image;
        this.Ports = Ports;
        this.Volumes = Volumes;
        this.Environment = Environment;
        this.Command = Command;
        this.BuildContext = BuildContext;
        this.Dockerfile = Dockerfile;
        this.HealthCheck = HealthCheck;
        this.RequiredForReady = RequiredForReady;
        this.ManagerInfrastructure = ManagerInfrastructure;
        this.Policy = Policy;
    }

    public WorkspaceSidecarDefinition(
        string Name,
        string Image,
        IReadOnlyList<WorkspaceSidecarPortBinding> Ports,
        IReadOnlyDictionary<string, string> Environment,
        IReadOnlyList<string> Command,
        string? BuildContext,
        string? Dockerfile,
        WorkspaceSidecarHealthCheck? HealthCheck,
        bool RequiredForReady = true,
        bool ManagerInfrastructure = false,
        string? Policy = null)
    {
        this.Name = Name;
        this.Image = Image;
        this.Ports = Ports;
        this.Volumes = [];
        this.Environment = Environment;
        this.Command = Command;
        this.BuildContext = BuildContext;
        this.Dockerfile = Dockerfile;
        this.HealthCheck = HealthCheck;
        this.RequiredForReady = RequiredForReady;
        this.ManagerInfrastructure = ManagerInfrastructure;
        this.Policy = Policy;
    }

    public string Name { get; init; }

    public string Image { get; init; }

    public IReadOnlyList<WorkspaceSidecarPortBinding> Ports { get; init; }

    public IReadOnlyList<WorkspaceSidecarVolumeBinding>? Volumes { get; init; }

    public IReadOnlyDictionary<string, string> Environment { get; init; }

    public IReadOnlyList<string> Command { get; init; }

    public string? BuildContext { get; init; }

    public string? Dockerfile { get; init; }

    public WorkspaceSidecarHealthCheck? HealthCheck { get; init; }

    public bool RequiredForReady { get; init; }

    public bool ManagerInfrastructure { get; init; }

    public string? Policy { get; init; }
}

public sealed record WorkspaceSidecarPortBinding(
    int HostPort,
    int ContainerPort,
    string? HostBindAddress = null);

public sealed record WorkspaceSidecarVolumeBinding(
    string HostPath,
    string ContainerPath,
    bool ReadOnly = false);

public sealed record WorkspaceSidecarHealthCheck(
    Uri Url,
    int TimeoutSeconds,
    int IntervalMilliseconds);

public static class WorkspaceSidecarCatalogLoader
{
    private const string prodSharedStateRoot = "/var/lib/kautuka";

    public static WorkspaceSidecarCatalog Load(string path, WorkspaceRuntimePaths? runtimePaths = null, ILogger? logger = null)
    {
        if (!File.Exists(path))
        {
            return WorkspaceSidecarCatalog.Empty;
        }

        WorkspaceSidecarCatalog catalog;
        try
        {
            using FileStream stream = File.OpenRead(path);
            catalog = JsonSerializer.Deserialize(stream, WorkspaceSidecarJsonContext.Default.WorkspaceSidecarCatalog)
                ?? WorkspaceSidecarCatalog.Empty;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            logger?.LogError(ex, "Failed to load sidecar catalog from {SidecarCatalogPath}. Starting with an empty sidecar catalog.", path);
            return WorkspaceSidecarCatalog.Empty;
        }

        string baseDirectory = Path.GetDirectoryName(path) ?? AppContext.BaseDirectory;
        return new WorkspaceSidecarCatalog(
            [.. catalog.Sidecars.Select(sidecar => sidecar with
            {
                Volumes = [.. (sidecar.Volumes ?? []).Select(volume => volume with
                {
                    HostPath = ResolveVolumeHostPath(baseDirectory, runtimePaths, volume.HostPath)
                })],
                BuildContext = ResolveRelativePath(baseDirectory, sidecar.BuildContext),
                Dockerfile = ResolveRelativePath(baseDirectory, sidecar.Dockerfile)
            })]);
    }

    private static string ResolveVolumeHostPath(string baseDirectory, WorkspaceRuntimePaths? runtimePaths, string hostPath)
    {
        string resolved = ResolveRelativePath(baseDirectory, hostPath) ?? hostPath;
        if (runtimePaths is null || string.IsNullOrWhiteSpace(resolved) || !Path.IsPathRooted(resolved))
        {
            return resolved;
        }

        if (!OperatingSystem.IsWindows())
        {
            return Path.GetFullPath(resolved);
        }

        string normalizedResolved = Path.GetFullPath(resolved);
        string normalizedProdRoot = Path.GetFullPath(prodSharedStateRoot);
        string normalizedSharedRoot = Path.GetFullPath(runtimePaths.SharedDataRoot);
        if (string.Equals(normalizedSharedRoot, normalizedProdRoot, StringComparison.OrdinalIgnoreCase))
        {
            return normalizedResolved;
        }

        string prodPrefix = normalizedProdRoot + Path.DirectorySeparatorChar;
        if (normalizedResolved.StartsWith(prodPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string relativeSuffix = normalizedResolved[prodPrefix.Length..];
            string mappedPath = Path.GetFullPath(Path.Combine(normalizedSharedRoot, relativeSuffix));
            Directory.CreateDirectory(mappedPath);
            return mappedPath;
        }

        if (string.Equals(normalizedResolved, normalizedProdRoot, StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(normalizedSharedRoot);
            return normalizedSharedRoot;
        }

        return normalizedResolved;
    }

    private static string? ResolveRelativePath(string baseDirectory, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        return Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(path, baseDirectory);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WorkspaceSidecarCatalog))]
internal sealed partial class WorkspaceSidecarJsonContext : JsonSerializerContext;
