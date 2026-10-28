using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Kautuka.Monitor;

public enum WslConfigSeverity
{
    /// <summary>The appliance is unreachable or non-functional while this setting stands.</summary>
    Breaking,

    /// <summary>The appliance still works, but worse. Report without blocking.</summary>
    Degrading,
}

public sealed record WslConfigFinding(
    string Key,
    string Value,
    WslConfigSeverity Severity,
    string Detail);

/// <summary>
/// Evaluates the host's <c>%USERPROFILE%\.wslconfig</c> for settings that break or degrade
/// the appliance.
/// <para>
/// This file is the appliance's largest uncontrolled dependency: it is machine-wide, applies to
/// every distro including <c>podman-machine-default</c>, and a user editing it for an unrelated
/// reason silently reconfigures the appliance. Everything in <c>/etc/wsl.conf</c> ships inside the
/// image and is ours; this file is not.
/// </para>
/// <para>
/// Findings are reported, never corrected. Rewriting this file to suit the appliance would reach
/// across into the user's other distros.
/// </para>
/// </summary>
public static class WslConfigDrift
{
    private const string wsl2Section = "wsl2";

    /// <summary>
    /// Minimum memory before the appliance is reported as starved. The user manager alone peaks
    /// around 224 MB; the full appliance with sidecars runs 1-2 GB.
    /// </summary>
    private const long minimumMemoryBytes = 2L * 1024 * 1024 * 1024;

    private static readonly string[] kernelKeys = ["kernel", "kernelModules"];

    /// <summary>WSL size suffixes, longest first so <c>GB</c> matches before <c>G</c>.</summary>
    private static readonly (string Suffix, long Scale)[] sizeSuffixes =
    [
        ("TB", 1L << 40), ("GB", 1L << 30), ("MB", 1L << 20), ("KB", 1L << 10),
        ("T", 1L << 40), ("G", 1L << 30), ("M", 1L << 20), ("K", 1L << 10),
    ];

    /// <summary>
    /// Returns the SHA-256 of the file's content, or an empty string when the file is absent.
    /// Callers compare this against an install-time baseline so an unchanged file costs one hash
    /// rather than a parse.
    /// </summary>
    public static string ComputeHash(string? configText)
    {
        if (configText is null)
            return string.Empty;

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(configText));
        return Convert.ToHexString(hash);
    }

    public static string DefaultConfigPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wslconfig");

    /// <summary>
    /// Reads the config file, returning <see langword="null"/> when it does not exist. An absent
    /// file is the healthy default and is not a finding.
    /// </summary>
    public static string? TryRead(string configPath)
        => File.Exists(configPath) ? File.ReadAllText(configPath) : null;

    public static IReadOnlyList<WslConfigFinding> Evaluate(string? configText)
    {
        if (string.IsNullOrWhiteSpace(configText))
            return [];

        Dictionary<string, string> wsl2 = ParseSection(configText, wsl2Section);
        if (wsl2.Count == 0)
            return [];

        List<WslConfigFinding> findings = [];

        // WebHost binds 127.0.0.1:20229 only, so localhost forwarding is the sole path in from
        // Windows. Without it the appliance is healthy inside and unreachable outside.
        if (TryGetBool(wsl2, "localhostForwarding", out bool forwarding) && !forwarding)
        {
            findings.Add(new WslConfigFinding(
                "wsl2.localhostForwarding",
                "false",
                WslConfigSeverity.Breaking,
                "Kautuka listens on 127.0.0.1:20229 inside the distro, so http://localhost:20229 only resolves from Windows while localhost forwarding is on. Remove this setting or set it to true."));
        }

        // Mirrored mode shares the host network namespace, so appliance listeners occupy host
        // ports directly. Port occupancy collides regardless of bind address, which is why the
        // appliance-side port lockdown cannot mitigate this one.
        if (wsl2.TryGetValue("networkingMode", out string? networkingMode)
            && networkingMode.Equals("mirrored", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new WslConfigFinding(
                "wsl2.networkingMode",
                networkingMode,
                WslConfigSeverity.Breaking,
                "Mirrored networking shares the host network stack, so the appliance's ports collide with anything already bound on Windows - including a host Podman. Set networkingMode=nat or remove the setting."));
        }

        if (wsl2.TryGetValue("memory", out string? memory)
            && TryParseSize(memory, out long memoryBytes)
            && memoryBytes < minimumMemoryBytes)
        {
            findings.Add(new WslConfigFinding(
                "wsl2.memory",
                memory,
                WslConfigSeverity.Degrading,
                "The appliance runs six services plus container sidecars and needs roughly 2 GB. Below that, services are killed under memory pressure."));
        }

        if (wsl2.TryGetValue("processors", out string? processors)
            && int.TryParse(processors, NumberStyles.Integer, CultureInfo.InvariantCulture, out int processorCount)
            && processorCount < 2)
        {
            findings.Add(new WslConfigFinding(
                "wsl2.processors",
                processors,
                WslConfigSeverity.Degrading,
                "A single processor serialises the appliance's services and its container builds, making first run very slow."));
        }

        if (wsl2.TryGetValue("swap", out string? swap)
            && TryParseSize(swap, out long swapBytes)
            && swapBytes == 0)
        {
            findings.Add(new WslConfigFinding(
                "wsl2.swap",
                swap,
                WslConfigSeverity.Degrading,
                "Disabling swap removes the appliance's headroom during container image builds."));
        }

        foreach (string kernelKey in kernelKeys)
        {
            if (wsl2.TryGetValue(kernelKey, out string? kernelValue) && !string.IsNullOrWhiteSpace(kernelValue))
            {
                findings.Add(new WslConfigFinding(
                    $"wsl2.{kernelKey}",
                    kernelValue,
                    WslConfigSeverity.Degrading,
                    "A custom kernel replaces the one the appliance's rootless Podman was validated against. Container startup may fail in ways that look like appliance faults."));
            }
        }

        return findings;
    }

    /// <summary>
    /// Parses the requested section of a <c>.wslconfig</c> INI document. Later duplicate keys win,
    /// matching how the file is read in practice. Keys are compared case-insensitively.
    /// </summary>
    private static Dictionary<string, string> ParseSection(string configText, string sectionName)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        var inSection = false;

        foreach (string rawLine in configText.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                string current = line[1..^1].Trim();
                inSection = current.Equals(sectionName, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inSection)
                continue;

            int separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
                continue;

            string key = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();
            if (key.Length > 0)
                values[key] = value;
        }

        return values;
    }

    private static bool TryGetBool(Dictionary<string, string> values, string key, out bool result)
    {
        result = false;
        if (!values.TryGetValue(key, out string? raw))
            return false;

        if (raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw == "1")
        {
            result = true;
            return true;
        }

        if (raw.Equals("false", StringComparison.OrdinalIgnoreCase) || raw == "0")
        {
            result = false;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Parses WSL size syntax - a number with an optional <c>KB</c>/<c>MB</c>/<c>GB</c>/<c>TB</c>
    /// suffix, where a bare number is bytes.
    /// </summary>
    private static bool TryParseSize(string raw, out long bytes)
    {
        bytes = 0;
        string trimmed = raw.Trim();
        if (trimmed.Length == 0)
            return false;

        long multiplier = 1;
        foreach ((string suffix, long scale) in sizeSuffixes)
        {
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                multiplier = scale;
                trimmed = trimmed[..^suffix.Length].Trim();
                break;
            }
        }

        if (!long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out long magnitude))
            return false;

        bytes = magnitude * multiplier;
        return true;
    }
}
