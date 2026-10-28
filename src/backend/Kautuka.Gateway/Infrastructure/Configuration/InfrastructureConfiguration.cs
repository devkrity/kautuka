namespace Kautuka.Gateway.Infrastructure.Configuration;

internal sealed class InfrastructureDefinition
{
    public Uri? BaseUrl { get; init; }

    public WorkspaceDefinition Workspace { get; init; } = new();

    public SandboxRuntimeDefinition Sandbox { get; init; } = new();

    public StorageDefinition Storage { get; init; } = new();
}

internal sealed class WorkspaceDefinition
{
    public string? BaseRoot { get; init; }
}

internal sealed class SandboxRuntimeDefinition
{
    public string? Image { get; init; }

    public int DefaultCpus { get; init; }

    public string? DefaultMemory { get; init; }

    public int DefaultTimeout { get; init; }

    /// <summary>
    /// Environment variables injected into every sandbox container at creation time.
    /// Stored as a JSON object in <c>infra.sandbox.container_env_json</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string> ContainerEnvironment { get; init; } = new Dictionary<string, string>();
}

internal sealed class StorageDefinition
{
    public SqliteStorageDefinition Sqlite { get; init; } = new();

    public PostgresStorageDefinition Postgres { get; init; } = new();
}

internal sealed class SqliteStorageDefinition
{
    public string? Path { get; init; }
}

internal sealed class PostgresStorageDefinition
{
    public Uri? Url { get; init; }
}
