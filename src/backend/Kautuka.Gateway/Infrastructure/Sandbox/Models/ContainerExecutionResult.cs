namespace Kautuka.Gateway.Infrastructure.Sandbox.Models;

internal sealed record ContainerExecutionResult(
    string Status,
    int ExitCode,
    string StdOut,
    string StdErr,
    string? ContainerName = null);
