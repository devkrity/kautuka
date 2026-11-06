using System.Globalization;
using System.Net;

using Kautuka.NetworkGateway.Admission;
using Kautuka.NetworkGateway.Packets;
using Kautuka.NetworkGateway.Probe;
using Kautuka.NetworkGateway.Storage;

if (args.Length == 4 && args[0] == "probe-verify-artifact")
{
    return ArtifactProbeCommand.Run(args[1], args[2], args[3]);
}

// Deliberately not an installable service yet. This command accepts only the isolated
// primitive fixture's source/sink and verifies its namespace before touching nftables.
if (args.Length != 4 || args[0] is not ("probe-admission" or "probe-registry") || !OperatingSystem.IsLinux()
    || !ushort.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out ushort queueNumber))
{
    Console.Error.WriteLine("Linux fixture only: probe-admission or probe-registry <ktng-namespace> <audit.sqlite> <queue-number>; probe-verify-artifact <manifest> <signature> <trusted-public-key>");
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
    string subject = args[0] == "probe-registry" ? "probe-artifacts" : "probe-source";
    SourceRegistration[] sources = [
        new(IPAddress.Parse("10.231.251.2"), subject, 1, ingress),
        new(IPAddress.Parse("fd7a:6b74:251::2"), subject, 1, ingress),
    ];
    EndpointRule[] rules = args[0] == "probe-registry"
        ? [new(subject, IPAddress.Parse("10.231.252.2"), 20443, 6)]
        : [
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
