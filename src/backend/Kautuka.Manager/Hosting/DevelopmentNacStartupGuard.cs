using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;

namespace Kautuka.Manager.Hosting;

/// <summary>
/// Keeps the development Manager from binding its readiness socket when NAC startup failed.
/// Production keeps serving fail-closed so administrators can inspect and recover the system.
/// </summary>
internal sealed class DevelopmentNacStartupGuard(
    IWorkloadStartupState workloadStartupState,
    IHostEnvironment environment,
    ILogger<DevelopmentNacStartupGuard> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (environment.IsDevelopment() && !workloadStartupState.IsStartupGateOpen)
        {
            const string message = "NAC startup gate is closed; Development Manager startup is aborted.";
            logger.LogCritical("{StartupFailure}", message);
            throw new InvalidOperationException(message);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
