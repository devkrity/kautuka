using System.Reflection;

using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Sandbox;

using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class V7_41_ConnectorDiagnosticsTests
{
    [Fact]
    public async Task V7_41_TransportClosedFailureIncludesContainerLogs_WithNegativeControl()
    {
        const string diagnostic = "synthetic connector startup diagnostic from Podman";
        var commands = new List<string[]>();
        var runner = new PodmanCommandRunner((args, _) =>
        {
            commands.Add([.. args]);
            return Task.FromResult(new PodmanCommandResult(
                0,
                args[0] == "logs" ? diagnostic : string.Empty,
                string.Empty,
                ProcessTerminationReason.Completed));
        });
        await using var service = new WorkspaceContainerConnectorExecutionService(
            new WorkspaceDesiredStateStore(),
            runner,
            new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance),
            new PodmanContainerTool(runner),
            NullLogger<WorkspaceContainerConnectorExecutionService>.Instance);

        MethodInfo describeFailure = typeof(WorkspaceContainerConnectorExecutionService)
            .GetMethod("DescribeFailureAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Task<string> descriptionTask = Assert.IsType<Task<string>>(describeFailure.Invoke(
            service,
            [new InvalidOperationException("The transport was closed."), "container-123", CancellationToken.None]));

        string description = await descriptionTask;

        Assert.Contains(diagnostic, description, StringComparison.Ordinal);
        Assert.Contains(commands, static args => args.SequenceEqual(["logs", "--tail", "100", "container-123"], StringComparer.Ordinal));
    }
}
