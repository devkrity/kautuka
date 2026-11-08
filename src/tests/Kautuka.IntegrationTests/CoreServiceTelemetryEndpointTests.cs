using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class CoreServiceTelemetryEndpointTests
{
    [Theory]
    [InlineData("http://10.90.0.1:5080/api/default", true)]
    [InlineData("http://172.16.4.1:5080/api/default", true)]
    [InlineData("http://192.168.1.1:5080/api/default", true)]
    [InlineData("http://127.0.0.1:5080/api/default", false)]
    [InlineData("http://8.8.8.8:5080/api/default", false)]
    [InlineData("http://10.90.0.1:5081/api/default", false)]
    [InlineData("https://10.90.0.1:5080/api/default", false)]
    [InlineData("http://10.90.0.1:5080/api/default?token=x", false)]
    [InlineData("http://[fd00::1]:5080/api/default", false)]
    public void CoreCredentialEndpointMustBePrivateFixedIpv4TelemetryBase(string endpoint, bool expected)
        => Assert.Equal(expected, CoreServiceSupervisor.IsCredentialedCoreTelemetryEndpoint(endpoint));
}
