using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

/// <summary>
/// Warn-only checks that the appliance host exposure matches the sealed port/socket set.
/// Does not fail reconcile — shared WSL netns and other distros can still hold ports.
/// </summary>
public static class ApplianceHostExposureAssert
{
    private static readonly int[] forbiddenTcpPorts =
    [
        20000, 20001, 20002, 20003, 20004, // former gRPC TCP plane (now Unix sockets)
        20015, // former OpenObserve wildcard publish
        20101, // former verdaccio host publish
        20102, // former proxpi host publish
    ];

    private static readonly string[] expectedSocketPaths =
    [
        "/run/kautuka/gateway/gateway.sock",
        "/run/kautuka/manager/manager.sock",
        "/run/kautuka/credentialshost/credentialshost.sock",
        "/run/kautuka/channelhost/channelhost.sock",
        "/run/kautuka/updateservice/updateservice.sock",
    ];

    public static void LogFindings(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        foreach (string socketPath in expectedSocketPaths)
        {
            if (!File.Exists(socketPath))
            {
                logger.LogWarning("Expected Unix socket missing at {SocketPath}.", socketPath);
            }
        }

        if (!TryCaptureListeningSockets(out string ssOutput))
        {
            logger.LogDebug("ss was unavailable; skipped TCP exposure assertions.");
            return;
        }

        foreach (int port in forbiddenTcpPorts)
        {
            if (IsPortListening(ssOutput, port))
            {
                logger.LogWarning(
                    "Forbidden TCP listener detected on port {Port}. Internal control-plane and unpublished sidecar ports should not be host-reachable.",
                    port);
            }
        }

        if (TryFindListenAddresses(ssOutput, 20215, out List<IPAddress> openObserveBinds))
        {
            foreach (IPAddress address in openObserveBinds)
            {
                if (!IPAddress.IsLoopback(address))
                {
                    logger.LogWarning(
                        "OpenObserve is listening on {Address}:20215; expected loopback-only publish.",
                        FormatAddress(address));
                }
            }
        }

        if (!IsPortListening(ssOutput, 20229))
        {
            logger.LogWarning("WebHost TCP listener on 127.0.0.1:20229 was not observed.");
        }
    }

    private static string FormatAddress(IPAddress address)
        => address.Equals(IPAddress.Any) ? "*" : address.ToString();

    private static bool TryCaptureListeningSockets(out string output)
    {
        output = string.Empty;
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ss",
                    ArgumentList = { "-lntp" },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            process.Start();
            output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsPortListening(string ssOutput, int port)
        => ListenAddressRegex(port).IsMatch(ssOutput);

    private static bool TryFindListenAddresses(string ssOutput, int port, out List<IPAddress> addresses)
    {
        addresses = [];
        foreach (Match match in ListenAddressRegex(port).Matches(ssOutput))
        {
            string host = match.Groups["host"].Value;
            if (host is "*" or "0.0.0.0" or "[::]" or "::")
            {
                addresses.Add(IPAddress.Any);
                continue;
            }

            string normalized = host.Trim('[', ']');
            if (IPAddress.TryParse(normalized, out IPAddress? address))
            {
                addresses.Add(address);
            }
        }

        return addresses.Count > 0;
    }

    private static Regex ListenAddressRegex(int port)
        => new(
            $@"LISTEN\s+\S+\s+\S+\s+(?<host>\S+):{port}\b",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
