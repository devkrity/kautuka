using System.Reflection;

using Kautuka.Manager.Protos;
using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Sandbox;
using Kautuka.Manager.RuntimeContainer.Sandbox.Models;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kautuka.IntegrationTests;

public sealed class PodmanReconciliationFailureTests
{
    [Theory]
    [InlineData("sandbox", "timeout")]
    [InlineData("sandbox", "malformed")]
    [InlineData("sandbox", "incomplete")]
    [InlineData("sandbox", "unavailable")]
    [InlineData("connector", "timeout")]
    [InlineData("connector", "malformed")]
    [InlineData("connector", "incomplete")]
    [InlineData("connector", "unavailable")]
    [InlineData("sidecar", "timeout")]
    [InlineData("sidecar", "malformed")]
    [InlineData("sidecar", "incomplete")]
    [InlineData("sidecar", "unavailable")]
    public async Task UnavailableInspectionNeverMutatesContainers(string target, string failure)
    {
        var commands = new List<string[]>();
        var runner = new PodmanCommandRunner((args, _) =>
        {
            commands.Add([.. args]);
            return Task.FromResult(args[0] == "inspect" ? Failure(failure) : Error());
        });

        await ExerciseAsync(target, runner);

        Assert.NotEmpty(commands);
        Assert.DoesNotContain(commands, static args => args[0] is "rm" or "run" or "start" or "stop");
        if (failure == "timeout")
        {
            Assert.Single(commands);
        }
    }

    [Theory]
    [InlineData("sandbox")]
    [InlineData("connector")]
    [InlineData("sidecar")]
    public async Task FailedRemovalNeverCreatesReplacement(string target)
    {
        var commands = new List<string[]>();
        var runner = new PodmanCommandRunner((args, _) =>
        {
            commands.Add([.. args]);
            return Task.FromResult(args[0] switch
            {
                "inspect" => Success(stoppedInspection),
                "image" => Success("sha256:current"),
                "rm" => Error(),
                _ => throw new InvalidOperationException("Unexpected command: " + string.Join(' ', args)),
            });
        });

        await ExerciseAsync(target, runner);

        Assert.Contains(commands, static args => args[0] == "rm");
        Assert.DoesNotContain(commands, static args => args[0] is "run" or "start");
    }

    [Fact]
    public async Task InspectorDistinguishesConfirmedAbsenceFromUnavailableObservationAndRecovers()
    {
        bool absent = false;
        bool recovered = false;
        var runner = new PodmanCommandRunner((args, _) => Task.FromResult(
            args[0] == "inspect"
                ? recovered ? Success(stoppedInspection) : Error()
                : absent ? new PodmanCommandResult(1, "", "", ProcessTerminationReason.NonZeroExit) : Error()));

        await Assert.ThrowsAsync<ContainerInspectionUnavailableException>(() =>
            PodmanContainerInspector.ReadAsync(runner, "test", TestContext.Current.CancellationToken));
        absent = true;
        Assert.Null(await PodmanContainerInspector.ReadAsync(runner, "test", TestContext.Current.CancellationToken));
        recovered = true;
        Assert.Equal(stoppedInspection, await PodmanContainerInspector.ReadAsync(runner, "test", TestContext.Current.CancellationToken));
    }

    private static async Task ExerciseAsync(string target, PodmanCommandRunner runner)
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-reconcile-test-" + Guid.NewGuid().ToString("N"));
        var context = new ContainerContext("agent", "test", "test-user", root, "/workspace", "localhost/test:local", 1, "512m", 30, [], new Dictionary<string, string>());
        try
        {
            if (target == "sandbox")
            {
                var tool = new PodmanContainerTool(runner);
                await Assert.ThrowsAsync<ContainerInspectionUnavailableException>(() => tool.SpawnSandboxAsync(context, TestContext.Current.CancellationToken));
            }
            else if (target == "connector")
            {
                await using var service = new WorkspaceContainerConnectorExecutionService(
                    new WorkspaceDesiredStateStore(), runner,
                    new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance),
                    new PodmanContainerTool(runner), NullLogger<WorkspaceContainerConnectorExecutionService>.Instance);
                var connector = new WorkspaceMcpContainerConnectorDefinition("test", "localhost/test:local", "node", [], true, false, null, null, [], 30, 600);
                MethodInfo method = typeof(WorkspaceContainerConnectorExecutionService).GetMethod("EnsureConnectorContainerRunningAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                await Assert.ThrowsAsync<ContainerInspectionUnavailableException>(() => (Task)method.Invoke(service,
                    [connector, "test", "test-container", WorkspaceMcpExecutionContext.Isolated, null, TestContext.Current.CancellationToken])!);
            }
            else
            {
                using ServiceProvider provider = new ServiceCollection().AddHttpClient().BuildServiceProvider();
                var supervisor = new WorkspaceSidecarSupervisor(WorkspaceSidecarCatalog.Empty,
                    provider.GetRequiredService<IHttpClientFactory>(), runner, Options.Create(new ManagerContainerStartupOptions()),
                    NullLogger<WorkspaceSidecarSupervisor>.Instance);
                var sidecar = new WorkspaceSidecarDefinition("test", "localhost/test:local", [], new Dictionary<string, string>(), [], null, null, null);
                MethodInfo method = typeof(WorkspaceSidecarSupervisor).GetMethod("EnsureSidecarAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                Assert.False(await (Task<bool>)method.Invoke(supervisor, [sidecar, TestContext.Current.CancellationToken])!);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static PodmanCommandResult Failure(string failure) => failure switch
    {
        "timeout" => new(124, "", "", ProcessTerminationReason.TimedOut),
        "malformed" => Success("not-json"),
        "incomplete" => Success("[{\"State\":{\"Status\":\"running\"}}]"),
        _ => Error(),
    };

    private static PodmanCommandResult Success(string output) => new(0, output, "", ProcessTerminationReason.Completed);
    private static PodmanCommandResult Error() => new(125, "", "synthetic backend failure", ProcessTerminationReason.NonZeroExit);
    private const string stoppedInspection = """
        [{"Image":"sha256:old","State":{"Status":"exited"},"HostConfig":{},
        "Config":{"Labels":{"kautuka.desired-version":"0","kautuka.spec-fingerprint":"old"}}}]
        """;
}
