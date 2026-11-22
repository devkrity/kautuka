using System.Text;

using Kautuka.Manager.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class ApplianceTelemetryTests : IDisposable
{
    private readonly DirectoryInfo state = Directory.CreateTempSubdirectory("kautuka-telemetry-tests-");

    public void Dispose() => state.Delete(recursive: true);

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task ProvisionsOnlyAnIngestionCredentialAndRespectsADisabledToken(bool enabled, bool malformedCache)
    {
        const string ingestionToken = "o2oi_fixture_token";
        const string secretName = "kautuka-appliance-otlp";
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ApplianceTelemetry.EnsureRootConfiguration(state.FullName);
        string rootAuthorization = ApplianceTelemetry.ReadRootAuthorization(state.FullName);
        string ingestionPath = Path.Combine(state.FullName, "manager", "telemetry", "ingestion");
        if (malformedCache)
            await File.WriteAllTextAsync(ingestionPath, "invalid", cancellationToken);
        string netNamespaceFixture = Path.Combine(Path.GetTempPath(), "netns", "netns-fixture");
        var requests = new List<bool>();
        bool tokenExistsInCollector = !enabled;
        var podman = new PodmanCommandRunner(async (args, token, input) =>
        {
            if (args[0] == "inspect") return new(0, netNamespaceFixture, "");
            if (args[0] == "unshare")
            {
                Assert.DoesNotContain(args, argument => argument.Contains(rootAuthorization, StringComparison.Ordinal));
                Assert.EndsWith("/api/default/ingestion-tokens", args[^1], StringComparison.Ordinal);
                using var reader = new StreamReader(Assert.IsAssignableFrom<Stream>(input), Encoding.ASCII, leaveOpen: true);
                string header = await reader.ReadToEndAsync(token);
                Assert.StartsWith("Authorization: Basic ", header, StringComparison.Ordinal);
                Assert.Equal($"Authorization: Basic {rootAuthorization}\n", header);
                bool create = args.Contains("--data-binary", StringComparer.Ordinal);
                requests.Add(create);
                if (create)
                {
                    tokenExistsInCollector = true;
                    return new(28, "", "simulated uncertain response");
                }
                string payload = tokenExistsInCollector
                    ? $$"""{"data":[{"name":"{{secretName}}","token":"{{ingestionToken}}","enabled":{{(enabled ? "true" : "false")}}}]}"""
                    : """{"data":[]}""";
                return new(0, payload, "");
            }
            throw new InvalidOperationException($"Unexpected Podman command {args[0]}.");
        });

        Assert.Null(ApplianceTelemetry.ReadIngestionAuthorization(state.FullName));
        string? authorization = await ApplianceTelemetry.ProvisionIngestionAuthorizationAsync(podman, state.FullName, new string('c', 64), cancellationToken);
        if (enabled)
        {
            Assert.Equal(new[] { false, true, false }, requests);
            Assert.Equal(ingestionToken, await File.ReadAllTextAsync(ingestionPath, cancellationToken));
            Assert.NotNull(authorization);
            Assert.Equal("default:" + ingestionToken, Encoding.ASCII.GetString(Convert.FromBase64String(authorization)));
            Assert.Equal(authorization, ApplianceTelemetry.ReadIngestionAuthorization(state.FullName));
        }
        else
        {
            Assert.Equal(new[] { false }, requests);
            Assert.False(File.Exists(ingestionPath));
            Assert.Null(authorization);
        }
    }

    [Fact]
    public void RetainsAnOwnerOnlyBootstrapAndRefusesToReplaceItOnceDataExists()
    {
        ApplianceTelemetry.EnsureRootConfiguration(state.FullName);
        string file = ApplianceTelemetry.RootConfigurationPath(state.FullName);
        string retained = ApplianceTelemetry.ReadRootAuthorization(state.FullName);
        ApplianceTelemetry.EnsureRootConfiguration(state.FullName);
        Assert.Equal(retained, ApplianceTelemetry.ReadRootAuthorization(state.FullName));
        if (OperatingSystem.IsLinux())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(file)!));

        string collectorState = Directory.CreateDirectory(Path.Combine(state.FullName, ApplianceTelemetry.StateName)).FullName;
        File.WriteAllText(Path.Combine(collectorState, "fixture"), "existing");
        File.Delete(file);
        Assert.Throws<InvalidOperationException>(() => ApplianceTelemetry.EnsureRootConfiguration(state.FullName));
        Assert.False(File.Exists(file));
    }
}
