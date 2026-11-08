using Kautuka.Manager.RuntimeContainer.Hosting;

using System.Text.Json;

namespace Kautuka.IntegrationTests;

public sealed class NetworkAddressPoolTests
{
    [Theory]
    [InlineData("10.0.0.0/8")]
    [InlineData("172.16.0.0/12")]
    [InlineData("192.168.0.0/16")]
    [InlineData("10.247.0.0/19")]
    public void PrivatePoolsAllocateFirstAndLast24(string value)
    {
        NetworkAddressPool pool = NetworkAddressPool.Parse(value);
        Assert.True(pool.TryGetSlot(pool.Subnet(0), out int first));
        Assert.True(pool.TryGetSlot(pool.Subnet(pool.SlotCount - 1), out int last));
        Assert.Equal(0, first);
        Assert.Equal(pool.SlotCount - 1, last);
        Assert.EndsWith(".1", pool.Gateway(0), StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => pool.Subnet(pool.SlotCount));
    }

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("100.64.0.0/10")]
    [InlineData("172.15.0.0/16")]
    [InlineData("172.32.0.0/16")]
    [InlineData("192.169.0.0/16")]
    [InlineData("10.1.0.0/20")]
    [InlineData("10.1.2.3/19")]
    [InlineData("10.1/16")]
    public void RejectsNonPrivateSmallOrNoncanonicalPools(string value)
        => Assert.Throws<InvalidOperationException>(() => NetworkAddressPool.Parse(value));

    [Fact]
    public void DetectsHostAndSupernetOverlapAcrossPoolBoundary()
    {
        NetworkAddressPool pool = NetworkAddressPool.Parse("10.247.0.0/19");
        Assert.True(pool.Overlaps("10.247.31.255/32"));
        Assert.True(pool.Overlaps("10.0.0.0/8"));
        Assert.False(pool.Overlaps("10.247.32.0/19"));
        Assert.True(pool.Overlaps("0.0.0.0/0"));
        Assert.False(pool.TryGetSlot("10.247.32.0/24", out _));
        Assert.False(pool.TryGetSlot("10.247.0.1/24", out _));
        Assert.Throws<InvalidOperationException>(() => pool.Overlaps("not-an-ip/24"));
        pool.ValidateResolvers(["169.254.169.254", "10.0.0.53"]);
        Assert.Throws<InvalidOperationException>(() => pool.ValidateResolvers(["10.0.0.53", "10.247.4.5"]));
    }

    [Fact]
    public void OwnedBridgeRoutesAreAcceptedButVpnAndOtherNetworksAreNot()
    {
        NetworkAddressPool pool = NetworkAddressPool.Parse("10.247.0.0/19");
        using JsonDocument networks = JsonDocument.Parse("""
            [{"name":"kautuka-outside","driver":"bridge","internal":false,"dns_enabled":false,"ipv6_enabled":false,
              "network_interface":"podman7","labels":{"kautuka.egress":"true","kautuka.egress.scope":"outside"},
              "subnets":[{"subnet":"10.247.0.0/24","gateway":"10.247.0.1"}]}]
            """);
        using JsonDocument ownedRoutes = JsonDocument.Parse("""
            [{"dst":"default","dev":"eth0"},{"dst":"10.247.0.0/24","dev":"podman7"},
             {"type":"local","dst":"10.247.0.1","dev":"podman7"},
             {"type":"broadcast","dst":"10.247.0.0","dev":"podman7"},
             {"type":"broadcast","dst":"10.247.0.255","dev":"podman7"}]
            """);
        Assert.Contains(0, NetworkPoolInventory.Validate(pool, networks.RootElement, ownedRoutes.RootElement));
        using JsonDocument vpn = JsonDocument.Parse("""[{"dst":"10.247.0.0/24","dev":"tun0"}]""");
        Assert.Throws<InvalidOperationException>(() => NetworkPoolInventory.Validate(pool, networks.RootElement, vpn.RootElement));
        using JsonDocument broadVpn = JsonDocument.Parse("""[{"dst":"10.247.0.0/16","dev":"podman7"}]""");
        Assert.Throws<InvalidOperationException>(() => NetworkPoolInventory.Validate(pool, networks.RootElement, broadVpn.RootElement));
        using JsonDocument otherNetwork = JsonDocument.Parse("""[{"name":"other","labels":{},"subnets":[{"subnet":"10.247.20.0/24"}]}]""");
        Assert.Throws<InvalidOperationException>(() => NetworkPoolInventory.Validate(pool, otherNetwork.RootElement, ownedRoutes.RootElement));
        using JsonDocument badRoute = JsonDocument.Parse("""[{"dev":"eth0"}]""");
        Assert.Throws<InvalidOperationException>(() => NetworkPoolInventory.Validate(pool, networks.RootElement, badRoute.RootElement));
        using JsonDocument viaRoute = JsonDocument.Parse("""[{"dst":"10.247.0.0/24","dev":"podman7","via":{"addr":"10.0.0.1"}}]""");
        Assert.Throws<InvalidOperationException>(() => NetworkPoolInventory.Validate(pool, networks.RootElement, viaRoute.RootElement));
        using JsonDocument otherDefaultSubnet = JsonDocument.Parse("""[{"name":"other","labels":{},"subnets":[{"subnet":"0.0.0.0/0"}]}]""");
        Assert.Throws<InvalidOperationException>(() => NetworkPoolInventory.Validate(pool, otherDefaultSubnet.RootElement, ownedRoutes.RootElement));
        using JsonDocument duplicateSlots = JsonDocument.Parse("""
            [{"name":"kautuka-outside","driver":"bridge","internal":false,"dns_enabled":false,"ipv6_enabled":false,
              "network_interface":"podman7","labels":{"kautuka.egress":"true","kautuka.egress.scope":"outside"},
              "subnets":[{"subnet":"10.247.0.0/24","gateway":"10.247.0.1"}]},
             {"name":"kautuka-core","driver":"bridge","internal":true,"dns_enabled":false,"ipv6_enabled":false,
              "network_interface":"podman8","labels":{"kautuka.egress":"true","kautuka.egress.scope":"core"},
              "subnets":[{"subnet":"10.247.0.0/24","gateway":"10.247.0.1"}]}]
            """);
        Assert.Throws<InvalidOperationException>(() => NetworkPoolInventory.Validate(pool, duplicateSlots.RootElement, ownedRoutes.RootElement));
        using JsonDocument legacy = JsonDocument.Parse("""
            [{"name":"kautuka-outside","driver":"bridge","internal":false,"dns_enabled":false,"ipv6_enabled":false,
              "network_interface":"podman7","labels":{"kautuka.egress":"true","kautuka.egress.scope":"outside"},
              "subnets":[{"subnet":"10.89.0.0/24","gateway":"10.89.0.1"}]}]
            """);
        Assert.Throws<InvalidOperationException>(() => NetworkPoolInventory.Validate(pool, legacy.RootElement, ownedRoutes.RootElement));
    }
}
