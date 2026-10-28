namespace Kautuka.Manager.RuntimeContainer.Configuration;

public sealed class WorkspaceDefinition
{
    public string? BaseRoot { get; init; }
}

public sealed class SandboxRuntimeDefinition
{
    public string? Image { get; init; }

    public int DefaultCpus { get; init; }

    public string? DefaultMemory { get; init; }

    public int DefaultTimeout { get; init; }

    public IReadOnlyDictionary<string, string> ContainerEnvironment { get; init; } = new Dictionary<string, string>();
}

public sealed class WorkspaceTelemetryDefinition
{
    public string? TracesEndpoint { get; init; }

    public string? LogsEndpoint { get; init; }

    public string? MetricsEndpoint { get; init; }

    public string? OtlpProtocol { get; init; }

    public string? OtlpHeaders { get; init; }
}
