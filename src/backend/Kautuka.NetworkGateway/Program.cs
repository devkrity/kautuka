using System.Globalization;
using System.Net;

using Kautuka.NetworkGateway.Admission;
using Kautuka.NetworkGateway.Packets;
using Kautuka.NetworkGateway.Probe;
using Kautuka.NetworkGateway.Storage;

// Deliberately not an installable service yet. This command accepts only the isolated
// primitive fixture's source/sink and verifies its namespace before touching nftables.
if (args.Length != 4 || args[0] != "probe-admission" || !OperatingSystem.IsLinux()
    || !ushort.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out ushort queueNumber))
{
    Console.Error.WriteLine("Linux prototype only: probe-admission <ktng-namespace> <audit.sqlite> <queue-number>");
    return 2;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
using var termination = System.Runtime.InteropServices.PosixSignalRegistration.Create(
    System.Runtime.InteropServices.PosixSignal.SIGTERM, context => { context.Cancel = true; cancellation.Cancel(); });

try
{
    var kernel = new ProbeKernel(args[1], queueNumber);
    uint ingress = uint.Parse(File.ReadAllText("/sys/class/net/g0/ifindex").Trim(), CultureInfo.InvariantCulture);
    SourceRegistration[] sources = [
        new(IPAddress.Parse("10.231.251.2"), "probe-source", 1, ingress),
        new(IPAddress.Parse("fd7a:6b74:251::2"), "probe-source", 1, ingress),
    ];
    EndpointRule[] rules = [
        new("probe-source", IPAddress.Parse("10.231.252.2"), 20199, 17),
        new("probe-source", IPAddress.Parse("10.231.252.2"), 20199, 6),
        new("probe-source", IPAddress.Parse("fd7a:6b74:252::2"), 20199, 17),
        new("probe-source", IPAddress.Parse("fd7a:6b74:252::2"), 20199, 6),
    ];
    var policy = new EndpointPolicy(1, sources, rules);
    using var ledger = new SqliteAdmissionLedger(args[2]);
    var coordinator = new AdmissionCoordinator(policy, ledger, kernel, TimeProvider.System);
    using var consumer = new LinuxNfQueue(queueNumber);
    // Fixed machine-readable handshake with the isolated probe, not localizable UI text.
#pragma warning disable CA1303
    Console.WriteLine("KTNG_PROBE_READY");
#pragma warning restore CA1303
    consumer.Run(packet =>
    {
        if (packet.Flow is null || packet.OriginalMark != 0)
        {
            consumer.Drop(packet.PacketId);
            return;
        }
        // An exception closes the using-owned consumer and exits. Never resume an uncertain queue.
        coordinator.Process(packet.PacketId, packet.Flow, consumer);
    }, cancellation.Token);
    return 0;
}
catch (Exception error) when (error is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
{
    Console.Error.WriteLine($"Admission prototype stopped closed: {error.Message}");
    return 1;
}
