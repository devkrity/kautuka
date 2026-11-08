using System.Security.Cryptography;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

internal sealed record CoreDnsConfiguration(string Corefile, string CanaryName, string CanaryAddress)
{
    internal static CoreDnsConfiguration Create(IReadOnlyList<string> upstreams)
    {
        NetworkDestinationPolicy.ValidateDnsUpstreams(upstreams);
        const string zone = "kautuka-dns-readiness.invalid";
        string name = "n" + Guid.NewGuid().ToString("N") + "." + zone;
        Span<byte> random = stackalloc byte[2];
        RandomNumberGenerator.Fill(random);
        string address = $"198.18.{random[0]}.{random[1]}";
        return new CoreDnsConfiguration(RenderVersion1(name, address, upstreams), name, address);
    }

    internal static string RenderVersion1(string name, string address, IReadOnlyList<string> upstreams)
    {
        const string zone = "kautuka-dns-readiness.invalid";
        return zone + ":53 {\n hosts /dev/null {\n  " + address + " " + name + "\n  ttl 1\n }\n}\n"
            + ".:53 {\n forward . " + string.Join(' ', upstreams) + "\n reload 2s 1s\n log\n errors\n}\n";
    }
}
