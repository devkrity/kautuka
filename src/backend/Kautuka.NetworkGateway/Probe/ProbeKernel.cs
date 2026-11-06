using System.Diagnostics;
using System.Globalization;

using Kautuka.NetworkGateway.Admission;

namespace Kautuka.NetworkGateway.Probe;

/// <summary>Isolated Gate 1 fixture only. Never used by the installer or an installed service.</summary>
internal sealed class ProbeKernel : IAdmissionKernel
{
    public ProbeKernel(string expectedNamespace, ushort queue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedNamespace);
        if (!expectedNamespace.StartsWith("ktng-", StringComparison.Ordinal)
            || expectedNamespace.Length > 48 || expectedNamespace.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException("Only an explicitly named ktng-* test namespace is allowed.", nameof(expectedNamespace));
        }
        string actual = Command("ip", ["netns", "identify"]);
        if (actual.Trim() != expectedNamespace)
        {
            throw new InvalidOperationException("Refusing to change rules outside the expected isolated test namespace.");
        }
        string queueNumber = queue.ToString(CultureInfo.InvariantCulture);
        Apply($$"""
            destroy table inet ktng_admission
            table inet ktng_admission {
              set valid { type mark; flags timeout; }
              counter boundary_denials { }
              chain boundary {
                meta nfproto != { ipv4, ipv6 } counter name boundary_denials drop
                iifname != { "g0", "g1" } counter name boundary_denials drop
                ct direction original iifname != "g0" counter name boundary_denials drop
                ct direction reply iifname != "g1" counter name boundary_denials drop
                iifname "g0" ip saddr != 10.231.251.2 counter name boundary_denials drop
                iifname "g0" ip daddr != 10.231.252.2 counter name boundary_denials drop
                iifname "g1" ip saddr != 10.231.252.2 counter name boundary_denials drop
                iifname "g1" ip daddr != 10.231.251.2 counter name boundary_denials drop
                iifname "g0" ip6 saddr != fd7a:6b74:251::2 counter name boundary_denials drop
                iifname "g0" ip6 daddr != fd7a:6b74:252::2 counter name boundary_denials drop
                iifname "g1" ip6 saddr != fd7a:6b74:252::2 counter name boundary_denials drop
                iifname "g1" ip6 daddr != fd7a:6b74:251::2 counter name boundary_denials drop
              }
              chain admit {
                type filter hook forward priority 0; policy drop;
                jump boundary
                ct state invalid drop
                ct mark @valid accept
                ct mark != 0 drop
                meta mark set 0
                ct state new queue num {{queueNumber}}
              }
              chain verify {
                type filter hook forward priority 10; policy drop;
                jump boundary
                ct mark @valid accept
                meta mark @valid ct mark set meta mark accept
              }
              chain output {
                type filter hook output priority 0; policy drop;
                oifname { "g0", "g1" } ip6 hoplimit 255 icmpv6 type { nd-neighbor-solicit, nd-neighbor-advert } accept
              }
            }
            """);
    }

    public void Publish(AdmissionLease lease)
    {
        long milliseconds = Math.Min(120000, (long)(lease.ExpiresAt - DateTimeOffset.UtcNow).TotalMilliseconds);
        if (milliseconds <= 0)
        {
            throw new IOException("Cannot publish an expired admission handle.");
        }
        Apply(FormattableString.Invariant($"add element inet ktng_admission valid {{ {lease.Handle} timeout {milliseconds}ms }}"));
    }

    public void Revoke(uint handle)
        => Apply(FormattableString.Invariant($"destroy element inet ktng_admission valid {{ {handle} }}"));

    private static void Apply(string rules) => Command("nft", ["-f", "-"], rules);

    internal static string Command(string program, string[] arguments, string? input = null)
    {
        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using Process process = Process.Start(start) ?? throw new IOException("Cannot start the probe's networking tool.");
        if (input is not null)
        {
            process.StandardInput.Write(input);
            process.StandardInput.Close();
        }
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(5000))
        {
            process.Kill(entireProcessTree: true);
            throw new IOException("Probe networking command timed out.");
        }
        if (process.ExitCode != 0)
        {
            throw new IOException($"Probe networking command failed: {errors.GetAwaiter().GetResult().Trim()}");
        }
        return output.GetAwaiter().GetResult();
    }
}
