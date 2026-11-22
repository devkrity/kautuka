namespace Kautuka.NetworkAccessController;

public static class NetworkRegistrableDomain
{
    private static readonly Lazy<SuffixRules> rules = new(LoadRules);

    public static bool TryGet(string? hostname, out string domain)
    {
        domain = string.Empty;
        if (!NetworkDestinationNormalizer.TryNormalizeHostname(hostname, out string normalized, out _)
            || !normalized.Contains('.', StringComparison.Ordinal)
            || normalized.EndsWith(".arpa", StringComparison.Ordinal)
            || normalized.EndsWith(".onion", StringComparison.Ordinal))
            return false;

        string[] labels = normalized.Split('.');
        SuffixRules current = rules.Value;
        int suffixLabels = 0;
        for (int index = labels.Length - 1; index >= 0; index--)
        {
            int count = labels.Length - index;
            string suffix = string.Join(".", labels, index, count);
            if (current.Exceptions.Contains(suffix))
            {
                suffixLabels = count - 1;
                break;
            }
            if (current.Exact.Contains(suffix)) suffixLabels = Math.Max(suffixLabels, count);
            if (index > 0 && current.Wildcards.Contains(suffix)) suffixLabels = Math.Max(suffixLabels, count + 1);
        }

        // Unknown suffixes remain eligible for exact hostname approval only.
        if (suffixLabels == 0 || labels.Length <= suffixLabels) return false;
        domain = string.Join(".", labels, labels.Length - suffixLabels - 1, suffixLabels + 1);
        return true;
    }

    private static SuffixRules LoadRules()
    {
        using Stream stream = typeof(NetworkRegistrableDomain).Assembly.GetManifestResourceStream(
            "Kautuka.NetworkAccessController.public_suffix_list.dat")
            ?? throw new InvalidOperationException("The packaged Public Suffix List is missing.");
        using var reader = new StreamReader(stream);
        var exact = new HashSet<string>(StringComparer.Ordinal);
        var wildcards = new HashSet<string>(StringComparer.Ordinal);
        var exceptions = new HashSet<string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            string rule = line.Trim();
            if (rule.Length == 0 || rule.StartsWith("//", StringComparison.Ordinal)) continue;
            int comment = rule.AsSpan().IndexOfAny(' ', '\t');
            if (comment >= 0) rule = rule[..comment];
            HashSet<string> target = exact;
            if (rule.StartsWith("*.", StringComparison.Ordinal))
            {
                target = wildcards;
                rule = rule[2..];
            }
            else if (rule.StartsWith('!'))
            {
                target = exceptions;
                rule = rule[1..];
            }
            if (NetworkDestinationNormalizer.TryNormalizeHostname(rule, out string normalized, out _))
                target.Add(normalized);
        }
        return new(exact, wildcards, exceptions);
    }

    private sealed record SuffixRules(HashSet<string> Exact, HashSet<string> Wildcards, HashSet<string> Exceptions);
}
