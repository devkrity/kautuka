using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class HostedProviderNetworkDefaultsTests
{
    [Fact]
    public void DefaultsMatchOnlyFixedHostedCatalogEndpointsAndGatewayIdentity()
    {
        string[] expected = [.. AiProviderCatalog.Defaults.Values
            .Where(static provider => !provider.RequiresBaseUrl && provider.DefaultBaseUrl is not null)
            .Select(static provider => new Uri(provider.DefaultBaseUrl!, UriKind.Absolute))
            .Where(static uri => uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443)
            .Select(static uri => uri.Host).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        IReadOnlyList<ContainerEgressGrant> grants = ContainerEgressRuntime.HostedProviderDefaults();
        Assert.Equal(6, grants.Count);
        Assert.Equal(expected, grants.Select(static grant => grant.Destination).Order(StringComparer.Ordinal));
        Assert.All(grants, grant =>
        {
            Assert.Equal(new NetworkAccessIdentity("core", "system", "gateway", "core", "gateway"), NetworkAccessPolicyStore.Identity(grant));
            Assert.Equal(443, grant.Port);
        });
        Assert.DoesNotContain(grants, static grant => grant.Destination.Contains('*', StringComparison.Ordinal)
            || grant.Destination == "localhost" || grant.Port == 11434);
    }

    [Fact]
    public void ExistingConfigurationGetsDefaultsButPersistedAdministratorBlockWinsAfterRestart()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-provider-policy-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(root, "state.json");
            var actor = new NetworkAccessActor("admin", true);
            var gateway = new NetworkAccessIdentity("core", "system", "gateway", "core", "gateway");
            var store = new NetworkAccessPolicyStore(path);
            // A pre-existing policy need not contain any hosted-provider grants.
            store.SetRule(actor, gateway, "custom.example.com", 443, true, 0);
            Assert.Equal(7, store.EffectiveGrants(ContainerEgressRuntime.HostedProviderDefaults()).Count);
            store.SetRule(actor, gateway, "api.openai.com", 443, false, 1);
            IReadOnlyList<ContainerEgressGrant> effective = new NetworkAccessPolicyStore(path)
                .EffectiveGrants(ContainerEgressRuntime.HostedProviderDefaults());
            Assert.DoesNotContain(effective, static grant => grant.Destination == "api.openai.com");
            Assert.Contains(effective, static grant => grant.Destination == "api.anthropic.com");
            Assert.Contains(effective, static grant => grant.Destination == "custom.example.com");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
