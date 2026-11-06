using System.Buffers.Binary;
using System.Net;

using Kautuka.NetworkGateway.Admission;
using Kautuka.NetworkGateway.Dns;
using Kautuka.NetworkGateway.Storage;

using Microsoft.Data.Sqlite;

namespace Kautuka.NetworkGateway.Tests;

public sealed class DnsQueryCoordinatorTests
{
    private static readonly DnsQuestion question = new(0x1234, "allowed.core.ktng.test", 1, 1);
    private static readonly DnsClientContext client = new(IPAddress.Parse("10.231.251.2"), 41000, 17, 11, 9);
    private static readonly IPAddress answer4 = IPAddress.Parse("10.231.252.2");
    private static readonly IPAddress answer6 = IPAddress.Parse("fd7a:6b74:252::2");

    [Theory]
    [InlineData(1)]
    [InlineData(28)]
    public async Task DurableAuthorizationIsVisibleBeforeUpstreamAndClientIdIsRestored(ushort type)
    {
        using var database = new Database();
        using var ledger = new SqliteDnsDecisionLedger(database.Path);
        DnsQuestion request = question with { Type = type };
        var upstream = new Upstream(async (bytes, token) =>
        {
            DnsQuestion sent = Parse(bytes);
            using var observer = new SqliteConnection($"Data Source={database.Path};Mode=ReadOnly;Pooling=False");
            await observer.OpenAsync(token).ConfigureAwait(true);
            using SqliteCommand command = observer.CreateCommand();
            command.CommandText = "SELECT kind, name, subject, question_id, upstream_id, namespace_generation FROM dns_decisions;";
            using SqliteDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(true);
            Assert.True(await reader.ReadAsync(token).ConfigureAwait(true));
            Assert.Equal("AuthorizedAttempt", reader.GetString(0));
            Assert.Equal(request.Name, reader.GetString(1));
            Assert.Equal("core", reader.GetString(2));
            Assert.Equal(request.Id, reader.GetInt32(3));
            Assert.Equal(sent.Id, reader.GetInt32(4));
            Assert.Equal(9, reader.GetInt64(5));
            Assert.False(await reader.ReadAsync(token).ConfigureAwait(true));
            return Answer(sent);
        });
        using var coordinator = Create(ledger, upstream);
        byte[] response = await coordinator.ResolveAsync(DnsWire.Query(request), client, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.True(DnsWire.TryParseAnswer(response, request, out DnsAnswer? resolved));
        Assert.Equal(type == 1 ? answer4 : answer6, resolved!.Address);
        using var check = new SqliteConnection($"Data Source={database.Path};Mode=ReadOnly;Pooling=False");
        await check.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using SqliteCommand count = check.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM dns_decisions WHERE kind='Resolved' AND answer_address IS NOT NULL;";
        Assert.Equal(1L, await count.ExecuteScalarAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("other-owner")]
    [InlineData("interface")]
    [InlineData("namespace")]
    [InlineData("address")]
    [InlineData("type")]
    [InlineData("class")]
    public async Task DenialIsRecordedWithoutUpstreamOrAnInventedRequestedPort(string change)
    {
        DnsQuestion request = change switch
        {
            "name" => question with { Name = "unapproved.core.ktng.test" },
            "other-owner" => question with { Name = "allowed.work.ktng.test" },
            "type" => question with { Type = 16 },
            "class" => question with { Class = 3 },
            _ => question,
        };
        DnsClientContext context = change switch
        {
            "interface" => client with { IngressInterfaceIndex = 12 },
            "namespace" => client with { NamespaceGeneration = 8 },
            "address" => client with { Address = IPAddress.Parse("10.231.251.9") },
            _ => client,
        };
        var ledger = new Ledger();
        var upstream = new Upstream((_, _) => throw new InvalidOperationException("Denied query reached upstream"));
        using var coordinator = Create(ledger, upstream);
        byte[] response = await coordinator.ResolveAsync(DnsWire.Query(request), context, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(5, Code(response));
        DnsDecision decision = Assert.Single(ledger.Rows);
        Assert.Equal(DnsDecisionKind.Denied, decision.Kind);
        Assert.Equal(request.Name, decision.Question.Name);
        Assert.Equal(request.Type, decision.Question.Type);
        Assert.Null(decision.UpstreamId);
        Assert.Equal(context.Port, decision.Client.Port); // Observed caller port, not an external permission.
        Assert.Equal(0, upstream.Calls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AuditFailureStopsUpstreamOrWithholdsAnswer(int failureAt)
    {
        var ledger = new Ledger { FailAt = failureAt };
        var upstream = new Upstream((bytes, _) => Task.FromResult(Answer(Parse(bytes))));
        using var coordinator = Create(ledger, upstream);
        Assert.Equal(2, Code(await coordinator.ResolveAsync(DnsWire.Query(question), client, TestContext.Current.CancellationToken).ConfigureAwait(true)));
        Assert.Equal(failureAt - 1, upstream.Calls);
        Assert.Equal(failureAt - 1, ledger.Rows.Count);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("address")]
    [InlineData("invalid-frame")]
    public async Task MismatchedAndUnauthorizedAnswersAreNotReleased(string mismatch)
    {
        var ledger = new Ledger();
        var upstream = new Upstream((bytes, _) =>
        {
            if (mismatch == "invalid-frame")
            {
                throw new InvalidDataException("Injected invalid upstream frame");
            }
            DnsQuestion sent = Parse(bytes);
            DnsQuestion echoed = mismatch switch
            {
                "id" => sent with { Id = (ushort)(sent.Id ^ 1) },
                "name" => sent with { Name = "other.ktng.test" },
                _ => sent,
            };
            return Task.FromResult(DnsWire.Response(echoed, new DnsAnswer(mismatch == "address" ? IPAddress.Loopback : answer4, 30)));
        });
        using var coordinator = Create(ledger, upstream);
        Assert.Equal(2, Code(await coordinator.ResolveAsync(DnsWire.Query(question), client, TestContext.Current.CancellationToken).ConfigureAwait(true)));
        Assert.Equal(DnsDecisionKind.ResponseRejected, ledger.Rows[^1].Kind);
        Assert.Null(ledger.Rows[^1].AnswerAddress);
    }

    [Fact]
    public async Task DeadlineAndCallerCancellationHaveDistinctOutcomes()
    {
        var timedLedger = new Ledger();
        var timed = new Upstream(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(true);
            return [];
        });
        using (var coordinator = Create(timedLedger, timed, deadline: TimeSpan.FromMilliseconds(50)))
        {
            byte[] response = await coordinator.ResolveAsync(DnsWire.Query(question), client, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.Equal(2, Code(response));
            Assert.Equal("upstream_timeout", timedLedger.Rows[^1].Reason);
        }

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callerLedger = new Ledger();
        var blocked = new Upstream(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(true);
            return [];
        });
        using var callerCoordinator = Create(callerLedger, blocked);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task<byte[]> pending = callerCoordinator.ResolveAsync(DnsWire.Query(question), client, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(true);
        await cancellation.CancelAsync().ConfigureAwait(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending).ConfigureAwait(true);
        Assert.Equal(DnsDecisionKind.AuthorizedAttempt, Assert.Single(callerLedger.Rows).Kind);
    }

    [Fact]
    public async Task OutstandingAndRequestLimitsRejectWithoutNewAuditOrUpstream()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ledger = new Ledger();
        var upstream = new Upstream(async (bytes, token) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token).ConfigureAwait(true);
            return Answer(Parse(bytes));
        });
        using var coordinator = Create(ledger, upstream, concurrency: 1, maximumRequests: 1);
        Task<byte[]> first = coordinator.ResolveAsync(DnsWire.Query(question), client, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(2, Code(await coordinator.ResolveAsync(DnsWire.Query(question), client, TestContext.Current.CancellationToken).ConfigureAwait(true)));
        Assert.Single(ledger.Rows);
        release.SetResult();
        Assert.Equal(0, Code(await first.ConfigureAwait(true)));
        Assert.Equal(2, Code(await coordinator.ResolveAsync(DnsWire.Query(question), client, TestContext.Current.CancellationToken).ConfigureAwait(true)));
        Assert.Equal(1, upstream.Calls);
        Assert.Equal(2, ledger.Rows.Count);
    }

    [Fact]
    public async Task MalformedTrafficDoesNotCreateUnlimitedDecisionRows()
    {
        var ledger = new Ledger();
        var upstream = new Upstream((_, _) => throw new InvalidOperationException("Malformed input reached upstream"));
        using var coordinator = Create(ledger, upstream);
        Assert.Equal(1, Code(await coordinator.ResolveAsync(new byte[] { 0x12, 0x34 }, client, TestContext.Current.CancellationToken).ConfigureAwait(true)));
        Assert.Empty(ledger.Rows);
        Assert.Equal(0, upstream.Calls);
    }

    [Fact]
    public async Task IndependentSqliteWriterLockPreventsUpstreamTraffic()
    {
        using var database = new Database();
        using var ledger = new SqliteDnsDecisionLedger(database.Path);
        using var blocker = new SqliteConnection($"Data Source={database.Path};Pooling=False");
        await blocker.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using var transaction = (SqliteTransaction)await blocker.BeginTransactionAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using SqliteCommand hold = blocker.CreateCommand();
        hold.Transaction = transaction;
        hold.CommandText = "UPDATE dns_decisions SET reason=reason;";
        await hold.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        var upstream = new Upstream((_, _) => throw new InvalidOperationException("Locked audit reached upstream"));
        using var coordinator = Create(ledger, upstream);
        byte[] response = await coordinator.ResolveAsync(DnsWire.Query(question), client, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(2, Code(response));
        Assert.Equal(0, upstream.Calls);
    }

    [Fact]
    public void DnsLedgerCapacityPersistsAndUnrelatedStorageIsPreserved()
    {
        using var database = new Database();
        DnsDecision row = new(Guid.NewGuid(), client, "core", 3, question, 1, DnsDecisionKind.Denied,
            "question_not_approved", null, null, null, 1);
        using (var ledger = new SqliteDnsDecisionLedger(database.Path, capacity: 1))
        {
            ledger.Append(row);
            Assert.Throws<IOException>(() => ledger.Append(row));
        }
        using (var reopened = new SqliteDnsDecisionLedger(database.Path, capacity: 1))
        {
            Assert.Throws<IOException>(() => reopened.Append(row));
        }
        using var unrelated = new Database();
        using (var application = new SqliteConnection($"Data Source={unrelated.Path};Pooling=False"))
        {
            application.Open();
            using SqliteCommand create = application.CreateCommand();
            create.CommandText = "CREATE TABLE user_data(value TEXT); INSERT INTO user_data VALUES ('preserve');";
            create.ExecuteNonQuery();
        }
        Assert.Throws<InvalidDataException>(() => new SqliteDnsDecisionLedger(unrelated.Path));
        using var observer = new SqliteConnection($"Data Source={unrelated.Path};Pooling=False");
        observer.Open();
        using SqliteCommand inspect = observer.CreateCommand();
        inspect.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("delete", inspect.ExecuteScalar());
        inspect.CommandText = "SELECT value FROM user_data;";
        Assert.Equal("preserve", inspect.ExecuteScalar());
    }

    private static DnsQueryCoordinator Create(IDnsDecisionLedger ledger, IDnsUpstream upstream, TimeSpan? deadline = null,
        int concurrency = 16, int maximumRequests = 4096)
        => new(Policy(), ledger, upstream, TimeProvider.System, deadline ?? TimeSpan.FromSeconds(2), concurrency, maximumRequests);

    private static DnsQueryPolicy Policy() => new(1, [
        new SourceRegistration(client.Address, "core", 3, 11, 9),
        new SourceRegistration(IPAddress.Parse("10.231.253.2"), "work", 4, 12, 9),
    ], [new DnsNameRule("core", question.Name, answer4, answer6), new DnsNameRule("work", "allowed.work.ktng.test", answer4, answer6)]);

    private static DnsQuestion Parse(ReadOnlyMemory<byte> bytes)
    {
        Assert.True(DnsWire.TryParseQuery(bytes.Span, out DnsQuestion? parsed));
        return parsed!;
    }

    private static byte[] Answer(DnsQuestion sent) => DnsWire.Response(sent, new DnsAnswer(sent.Type == 1 ? answer4 : answer6, 30));
    private static int Code(byte[] message) => BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(2)) & 15;

    private sealed class Upstream(Func<ReadOnlyMemory<byte>, CancellationToken, Task<byte[]>> exchange) : IDnsUpstream
    {
        public int Calls { get; private set; }
        public Task<byte[]> ExchangeAsync(ReadOnlyMemory<byte> bytes, byte protocol, CancellationToken token)
        {
            Calls++;
            return exchange(bytes, token);
        }
    }

    private sealed class Ledger : IDnsDecisionLedger
    {
        public List<DnsDecision> Rows { get; } = [];
        public int FailAt { get; init; }
        public void Append(DnsDecision decision)
        {
            if (Rows.Count + 1 == FailAt)
            {
                throw new IOException("Injected DNS audit failure");
            }
            Rows.Add(decision);
        }
    }

    private sealed class Database : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ktng-dns-" + Guid.NewGuid().ToString("N") + ".sqlite");
        public void Dispose()
        {
            foreach (string suffix in new[] { "", "-wal", "-shm" })
            {
                File.Delete(Path + suffix);
            }
        }
    }
}
