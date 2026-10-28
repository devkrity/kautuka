using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Domain.ModelCapability;

internal interface IModelCapabilityProber
{
    /// <summary>
    /// Runs bounded probes against the raw (undecorated) chat client to determine
    /// streaming, function calling, reasoning, and structured output capabilities.
    /// Times out after a short deadline — unknown capabilities stay null.
    /// </summary>
    ValueTask<ProbedCapabilities> ProbeAsync(
        IChatClient rawClient,
        CancellationToken ct = default);
}
