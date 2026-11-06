using Kautuka.IntegrationTests.Fixtures;

if (System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
    throw new InvalidOperationException("Publish this test host with Native AOT.");
using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(160));
Console.WriteLine((await ContainerEgressManagerFixture.RunAsync(args[0], cancellation.Token)).ToJsonString());
