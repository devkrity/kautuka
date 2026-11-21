using Kautuka.Manager.Hosting;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class EnvoySocketSelinuxLabelTests
{
    [Fact]
    public void InstalledSelinuxStartupRelabelsSocketDirectoryAndFailsClosedOnError()
    {
        string? relabelled = null;
        EnvoySocketSelinuxLabel.RelabelIfRequired(true, "1", "/run/kautuka/networkaccess-envoy", path =>
        {
            relabelled = path;
            return 0;
        });
        Assert.Equal("/run/kautuka/networkaccess-envoy", relabelled);
        Assert.Throws<InvalidOperationException>(() => EnvoySocketSelinuxLabel.RelabelIfRequired(true, "1", "/run/kautuka/networkaccess-envoy", _ => 1));
        EnvoySocketSelinuxLabel.RelabelIfRequired(false, "1", "/unused", _ => throw new InvalidOperationException("unexpected relabel"));
        EnvoySocketSelinuxLabel.RelabelIfRequired(true, null, "/unused", _ => throw new InvalidOperationException("unexpected relabel"));
    }
}
