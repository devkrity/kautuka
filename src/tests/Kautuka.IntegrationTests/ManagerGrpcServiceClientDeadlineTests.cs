namespace Kautuka.IntegrationTests;

public sealed class ManagerGrpcServiceClientDeadlineTests
{
    [Theory]
    [InlineData("", 30, 45)]
    [InlineData("/packages/connector.mcpb", 30, 210)]
    [InlineData("/packages/connector.mcpb", 10, 190)]
    public void ProbeDeadlineAllowsPackagePreparationAndPreservesCancellation(
        string bundlePath,
        int connectorTimeoutSeconds,
        int expectedDeadlineSeconds)
    {
        DateTime utcNow = new(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);
        using var cancellation = new CancellationTokenSource();
        var request = new Manager.Protos.WorkspaceMcpContainerProbeRequest
        {
            BundlePath = bundlePath,
            TimeoutSeconds = connectorTimeoutSeconds,
        };

        Grpc.Core.CallOptions options = Gateway.Infrastructure.Sandbox.ManagerGrpcServiceClient
            .BuildProbeCallOptions(request, utcNow, cancellation.Token);

        Assert.Equal(utcNow.AddSeconds(expectedDeadlineSeconds), options.Deadline);
        Assert.Equal(cancellation.Token, options.CancellationToken);
        Assert.Equal(connectorTimeoutSeconds, request.TimeoutSeconds);
    }

    [Fact]
    public void ConfiguredPreparationBudgetOnlyExtendsBundleProbes()
    {
        DateTime start = new(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);
        var request = new Manager.Protos.WorkspaceMcpContainerProbeRequest { TimeoutSeconds = 30 };

        Assert.Equal(start.AddSeconds(45), Gateway.Infrastructure.Sandbox.ManagerGrpcServiceClient
            .BuildProbeCallOptions(request, start, CancellationToken.None, bundlePreparationSeconds: 240).Deadline);
        request.BundlePath = "/packages/connector.mcpb";
        Assert.Equal(start.AddSeconds(270), Gateway.Infrastructure.Sandbox.ManagerGrpcServiceClient
            .BuildProbeCallOptions(request, start, CancellationToken.None, bundlePreparationSeconds: 240).Deadline);
    }

    [Fact]
    public void ConfiguredPreparationBudgetExtendsOnlyToolCallsInsideTheAgentSandbox()
    {
        DateTime start = new(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);
        using var cancellation = new CancellationTokenSource();
        var request = new Manager.Protos.WorkspaceMcpContainerCallRequest { TimeoutSeconds = 60 };
        Assert.Equal(start.AddSeconds(75), Gateway.Infrastructure.Sandbox.ManagerGrpcServiceClient
            .BuildToolCallOptions(request, start, cancellation.Token, bundlePreparationSeconds: 240).Deadline);

        request.ExecutionContext = Manager.Protos.WorkspaceMcpExecutionContext.AgentSandbox;
        Grpc.Core.CallOptions options = Gateway.Infrastructure.Sandbox.ManagerGrpcServiceClient
            .BuildToolCallOptions(request, start, cancellation.Token, bundlePreparationSeconds: 240);
        Assert.Equal(start.AddSeconds(300), options.Deadline);
        Assert.Equal(cancellation.Token, options.CancellationToken);
        Assert.Equal(60, request.TimeoutSeconds);
    }

    [Fact]
    public void DesiredStateActivationDeadlineCoversTwoBoundedColdArtifactBuildsAndPreservesCancellation()
    {
        DateTime utcNow = new(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);
        using var cancellation = new CancellationTokenSource();

        Grpc.Core.CallOptions options = Gateway.Infrastructure.Sandbox.ManagerGrpcServiceClient
            .BuildDesiredStateCallOptions(utcNow, cancellation.Token);

        Assert.Equal(utcNow.AddMinutes(31), options.Deadline);
        Assert.Equal(cancellation.Token, options.CancellationToken);
        Assert.True(Gateway.Infrastructure.Sandbox.ManagerGrpcServiceClient.DesiredStateMaterializationDeadline >= TimeSpan.FromMinutes(30));
        Assert.True(Gateway.Infrastructure.Sandbox.ManagerGrpcServiceClient.DesiredStateMaterializationDeadline <= TimeSpan.FromMinutes(32));
    }
}
