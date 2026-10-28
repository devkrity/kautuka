using Grpc.Core;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Manager.Protos;

namespace Kautuka.Gateway.Infrastructure.Sandbox;

internal sealed class ManagerExecutionDependencyProbe(IManagerGrpcService manager) : IAgentExecutionDependencyProbe
{
    private const string dependencyName = "manager";
    private const string containerRuntimeDependencyName = "container_runtime";
    private const int maxAttempts = 2;
    // Manager's bounded Podman health check can consume a little over six seconds
    // when it confirms a timeout. Keep the transport deadline outside that boundary
    // so a healthy Manager is never mislabeled because both timers expire together.
    private static readonly TimeSpan probeTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan retryDelay = TimeSpan.FromMilliseconds(100);

    public string Dependency => dependencyName;

    public async Task<AgentExecutionDependencyStatus> CheckAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(probeTimeout);

            try
            {
                ManagerStatusResponse status = await manager.GetHostStatusAsync(
                    new WorkspacePingRequest { Message = "agent-execution-readiness" },
                    timeout.Token).ConfigureAwait(false);

                if (string.Equals(status.LastFailureClass, "runtime_unavailable", StringComparison.Ordinal))
                {
                    return new AgentExecutionDependencyStatus(
                        containerRuntimeDependencyName,
                        Ready: false,
                        Code: "CONTAINER_RUNTIME_UNAVAILABLE",
                        Message: "Agent execution is unavailable because Podman is not ready. Install rootless Podman, then restart Kautuka.",
                        Detail: status.LastFailureClass);
                }

                if (status.CoreReady)
                {
                    return new AgentExecutionDependencyStatus(
                        dependencyName,
                        Ready: true,
                        Code: "READY",
                        Message: "Manager and container runtime are ready.");
                }

                return new AgentExecutionDependencyStatus(
                    dependencyName,
                    Ready: false,
                    Code: "MANAGER_NOT_READY",
                    Message: "Agent execution is unavailable because Manager is not ready. Check that the Manager process is running, then retry.",
                    Detail: string.IsNullOrWhiteSpace(status.LastFailureClass) ? status.Lifecycle : status.LastFailureClass);
            }
            catch (RpcException exception) when (
                !cancellationToken.IsCancellationRequested
                && exception.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded or StatusCode.Cancelled
                && attempt < maxAttempts)
            {
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException) when (attempt < maxAttempts)
            {
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested && attempt < maxAttempts)
            {
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (RpcException exception) when (
                !cancellationToken.IsCancellationRequested
                && exception.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded or StatusCode.Cancelled)
            {
                return ManagerUnavailable();
            }
            catch (InvalidOperationException)
            {
                return ManagerUnavailable();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return ManagerUnavailable();
            }
        }

        return ManagerUnavailable();
    }

    private static AgentExecutionDependencyStatus ManagerUnavailable()
        => new(
            dependencyName,
            Ready: false,
            Code: "MANAGER_UNAVAILABLE",
            Message: "Agent execution is unavailable because Manager cannot be reached. Check that the Manager process is running, then retry.");
}
