using System.Text;

namespace Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;

// Adapter for the shared workload renderer; it does not pull in the Manager host.
internal static class BoundaryRulesetRenderer
{
    internal static string ResolveTemplate(string name)
    {
        using Stream stream = typeof(BoundaryRulesetRenderer).Assembly.GetManifestResourceStream("Kautuka.OciHook." + name)
            ?? throw new InvalidOperationException("Missing embedded nft template.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    internal static string ComputeCanonicalNftJsonHash(string json, IReadOnlyList<string>? requiredComments, string? requiredWorkloadIp)
        => NftRulesetCanonicalizer.ComputeHash(json, requiredComments, requiredWorkloadIp);
}
