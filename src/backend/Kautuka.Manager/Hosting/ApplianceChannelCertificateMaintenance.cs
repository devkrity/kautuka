using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

using Kautuka.Hosting.Hosting;
using Kautuka.Telemetry.Security;

namespace Kautuka.Manager.Hosting;

internal static class ApplianceChannelCertificateMaintenance
{
    private const string currentRoot = "/var/lib/kautuka/security/channel";
    private const string candidateRoot = "/var/lib/kautuka/security/.channel-renewal";
    private const int atCurrentWorkingDirectory = -100;
    private const uint renameExchange = 2;
    private const int renewalDue = 10;

    internal static async Task<int> RunAsync(string action)
    {
        if (!OperatingSystem.IsLinux() || GetEffectiveUserId() != 0)
        {
            await Console.Error.WriteLineAsync("Channel certificate maintenance requires the Linux host administrator.").ConfigureAwait(false);
            return 1;
        }

        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            switch (action)
            {
                case "check":
                    return ChannelSecurityMaterialProvisioner.GetApplianceRenewalState(RuntimeDirectories(currentRoot), now) switch
                    {
                        ChannelSecurityMaterialProvisioner.ApplianceRenewalState.Healthy => 0,
                        ChannelSecurityMaterialProvisioner.ApplianceRenewalState.Routine => renewalDue,
                        ChannelSecurityMaterialProvisioner.ApplianceRenewalState.FinalDay => 11,
                        _ => 12,
                    };
                case "check-serving":
                    var active = RuntimeDirectories(currentRoot);
                    ChannelSecurityMaterialProvisioner.ValidateRuntimeInventories(active);
                    if (ChannelSecurityMaterialProvisioner.GetApplianceRenewalState(active, now) ==
                        ChannelSecurityMaterialProvisioner.ApplianceRenewalState.ExpiredOrInvalid)
                        return 1;
                    using (var configuration = new ConfigurationManager { ["Kautuka:Mode"] = "prod" })
                    {
                        string socket = Path.Combine(KautukaHostNetworkPolicy.ResolveSocketRoot(configuration),
                            "channelhost", "channelhost.sock");
                        return await ApplianceServiceReadiness.CheckAsync(socket,
                            Path.Combine(active.GatewayTrustRoot, ChannelSecurityMaterialProvisioner.CaCertificate),
                            Path.Combine(active.GatewayPrivateRoot, ChannelSecurityMaterialProvisioner.GatewayClientPfx),
                            CancellationToken.None).ConfigureAwait(false) ? 0 : 1;
                    }
                case "stage":
                    ChannelSecurityMaterialProvisioner.StageApplianceLeaves(
                        Path.Combine(currentRoot, "offline-authority"), RuntimeDirectories(candidateRoot), now);
                    return 0;
                case "publish":
                    ChannelSecurityMaterialProvisioner.ValidateRuntimeInventories(RuntimeDirectories(candidateRoot));
                    if (ExchangeDirectories(atCurrentWorkingDirectory, candidateRoot, atCurrentWorkingDirectory, currentRoot, renameExchange) != 0)
                        throw new IOException("Could not atomically exchange the Channel certificate inventories.",
                            new Win32Exception(Marshal.GetLastPInvokeError()));
                    return 0;
                default:
                    await Console.Error.WriteLineAsync("Unknown Channel certificate maintenance action.").ConfigureAwait(false);
                    return 1;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or InvalidOperationException)
        {
            await Console.Error.WriteLineAsync($"Channel certificate maintenance failed: {error.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    private static ChannelSecurityMaterialDirectories RuntimeDirectories(string root)
        => new(Path.Combine(root, "gateway-private"), Path.Combine(root, "gateway-trust"),
            Path.Combine(root, "channel-private"), Path.Combine(root, "channel-trust"));

#pragma warning disable SYSLIB1054, CA2101 // Fixed Linux signatures use UTF-8 without unsafe code.
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int ExchangeDirectories(
        int oldDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
        int newDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath, uint flags);
#pragma warning restore SYSLIB1054, CA2101
}
