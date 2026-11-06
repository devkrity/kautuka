using System.Net;

using Kautuka.NetworkGateway.Admission;
using Kautuka.NetworkGateway.Storage;

using Microsoft.Data.Sqlite;

namespace Kautuka.NetworkGateway.Tests;

public sealed class LocalOutputIdentityTests
{
    private static readonly FlowKey output = new(42, 0, IPAddress.Parse("10.231.252.1"), 41000,
        IPAddress.Parse("10.231.252.2"), 20199, 17, Path: AdmissionPath.LocalOutput,
        EgressInterfaceIndex: 12, SocketUid: 215, NamespaceGeneration: 9);
    private static readonly LocalOutputRegistration resolver = new(output.Source, "resolver", 3, 215, 12, 9);

    [Fact]
    public void RegisteredOutputActorIsDistinctFromForwardedSourceAddress()
    {
        EndpointPolicy policy = Policy();
        Assert.Equal(resolver, policy.Resolve(output));
        Assert.True(policy.Allows(resolver, output));
        FlowKey forwarded = output with { Path = AdmissionPath.Forwarded, SocketUid = null, IngressInterfaceIndex = 11 };
        ActorRegistration actor = Assert.IsType<SourceRegistration>(policy.Resolve(forwarded));
        Assert.False(policy.Allows(actor, forwarded));
        Assert.False(policy.Allows(resolver, forwarded));
    }

    [Fact]
    public void MissingRootOtherAndStaleOutputIdentitiesAreDenied()
    {
        EndpointPolicy policy = Policy();
        FlowKey[] denied = [
            output with { SocketUid = null }, output with { SocketUid = 0 }, output with { SocketUid = 216 },
            output with { EgressInterfaceIndex = 0 }, output with { EgressInterfaceIndex = 13 },
            output with { NamespaceGeneration = 8 }, output with { IngressInterfaceIndex = 11 },
        ];
        foreach (FlowKey flow in denied)
        {
            Assert.False(policy.Allows(resolver, flow));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => new EndpointPolicy(1, [], [], [resolver with { SocketUid = 0 }]));
    }

    [Fact]
    public void OutputIdentityIsDurableBeforeKernelPublicationAndRevocationSurvivesRestart()
    {
        string path = Path.Combine(Path.GetTempPath(), "ktng-output-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            using (var ledger = new SqliteAdmissionLedger(path))
            {
                var kernel = new Kernel(lease =>
                {
                    using var observer = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
                    observer.Open();
                    using SqliteCommand command = observer.CreateCommand();
                    command.CommandText = "SELECT hook, ingress_interface, egress_interface, socket_uid, namespace_generation, subject FROM gateway_admissions WHERE id=$id";
                    command.Parameters.AddWithValue("$id", lease.Handle);
                    using SqliteDataReader reader = command.ExecuteReader();
                    Assert.True(reader.Read());
                    Assert.Equal(3, reader.GetInt32(0));
                    Assert.Equal(0, reader.GetInt32(1));
                    Assert.Equal(12, reader.GetInt32(2));
                    Assert.Equal(215, reader.GetInt32(3));
                    Assert.Equal(9, reader.GetInt64(4));
                    Assert.Equal("resolver", reader.GetString(5));
                });
                var coordinator = new AdmissionCoordinator(Policy(), ledger, kernel, TimeProvider.System);
                var verdicts = new Verdicts(() => Assert.Equal(1, kernel.Published));
                Assert.True(coordinator.Process(1, output, verdicts).Allowed);
                coordinator.RevokeSubject("resolver");
                Assert.Equal(1, kernel.Revoked);
            }
            using (var reopened = new SqliteAdmissionLedger(path))
            {
                var kernel = new Kernel(_ => Assert.Fail("Revoked identity must not publish"));
                var coordinator = new AdmissionCoordinator(Policy(), reopened, kernel, TimeProvider.System);
                Assert.False(coordinator.Process(2, output with { ConntrackId = 43 }, new Verdicts(() => Assert.Fail("Revoked identity must not allow"))).Allowed);
            }
        }
        finally
        {
            foreach (string suffix in new[] { "", "-wal", "-shm" })
            {
                File.Delete(path + suffix);
            }
        }
    }

    private static EndpointPolicy Policy() => new(1,
        [new SourceRegistration(output.Source, "forwarded", 4, 11, 9)],
        [new EndpointRule("resolver", output.Destination, 20199, 17)], [resolver]);

    private sealed class Kernel(Action<AdmissionLease> publish) : IAdmissionKernel
    {
        public int Published { get; private set; }
        public int Revoked { get; private set; }
        public void Publish(AdmissionLease lease) { publish(lease); Published++; }
        public void Revoke(uint handle) => Revoked++;
    }

    private sealed class Verdicts(Action allowed) : IQueueVerdicts
    {
        public void Allow(uint packetId, uint handle) => allowed();
        public void Drop(uint packetId) { }
    }
}
