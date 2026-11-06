using System.Net.Sockets;
using System.Security.Cryptography;

using Kautuka.NetworkGateway.Admission;

namespace Kautuka.NetworkGateway.Dns;

internal enum DnsDecisionKind { AuthorizedAttempt, Denied, Resolved, UpstreamFailed, ResponseRejected }

internal sealed record DnsDecision(Guid Operation, DnsClientContext Client, string? Subject, long? IdentityGeneration,
    DnsQuestion Question, long PolicyGeneration, DnsDecisionKind Kind, string Reason, ushort? UpstreamId,
    string? AnswerAddress, uint? AnswerTtl, long Timestamp);

internal interface IDnsDecisionLedger
{
    /// <summary>Returns only after synchronous durable commit; capacity exhaustion must throw.</summary>
    void Append(DnsDecision decision);
}

internal interface IDnsUpstream
{
    /// <summary>Honors cancellation and closes request resources before its task completes.</summary>
    Task<byte[]> ExchangeAsync(ReadOnlyMemory<byte> query, byte protocol, CancellationToken cancellationToken);
}

/// <summary>Fixture query authorization. No cache or DNS-to-connect permission is created.</summary>
internal sealed class DnsQueryCoordinator : IDisposable
{
    private readonly DnsQueryPolicy policy;
    private readonly IDnsDecisionLedger ledger;
    private readonly IDnsUpstream upstream;
    private readonly TimeProvider time;
    private readonly TimeSpan deadline;
    private readonly SemaphoreSlim slots;
    private readonly int maximumRequests;
    private long requests;

    public DnsQueryCoordinator(DnsQueryPolicy policy, IDnsDecisionLedger ledger, IDnsUpstream upstream,
        TimeProvider time, TimeSpan deadline, int concurrency = 16, int maximumRequests = 4096)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(upstream);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(concurrency);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRequests);
        if (deadline <= TimeSpan.Zero || deadline > TimeSpan.FromSeconds(5))
        {
            throw new ArgumentOutOfRangeException(nameof(deadline));
        }
        this.policy = policy;
        this.ledger = ledger;
        this.upstream = upstream;
        this.time = time;
        this.deadline = deadline;
        this.maximumRequests = maximumRequests;
        slots = new SemaphoreSlim(concurrency, concurrency);
    }

    public async Task<byte[]> ResolveAsync(ReadOnlyMemory<byte> request, DnsClientContext client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        cancellationToken.ThrowIfCancellationRequested();
        if (!DnsWire.TryParseQuery(request.Span, out DnsQuestion? parsed))
        {
            // Malformed traffic is bounded by the listener; it does not receive unlimited audit rows.
            return DnsWire.MalformedResponse(request.Span);
        }
        DnsQuestion question = parsed!;
        if (!slots.Wait(0, cancellationToken))
        {
            return DnsWire.Response(question, null, 2);
        }
        try
        {
            if (Interlocked.Increment(ref requests) > maximumRequests)
            {
                return DnsWire.Response(question, null, 2);
            }
            using var timer = new CancellationTokenSource(deadline, time);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);
            SourceRegistration? actor = policy.Resolve(client);
            DnsNameRule? rule = actor is null ? null : policy.Authorize(actor, question);
            ushort? upstreamId = rule is null ? null : (ushort)RandomNumberGenerator.GetInt32(65536);
            var decision = new DnsDecision(Guid.NewGuid(), client, actor?.Subject, actor?.Generation, question,
                policy.Generation, rule is null ? DnsDecisionKind.Denied : DnsDecisionKind.AuthorizedAttempt,
                actor is null ? "unregistered_owner" : rule is null ? "question_not_approved" : "authorized_attempt",
                upstreamId, null, null, time.GetUtcNow().ToUnixTimeMilliseconds());
            if (!Commit(decision))
            {
                return DnsWire.Response(question, null, 2);
            }
            if (rule is null)
            {
                return DnsWire.Response(question, null, 5);
            }

            DnsAnswer? answer = null;
            DnsDecisionKind outcome = DnsDecisionKind.ResponseRejected;
            string reason = "invalid_upstream_response";
            try
            {
                DnsQuestion expected = question with { Id = upstreamId!.Value };
                timeout.Token.ThrowIfCancellationRequested();
                byte[] response = await upstream.ExchangeAsync(DnsWire.Query(expected), client.Protocol, timeout.Token).ConfigureAwait(false);
                if (DnsWire.TryParseAnswer(response, expected, out DnsAnswer? received))
                {
                    if (DnsQueryPolicy.AllowsAnswer(rule, expected, received!))
                    {
                        answer = received;
                        outcome = DnsDecisionKind.Resolved;
                        reason = "resolved";
                    }
                    else
                    {
                        reason = "answer_not_approved";
                    }
                }
            }
            catch (InvalidDataException)
            {
                outcome = DnsDecisionKind.ResponseRejected;
                reason = "invalid_upstream_response";
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                outcome = DnsDecisionKind.UpstreamFailed;
                reason = "upstream_timeout";
            }
            catch (Exception error) when (error is IOException or SocketException)
            {
                outcome = DnsDecisionKind.UpstreamFailed;
                reason = "upstream_failed";
            }
            if (!Commit(decision with
            {
                Kind = outcome,
                Reason = reason,
                AnswerAddress = answer?.Address.ToString(),
                AnswerTtl = answer?.Ttl,
                Timestamp = time.GetUtcNow().ToUnixTimeMilliseconds()
            }))
            {
                return DnsWire.Response(question, null, 2);
            }
            return DnsWire.Response(question, answer, answer is null ? (byte)2 : (byte)0);
        }
        finally
        {
            slots.Release();
        }
    }

    /// <summary>The owner cancels and drains all outstanding resolutions before disposal.</summary>
    public void Dispose() => slots.Dispose();

    private bool Commit(DnsDecision decision)
    {
        try
        {
            ledger.Append(decision);
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
        {
            return false;
        }
    }
}
