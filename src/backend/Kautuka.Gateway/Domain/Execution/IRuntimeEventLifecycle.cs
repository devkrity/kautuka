namespace Kautuka.Gateway.Domain.Execution;

internal interface IRuntimeEventLifecycle
{
    ValueTask QuiesceAgentAsync(string agentId, CancellationToken cancellationToken = default);

    void ResumeAgent(string agentId);

    void ForgetAgent(string agentId);
}
