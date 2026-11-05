using System.Net;
using System.Text.Json;

using Kautuka.Gateway.Domain.Connectors;
using Kautuka.Gateway.Infrastructure.Connectors;

namespace Kautuka.IntegrationTests;

public sealed class BrokerMailCoordinatorTests
{
    private static readonly string Id = new('A', 43);
    private static readonly string Token = "kmf1." + new string('x', 40);
    private static readonly string Next = "kmf1." + new string('y', 40);

    [Fact]
    public async Task NonceChallengeRetriesOnceWithFreshProofsAndIdenticalBody()
    {
        using var identities = new BrokerInstallationIdentityProvider(new IdentityStorage());
        using var handler = new NonceChallengeHandler();
        using var registration = new BrokerInstallationRegistration(identities, new HttpClient(handler, false));
        using var broker = new BrokerHandoffClient(identities, registration, new HttpClient(handler, false));
        bool active = await broker.IsMailActiveAsync(new Uri("https://auth.kautuka.io/"), "google", Id, 1, Id, ConnectorDpop.CreatePrivateJwk(), CancellationToken.None);
        Assert.True(active);
        Assert.Equal(2, handler.Calls.Count);
        Assert.Equal(handler.Calls[0].Body, handler.Calls[1].Body);
        Assert.NotEqual(handler.Calls[0].InstallationProof, handler.Calls[1].InstallationProof);
        Assert.NotEqual(handler.Calls[0].Dpop, handler.Calls[1].Dpop);
        using JsonDocument first = ReadJwtPayload(handler.Calls[0].Dpop);
        using JsonDocument second = ReadJwtPayload(handler.Calls[1].Dpop);
        Assert.False(first.RootElement.TryGetProperty("nonce", out _));
        Assert.Equal(Id, second.RootElement.GetProperty("nonce").GetString());
        Assert.NotEqual(first.RootElement.GetProperty("jti").GetString(), second.RootElement.GetProperty("jti").GetString());
    }

    [Fact]
    public async Task SuspendedInstallationDoesNotTriggerKeyRecoveryOrRepeatTheMailRequest()
    {
        using var identities = new BrokerInstallationIdentityProvider(new IdentityStorage());
        using var handler = new NonceChallengeHandler { Suspended = true };
        using var registration = new BrokerInstallationRegistration(identities, new HttpClient(handler, false));
        using var broker = new BrokerHandoffClient(identities, registration, new HttpClient(handler, false));
        BrokerInstallationIdentity before = await identities.GetOrCreateAsync(CancellationToken.None);
        await Assert.ThrowsAsync<BrokerInstallationSuspendedException>(() => broker.IsMailActiveAsync(
            new Uri("https://auth.kautuka.io/"), "google", Id, 1, Id, ConnectorDpop.CreatePrivateJwk(), CancellationToken.None));
        Assert.Single(handler.Calls);
        BrokerInstallationIdentity after = await identities.GetOrCreateAsync(CancellationToken.None);
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.PrivateJwk, after.PrivateJwk);
    }

    [Theory]
    [InlineData(true, false, 2)]
    [InlineData(false, true, 1)]
    public async Task NonceRetryIsBoundedAndRejectsMalformedChallenges(bool repeatChallenge, bool malformed, int expectedCalls)
    {
        using var identities = new BrokerInstallationIdentityProvider(new IdentityStorage());
        using var handler = new NonceChallengeHandler { RepeatChallenge = repeatChallenge, Malformed = malformed };
        using var registration = new BrokerInstallationRegistration(identities, new HttpClient(handler, false));
        using var broker = new BrokerHandoffClient(identities, registration, new HttpClient(handler, false));
        await Assert.ThrowsAsync<BrokerMailRequestException>(() => broker.IsMailActiveAsync(
            new Uri("https://auth.kautuka.io/"), "google", Id, 1, Id, ConnectorDpop.CreatePrivateJwk(), CancellationToken.None));
        Assert.Equal(expectedCalls, handler.Calls.Count);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task UnrecognizedNonceChallengeDoesNotRetry(bool wrongError, bool multipleNonces)
    {
        using var identities = new BrokerInstallationIdentityProvider(new IdentityStorage());
        using var handler = new NonceChallengeHandler { WrongError = wrongError, MultipleNonces = multipleNonces };
        using var registration = new BrokerInstallationRegistration(identities, new HttpClient(handler, false));
        using var broker = new BrokerHandoffClient(identities, registration, new HttpClient(handler, false));
        await Assert.ThrowsAsync<BrokerMailRequestException>(() => broker.IsMailActiveAsync(
            new Uri("https://auth.kautuka.io/"), "google", Id, 1, Id, ConnectorDpop.CreatePrivateJwk(), CancellationToken.None));
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task NonceNegotiationRemainsBoundedWhenInstallationKeyRotates()
    {
        var identityStore = new IdentityStorage();
        using var identities = new BrokerInstallationIdentityProvider(identityStore);
        using var handler = new RotateDuringNonceHandler(identityStore);
        using var registration = new BrokerInstallationRegistration(identities, new HttpClient(handler, false));
        using var broker = new BrokerHandoffClient(identities, registration, new HttpClient(handler, false));
        Assert.True(await broker.IsMailActiveAsync(new Uri("https://auth.kautuka.io/"), "google", Id, 1, Id, ConnectorDpop.CreatePrivateJwk(), CancellationToken.None));
        Assert.Equal(4, handler.Calls.Count);
        Assert.All(handler.Calls, call => Assert.Equal(handler.Calls[0].Body, call.Body));
        Assert.Equal(4, handler.Calls.Select(call => call.Proof).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(4, handler.Calls.Select(call => call.Dpop).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, handler.Registrations);
    }

    private sealed class RotateDuringNonceHandler(IdentityStorage storage) : HttpMessageHandler
    {
        public List<(string Body, string Proof, string Dpop)> Calls { get; } = [];
        public int Registrations { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/installation/register") { Registrations++; return new HttpResponseMessage(HttpStatusCode.NoContent); }
            Calls.Add((await request.Content!.ReadAsStringAsync(ct), request.Headers.GetValues("Kautuka-Installation-Proof").Single(), request.Headers.GetValues("DPoP").Single()));
            if (Calls.Count is 1 or 3)
            {
                var challenge = new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"use_dpop_nonce\"}") };
                challenge.Headers.TryAddWithoutValidation("DPoP-Nonce", Id);
                return challenge;
            }
            if (Calls.Count == 2)
            {
                string current = (await storage.ReadAsync(ct))!;
                await storage.WriteAsync(current[..43] + ":" + ConnectorDpop.CreatePrivateJwk(), ct);
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"active\":true}") };
        }
    }

    private static JsonDocument ReadJwtPayload(string jwt)
    {
        string part = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        return JsonDocument.Parse(Convert.FromBase64String(part.PadRight((part.Length + 3) / 4 * 4, '=')));
    }

    private sealed class NonceChallengeHandler : HttpMessageHandler
    {
        public bool Suspended { get; init; }
        public bool RepeatChallenge { get; init; }
        public bool Malformed { get; init; }
        public bool WrongError { get; init; }
        public bool MultipleNonces { get; init; }
        public List<(string Body, string InstallationProof, string Dpop)> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/installation/register") return new HttpResponseMessage(HttpStatusCode.NoContent);
            Calls.Add((await request.Content!.ReadAsStringAsync(cancellationToken), request.Headers.GetValues("Kautuka-Installation-Proof").Single(), request.Headers.GetValues("DPoP").Single()));
            if (Suspended)
            {
                var suspended = new HttpResponseMessage(HttpStatusCode.Forbidden);
                suspended.Headers.TryAddWithoutValidation("Kautuka-Installation-Error", "installation_suspended");
                return suspended;
            }
            if (Calls.Count == 1 || RepeatChallenge)
            {
                var challenge = new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(WrongError ? "{\"error\":\"other\"}" : "{\"error\":\"use_dpop_nonce\"}") };
                challenge.Headers.TryAddWithoutValidation("DPoP-Nonce", Malformed ? "invalid" : Id);
                if (MultipleNonces) challenge.Headers.TryAddWithoutValidation("DPoP-Nonce", Id);
                return challenge;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"active\":true}") };
        }
    }

    [Fact]
    public async Task PartialRefreshMarksConnectionForReconnectAndStillAllowsRemoval()
    {
        using var store = new BrokerMailCredentialStore(new MemoryStorage());
        using var identities = new BrokerInstallationIdentityProvider(new IdentityStorage());
        using var handler = new BrokerHandler { InsufficientRefreshScope = true };
        using var registration = new BrokerInstallationRegistration(identities, new HttpClient(handler, false));
        using var broker = new BrokerHandoffClient(identities, registration, new HttpClient(handler, false));
        using var coordinator = new BrokerMailCoordinator(store, broker);
        var origin = new Uri("https://auth.kautuka.io/");
        var credential = new BrokerMailCredential("google", Id, Id, 1, Token, ConnectorDpop.CreatePrivateJwk(), "https://auth.kautuka.io");
        await coordinator.ActivateAsync(origin, "u", "c", new(credential, Id), CancellationToken.None);
        BrokerMailRequestException failure = await Assert.ThrowsAsync<BrokerMailRequestException>(() => coordinator.RefreshAsync(origin, "u", "c", CancellationToken.None));
        Assert.Equal("insufficient_scope", failure.MailError);
        BrokerMailStoredState state = await store.ReadAsync("u", "c", CancellationToken.None);
        Assert.True(state.RequiresReconnect);
        Assert.Null(state.PendingRefresh);
        Assert.Equal(credential, state.Active);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RefreshAsync(origin, "u", "c", CancellationToken.None));
        Assert.Equal(1, handler.RefreshCalls);
        await coordinator.DisconnectAsync(origin, "u", "c", CancellationToken.None);
        Assert.Null((await store.ReadAsync("u", "c", CancellationToken.None)).Active);
        Assert.Equal(1, handler.DisconnectCalls);
    }

    [Fact]
    public async Task BrokerSuspensionIsPersistedWithoutDiscardingMailCredentials()
    {
        var storage = new MemoryStorage();
        using var store = new BrokerMailCredentialStore(storage);
        using var identities = new BrokerInstallationIdentityProvider(new IdentityStorage());
        using var handler = new BrokerHandler { SuspendRefresh = true };
        using var registration = new BrokerInstallationRegistration(identities, new HttpClient(handler, false));
        using var broker = new BrokerHandoffClient(identities, registration, new HttpClient(handler, false));
        using var coordinator = new BrokerMailCoordinator(store, broker);
        var origin = new Uri("https://auth.kautuka.io/");
        var credential = new BrokerMailCredential("google", Id, Id, 1, Token, ConnectorDpop.CreatePrivateJwk(), "https://auth.kautuka.io");
        await coordinator.ActivateAsync(origin, "u", "c", new(credential, Id), CancellationToken.None);
        await Assert.ThrowsAsync<BrokerInstallationSuspendedException>(() => coordinator.RefreshAsync(origin, "u", "c", CancellationToken.None));
        BrokerMailStoredState saved = await store.ReadAsync("u", "c", CancellationToken.None);
        Assert.Equal("https://auth.kautuka.io", saved.SuspendedBrokerOrigin);
        Assert.Equal(credential, saved.Active);
        Assert.NotNull(saved.PendingRefresh);
        using var restarted = new BrokerMailCredentialStore(storage);
        Assert.Equal("https://auth.kautuka.io", (await restarted.ReadAsync("u", "c", CancellationToken.None)).SuspendedBrokerOrigin);
        using var restartedCoordinator = new BrokerMailCoordinator(restarted, broker);
        await Assert.ThrowsAsync<BrokerInstallationSuspendedException>(() => restartedCoordinator.RefreshAsync(origin, "u", "c", CancellationToken.None));
        Assert.Equal(1, handler.RefreshCalls);
    }

    [Fact]
    public async Task EmptySuspendedStateDoesNotBlockANewBrokerAfterRestart()
    {
        var storage = new MemoryStorage();
        using var first = new BrokerMailCredentialStore(storage);
        await first.MarkBrokerSuspendedAsync("u", "c", "https://old.example", CancellationToken.None);
        Assert.Null((await first.ReadAsync("u", "c", CancellationToken.None)).SuspendedBrokerOrigin);
        using var reopened = new BrokerMailCredentialStore(storage);
        using var identities = new BrokerInstallationIdentityProvider(new IdentityStorage());
        using var handler = new BrokerHandler();
        using var registration = new BrokerInstallationRegistration(identities, new HttpClient(handler, false));
        using var broker = new BrokerHandoffClient(identities, registration, new HttpClient(handler, false));
        using var coordinator = new BrokerMailCoordinator(reopened, broker);
        var credential = new BrokerMailCredential("google", Id, Id, 1, Token, ConnectorDpop.CreatePrivateJwk(), "https://new.example");
        Assert.Equal(credential, await coordinator.ActivateAsync(new Uri("https://new.example/"), "u", "c", new(credential, Id), CancellationToken.None));
    }

    [Fact]
    public async Task AccountLimitDenialRetainsTerminalMarkerAndLongRetryDelay()
    {
        using var identities = new BrokerInstallationIdentityProvider(new IdentityStorage());
        using var handler = new BrokerHandler { DenyNewAccount = true };
        using var registration = new BrokerInstallationRegistration(identities, new HttpClient(handler, false));
        using var broker = new BrokerHandoffClient(identities, registration, new HttpClient(handler, false));
        BrokerMailRequestException error = await Assert.ThrowsAsync<BrokerMailRequestException>(() =>
            broker.ExchangeMailAsync(new Uri("https://auth.kautuka.io/"), "google", Id, Id, ConnectorDpop.CreatePrivateJwk(), CancellationToken.None));
        Assert.Equal("new_account_limit", error.AdmissionDenial);
        Assert.Equal(TimeSpan.FromSeconds(86400), error.RetryAfter);
    }

    [Fact]
    public async Task LostActivationAcknowledgementRecoversFromBrokerStatus()
    {
        var storage = new MemoryStorage();
        using var store = new BrokerMailCredentialStore(storage);
        using var identities = new BrokerInstallationIdentityProvider(new IdentityStorage());
        using var handler = new BrokerHandler { LoseActivationAck = true };
        using var registration = new BrokerInstallationRegistration(identities, new HttpClient(handler, false));
        using var broker = new BrokerHandoffClient(identities, registration, new HttpClient(handler, false));
        using var coordinator = new BrokerMailCoordinator(store, broker);
        var origin = new Uri("https://auth.kautuka.io/");
        var credential = new BrokerMailCredential("google", Id, Id, 1, Token, ConnectorDpop.CreatePrivateJwk(), "https://auth.kautuka.io");
        await Assert.ThrowsAsync<HttpRequestException>(() => coordinator.ActivateAsync(origin, "u", "c", new(credential, Id), CancellationToken.None));
        Assert.Null((await store.ReadAsync("u", "c", CancellationToken.None)).Active);
        Assert.Equal(credential, await coordinator.RecoverActivationAsync(origin, "u", "c", CancellationToken.None));
        Assert.Equal(1, handler.ActivationAcks);
    }

    [Fact]
    public async Task LostRefreshAcknowledgementKeepsReplacementAndRecoversBeforeNextCall()
    {
        var storage = new MemoryStorage();
        using var store = new BrokerMailCredentialStore(storage);
        using var identities = new BrokerInstallationIdentityProvider(new IdentityStorage());
        using var handler = new BrokerHandler { LoseRefreshAck = true };
        using var registration = new BrokerInstallationRegistration(identities, new HttpClient(handler, false));
        using var broker = new BrokerHandoffClient(identities, registration, new HttpClient(handler, false));
        using var coordinator = new BrokerMailCoordinator(store, broker);
        var origin = new Uri("https://auth.kautuka.io/");
        var credential = new BrokerMailCredential("google", Id, Id, 1, Token, ConnectorDpop.CreatePrivateJwk(), "https://auth.kautuka.io");
        await coordinator.ActivateAsync(origin, "u", "c", new(credential, Id), CancellationToken.None);
        await Assert.ThrowsAsync<HttpRequestException>(() => coordinator.RefreshAsync(origin, "u", "c", CancellationToken.None));
        BrokerMailStoredState saved = await store.ReadAsync("u", "c", CancellationToken.None);
        Assert.Equal(Next, saved.Active!.RefreshToken);
        Assert.NotNull(saved.PendingRefresh!.ReplacementRefreshToken);
        BrokerMailRefreshResult? result = await coordinator.RefreshAsync(origin, "u", "c", CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal(2, handler.RefreshCalls);
        Assert.Null((await store.ReadAsync("u", "c", CancellationToken.None)).PendingRefresh);
        BrokerMailCredential retried = await coordinator.ActivateAsync(origin, "u", "c", new(credential, Id), CancellationToken.None);
        Assert.Equal(Next, retried.RefreshToken);
        Assert.Equal(Next, (await store.ReadAsync("u", "c", CancellationToken.None)).Active!.RefreshToken);
    }

    [Fact]
    public async Task TerminalActivationAndRefreshFailuresPermitSafeReconnect()
    {
        using var store = new BrokerMailCredentialStore(new MemoryStorage());
        using var identities = new BrokerInstallationIdentityProvider(new IdentityStorage());
        using var handler = new BrokerHandler { ActivationConflict = true };
        using var registration = new BrokerInstallationRegistration(identities, new HttpClient(handler, false));
        using var broker = new BrokerHandoffClient(identities, registration, new HttpClient(handler, false));
        using var coordinator = new BrokerMailCoordinator(store, broker);
        var origin = new Uri("https://auth.kautuka.io/");
        var credential = new BrokerMailCredential("google", Id, Id, 1, Token, ConnectorDpop.CreatePrivateJwk(), "https://auth.kautuka.io");
        await Assert.ThrowsAsync<BrokerMailRequestException>(() => coordinator.ActivateAsync(origin, "u", "c", new(credential, Id), CancellationToken.None));
        Assert.Null((await store.ReadAsync("u", "c", CancellationToken.None)).PendingActivation);
        handler.ActivationConflict = false;
        await coordinator.ActivateAsync(origin, "u", "c", new(credential, Id), CancellationToken.None);
        handler.RefreshGone = true;
        await Assert.ThrowsAsync<BrokerMailRequestException>(() => coordinator.RefreshAsync(origin, "u", "c", CancellationToken.None));
        BrokerMailStoredState state = await store.ReadAsync("u", "c", CancellationToken.None);
        Assert.True(state.RequiresReconnect);
        Assert.Null(state.PendingRefresh);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RefreshAsync(origin, "u", "c", CancellationToken.None));
    }

    [Fact]
    public async Task DisconnectRetainsLocalCredentialsUntilBrokerConfirmsAndRetriesSafely()
    {
        using var store = new BrokerMailCredentialStore(new MemoryStorage());
        using var identities = new BrokerInstallationIdentityProvider(new IdentityStorage());
        using var handler = new BrokerHandler { LoseDisconnect = true };
        using var registration = new BrokerInstallationRegistration(identities, new HttpClient(handler, false));
        using var broker = new BrokerHandoffClient(identities, registration, new HttpClient(handler, false));
        using var coordinator = new BrokerMailCoordinator(store, broker);
        var origin = new Uri("https://auth.kautuka.io/");
        var credential = new BrokerMailCredential("google", Id, Id, 1, Token, ConnectorDpop.CreatePrivateJwk(), "https://auth.kautuka.io");
        await coordinator.ActivateAsync(origin, "u", "c", new(credential, Id), CancellationToken.None);
        await Assert.ThrowsAsync<HttpRequestException>(() => coordinator.DisconnectAsync(origin, "u", "c", CancellationToken.None));
        Assert.NotNull((await store.ReadAsync("u", "c", CancellationToken.None)).Active);
        handler.DisconnectMarkerExpired = true;
        await coordinator.DisconnectAsync(origin, "u", "c", CancellationToken.None);
        Assert.Null((await store.ReadAsync("u", "c", CancellationToken.None)).Active);
    }

    [Fact]
    public async Task DisconnectReconcilesAnAlreadyAcknowledgedReplacement()
    {
        using var store = new BrokerMailCredentialStore(new MemoryStorage());
        using var identities = new BrokerInstallationIdentityProvider(new IdentityStorage());
        using var handler = new BrokerHandler();
        using var registration = new BrokerInstallationRegistration(identities, new HttpClient(handler, false));
        using var broker = new BrokerHandoffClient(identities, registration, new HttpClient(handler, false));
        using var coordinator = new BrokerMailCoordinator(store, broker);
        var origin = new Uri("https://auth.kautuka.io/");
        var original = new BrokerMailCredential("google", Id, Id, 1, Token, ConnectorDpop.CreatePrivateJwk(), "https://auth.kautuka.io");
        await coordinator.ActivateAsync(origin, "u", "c", new(original, Id), CancellationToken.None);
        var replacement = original with { FlowId = new string('B', 43), Generation = 2, RefreshToken = Next };
        await store.StageActivationAsync("u", "c", new(replacement, Id), CancellationToken.None);
        await coordinator.DisconnectAsync(origin, "u", "c", CancellationToken.None);
        BrokerMailStoredState state = await store.ReadAsync("u", "c", CancellationToken.None);
        Assert.Null(state.Active);
        Assert.Null(state.PendingActivation);
    }

    [Fact]
    public async Task RemoveRecoversInactiveReplacementAfterLostCompensation()
    {
        using var store = new BrokerMailCredentialStore(new MemoryStorage());
        using var identities = new BrokerInstallationIdentityProvider(new IdentityStorage());
        using var handler = new BrokerHandler();
        using var registration = new BrokerInstallationRegistration(identities, new HttpClient(handler, false));
        using var broker = new BrokerHandoffClient(identities, registration, new HttpClient(handler, false));
        using var coordinator = new BrokerMailCoordinator(store, broker);
        var origin = new Uri("https://auth.kautuka.io/");
        var original = new BrokerMailCredential("google", Id, Id, 1, Token, ConnectorDpop.CreatePrivateJwk(), "https://auth.kautuka.io");
        await coordinator.ActivateAsync(origin, "u", "c", new(original, Id), CancellationToken.None);
        var replacement = original with { FlowId = new string('B', 43), Generation = 2, RefreshToken = Next };
        await store.StageActivationAsync("u", "c", new(replacement, Id), CancellationToken.None);
        handler.ForceInactive = true;
        handler.AlwaysCancelConflict = true;
        handler.DisconnectMarkerExpired = true;
        await coordinator.DisconnectAsync(origin, "u", "c", CancellationToken.None);
        var state = await store.ReadAsync("u", "c", CancellationToken.None);
        Assert.Null(state.Active);
        Assert.Null(state.PendingActivation);
    }

    [Fact]
    public async Task RevokedBrowserAfterExchangeCannotActivateMail()
    {
        using var store = new BrokerMailCredentialStore(new MemoryStorage());
        using var identities = new BrokerInstallationIdentityProvider(new IdentityStorage());
        using var handler = new BrokerHandler();
        using var registration = new BrokerInstallationRegistration(identities, new HttpClient(handler, false));
        using var broker = new BrokerHandoffClient(identities, registration, new HttpClient(handler, false));
        using var coordinator = new BrokerMailCoordinator(store, broker);
        var session = new BoundMailSession
        {
            UserId = "u",
            ConnectorId = "c",
            CatalogConnectorId = "c",
            Provider = "google",
            FlowId = Id,
            ConnectionId = Id,
            Verifier = Id,
            DpopPrivateJwk = ConnectorDpop.CreatePrivateJwk(),
            Origin = "http://localhost:20229",
            BrokerOrigin = "https://auth.kautuka.io",
            ExpectedGeneration = 0,
            CookieHash = Id,
            TabHash = Id,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
        };
        int checks = 0;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => coordinator.CompleteBoundAsync(session, () => ++checks == 1, CancellationToken.None));
        Assert.Null((await store.ReadAsync("u", "c", CancellationToken.None)).PendingActivation);
        Assert.Equal(0, handler.ActivationAcks);
    }

    [Fact]
    public async Task RevocationDuringPromotionRemovesTheNewlyActivatedConnection()
    {
        using var store = new BrokerMailCredentialStore(new MemoryStorage());
        using var identities = new BrokerInstallationIdentityProvider(new IdentityStorage());
        using var handler = new BrokerHandler();
        using var registration = new BrokerInstallationRegistration(identities, new HttpClient(handler, false));
        using var broker = new BrokerHandoffClient(identities, registration, new HttpClient(handler, false));
        using var coordinator = new BrokerMailCoordinator(store, broker);
        var session = new BoundMailSession
        {
            UserId = "u",
            ConnectorId = "c",
            CatalogConnectorId = "c",
            Provider = "google",
            FlowId = Id,
            ConnectionId = Id,
            Verifier = Id,
            DpopPrivateJwk = ConnectorDpop.CreatePrivateJwk(),
            Origin = "http://localhost:20229",
            BrokerOrigin = "https://auth.kautuka.io",
            ExpectedGeneration = 0,
            CookieHash = Id,
            TabHash = Id,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
        };
        int checks = 0;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => coordinator.CompleteBoundAsync(session, () => ++checks <= 5, CancellationToken.None));
        Assert.Null((await store.ReadAsync("u", "c", CancellationToken.None)).Active);
        Assert.Equal(1, handler.ActivationAcks);
    }

    [Fact]
    public async Task DisconnectRecoversAnAcknowledgedFlowWhenItsBrowserSessionIsStillPending()
    {
        using var store = new BrokerMailCredentialStore(new MemoryStorage());
        using var identities = new BrokerInstallationIdentityProvider(new IdentityStorage());
        using var handler = new BrokerHandler { LoseActivationAck = true };
        using var registration = new BrokerInstallationRegistration(identities, new HttpClient(handler, false));
        using var broker = new BrokerHandoffClient(identities, registration, new HttpClient(handler, false));
        var sessions = new BoundMailSessionStore();
        using var coordinator = new BrokerMailCoordinator(store, broker, sessions);
        var origin = new Uri("https://auth.kautuka.io/");
        string key = ConnectorDpop.CreatePrivateJwk();
        var credential = new BrokerMailCredential("google", Id, Id, 1, Token, key, "https://auth.kautuka.io");
        var session = new BoundMailSession
        {
            UserId = "u",
            ConnectorId = "c",
            CatalogConnectorId = "c",
            Provider = "google",
            FlowId = Id,
            ConnectionId = Id,
            Verifier = Id,
            DpopPrivateJwk = key,
            Origin = "http://localhost:20229",
            BrokerOrigin = "https://auth.kautuka.io",
            ExpectedGeneration = 0,
            CookieHash = Id,
            TabHash = Id,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
        };
        Assert.True(sessions.TrySave(Id, session, DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<HttpRequestException>(() => coordinator.ActivateAsync(origin, "u", "c", new(credential, Id), CancellationToken.None));
        await coordinator.DisconnectAsync(origin, "u", "c", CancellationToken.None);
        Assert.Null((await store.ReadAsync("u", "c", CancellationToken.None)).Active);
        Assert.Null((await store.ReadAsync("u", "c", CancellationToken.None)).PendingActivation);
    }

    [Fact]
    public async Task CancellingAnOldFlowDoesNotDisconnectItsReplacement()
    {
        using var store = new BrokerMailCredentialStore(new MemoryStorage());
        using var identities = new BrokerInstallationIdentityProvider(new IdentityStorage());
        using var handler = new BrokerHandler { AllowUnrelatedCancel = true };
        using var registration = new BrokerInstallationRegistration(identities, new HttpClient(handler, false));
        using var broker = new BrokerHandoffClient(identities, registration, new HttpClient(handler, false));
        using var coordinator = new BrokerMailCoordinator(store, broker);
        var origin = new Uri("https://auth.kautuka.io/");
        var original = new BrokerMailCredential("google", Id, Id, 1, Token, ConnectorDpop.CreatePrivateJwk(), "https://auth.kautuka.io");
        await coordinator.ActivateAsync(origin, "u", "c", new(original, Id), CancellationToken.None);
        var replacement = original with { FlowId = new string('B', 43), Generation = 2, RefreshToken = Next };
        await store.StageActivationAsync("u", "c", new(replacement, Id), CancellationToken.None);
        await store.PromoteActivationAsync("u", "c", replacement.FlowId, CancellationToken.None);
        var oldSession = new BoundMailSession
        {
            UserId = "u",
            ConnectorId = "c",
            CatalogConnectorId = "c",
            Provider = "google",
            FlowId = Id,
            ConnectionId = Id,
            Verifier = Id,
            DpopPrivateJwk = original.DpopPrivateJwk,
            Origin = "http://localhost:20229",
            BrokerOrigin = "https://auth.kautuka.io",
            ExpectedGeneration = 0,
            CookieHash = Id,
            TabHash = Id,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
        };
        await coordinator.CancelFlowAsync(oldSession, CancellationToken.None);
        Assert.Equal(0, handler.DisconnectCalls);
        Assert.Equal(1, handler.CancelCalls);
        Assert.Equal(replacement, (await store.ReadAsync("u", "c", CancellationToken.None)).Active);
    }

    [Fact]
    public async Task FailedDisconnectBlocksRefreshAcrossRestart()
    {
        var storage = new MemoryStorage();
        using var store = new BrokerMailCredentialStore(storage);
        using var identities = new BrokerInstallationIdentityProvider(new IdentityStorage());
        using var handler = new BrokerHandler { FailDisconnectBeforeMutation = true };
        using var registration = new BrokerInstallationRegistration(identities, new HttpClient(handler, false));
        using var broker = new BrokerHandoffClient(identities, registration, new HttpClient(handler, false));
        using var coordinator = new BrokerMailCoordinator(store, broker);
        var origin = new Uri("https://auth.kautuka.io/");
        var credential = new BrokerMailCredential("google", Id, Id, 1, Token, ConnectorDpop.CreatePrivateJwk(), "https://auth.kautuka.io");
        await coordinator.ActivateAsync(origin, "u", "c", new(credential, Id), CancellationToken.None);
        await Assert.ThrowsAsync<HttpRequestException>(() => coordinator.DisconnectAsync(origin, "u", "c", CancellationToken.None));
        using var restartedStore = new BrokerMailCredentialStore(storage);
        using var restarted = new BrokerMailCoordinator(restartedStore, broker);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.RefreshAsync(origin, "u", "c", CancellationToken.None));
        Assert.Equal(0, handler.RefreshCalls);
        Assert.True((await restartedStore.ReadAsync("u", "c", CancellationToken.None)).DisconnectPending);
    }

    [Fact]
    public async Task ExistingMailCredentialCannotBeSentToAnotherBroker()
    {
        using var store = new BrokerMailCredentialStore(new MemoryStorage());
        using var identities = new BrokerInstallationIdentityProvider(new IdentityStorage());
        using var handler = new BrokerHandler();
        using var registration = new BrokerInstallationRegistration(identities, new HttpClient(handler, false));
        using var broker = new BrokerHandoffClient(identities, registration, new HttpClient(handler, false));
        using var coordinator = new BrokerMailCoordinator(store, broker);
        var original = new BrokerMailCredential("google", Id, Id, 1, Token, ConnectorDpop.CreatePrivateJwk(), "https://auth.kautuka.io");
        await coordinator.ActivateAsync(new Uri("https://auth.kautuka.io/"), "u", "c", new(original, Id), CancellationToken.None);
        var changed = new Uri("https://other-broker.example/");
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RefreshAsync(changed, "u", "c", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.DisconnectAsync(changed, "u", "c", CancellationToken.None));
        Assert.Equal(0, handler.RefreshCalls);
        Assert.Equal(0, handler.DisconnectCalls);
        Assert.False((await store.ReadAsync("u", "c", CancellationToken.None)).DisconnectPending);
    }

    [Fact]
    public async Task PendingMailOperationsAndCancellationRejectAnotherBrokerBeforeAnyRequest()
    {
        using var store = new BrokerMailCredentialStore(new MemoryStorage());
        using var identities = new BrokerInstallationIdentityProvider(new IdentityStorage());
        using var handler = new BrokerHandler();
        using var registration = new BrokerInstallationRegistration(identities, new HttpClient(handler, false));
        using var broker = new BrokerHandoffClient(identities, registration, new HttpClient(handler, false));
        using var coordinator = new BrokerMailCoordinator(store, broker);
        var original = new BrokerMailCredential("google", Id, Id, 1, Token, ConnectorDpop.CreatePrivateJwk(), "https://auth.kautuka.io");
        await store.StageActivationAsync("u", "c", new(original, Id), CancellationToken.None);
        var changed = new Uri("https://other-broker.example/");
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RecoverActivationAsync(changed, "u", "c", CancellationToken.None));
        await store.PromoteActivationAsync("u", "c", Id, CancellationToken.None);
        await store.StageRefreshAsync("u", "c", new string('B', 43), CancellationToken.None);
        await store.SaveRefreshResultAsync("u", "c", new string('B', 43), Next, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RefreshAsync(changed, "u", "c", CancellationToken.None));
        var session = new BoundMailSession
        {
            UserId = "u",
            ConnectorId = "c",
            CatalogConnectorId = "c",
            Provider = "google",
            FlowId = Id,
            ConnectionId = Id,
            Verifier = Id,
            DpopPrivateJwk = original.DpopPrivateJwk,
            Origin = "http://localhost:20229",
            BrokerOrigin = "https://other-broker.example",
            ExpectedGeneration = 0,
            CookieHash = Id,
            TabHash = Id,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CancelFlowAsync(session, CancellationToken.None));
        Assert.Equal(0, handler.TotalCalls);
    }

    [Fact]
    public async Task CancellingUnstagedReplacementCancelsItsBrokerFlowAndPreservesOldConnection()
    {
        using var store = new BrokerMailCredentialStore(new MemoryStorage());
        using var identities = new BrokerInstallationIdentityProvider(new IdentityStorage());
        using var handler = new BrokerHandler { AllowUnrelatedCancel = true };
        using var registration = new BrokerInstallationRegistration(identities, new HttpClient(handler, false));
        using var broker = new BrokerHandoffClient(identities, registration, new HttpClient(handler, false));
        using var coordinator = new BrokerMailCoordinator(store, broker);
        var original = new BrokerMailCredential("google", Id, Id, 1, Token, ConnectorDpop.CreatePrivateJwk(), "https://auth.kautuka.io");
        await coordinator.ActivateAsync(new Uri("https://auth.kautuka.io/"), "u", "c", new(original, Id), CancellationToken.None);
        var replacement = new BoundMailSession
        {
            UserId = "u",
            ConnectorId = "c",
            CatalogConnectorId = "c",
            Provider = "google",
            FlowId = new string('B', 43),
            ConnectionId = Id,
            Verifier = Id,
            DpopPrivateJwk = original.DpopPrivateJwk,
            Origin = "http://localhost:20229",
            BrokerOrigin = "https://auth.kautuka.io",
            ExpectedGeneration = 1,
            CookieHash = Id,
            TabHash = Id,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
        };
        await coordinator.CancelFlowAsync(replacement, CancellationToken.None);
        Assert.Equal(1, handler.CancelCalls);
        Assert.Equal(0, handler.DisconnectCalls);
        Assert.Equal(original, (await store.ReadAsync("u", "c", CancellationToken.None)).Active);
    }

    [Fact]
    public async Task CancelRecoversLostDisconnectAfterBrokerRetryMarkerExpires()
    {
        using var store = new BrokerMailCredentialStore(new MemoryStorage());
        using var identities = new BrokerInstallationIdentityProvider(new IdentityStorage());
        using var handler = new BrokerHandler();
        using var registration = new BrokerInstallationRegistration(identities, new HttpClient(handler, false));
        using var broker = new BrokerHandoffClient(identities, registration, new HttpClient(handler, false));
        using var coordinator = new BrokerMailCoordinator(store, broker);
        var credential = new BrokerMailCredential("google", Id, Id, 1, Token, ConnectorDpop.CreatePrivateJwk(), "https://auth.kautuka.io");
        await coordinator.ActivateAsync(new Uri("https://auth.kautuka.io/"), "u", "c", new(credential, Id), CancellationToken.None);
        var session = new BoundMailSession
        {
            UserId = "u",
            ConnectorId = "c",
            CatalogConnectorId = "c",
            Provider = "google",
            FlowId = Id,
            ConnectionId = Id,
            Verifier = Id,
            DpopPrivateJwk = credential.DpopPrivateJwk,
            Origin = "http://localhost:20229",
            BrokerOrigin = "https://auth.kautuka.io",
            ExpectedGeneration = 0,
            CookieHash = Id,
            TabHash = Id,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
        };
        handler.LoseDisconnect = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => coordinator.CancelFlowAsync(session, CancellationToken.None));
        handler.DisconnectMarkerExpired = true;
        await coordinator.CancelFlowAsync(session, CancellationToken.None);
        Assert.Null((await store.ReadAsync("u", "c", CancellationToken.None)).Active);
    }

    [Fact]
    public async Task LostCompensatingDisconnectDoesNotRestoreInvalidatedOldCredential()
    {
        using var store = new BrokerMailCredentialStore(new MemoryStorage());
        using var identities = new BrokerInstallationIdentityProvider(new IdentityStorage());
        using var handler = new BrokerHandler();
        using var registration = new BrokerInstallationRegistration(identities, new HttpClient(handler, false));
        using var broker = new BrokerHandoffClient(identities, registration, new HttpClient(handler, false));
        using var coordinator = new BrokerMailCoordinator(store, broker);
        var origin = new Uri("https://auth.kautuka.io/");
        var original = new BrokerMailCredential("google", Id, Id, 1, Token, ConnectorDpop.CreatePrivateJwk(), "https://auth.kautuka.io");
        await coordinator.ActivateAsync(origin, "u", "c", new(original, Id), CancellationToken.None);
        var replacement = original with { FlowId = new string('B', 43), Generation = 2, RefreshToken = Next };
        await store.StageActivationAsync("u", "c", new(replacement, Id), CancellationToken.None);
        var session = new BoundMailSession
        {
            UserId = "u",
            ConnectorId = "c",
            CatalogConnectorId = "c",
            Provider = "google",
            FlowId = replacement.FlowId,
            ConnectionId = Id,
            Verifier = Id,
            DpopPrivateJwk = original.DpopPrivateJwk,
            Origin = "http://localhost:20229",
            BrokerOrigin = "https://auth.kautuka.io",
            ExpectedGeneration = 1,
            CookieHash = Id,
            TabHash = Id,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
        };
        handler.LoseDisconnect = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => coordinator.CancelFlowAsync(session, CancellationToken.None));
        await coordinator.CancelFlowAsync(session, CancellationToken.None);
        BrokerMailStoredState state = await store.ReadAsync("u", "c", CancellationToken.None);
        Assert.Null(state.PendingActivation);
        Assert.True(state.RequiresReconnect);
    }

    private sealed class BrokerHandler : HttpMessageHandler
    {
        private bool active;
        public bool LoseActivationAck { get; set; }
        public bool LoseRefreshAck { get; set; }
        public bool ActivationConflict { get; set; }
        public bool RefreshGone { get; set; }
        public bool InsufficientRefreshScope { get; set; }
        public bool SuspendRefresh { get; set; }
        public bool LoseDisconnect { get; set; }
        public bool DisconnectMarkerExpired { get; set; }
        public bool DenyNewAccount { get; set; }
        public bool AllowUnrelatedCancel { get; set; }
        public bool FailDisconnectBeforeMutation { get; set; }
        public bool ForceInactive { set { if (value) active = false; } }
        public bool AlwaysCancelConflict { get; set; }
        public int CancelCalls { get; private set; }
        public int TotalCalls { get; private set; }
        public int ActivationAcks { get; private set; }
        public int RefreshCalls { get; private set; }
        public int DisconnectCalls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            TotalCalls++;
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/installation/register": return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
                case "/installation/mail-status":
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(active ? "{\"active\":true}" : "{\"active\":false}") });
                case "/installation/mail-exchange":
                    if (DenyNewAccount)
                    {
                        var denied = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                        denied.Headers.TryAddWithoutValidation("Kautuka-Admission-Denied", "new_account_limit");
                        denied.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromDays(1));
                        return Task.FromResult(denied);
                    }
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"provider\":\"google\",\"refreshToken\":\"{Token}\",\"generation\":1}}") });
                case "/installation/mail-ack":
                    if (ActivationConflict) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));
                    active = true; ActivationAcks++;
                    if (LoseActivationAck) { LoseActivationAck = false; throw new HttpRequestException("response lost"); }
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
                case "/installation/mail-refresh":
                    if (RefreshGone) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Gone));
                    RefreshCalls++;
                    if (SuspendRefresh)
                    {
                        var suspended = new HttpResponseMessage(HttpStatusCode.Forbidden);
                        suspended.Headers.TryAddWithoutValidation("Kautuka-Installation-Error", "installation_suspended");
                        return Task.FromResult(suspended);
                    }
                    if (InsufficientRefreshScope)
                    {
                        var denied = new HttpResponseMessage(HttpStatusCode.Forbidden);
                        denied.Headers.TryAddWithoutValidation("Kautuka-Mail-Error", "insufficient_scope");
                        return Task.FromResult(denied);
                    }
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(new
                        { accessToken = "access", refreshToken = Next, tokenType = "Bearer", issuedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), expiresAt = (long?)null }))
                    });
                case "/installation/mail-refresh-ack":
                    if (LoseRefreshAck) { LoseRefreshAck = false; throw new HttpRequestException("response lost"); }
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
                case "/installation/mail-cancel":
                    CancelCalls++;
                    return Task.FromResult(new HttpResponseMessage(AlwaysCancelConflict || active && !AllowUnrelatedCancel ? HttpStatusCode.Conflict : HttpStatusCode.NoContent));
                case "/installation/mail-disconnect":
                    DisconnectCalls++;
                    if (FailDisconnectBeforeMutation) throw new HttpRequestException("broker unavailable");
                    if (!active && DisconnectMarkerExpired) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));
                    active = false;
                    if (LoseDisconnect) { LoseDisconnect = false; throw new HttpRequestException("response lost"); }
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
                default: throw new InvalidOperationException("Unexpected broker request.");
            }
        }
    }

    private sealed class MemoryStorage : IBrokerMailCredentialStorage
    {
        private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
        public Task<string?> ReadAsync(string key, CancellationToken ct) => Task.FromResult(values.GetValueOrDefault(key));
        public Task WriteAsync(string key, string value, CancellationToken ct) { values[key] = value; return Task.CompletedTask; }
    }

    private sealed class IdentityStorage : IBrokerInstallationIdentityStore
    {
        private string? value;
        public Task<string?> ReadAsync(CancellationToken ct) => Task.FromResult(value);
        public Task WriteAsync(string newValue, CancellationToken ct) { value = newValue; return Task.CompletedTask; }
    }
}
