using System.Text;

namespace Kautuka.NetworkAccessController;

public sealed class NetworkRegistrableDomain(string? path, int minimumRuleCount = NetworkRegistrableDomain.DefaultMinimumRuleCount)
{
    public const string ListFileName = "public_suffix_list.dat";
    public const int DefaultMinimumRuleCount = 5000;
    private static readonly UTF8Encoding utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly string[] sectionMarkers =
    [
        "// ===BEGIN ICANN DOMAINS===",
        "// ===END ICANN DOMAINS===",
        "// ===BEGIN PRIVATE DOMAINS===",
        "// ===END PRIVATE DOMAINS===",
    ];

    private readonly object gate = new();
    private readonly int requiredRuleCount = minimumRuleCount > 0 ? minimumRuleCount : DefaultMinimumRuleCount;
    private CachedList? cached;

    public bool TryGet(string? hostname, out string domain)
        => TryGet(hostname, out domain, out _);

    internal bool TryGet(string? hostname, out string domain, out bool listAvailable)
    {
        domain = string.Empty;
        SuffixRules? current = LoadRules();
        listAvailable = current is not null;
        if (current is null || !NetworkDestinationNormalizer.TryNormalizeHostname(hostname, out string normalized, out _)
            || !normalized.Contains('.', StringComparison.Ordinal)
            || normalized.EndsWith(".arpa", StringComparison.Ordinal)
            || normalized.EndsWith(".onion", StringComparison.Ordinal))
            return false;

        string[] labels = normalized.Split('.');
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

    private SuffixRules? LoadRules()
    {
        if (path is null) return null;
        try
        {
            lock (gate)
            {
                var file = new FileInfo(path);
                if (!file.Exists)
                {
                    cached = null;
                    return null;
                }
                if (cached is { } snapshot && snapshot.LastWriteUtc == file.LastWriteTimeUtc && snapshot.Length == file.Length)
                    return snapshot.Rules;

                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                using var reader = new StreamReader(input, utf8, detectEncodingFromByteOrderMarks: false);
                SuffixRules? parsed = Parse(reader);
                if (parsed is not null && parsed.Count < requiredRuleCount) parsed = null;
                cached = new(file.LastWriteTimeUtc, file.Length, parsed);
                return parsed;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return null;
        }
    }

    public static int CountRules(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return Parse(reader)?.Count ?? 0;
    }

    private static SuffixRules? Parse(TextReader reader)
    {
        var exact = new HashSet<string>(StringComparer.Ordinal);
        var wildcards = new HashSet<string>(StringComparer.Ordinal);
        var exceptions = new HashSet<string>(StringComparer.Ordinal);
        int markerIndex = 0;
        int sectionRuleCount = 0;
        while (reader.ReadLine() is { } line)
        {
            string rule = line.Trim();
            if (sectionMarkers.Contains(rule, StringComparer.Ordinal))
            {
                if (markerIndex == sectionMarkers.Length || rule != sectionMarkers[markerIndex]
                    || (markerIndex % 2 == 1 && sectionRuleCount == 0))
                    return null;
                markerIndex++;
                sectionRuleCount = 0;
                continue;
            }

            if (rule.Length == 0 || rule.StartsWith("//", StringComparison.Ordinal)) continue;
            if (markerIndex % 2 == 0) return null;
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
            if (!NetworkDestinationNormalizer.TryNormalizeHostname(rule, out string normalized, out _)) return null;
            if (target.Add(normalized)) sectionRuleCount++;
        }
        return markerIndex == sectionMarkers.Length ? new(exact, wildcards, exceptions) : null;
    }

    private sealed record SuffixRules(HashSet<string> Exact, HashSet<string> Wildcards, HashSet<string> Exceptions)
    {
        internal int Count => Exact.Count + Wildcards.Count + Exceptions.Count;
    }
    private sealed record CachedList(DateTime LastWriteUtc, long Length, SuffixRules? Rules);
}
