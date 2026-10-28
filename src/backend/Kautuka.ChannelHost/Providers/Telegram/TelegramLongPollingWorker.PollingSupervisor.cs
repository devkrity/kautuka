using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

using Grpc.Core;

namespace Kautuka.ChannelHost.Providers.Telegram;

internal sealed partial class TelegramLongPollingWorker
{
    private readonly ConcurrentDictionary<string, string> pollingIssues = new(StringComparer.Ordinal);
    private static readonly TimeSpan rosterRefreshInterval = TimeSpan.FromSeconds(1);

    private sealed record BotPoller(PollingRegistration Registration, CancellationTokenSource Cancellation, Task Completion);
    private sealed record BotVerification(PollingRegistration Registration, CancellationTokenSource Cancellation, Task<(string? BotId, int RetrySeconds)> Completion)
    {
        public DateTimeOffset RetryAt { get; set; } = DateTimeOffset.MaxValue;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Roster failures must not terminate healthy polling loops; the failure type is reported without credentials.")]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Each linked cancellation source transfers to the pollers collection and is disposed after its task is observed, including in the supervisor finally block.")]
    private async Task RunSupervisedPollingAsync(CancellationToken stoppingToken)
    {
        Dictionary<string, BotPoller> pollers = new(StringComparer.Ordinal);
        Dictionary<string, BotVerification> verifications = new(StringComparer.Ordinal);
        List<BotVerification> retiredVerifications = [];
        HashSet<string> conflicts = new(StringComparer.Ordinal);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                Dictionary<string, PollingRegistration>? desired = null;
                try
                {
                    desired = new(StringComparer.Ordinal);
                    HashSet<string> currentConflicts = new(StringComparer.Ordinal);
                    foreach (BotVerification retired in retiredVerifications.Where(static item => item.Completion.IsCompleted).ToArray())
                    {
                        await DisposeVerificationAsync(retired).ConfigureAwait(false);
                        retiredVerifications.Remove(retired);
                    }
                    PollingRegistration[] verified = GetVerifiedRegistrations(verifications, retiredVerifications, stoppingToken);
                    foreach (IGrouping<string, PollingRegistration> group in verified.GroupBy(static item => item.BotIdentity, StringComparer.Ordinal))
                    {
                        PollingRegistration[] registrations = [.. group];
                        if (registrations.Length != 1)
                        {
                            currentConflicts.Add(group.Key);
                            pollingIssues["conflict:" + group.Key] = "ambiguous_bot_ownership";
                            if (!conflicts.Contains(group.Key)) LogBotOwnershipConflict(logger, group.Key, registrations.Length);
                            continue;
                        }
                        desired.Add(group.Key, registrations[0]);
                    }
                    foreach (string resolved in conflicts.Except(currentConflicts)) pollingIssues.TryRemove("conflict:" + resolved, out _);
                    conflicts = currentConflicts;
                    pollingIssues.TryRemove("roster", out _);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    desired = null;
                    pollingIssues["roster"] = "roster_refresh_failure";
                    LogPollingRosterFailure(logger, ex.GetType().Name);
                }

                if (desired is not null)
                {
                    foreach ((string key, BotPoller poller) in pollers.ToArray())
                    {
                        if (!desired.TryGetValue(key, out PollingRegistration? registration) || !SamePollingConfiguration(poller.Registration, registration))
                            await poller.Cancellation.CancelAsync().ConfigureAwait(false);
                        if (!poller.Completion.IsCompleted) continue;
                        await ObservePollerAsync(poller).ConfigureAwait(false);
                        poller.Cancellation.Dispose();
                        pollers.Remove(key);
                        pollingIssues.TryRemove(key, out _);
                    }

                    foreach ((string key, PollingRegistration registration) in desired)
                    {
                        // A cancelled loop retains ownership until it has actually exited.
                        if (pollers.ContainsKey(key)) continue;
                        CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        pollers.Add(key, new BotPoller(registration, cancellation, RunBotPollingAsync(registration, cancellation.Token)));
                    }
                }

                await Task.Delay(rosterRefreshInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            foreach (BotPoller poller in pollers.Values) await poller.Cancellation.CancelAsync().ConfigureAwait(false);
            foreach (BotVerification verification in verifications.Values.Concat(retiredVerifications)) await verification.Cancellation.CancelAsync().ConfigureAwait(false);
            foreach (BotPoller poller in pollers.Values)
            {
                await ObservePollerAsync(poller).ConfigureAwait(false);
                poller.Cancellation.Dispose();
            }
            foreach (BotVerification verification in verifications.Values.Concat(retiredVerifications)) await DisposeVerificationAsync(verification).ConfigureAwait(false);
        }
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Verification cancellation sources transfer to the active or retired collections and are awaited and disposed by the supervisor.")]
    private PollingRegistration[] GetVerifiedRegistrations(Dictionary<string, BotVerification> verifications, List<BotVerification> retired, CancellationToken stoppingToken)
    {
        PollingRegistration[] registrations = [.. GetCachedRegistrations()];
        foreach ((string id, BotVerification verification) in verifications.ToArray())
        {
            PollingRegistration? current = registrations.FirstOrDefault(item => string.Equals(item.RegistrationId, id, StringComparison.Ordinal));
            bool expiredFailure = verification.Completion.IsCompletedSuccessfully && verification.Completion.Result.BotId is null && verification.RetryAt <= DateTimeOffset.UtcNow;
            if (current is not null && string.Equals(current.BotToken, verification.Registration.BotToken, StringComparison.Ordinal) && !expiredFailure) continue;
            verification.Cancellation.Cancel();
            retired.Add(verification);
            verifications.Remove(id);
            pollingIssues.TryRemove("verification:" + id, out _);
        }

        List<PollingRegistration> verified = [];
        foreach (PollingRegistration registration in registrations)
        {
            if (!verifications.TryGetValue(registration.RegistrationId, out BotVerification? verification))
            {
                CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                verification = new BotVerification(registration, cancellation, VerifyBotIdentityAsync(registration, cancellation.Token));
                verifications.Add(registration.RegistrationId, verification);
            }
            if (verification.Completion.IsCompletedSuccessfully)
            {
                (string? botId, int retrySeconds) = verification.Completion.Result;
                if (botId is not null)
                {
                    pollingIssues.TryRemove("verification:" + registration.RegistrationId, out _);
                    verified.Add(registration with { VerifiedBotId = botId });
                    continue;
                }
                if (verification.RetryAt == DateTimeOffset.MaxValue) verification.RetryAt = DateTimeOffset.UtcNow.AddSeconds(retrySeconds);
            }
            pollingIssues["verification:" + registration.RegistrationId] = "bot_identity_not_verified";
        }
        return [.. verified];
    }

    private static async Task DisposeVerificationAsync(BotVerification verification)
    {
        await verification.Cancellation.CancelAsync().ConfigureAwait(false);
        try { await verification.Completion.ConfigureAwait(false); }
        catch (OperationCanceledException) when (verification.Cancellation.IsCancellationRequested) { }
        finally { verification.Cancellation.Dispose(); }
    }

    private static bool SamePollingConfiguration(PollingRegistration left, PollingRegistration right)
        => string.Equals(left.RegistrationId, right.RegistrationId, StringComparison.Ordinal)
            && string.Equals(left.OwnerUserId, right.OwnerUserId, StringComparison.Ordinal)
            && string.Equals(left.BotToken, right.BotToken, StringComparison.Ordinal)
            && left.PollTimeoutSeconds == right.PollTimeoutSeconds
            && string.Equals(GetStringSetting(left.Settings, "channel_device_id"), GetStringSetting(right.Settings, "channel_device_id"), StringComparison.Ordinal);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A faulted bot task is observed, reported and restarted independently of other bots.")]
    private async Task ObservePollerAsync(BotPoller poller)
    {
        try { await poller.Completion.ConfigureAwait(false); }
        catch (OperationCanceledException) when (poller.Cancellation.IsCancellationRequested) { }
        catch (Exception ex) { LogPollingRosterFailure(logger, ex.GetType().Name); }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Provider failures, database contention and resource limits must back off this bot rather than terminating all polling.")]
    private async Task RunBotPollingAsync(PollingRegistration registration, CancellationToken cancellationToken)
    {
        await Task.Yield();
        LogBotPollerState(logger, "started", registration.RegistrationId, registration.BotIdentity);
        int failures = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int retrySeconds;
                string reason;
                try
                {
                    retrySeconds = await PollRegistrationAsync(registration, cancellationToken).ConfigureAwait(false);
                    reason = "provider_rejected_poll";
                    if (retrySeconds == 0)
                    {
                        failures = 0;
                        pollingIssues.TryRemove(registration.BotIdentity, out _);
                        await Task.Yield();
                        continue;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    failures = Math.Min(failures + 1, 5);
                    retrySeconds = ex is RpcException { StatusCode: StatusCode.ResourceExhausted } ? 60 : Math.Min(30, 1 << failures);
                    reason = ex.GetType().Name;
                }

                pollingIssues[registration.BotIdentity] = reason;
                double delayMs = retrySeconds * 1000d + RandomNumberGenerator.GetInt32(251);
                LogBotPollerBackoff(logger, registration.RegistrationId, reason, delayMs);
                await Task.Delay(TimeSpan.FromMilliseconds(delayMs), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            LogBotPollerState(logger, "stopped", registration.RegistrationId, registration.BotIdentity);
        }
    }
}
