namespace Kautuka.IntegrationTests;

public sealed class ManagerGrpcServiceClientDeadlineTests
{
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
