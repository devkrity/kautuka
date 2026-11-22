using System.Text;

using Kautuka.Manager.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class ApplianceTelemetryTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task ProvisionsOnlyAnIngestionCredentialAndRespectsADisabledToken(bool enabled, bool malformedCache)
    {
        const string ingestionToken = "o2oi_fixture_token";
        const string secretName = "kautuka-appliance-otlp";
        string password = "A!9" + new string('a', 48);
        string netNamespaceFixture = Path.Combine(Path.GetTempPath(), "netns", "netns-fixture");
        var requests = new List<bool>();
        string? retainedToken = malformedCache ? password : null;
        bool tokenExistsInCollector = !enabled;
        var podman = new PodmanCommandRunner(async (args, token, input) =>
        {
            if (args[0] == "inspect") return new(0, netNamespaceFixture, "");
            if (args[0] == "unshare")
            {
                Assert.DoesNotContain(args, argument => argument.Contains(password, StringComparison.Ordinal));
                Assert.EndsWith("/api/default/ingestion-tokens", args[^1], StringComparison.Ordinal);
                using var reader = new StreamReader(Assert.IsAssignableFrom<Stream>(input), Encoding.ASCII, leaveOpen: true);
                string header = await reader.ReadToEndAsync(token);
                Assert.StartsWith("Authorization: Basic ", header, StringComparison.Ordinal);
                Assert.EndsWith(":" + password, Encoding.ASCII.GetString(Convert.FromBase64String(header[21..].Trim())), StringComparison.Ordinal);
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
            if (args[1] == "exists") return new(retainedToken is null ? 1 : 0, "", "");
            if (args[1] == "inspect") return new(0, args[^1] == secretName ? retainedToken! : password, "");

            Assert.Equal(new[] { "secret", "create", "--replace", secretName, "-" }, args);
            using var secret = new StreamReader(Assert.IsAssignableFrom<Stream>(input), Encoding.ASCII, leaveOpen: true);
            retainedToken = await secret.ReadToEndAsync(token);
            return new(0, "", "");
        });

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Assert.Null(await ApplianceTelemetry.ReadIngestionAuthorizationAsync(podman, cancellationToken));
        string? authorization = await ApplianceTelemetry.ProvisionIngestionAuthorizationAsync(podman, new string('c', 64), cancellationToken);
        if (enabled)
        {
            Assert.Equal(new[] { false, true, false }, requests);
            Assert.Equal(ingestionToken, retainedToken);
            Assert.NotNull(authorization);
            Assert.Equal("default:" + ingestionToken, Encoding.ASCII.GetString(Convert.FromBase64String(authorization)));
            Assert.Equal(authorization, await ApplianceTelemetry.ReadIngestionAuthorizationAsync(podman, cancellationToken));
        }
        else
        {
            Assert.Equal(new[] { false }, requests);
            Assert.Null(retainedToken);
            Assert.Null(authorization);
        }
    }
}
