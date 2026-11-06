using System.Net;

using Kautuka.NetworkGateway.Admission;
using Kautuka.NetworkGateway.Storage;

using Microsoft.Data.Sqlite;

namespace Kautuka.NetworkGateway.Tests;

public sealed class AdmissionCoordinatorTests
{
    private static readonly FlowKey allowedFlow = new(12, 0, IPAddress.Parse("10.231.251.2"), 41000, IPAddress.Parse("10.231.252.2"), 20199, 17, 11);
    private static readonly SourceRegistration registeredSource = new(allowedFlow.Source, "probe-source", 3, 11);

    [Fact]
    public void DurableRowIsVisibleToAnotherConnectionBeforeKernelPublishAndVerdict()
    {
        using var directory = new TestDirectory();
        string path = directory.DatabasePath;
        using var ledger = new SqliteAdmissionLedger(path);
        var order = new List<string>();
        var kernel = new TestKernel(lease =>
        {
            using var observer = new SqliteConnection($"Data Source={path};Pooling=False");
            observer.Open();
            using SqliteCommand command = observer.CreateCommand();
            command.CommandText = "SELECT subject FROM gateway_admissions WHERE id = $id;";
            command.Parameters.AddWithValue("$id", lease.Handle);
            Assert.Equal(registeredSource.Subject, command.ExecuteScalar());
            order.Add("durable");
        });
        var coordinator = Create(ledger, kernel);
        var verdicts = new TestVerdicts((_, _) => order.Add("released"));

        Assert.True(coordinator.Process(1, allowedFlow, verdicts).Allowed);
        Assert.Equal(["durable", "released"], order);
        Assert.Equal(0, verdicts.Drops);
    }

    [Fact]
    public void RestartedLedgerPreservesRowsAndNeverReusesAHandle()
    {
        using var directory = new TestDirectory();
        long first;
        using (var ledger = new SqliteAdmissionLedger(directory.DatabasePath))
        {
            first = ledger.Append(allowedFlow, registeredSource, 1, DateTimeOffset.UtcNow.AddMinutes(1));
        }

        using var reopened = new SqliteAdmissionLedger(directory.DatabasePath);
        long next = reopened.Append(allowedFlow with { ConntrackId = 13 }, registeredSource, 1, DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.True(next > first);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnrelatedOrOldPrototypeDatabaseIsRefusedBeforeSchemaOrJournalChanges(bool oldPrototype)
    {
        using var directory = new TestDirectory();
        using (var application = new SqliteConnection($"Data Source={directory.DatabasePath};Pooling=False"))
        {
            application.Open();
            using SqliteCommand command = application.CreateCommand();
            command.CommandText = (oldPrototype ? "PRAGMA application_id=0x4B4E4731; PRAGMA user_version=1;" : "")
                + "CREATE TABLE user_data (value TEXT); INSERT INTO user_data VALUES ('preserve');";
            command.ExecuteNonQuery();
        }

        Assert.Throws<InvalidDataException>(() => new SqliteAdmissionLedger(directory.DatabasePath));
        using var observer = new SqliteConnection($"Data Source={directory.DatabasePath};Pooling=False");
        observer.Open();
        using SqliteCommand check = observer.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'gateway_%';";
        Assert.Equal(0L, check.ExecuteScalar());
        check.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("delete", check.ExecuteScalar());
        check.CommandText = "SELECT value FROM user_data;";
        Assert.Equal("preserve", check.ExecuteScalar());
    }

    [Fact]
    public void RealSqliteWriterContentionDeniesInsteadOfAllowingWithoutRecord()
    {
        using var directory = new TestDirectory();
        using var ledger = new SqliteAdmissionLedger(directory.DatabasePath);
        using var blocker = new SqliteConnection($"Data Source={directory.DatabasePath};Pooling=False");
        blocker.Open();
        using SqliteTransaction transaction = blocker.BeginTransaction();
        var kernel = new TestKernel();
        var verdicts = new TestVerdicts();
        Assert.Equal("audit_unavailable", Create(ledger, kernel).Process(1, allowedFlow, verdicts).Reason);
        Assert.Empty(kernel.Published);
        Assert.Empty(verdicts.Allowed);
        Assert.Equal(1, verdicts.Drops);
    }

    [Fact]
    public void UnavailableAuditNeverPublishesOrReleasesPermission()
    {
        var ledger = new TestLedger { OnAppend = () => throw new IOException("injected disk failure") };
        var kernel = new TestKernel();
        var verdicts = new TestVerdicts();
        AdmissionResult result = Create(ledger, kernel).Process(1, allowedFlow, verdicts);
        Assert.Equal("audit_unavailable", result.Reason);
        Assert.Empty(kernel.Published);
        Assert.Empty(verdicts.Allowed);
        Assert.Equal(1, verdicts.Drops);
    }

    [Fact]
    public void PartialKernelPublishIsRevokedAndNeverReleased()
    {
        var kernel = new TestKernel(_ => throw new IOException("partial publish"));
        var verdicts = new TestVerdicts();
        AdmissionResult result = Create(new TestLedger(), kernel).Process(1, allowedFlow, verdicts);
        Assert.Equal("kernel_unavailable", result.Reason);
        Assert.Equal([1u], kernel.Revoked);
        Assert.Empty(verdicts.Allowed);
        Assert.Equal(1, verdicts.Drops);
    }

    [Fact]
    public void UnknownSourceAndWrongEndpointNeverReachLedger()
    {
        var ledger = new TestLedger();
        var coordinator = Create(ledger, new TestKernel());
        var verdicts = new TestVerdicts();
        Assert.False(coordinator.Process(1, allowedFlow with { Source = IPAddress.Parse("10.231.251.3") }, verdicts).Allowed);
        Assert.False(coordinator.Process(2, allowedFlow with { DestinationPort = 443 }, verdicts).Allowed);
        Assert.False(coordinator.Process(3, allowedFlow with { ConntrackId = 0 }, verdicts).Allowed);
        Assert.Equal(0, ledger.Appended);
        Assert.Equal(3, verdicts.Drops);
    }

    [Fact]
    public void RetransmissionSharesDurableAdmissionButRevokeBlocksLateDuplicate()
    {
        var ledger = new TestLedger();
        var kernel = new TestKernel();
        var coordinator = Create(ledger, kernel);
        var verdicts = new TestVerdicts();
        Assert.True(coordinator.Process(1, allowedFlow, verdicts).Allowed);
        Assert.True(coordinator.Process(2, allowedFlow, verdicts).Allowed);
        Assert.Equal(1, ledger.Appended);
        coordinator.RevokeSubject(registeredSource.Subject);
        Assert.False(coordinator.Process(3, allowedFlow, verdicts).Allowed);
        Assert.Single(kernel.Revoked);
        Assert.Equal(2, verdicts.Allowed.Count);
    }

    [Fact]
    public void SameTupleWithNewConntrackGenerationNeedsAnotherAdmission()
    {
        var ledger = new TestLedger();
        var coordinator = Create(ledger, new TestKernel());
        var verdicts = new TestVerdicts();
        AdmissionResult first = coordinator.Process(1, allowedFlow, verdicts);
        AdmissionResult next = coordinator.Process(2, allowedFlow with { ConntrackId = 13 }, verdicts);
        Assert.NotEqual(first.Handle, next.Handle);
        Assert.Equal(2, ledger.Appended);
    }

    [Fact]
    public void CapacityExhaustionDeniesUntilTrustedDestroyRetiresFlow()
    {
        var ledger = new TestLedger();
        var coordinator = Create(ledger, new TestKernel(), capacity: 1);
        var verdicts = new TestVerdicts();
        Assert.True(coordinator.Process(1, allowedFlow, verdicts).Allowed);
        FlowKey next = allowedFlow with { ConntrackId = 13 };
        Assert.Equal("capacity_exhausted", coordinator.Process(2, next, verdicts).Reason);
        coordinator.ForgetDestroyedFlow(allowedFlow);
        Assert.True(coordinator.Process(3, next, verdicts).Allowed);
    }

    [Fact]
    public void ExpiredFlowIsNotReAdmittedUnderItsOldConntrackGeneration()
    {
        var clock = new TestClock();
        var coordinator = Create(new TestLedger(), new TestKernel(), clock);
        var verdicts = new TestVerdicts();
        Assert.True(coordinator.Process(1, allowedFlow, verdicts).Allowed);
        clock.Now += TimeSpan.FromSeconds(121);
        Assert.Equal("lease_expired", coordinator.Process(2, allowedFlow, verdicts).Reason);
    }

    [Fact]
    public void AuditDelayCannotReleaseAlreadyExpiredLease()
    {
        var clock = new TestClock();
        var ledger = new TestLedger { OnAppend = () => clock.Now += TimeSpan.FromSeconds(121) };
        var kernel = new TestKernel();
        var verdicts = new TestVerdicts();
        Assert.Equal("lease_expired_before_release", Create(ledger, kernel, clock).Process(1, allowedFlow, verdicts).Reason);
        Assert.Empty(kernel.Published);
        Assert.Empty(verdicts.Allowed);
    }

    [Fact]
    public void ExhaustedHandleSpaceNeverWrapsOrPublishes()
    {
        var ledger = new TestLedger { Next = (long)uint.MaxValue + 1 };
        var kernel = new TestKernel();
        var verdicts = new TestVerdicts();
        Assert.Equal("handle_space_exhausted", Create(ledger, kernel).Process(1, allowedFlow, verdicts).Reason);
        Assert.Empty(kernel.Published);
        Assert.Empty(verdicts.Allowed);
    }

    [Fact]
    public void FailedRevocationIsNotAcknowledgedAndDoesNotPermitNewAdmissions()
    {
        var kernel = new TestKernel { OnRevoke = () => throw new IOException("kernel unavailable") };
        var coordinator = Create(new TestLedger(), kernel);
        var verdicts = new TestVerdicts();
        coordinator.Process(1, allowedFlow, verdicts);
        Assert.Throws<IOException>(() => coordinator.RevokeSubject(registeredSource.Subject));
        Assert.False(coordinator.Process(2, allowedFlow with { ConntrackId = 13 }, verdicts).Allowed);
    }

    [Fact]
    public void PublicationDelayCannotReleaseExpiredPermission()
    {
        var clock = new TestClock();
        var kernel = new TestKernel(_ => clock.Now += TimeSpan.FromSeconds(121));
        var verdicts = new TestVerdicts();
        Assert.Equal("lease_expired_before_release", Create(new TestLedger(), kernel, clock).Process(1, allowedFlow, verdicts).Reason);
        Assert.Equal([1u], kernel.Revoked);
        Assert.Empty(verdicts.Allowed);
        Assert.Equal(1, verdicts.Drops);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FailedPublicationCleanupDropsPacketAndFaultsCoordinator(bool revokeFails)
    {
        var kernel = new TestKernel(_ => throw new IOException("publish failed"))
        {
            OnRevoke = revokeFails ? () => throw new IOException("revoke failed") : null,
        };
        var ledger = new TestLedger
        {
            OnReleaseFailure = !revokeFails ? () => throw new IOException("status write failed") : null,
        };
        var coordinator = Create(ledger, kernel);
        var verdicts = new TestVerdicts();
        Assert.Throws<IOException>(() => coordinator.Process(1, allowedFlow, verdicts));
        Assert.Equal(1, verdicts.Drops);
        Assert.Equal("coordinator_faulted", coordinator.Process(2, allowedFlow, verdicts).Reason);
        Assert.Empty(verdicts.Allowed);
    }

    [Fact]
    public void FailedVerdictFaultsConsumerContractInsteadOfContinuing()
    {
        var coordinator = Create(new TestLedger(), new TestKernel());
        var failed = new TestVerdicts((_, _) => throw new IOException("queue closed"));
        Assert.Throws<IOException>(() => coordinator.Process(1, allowedFlow, failed));
        var next = new TestVerdicts();
        Assert.Equal("coordinator_faulted", coordinator.Process(2, allowedFlow, next).Reason);
        Assert.Empty(next.Allowed);
    }

    [Fact]
    public void SpoofedSourceAddressOnAnotherInterfaceCannotBorrowPermission()
    {
        var ledger = new TestLedger();
        var verdicts = new TestVerdicts();
        AdmissionResult result = Create(ledger, new TestKernel()).Process(1, allowedFlow with { IngressInterfaceIndex = 12 }, verdicts);
        Assert.False(result.Allowed);
        Assert.Equal(0, ledger.Appended);
        Assert.Empty(verdicts.Allowed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReopenCannotUndoRevocationEvenBeforeFirstFlow(bool previouslyAdmitted)
    {
        using var directory = new TestDirectory();
        using (var ledger = new SqliteAdmissionLedger(directory.DatabasePath))
        {
            var coordinator = Create(ledger, new TestKernel());
            if (previouslyAdmitted)
            {
                coordinator.Process(1, allowedFlow, new TestVerdicts());
            }
            coordinator.RevokeSubject(registeredSource.Subject);
        }

        using var reopened = new SqliteAdmissionLedger(directory.DatabasePath);
        var verdicts = new TestVerdicts();
        AdmissionResult result = Create(reopened, new TestKernel()).Process(2, allowedFlow with { ConntrackId = 13 }, verdicts);
        Assert.Equal("identity_revoked", result.Reason);
        Assert.Empty(verdicts.Allowed);
    }

    private static AdmissionCoordinator Create(IAdmissionLedger ledger, IAdmissionKernel kernel, TimeProvider? clock = null, int capacity = 4096)
        => new(new EndpointPolicy(7, [registeredSource], [new EndpointRule(registeredSource.Subject, allowedFlow.Destination, allowedFlow.DestinationPort, 17)]), ledger, kernel, clock ?? TimeProvider.System, capacity);

    private sealed class TestLedger : IAdmissionLedger
    {
        public long Next { get; set; } = 1;
        public int Appended { get; private set; }
        public Action? OnAppend { get; init; }
        public Action? OnReleaseFailure { get; init; }
        public long Append(FlowKey flow, ActorRegistration source, long policyGeneration, DateTimeOffset expiresAt)
        {
            OnAppend?.Invoke();
            Appended++;
            return Next++;
        }
        public void RecordReleaseFailure(long admissionId) => OnReleaseFailure?.Invoke();
        public bool IsRevoked(string subject, long identityGeneration) => false;
        public void Revoke(string subject, long identityGeneration) { }
    }

    private sealed class TestKernel(Action<AdmissionLease>? onPublish = null) : IAdmissionKernel
    {
        public List<AdmissionLease> Published { get; } = [];
        public List<uint> Revoked { get; } = [];
        public Action? OnRevoke { get; init; }
        public void Publish(AdmissionLease lease)
        {
            Published.Add(lease);
            onPublish?.Invoke(lease);
        }
        public void Revoke(uint handle)
        {
            OnRevoke?.Invoke();
            Revoked.Add(handle);
        }
    }

    private sealed class TestVerdicts(Action<uint, uint>? onAllow = null) : IQueueVerdicts
    {
        public List<uint> Allowed { get; } = [];
        public int Drops { get; private set; }
        public void Allow(uint packetId, uint handle)
        {
            onAllow?.Invoke(packetId, handle);
            Allowed.Add(handle);
        }
        public void Drop(uint packetId) => Drops++;
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class TestDirectory : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "kautuka-network-ledger-" + Guid.NewGuid().ToString("N"));
        public TestDirectory() => Directory.CreateDirectory(root);
        public string DatabasePath => Path.Combine(root, "admissions.sqlite");
        public void Dispose() => Directory.Delete(root, recursive: true);
    }
}
