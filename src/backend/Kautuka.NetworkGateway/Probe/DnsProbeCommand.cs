using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

using Kautuka.NetworkGateway.Admission;
using Kautuka.NetworkGateway.Dns;
using Kautuka.NetworkGateway.Storage;

namespace Kautuka.NetworkGateway.Probe;

internal static partial class DnsProbeCommand
{
    public static int Run(string expectedNamespace, string ledgerPath, string subject)
        => RunAsync(expectedNamespace, ledgerPath, subject).GetAwaiter().GetResult();

    // The trusted root fixture selects the subject while launching this dedicated UID
    // inside the registered namespace. Neither the DNS payload nor client supplies it.
    public static async Task<int> RunAsync(string expectedNamespace, string ledgerPath, string subject)
    {
        if (!OperatingSystem.IsLinux() || subject is not ("core" or "work"))
        {
            return 2;
        }
        using var cancellation = new CancellationTokenSource();
        using var termination = PosixSignalRegistration.Create(PosixSignal.SIGTERM,
            context => { context.Cancel = true; cancellation.Cancel(); });
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        try
        {
            if (GetEffectiveUid() == 0)
            {
                throw new InvalidOperationException("DNS fixture resolver must run as its dedicated unprivileged UID.");
            }
            ProbeKernel.VerifyNamespace(expectedNamespace);
            uint ingress = uint.Parse((await File.ReadAllTextAsync("/sys/class/net/g0/ifindex", cancellation.Token).ConfigureAwait(false)).Trim(), CultureInfo.InvariantCulture);
            IPAddress source = IPAddress.Parse("10.231.251.2");
            IPAddress answer4 = IPAddress.Parse("10.231.252.2");
            IPAddress answer6 = IPAddress.Parse("fd7a:6b74:252::2");
            var policy = new DnsQueryPolicy(1, [new SourceRegistration(source, subject, 1, ingress)], [
                new DnsNameRule(subject, $"allowed.{subject}.ktng.test", answer4, answer6),
                new DnsNameRule(subject, $"forged.{subject}.ktng.test", answer4, answer6),
            ]);
            using var ledger = new SqliteDnsDecisionLedger(ledgerPath);
            var upstream = new SocketDnsUpstream(new IPEndPoint(answer4, 2053));
            using var coordinator = new DnsQueryCoordinator(policy, ledger, upstream, TimeProvider.System, TimeSpan.FromSeconds(1));
            using var listener = new DnsResolverListener(new IPEndPoint(IPAddress.Parse("10.231.251.1"), 2053),
                ingress, 1, coordinator, TimeSpan.FromSeconds(2));
#pragma warning disable CA1303
            await Console.Out.WriteLineAsync("KTNG_DNS_READY").ConfigureAwait(false);
#pragma warning restore CA1303
            await listener.RunAsync(cancellation.Token).ConfigureAwait(false);
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or SocketException
            or ArgumentException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            await Console.Error.WriteLineAsync($"DNS prototype stopped closed: {error.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUid();
}
