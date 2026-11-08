using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Telemetry.DependencyInjection;
using Kautuka.Telemetry.Telemetry;

using Microsoft.Extensions.Configuration;

namespace Kautuka.IntegrationTests;

public sealed class ManagedHostTelemetryConfigurationTests
{
    [Fact]
    public void HostExportersUseTheProvisionedIngestionFileAndFixedSocketWithoutStaticHeaders()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-host-telemetry-" + Guid.NewGuid().ToString("N"));
        string managerRoot = Path.Combine(root, "manager");
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [KautukaTelemetryDefaults.HeadersKey] = "Authorization=Basic stale-credential",
            [KautukaTelemetryDefaults.LogsEndpointKey] = "https://unrelated.example.com/logs",
            [KautukaTelemetryDefaults.UnixSocketPathKey] = string.Empty,
        });
        try
        {
            var credentials = new OpenObserveCredentialStore(root);
            credentials.SaveIngestionAuthorizationHeader("Basic " + Convert.ToBase64String("fixture:ingestion-only"u8));
            ManagedHostTelemetryConfiguration.Apply(configuration, root, managerRoot);
            Assert.Equal(credentials.IngestionAuthorizationPath, configuration[KautukaTelemetryDefaults.AuthorizationHeaderFileKey]);
            Assert.True(File.Exists(configuration[KautukaTelemetryDefaults.AuthorizationHeaderFileKey]));
            Assert.NotEqual(credentials.RootPasswordPath, configuration[KautukaTelemetryDefaults.AuthorizationHeaderFileKey]);
            Assert.Equal(string.Empty, configuration[KautukaTelemetryDefaults.HeadersKey]);
            Assert.Equal(Path.Combine(managerRoot, "egress", "host", "connect.sock"), configuration[KautukaTelemetryDefaults.UnixSocketPathKey]);
            Assert.Equal(KautukaTelemetryDefaults.ManagedHostEndpoint + "/v1/logs", configuration[KautukaTelemetryDefaults.LogsEndpointKey]);
            Assert.Equal(KautukaTelemetryDefaults.ManagedHostEndpoint + "/v1/metrics", configuration[KautukaTelemetryDefaults.MetricsEndpointKey]);
            Assert.Equal(KautukaTelemetryDefaults.ManagedHostEndpoint + "/v1/traces", configuration[KautukaTelemetryDefaults.TracesEndpointKey]);
            Assert.Equal("http/protobuf", configuration[KautukaTelemetryDefaults.ProtocolKey]);
        }
        finally { configuration.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
