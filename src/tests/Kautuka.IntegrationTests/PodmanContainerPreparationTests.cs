using System.Text.Json.Nodes;

using Kautuka.Manager.Protos;
using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class PodmanContainerPreparationTests
{
    private static readonly string id = new('a', 64);
    private const string network = "kautuka-owner-agent";
    private const string sandbox = "/run/user/60123/netns/netns-1234-abcd";
    private static readonly ResolvedRuntimeImage helper = new("localhost/kautuka-route:fixture", "linux/amd64", "sha256:" + new string('f', 64));

    [Fact]
    public async Task PreparationUsesNativeAssignedIdentityWithoutStartingApplication()
    {
        var commands = new List<string[]>();
        var preparation = new PodmanContainerPreparation(Runner(commands));
        PreparedPodmanContainer prepared = await preparation.PrepareAsync(
            ["create", "--pull=never", "fixture"], network, "10.247.91.2", helper, CancellationToken.None);

        Assert.Equal(id, prepared.Id);
        Assert.Equal("10.247.91.1", prepared.Address); // Native IPAM can legitimately assign .1.
        Assert.Equal(network, prepared.NetworkName);
        Assert.Equal(sandbox, prepared.SandboxKey);
        Assert.DoesNotContain(commands, command => command[0] == "start");
        Assert.All(commands.Where(command => command[0] == "inspect" || command[0] == "init"), command => Assert.Equal(id, command[1]));
        string[] route = Assert.Single(commands, command => command.Contains("--cap-add=NET_ADMIN", StringComparer.Ordinal));
        Assert.Contains("ns:" + sandbox, route);
        Assert.DoesNotContain(route, argument => argument.StartsWith("container:", StringComparison.Ordinal));
        Assert.Contains("--pull=never", route);
        Assert.Contains(helper.LocalImageId, route);
        Assert.DoesNotContain(helper.SourceReference, route);
        Assert.Equal(new[] { "start", id }, prepared.StartArguments(attach: false));
        Assert.Equal(new[] { "start", "--attach", "--interactive", id }, prepared.StartArguments(attach: true));
    }

    [Theory]
    [InlineData("running")]
    [InlineData("extra-network")]
    [InlineData("wrong-id")]
    [InlineData("wrong-owner")]
    [InlineData("host-namespace")]
    [InlineData("ipv6")]
    public async Task UnpreparedOrUnexpectedIdentityIsRemovedByImmutableId(string fault)
    {
        var commands = new List<string[]>();
        JsonObject inspect = Inspection();
        switch (fault)
        {
            case "running": inspect["State"]!["Running"] = true; break;
            case "extra-network": inspect["NetworkSettings"]!["Networks"]!["outside"] = new JsonObject(); break;
            case "wrong-id": inspect["Id"] = new string('b', 64); break;
            case "wrong-owner": inspect["Config"]!["Labels"]!["kautuka.managed"] = "other"; break;
            case "host-namespace": inspect["NetworkSettings"]!["SandboxKey"] = "/proc/1/ns/net"; break;
            case "ipv6": inspect["NetworkSettings"]!["Networks"]![network]!["IPAddress"] = "::1"; break;
        }

        var preparation = new PodmanContainerPreparation(Runner(commands, inspect));
        await Assert.ThrowsAsync<InvalidOperationException>(() => preparation.PrepareAsync(
            ["create", "--pull=never", "fixture"], network, "10.247.91.2", helper, CancellationToken.None));
        Assert.Equal(new[] { "rm", "--force", id }, Assert.Single(commands, command => command[0] == "rm"));
        Assert.DoesNotContain(commands, command => command[0] == "run" || command[0] == "start");
    }

    [Theory]
    [InlineData("changed-address")]
    [InlineData("changed-namespace")]
    [InlineData("wrong-route")]
    [InlineData("duplicate-default")]
    public async Task RouteOrIdentityChangePreventsHandoffAndRetiresAllocation(string fault)
    {
        var commands = new List<string[]>();
        int inspections = 0;
        var runner = new PodmanCommandRunner((arguments, _) =>
        {
            commands.Add([.. arguments]);
            JsonObject inspect = Inspection();
            if (arguments[0] == "inspect" && ++inspections == 2)
            {
                if (fault == "changed-address") inspect["NetworkSettings"]!["Networks"]![network]!["IPAddress"] = "10.247.91.3";
                if (fault == "changed-namespace") inspect["NetworkSettings"]!["SandboxKey"] = sandbox + "-new";
            }

            string routes = fault == "wrong-route" ? "[{\"dst\":\"default\",\"gateway\":\"10.247.91.254\"}]"
                : fault == "duplicate-default" ? "[{\"dst\":\"default\",\"gateway\":\"10.247.91.2\"},{\"dst\":\"default\",\"gateway\":\"10.247.91.254\"}]" : DefaultRoute;
            return Task.FromResult(Result(arguments, commands, inspect, routes));
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => new PodmanContainerPreparation(runner).PrepareAsync(
            ["create", "--pull=never", "fixture"], network, "10.247.91.2", helper, CancellationToken.None));
        Assert.Equal(new[] { "rm", "--force", id }, Assert.Single(commands, command => command[0] == "rm"));
        Assert.DoesNotContain(commands, command => command[0] == "start");
    }

    [Fact]
    public async Task CancellationAfterCreateStillRetiresCreatedIdWithIndependentCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var commands = new List<string[]>();
        var runner = new PodmanCommandRunner(async (arguments, token) =>
        {
            commands.Add([.. arguments]);
            if (arguments[0] == "create") await cancellation.CancelAsync();
            if (arguments[0] == "rm") Assert.False(token.IsCancellationRequested);
            return Result(arguments, commands, Inspection(), DefaultRoute);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PodmanContainerPreparation(runner).PrepareAsync(
            ["create", "--pull=never", "fixture"], network, "10.247.91.2", helper, cancellation.Token));
        Assert.Equal(5, commands.Count);
        Assert.Equal(new[] { "rm", "--force", id }, Assert.Single(commands, command => command[0] == "rm"));
    }

    [Fact]
    public async Task CleanupFailureIsReportedWithoutRemovingANameOrStarting()
    {
        var commands = new List<string[]>();
        var runner = new PodmanCommandRunner((arguments, _) =>
        {
            commands.Add([.. arguments]);
            if (arguments[0] == "container") return Task.FromResult(new PodmanCommandResult(124, "", "fixture timeout", ProcessTerminationReason.TimedOut));
            return Task.FromResult(arguments[0] is "init" or "rm"
                ? new PodmanCommandResult(1, "", "fixture failure") : Result(arguments, commands, Inspection(), DefaultRoute));
        });
        AggregateException error = await Assert.ThrowsAsync<AggregateException>(() => new PodmanContainerPreparation(runner).PrepareAsync(
            ["create", "--pull=never", "fixture"], network, "10.247.91.2", helper, CancellationToken.None));
        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Equal(new[] { "rm", "--force", id }, Assert.Single(commands, command => command[0] == "rm"));
    }

    [Fact]
    public void ConnectorCreatePreservesBoundariesAndCreationTimeStdin()
    {
        var connector = new WorkspaceMcpContainerConnectorDefinition(
            "owner::connector", "localhost/kautuka-connector:fixture", "", [], true, false, null, null, [], 30, 600)
        { ResolvedImage = new("localhost/kautuka-connector:fixture", "linux/amd64", "sha256:" + new string('a', 64)) };
        List<string> once = ConnectorContainerSpecBuilder.BuildCreate(connector, "fixture", WorkspaceMcpExecutionContext.Isolated,
            null, persistent: false, network, "10.247.91.2");
        List<string> persistent = ConnectorContainerSpecBuilder.BuildCreate(connector, "fixture", WorkspaceMcpExecutionContext.Isolated,
            null, persistent: true, network, "10.247.91.2");
        Assert.Equal("create", once[0]);
        Assert.Contains("--pull=never", once);
        Assert.Contains("--interactive", once);
        Assert.DoesNotContain("--rm", once); // Manager owns retirement after policy removal.
        Assert.DoesNotContain("-d", persistent);
        Assert.DoesNotContain("--interactive", persistent);
        Assert.Contains(network, once);
        Assert.DoesNotContain(ConnectorContainerSpecBuilder.SandboxNetworkName, once);
        Assert.Contains("10.247.91.2", once);
        Assert.Contains("net.ipv6.conf.all.disable_ipv6=1", once);
        Assert.Contains("ALL", once);
        Assert.Contains("--read-only", once);
        Assert.DoesNotContain("--volume", once);
    }

    [Theory]
    [InlineData("create", false)]
    [InlineData("create", true)]
    [InlineData("route", false)]
    [InlineData("route", true)]
    [InlineData("read-route", false)]
    [InlineData("read-route", true)]
    public async Task InterruptedCliRecoversOwnedObjectsAndRetiresHelperBeforeApplication(string stage, bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var commands = new List<string[]>();
        string helperId = new('b', 64);
        bool interrupted = false;
        var runner = new PodmanCommandRunner(async (arguments, token) =>
        {
            commands.Add([.. arguments]);
            bool fail = stage == "create" && arguments[0] == "create"
                || stage == "route" && arguments[0] == "run" && !arguments.Contains("-j")
                || stage == "read-route" && arguments[0] == "run" && arguments.Contains("-j");
            if (fail)
            {
                interrupted = true;
                if (cancel) await cancellation.CancelAsync();
                return new PodmanCommandResult(cancel ? 130 : 124, "", "interrupted",
                    cancel ? ProcessTerminationReason.CallerCancelled : ProcessTerminationReason.TimedOut);
            }

            if (interrupted) Assert.False(token.IsCancellationRequested);
            if (arguments[0] == "ps") return new PodmanCommandResult(0, id + (stage == "create" ? "" : "\n" + helperId), "");
            PodmanCommandResult result = Result(arguments, commands, Inspection(), DefaultRoute);
            if (arguments[0] == "inspect" && arguments[1] == helperId)
            {
                JsonNode inspect = JsonNode.Parse(result.StdOut)!;
                inspect[0]!["Id"] = helperId;
                inspect[0]!["Config"]!["Labels"]!["kautuka.preparation-helper"] = "true";
                return result with { StdOut = inspect.ToJsonString() };
            }

            return result;
        });

        Exception? error = await Record.ExceptionAsync(() => new PodmanContainerPreparation(runner).PrepareAsync(
            ["create", "--pull=never", "fixture"], network, "10.247.91.2", helper, cancellation.Token));
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.IsType<InvalidOperationException>(error);
        string[] retired = [.. commands.Where(command => command[0] == "rm").Select(command => command[2])];
        Assert.Equal(stage == "create" ? [id] : new[] { helperId, id }, retired);
        Assert.DoesNotContain(commands, command => command[0] == "start");
    }

    private const string DefaultRoute = "[{\"dst\":\"default\",\"gateway\":\"10.247.91.2\",\"dev\":\"eth0\"}]";

    [Theory]
    [InlineData("inspect")]
    [InlineData("rm")]
    public async Task AutoRemovedHelperRaceStillRetiresApplication(string disappearsBefore)
    {
        var commands = new List<string[]>();
        string helperId = new('b', 64);
        var runner = new PodmanCommandRunner((arguments, _) =>
        {
            commands.Add([.. arguments]);
            if (arguments[0] == "init") return Task.FromResult(new PodmanCommandResult(124, "", "timed out", ProcessTerminationReason.TimedOut));
            if (arguments[0] == "ps") return Task.FromResult(new PodmanCommandResult(0, helperId + "\n" + id, ""));
            if (arguments[0] == disappearsBefore && arguments[1 + (disappearsBefore == "rm" ? 1 : 0)] == helperId)
            {
                return Task.FromResult(new PodmanCommandResult(125, "", "no such container"));
            }

            PodmanCommandResult result = Result(arguments, commands, Inspection(), DefaultRoute);
            if (arguments[0] == "inspect" && arguments[1] == helperId)
            {
                JsonNode inspect = JsonNode.Parse(result.StdOut)!;
                inspect[0]!["Id"] = helperId;
                inspect[0]!["Config"]!["Labels"]!["kautuka.preparation-helper"] = "true";
                result = result with { StdOut = inspect.ToJsonString() };
            }

            return Task.FromResult(result);
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => new PodmanContainerPreparation(runner).PrepareAsync(
            ["create", "--pull=never", "fixture"], network, "10.247.91.2", helper, CancellationToken.None));
        Assert.Equal(new[] { "rm", "--force", id }, commands.Last(command => command[0] == "rm"));
    }

    private static PodmanCommandRunner Runner(List<string[]> commands, JsonObject? inspection = null)
        => new((arguments, _) =>
        {
            commands.Add([.. arguments]);
            return Task.FromResult(Result(arguments, commands, inspection ?? Inspection(), DefaultRoute));
        });

    private static PodmanCommandResult Result(IReadOnlyList<string> arguments, List<string[]> commands, JsonObject inspection, string routes)
    {
        if (arguments[0] == "container") return new(1, "", "", ProcessTerminationReason.NonZeroExit);
        if (arguments[0] == "inspect")
        {
            if (commands.Any(command => command[0] == "ps")) inspection = Inspection();
            string operation = commands.First(command => command[0] == "create")[2].Split('=')[1];
            inspection["Config"]!["Labels"]!["kautuka.preparation"] = operation;
        }

        return new(0, arguments[0] switch
        {
            "create" or "ps" => id,
            "inspect" => "[" + inspection.ToJsonString() + "]",
            "run" when arguments.Contains("-j") => routes,
            _ => "",
        }, "", ProcessTerminationReason.Completed);
    }

    private static JsonObject Inspection() => new()
    {
        ["Id"] = id,
        ["State"] = new JsonObject { ["Status"] = "initialized", ["Running"] = false },
        ["Config"] = new JsonObject { ["Labels"] = new JsonObject { ["kautuka.managed"] = "manager" } },
        ["NetworkSettings"] = new JsonObject
        {
            ["SandboxKey"] = sandbox,
            ["Networks"] = new JsonObject { [network] = new JsonObject { ["IPAddress"] = "10.247.91.1" } },
        },
    };
}
