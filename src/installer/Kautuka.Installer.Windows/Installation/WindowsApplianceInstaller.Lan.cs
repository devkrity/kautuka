using System.Globalization;
using System.Net;

namespace Kautuka.Installer.Windows.Installation;

internal static partial class WindowsApplianceInstaller
{
    private const string lanFirewallRuleName = "Kautuka LAN";
    private const int webPort = 20229;

    /// <summary>
    /// The appliance listens on 127.0.0.1 only. A firewall rule without a
    /// forward still leaves the local network unable to reach it. The forward
    /// listens on each private-network IPv4 address, not on 0.0.0.0: listening
    /// on every address also occupies 127.0.0.1 and Windows never reaches the distro.
    /// </summary>
    private static async Task ApplyLanChoiceAsync(
        bool allowLanAccess,
        Func<string, Task> log,
        CancellationToken cancellationToken)
    {
        if (!allowLanAccess)
        {
            await RemoveLanAccessAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!await TryStartIpHelperAsync(cancellationToken).ConfigureAwait(false))
        {
            await log("Kautuka was not opened on the local network because the IP Helper service could not be started.")
                .ConfigureAwait(false);
            return;
        }

        await RemoveLanAccessAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<string> addresses = await PrivateLanAddressesAsync(cancellationToken).ConfigureAwait(false);
        if (addresses.Count == 0)
        {
            await log("Kautuka was not opened on the local network. Windows has no private-network IPv4 address. Public networks stay blocked.")
                .ConfigureAwait(false);
            return;
        }

        List<string> proxyErrors = [];
        foreach (string address in addresses)
        {
            ProcessResult proxy = await RunProcessAsync(
                "netsh",
                [
                    "interface", "portproxy", "add", "v4tov4",
                    $"listenaddress={address}",
                    $"listenport={webPort}",
                    "connectaddress=127.0.0.1",
                    $"connectport={webPort}",
                ],
                allowFailure: true,
                cancellationToken).ConfigureAwait(false);
            if (proxy.ExitCode != 0)
            {
                proxyErrors.Add(string.Join(
                    " ",
                    new[] { proxy.StandardError, proxy.StandardOutput }.Where(static line => !string.IsNullOrWhiteSpace(line))));
            }
        }

        // netsh treats quotes inside an ArgumentList value as part of the name
        // and rejects name="Kautuka LAN". The space is quoted by ArgumentList.
        ProcessResult firewall = await RunProcessAsync(
            "netsh",
            [
                "advfirewall", "firewall", "add", "rule",
                $"name={lanFirewallRuleName}",
                "dir=in",
                "action=allow",
                "protocol=TCP",
                $"localport={webPort}",
                "profile=private",
            ],
            allowFailure: true,
            cancellationToken).ConfigureAwait(false);

        if (proxyErrors.Count > 0 || firewall.ExitCode != 0)
        {
            await RemoveLanAccessAsync(cancellationToken).ConfigureAwait(false);
            string detail = string.Join(
                " ",
                proxyErrors.Append(firewall.StandardError).Append(firewall.StandardOutput)
                    .Where(static line => !string.IsNullOrWhiteSpace(line)));
            await log($"Kautuka was not opened on the local network. {detail}".Trim()).ConfigureAwait(false);
            return;
        }

        await log($"Opened TCP {webPort} on the Private network profile and forwarded {string.Join(", ", addresses)} to localhost. Public networks stay blocked.")
            .ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<string>> PrivateLanAddressesAsync(CancellationToken cancellationToken)
    {
        const string script = """
            $ErrorActionPreference = 'SilentlyContinue'
            Get-NetConnectionProfile | Where-Object { $_.NetworkCategory -eq 'Private' } | ForEach-Object {
              Get-NetIPAddress -InterfaceIndex $_.InterfaceIndex -AddressFamily IPv4 |
                Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } |
                ForEach-Object { $_.IPAddress }
            }
            """;
        ProcessResult result = await RunPowerShellScriptAsync(script, cancellationToken, allowFailure: true)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
            return [];

        string text = (result.StandardOutput + result.StandardError).Replace("\0", "", StringComparison.Ordinal);
        List<string> addresses = [];
        foreach (string line in text.Split(newLineSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = line.Trim();
            if (!IPAddress.TryParse(candidate, out IPAddress? address))
                continue;
            if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any))
                continue;
            if (!addresses.Contains(address.ToString(), StringComparer.Ordinal))
                addresses.Add(address.ToString());
        }

        return addresses;
    }

    private static async Task RemoveLanAccessAsync(CancellationToken cancellationToken)
    {
        await RunProcessAsync(
            "netsh",
            ["advfirewall", "firewall", "delete", "rule", $"name={lanFirewallRuleName}"],
            allowFailure: true,
            cancellationToken).ConfigureAwait(false);

        ProcessResult shown = await RunProcessAsync(
            "netsh",
            ["interface", "portproxy", "show", "v4tov4"],
            allowFailure: true,
            cancellationToken).ConfigureAwait(false);
        foreach (string address in ListenAddressesForPort(shown.StandardOutput + shown.StandardError, webPort))
        {
            await RunProcessAsync(
                "netsh",
                [
                    "interface", "portproxy", "delete", "v4tov4",
                    $"listenaddress={address}",
                    $"listenport={webPort}",
                ],
                allowFailure: true,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static IEnumerable<string> ListenAddressesForPort(string showText, int port)
    {
        string text = showText.Replace("\0", "", StringComparison.Ordinal);
        foreach (string line in text.Split(newLineSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !IPAddress.TryParse(parts[0], out IPAddress? address))
                continue;
            if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int listenPort) || listenPort != port)
                continue;

            yield return address.ToString();
        }
    }

    private static async Task<bool> TryStartIpHelperAsync(CancellationToken cancellationToken)
    {
        ProcessResult query = await RunProcessAsync(
            "sc.exe",
            ["query", "iphlpsvc"],
            allowFailure: true,
            cancellationToken).ConfigureAwait(false);
        if (query.StandardOutput.Contains("RUNNING", StringComparison.Ordinal))
            return true;

        ProcessResult start = await RunProcessAsync(
            "sc.exe",
            ["start", "iphlpsvc"],
            allowFailure: true,
            cancellationToken).ConfigureAwait(false);
        return start.ExitCode == 0
            || start.StandardOutput.Contains("RUNNING", StringComparison.Ordinal)
            || start.StandardError.Contains("RUNNING", StringComparison.Ordinal);
    }
}
