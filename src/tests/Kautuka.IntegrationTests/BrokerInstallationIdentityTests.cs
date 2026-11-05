using System.Security.Cryptography;
using System.Net;
using System.Text;
using System.Text.Json;

using Kautuka.Gateway.Domain.Connectors;
using Kautuka.Gateway.Infrastructure.Connectors;
using Kautuka.Gateway.Infrastructure.Credentials;
using Kautuka.Protos;

namespace Kautuka.IntegrationTests;

public sealed class BrokerInstallationIdentityTests
{
    [Fact]
    public async Task ReadingInstallationIdDoesNotCreateOrRotateAnIdentity()
    {
        var store = new MemoryStore();
        using var provider = new BrokerInstallationIdentityProvider(store);
        Assert.Null(await provider.GetExistingIdAsync(CancellationToken.None));
        Assert.Equal(0, store.Writes);
        BrokerInstallationIdentity identity = await provider.GetOrCreateAsync(CancellationToken.None);
        Assert.Equal(identity.Id, await provider.GetExistingIdAsync(CancellationToken.None));
        Assert.Equal(1, store.Writes);
    }

    [Fact]
    public async Task ConcurrentInitializationPersistsOneIdentityAndReusesItAfterRestart()
    {
        var store = new MemoryStore();
        using var provider = new BrokerInstallationIdentityProvider(store);
        BrokerInstallationIdentity[] values = await Task.WhenAll(
            Enumerable.Range(0, 30).Select(_ => provider.GetOrCreateAsync(CancellationToken.None)));

        Assert.All(values, value => Assert.Equal(values[0], value));
        Assert.Equal(1, store.Writes);
        Assert.Equal(43, values[0].Id.Length);
        Assert.Equal(43, ConnectorDpop.Thumbprint(values[0].PrivateJwk).Length);
        Assert.DoesNotContain(values[0].PrivateJwk, values[0].ToString(), StringComparison.Ordinal);
        using var restarted = new BrokerInstallationIdentityProvider(store);
        Assert.Equal(values[0], await restarted.GetOrCreateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task EachBrokerRetainsItsOwnIdentityAndRotationAcrossSwitches()
    {
        var records = new Dictionary<string, string>(StringComparer.Ordinal);
        var aStore = new OriginIdentityStore(records, "https://a.example");
        var bStore = new OriginIdentityStore(records, "https://b.example");
        Assert.NotEqual(CredentialsHostBrokerInstallationIdentityStore.KeyForOrigin(aStore.BrokerOrigin),
            CredentialsHostBrokerInstallationIdentityStore.KeyForOrigin(bStore.BrokerOrigin));
        using var a = new BrokerInstallationIdentityProvider(aStore);
        using var b = new BrokerInstallationIdentityProvider(bStore);
        BrokerInstallationIdentity firstA = await a.GetOrCreateAsync(CancellationToken.None);
        BrokerInstallationIdentity firstB = await b.GetOrCreateAsync(CancellationToken.None);
        Assert.NotEqual(firstA.Id, firstB.Id);
        Assert.Throws<InvalidOperationException>(() => a.ValidateBrokerOrigin(new Uri("https://b.example/")));
        await Assert.ThrowsAsync<HttpRequestException>(() => b.RotateAsync(bStore.BrokerOrigin, (_, _, _, _) => throw new HttpRequestException("response lost"), true, CancellationToken.None));
        using var restartedB = new BrokerInstallationIdentityProvider(bStore);
        await restartedB.RotateAsync(bStore.BrokerOrigin, (_, _, _, _) => Task.CompletedTask, false, CancellationToken.None);
        BrokerInstallationIdentity secondB = await restartedB.GetOrCreateAsync(CancellationToken.None);
        Assert.Equal(firstB.Id, secondB.Id);
        Assert.Equal(firstA, await a.GetOrCreateAsync(CancellationToken.None));
        await a.RotateAsync(aStore.BrokerOrigin, (_, _, _, _) => Task.CompletedTask, true, CancellationToken.None);
        BrokerInstallationIdentity secondA = await a.GetOrCreateAsync(CancellationToken.None);
        Assert.Equal(firstA.Id, secondA.Id);
        Assert.NotEqual(ConnectorDpop.Thumbprint(firstA.PrivateJwk), ConnectorDpop.Thumbprint(secondA.PrivateJwk));
        Assert.Equal(secondB, await restartedB.GetOrCreateAsync(CancellationToken.None));
    }

    private sealed class OriginIdentityStore(Dictionary<string, string> values, string origin) : IBrokerInstallationIdentityStore
    {
        public string BrokerOrigin => origin;
        public Task<string?> ReadAsync(CancellationToken ct) => Task.FromResult(values.GetValueOrDefault(origin));
        public Task WriteAsync(string value, CancellationToken ct) { values[origin] = value; return Task.CompletedTask; }
    }

    [Fact]
    public async Task CredentialsHostIdentityAdapterUsesDistinctBrokerScopedKeys()
    {
        var client = new FakeCredentialsServiceClient();
        var resolver = new CredentialResolverService(new Lazy<CredentialsService.CredentialsServiceClient>(client));
        var a = new CredentialsHostBrokerInstallationIdentityStore(resolver, ConnectorAuthEndpoint.Parse("https://a.example"));
        var b = new CredentialsHostBrokerInstallationIdentityStore(resolver, ConnectorAuthEndpoint.Parse("https://b.example"));
        await a.WriteAsync("identity-a", CancellationToken.None);
        await b.WriteAsync("identity-b", CancellationToken.None);
        Assert.Equal("identity-a", await a.ReadAsync(CancellationToken.None));
        Assert.Equal("identity-b", await b.ReadAsync(CancellationToken.None));
        var keys = await resolver.ListSystemKeysAsync(CancellationToken.None);
        Assert.Contains(CredentialsHostBrokerInstallationIdentityStore.KeyForOrigin("https://a.example"), keys);
        Assert.Contains(CredentialsHostBrokerInstallationIdentityStore.KeyForOrigin("https://b.example"), keys);
        using var disabled = new BrokerInstallationIdentityProvider(new CredentialsHostBrokerInstallationIdentityStore(resolver, ConnectorAuthEndpoint.Local));
        Assert.Throws<InvalidOperationException>(() => disabled.ValidateBrokerOrigin(new Uri("https://a.example/")));
    }

    [Fact]
    public async Task FailedPersistenceDoesNotReturnAnUnstoredIdentity()
    {
        var store = new MemoryStore { DropWrites = true };
        using var provider = new BrokerInstallationIdentityProvider(store);
        await Assert.ThrowsAsync<CryptographicException>(() => provider.GetOrCreateAsync(CancellationToken.None));
        Assert.Null(store.Value);
    }

    [Fact]
    public async Task CorruptStoredIdentityFailsClosedWithoutReplacement()
    {
        var store = new MemoryStore { Value = "corrupt" };
        using var provider = new BrokerInstallationIdentityProvider(store);
        await Assert.ThrowsAsync<CryptographicException>(() => provider.GetOrCreateAsync(CancellationToken.None));
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task LostRotationResponseKeepsBothKeysAndRecoversAfterRestart()
    {
        var store = new MemoryStore();
        using var provider = new BrokerInstallationIdentityProvider(store);
        BrokerInstallationIdentity original = await provider.GetOrCreateAsync(CancellationToken.None);
        BrokerInstallationIdentity? next = null;
        string? operation = null;
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.RotateAsync("https://auth.kautuka.io", (oldKey, newKey, id, _) =>
        {
            Assert.Equal(original, oldKey);
            next = newKey; operation = id;
            throw new HttpRequestException("response lost after broker commit");
        }, create: true, CancellationToken.None));
        await Assert.ThrowsAsync<CryptographicException>(() => provider.GetOrCreateAsync(CancellationToken.None));
        using var restarted = new BrokerInstallationIdentityProvider(store);
        await restarted.RotateAsync("https://auth.kautuka.io", (oldKey, newKey, id, _) =>
        {
            Assert.Equal(original, oldKey);
            Assert.Equal(next, newKey);
            Assert.Equal(operation, id);
            return Task.CompletedTask;
        }, create: false, CancellationToken.None);
        Assert.Equal(next, await restarted.GetOrCreateAsync(CancellationToken.None));
        Assert.Equal(original.Id, next!.Id);
        Assert.NotEqual(ConnectorDpop.Thumbprint(original.PrivateJwk), ConnectorDpop.Thumbprint(next.PrivateJwk));
    }

    [Fact]
    public async Task RotationDoesNotContactBrokerWhenPendingRecordCannotBePersisted()
    {
        var store = new MemoryStore();
        using var provider = new BrokerInstallationIdentityProvider(store);
        await provider.GetOrCreateAsync(CancellationToken.None);
        store.DropWrites = true;
        bool sent = false;
        await Assert.ThrowsAsync<CryptographicException>(() => provider.RotateAsync("https://auth.kautuka.io", (_, _, _, _) =>
        { sent = true; return Task.CompletedTask; }, create: true, CancellationToken.None));
        Assert.False(sent);
    }

    [Fact]
    public async Task FailedFinalKeyWriteRetainsPendingRotationForRestart()
    {
        var store = new MemoryStore();
        using var provider = new BrokerInstallationIdentityProvider(store);
        await provider.GetOrCreateAsync(CancellationToken.None);
        BrokerInstallationIdentity? replacement = null;
        await Assert.ThrowsAsync<CryptographicException>(() => provider.RotateAsync("https://auth.kautuka.io", (_, next, _, _) =>
        { replacement = next; store.DropWrites = true; return Task.CompletedTask; }, create: true, CancellationToken.None));
        Assert.StartsWith("rotation|", store.Value, StringComparison.Ordinal);
        store.DropWrites = false;
        using var restarted = new BrokerInstallationIdentityProvider(store);
        await restarted.RotateAsync("https://auth.kautuka.io", (_, next, _, _) =>
        { Assert.Equal(replacement, next); return Task.CompletedTask; }, create: false, CancellationToken.None);
        Assert.Equal(replacement, await restarted.GetOrCreateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RegistrationRecoversAKeyRotationWhoseResponseWasLost()
    {
        var store = new MemoryStore();
        using var identities = new BrokerInstallationIdentityProvider(store);
        using var handler = new RotationHandler();
        using var registration = new BrokerInstallationRegistration(identities, new HttpClient(handler, false));
        var origin = new Uri("https://auth.kautuka.io/");
        await registration.RegisterAsync(origin, CancellationToken.None);
        var old = await identities.GetOrCreateAsync(CancellationToken.None);
        await Assert.ThrowsAsync<HttpRequestException>(() => registration.RotateAsync(origin, CancellationToken.None));
        await registration.RegisterAsync(origin, CancellationToken.None);
        var current = await identities.GetOrCreateAsync(CancellationToken.None);
        Assert.Equal(old.Id, current.Id);
        Assert.NotEqual(ConnectorDpop.Thumbprint(old.PrivateJwk), ConnectorDpop.Thumbprint(current.PrivateJwk));
        Assert.Equal(2, handler.RotationCalls);
    }

    [Fact]
    public async Task LocalWorkerRecoversRotationAfterARealLostResponse()
    {
        string? value = Environment.GetEnvironmentVariable("KAUTUKA_TEST_LOCAL_BROKER_ORIGIN");
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? origin) || origin.Scheme != Uri.UriSchemeHttps
            || origin.Host != "127.0.0.1" || origin.AbsolutePath != "/")
        {
            Assert.Skip("Set KAUTUKA_TEST_LOCAL_BROKER_ORIGIN to an isolated HTTPS loopback Wrangler Worker.");
            return;
        }
        var store = new MemoryStore();
        using var identities = new BrokerInstallationIdentityProvider(store);
        using var handler = new DropCommittedRotationResponseHandler(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (request, _, _, _) => request.RequestUri?.Host == "127.0.0.1",
        });
        using var registration = new BrokerInstallationRegistration(identities, new HttpClient(handler, false));
        await registration.RegisterAsync(origin, CancellationToken.None);
        BrokerInstallationIdentity original = await identities.GetOrCreateAsync(CancellationToken.None);
        await Assert.ThrowsAsync<HttpRequestException>(() => registration.RotateAsync(origin, CancellationToken.None));
        using var restarted = new BrokerInstallationIdentityProvider(store);
        using var recovery = new BrokerInstallationRegistration(restarted, new HttpClient(handler, false));
        BrokerInstallationIdentity rotated = await recovery.RegisterAsync(origin, CancellationToken.None);
        Assert.Equal(original.Id, rotated.Id);
        Assert.NotEqual(ConnectorDpop.Thumbprint(original.PrivateJwk), ConnectorDpop.Thumbprint(rotated.PrivateJwk));
        Assert.Equal(2, handler.RotationCalls);
    }

    private sealed class DropCommittedRotationResponseHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public int RotationCalls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            HttpResponseMessage response = await base.SendAsync(request, ct);
            if (request.RequestUri!.AbsolutePath == "/installation/rotate" && ++RotationCalls == 1 && response.IsSuccessStatusCode)
            {
                response.Dispose();
                throw new HttpRequestException("simulated lost response after real broker commit");
            }
            return response;
        }
    }

    [Fact]
    public async Task DisconnectDirectlyRecoversPendingRotationAfterRestart()
    {
        var store = new MemoryStore();
        using var identities = new BrokerInstallationIdentityProvider(store);
        using var handler = new RotationHandler();
        var origin = new Uri("https://auth.kautuka.io/");
        using (var registration = new BrokerInstallationRegistration(identities, new HttpClient(handler, false)))
        {
            await registration.RegisterAsync(origin, CancellationToken.None);
            await Assert.ThrowsAsync<HttpRequestException>(() => registration.RotateAsync(origin, CancellationToken.None));
        }
        using var restartedIdentity = new BrokerInstallationIdentityProvider(store);
        using var restartedRegistration = new BrokerInstallationRegistration(restartedIdentity, new HttpClient(handler, false));
        using var client = new BrokerHandoffClient(restartedIdentity, restartedRegistration, new HttpClient(handler, false));
        string id = new('A', 43), token = "kmf1." + new string('x', 40);
        var credential = new BrokerMailCredential("google", id, id, 1, token, ConnectorDpop.CreatePrivateJwk(), "https://auth.kautuka.io");
        await client.DisconnectMailAsync(origin, credential, CancellationToken.None);
        Assert.Equal(2, handler.RotationCalls);
        Assert.DoesNotContain("rotation|", (await store.ReadAsync(CancellationToken.None))!, StringComparison.Ordinal);
    }

    private sealed class RotationHandler : HttpMessageHandler
    {
        public int RotationCalls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/installation/rotate")
            {
                Assert.True(request.Headers.Contains("Kautuka-Installation-Proof"));
                Assert.True(request.Headers.Contains("Kautuka-Replacement-Proof"));
                if (++RotationCalls == 1) throw new HttpRequestException("response lost");
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }

    [Fact]
    public async Task RefreshRacingRotationRetriesOnlyWithNewKeyAndSameOperation()
    {
        using var identities = new BrokerInstallationIdentityProvider(new MemoryStore());
        using var handler = new RacingRotationHandler();
        using var registration = new BrokerInstallationRegistration(identities, new HttpClient(handler, false));
        using var client = new BrokerHandoffClient(identities, registration, new HttpClient(handler, false));
        var origin = new Uri("https://auth.kautuka.io/");
        await registration.RegisterAsync(origin, CancellationToken.None);
        string id = new('A', 43), key = ConnectorDpop.CreatePrivateJwk(), token = "kmf1." + new string('x', 40);
        Task<BrokerMailRefreshResult?> refresh = client.RefreshMailAsync(origin, "google", id, 1, id, id, token, key, CancellationToken.None);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await registration.RotateAsync(origin, CancellationToken.None);
        handler.Continue.TrySetResult();
        Assert.NotNull(await refresh);
        Assert.Equal(2, handler.RefreshBodies.Count);
        Assert.Equal(handler.RefreshBodies[0], handler.RefreshBodies[1]);
        Assert.NotEqual(handler.InstallationProofs[0], handler.InstallationProofs[1]);
        Assert.NotEqual(handler.DpopProofs[0], handler.DpopProofs[1]);
        Assert.Equal(1, handler.UpstreamRefreshes);
    }

    private sealed class RacingRotationHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> RefreshBodies { get; } = [];
        public List<string> InstallationProofs { get; } = [];
        public List<string> DpopProofs { get; } = [];
        public int UpstreamRefreshes { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath != "/installation/mail-refresh") return new HttpResponseMessage(HttpStatusCode.NoContent);
            RefreshBodies.Add(await request.Content!.ReadAsStringAsync(ct));
            InstallationProofs.Add(request.Headers.GetValues("Kautuka-Installation-Proof").Single());
            DpopProofs.Add(request.Headers.GetValues("DPoP").Single());
            if (RefreshBodies.Count == 1)
            {
                Started.TrySetResult();
                await Continue.Task.WaitAsync(ct);
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }
            UpstreamRefreshes++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                { accessToken = "access", refreshToken = "kmf1." + new string('x', 40), tokenType = "Bearer", issuedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), expiresAt = (long?)null }))
            };
        }
    }

    [Fact]
    public async Task ForbiddenRefreshDoesNotRetryWhenKeyIsUnchanged()
    {
        using var identities = new BrokerInstallationIdentityProvider(new MemoryStore());
        using var handler = new ForbiddenRefreshHandler();
        using var registration = new BrokerInstallationRegistration(identities, new HttpClient(handler, false));
        using var client = new BrokerHandoffClient(identities, registration, new HttpClient(handler, false));
        string id = new('A', 43), token = "kmf1." + new string('x', 40);
        await Assert.ThrowsAsync<BrokerMailRequestException>(() => client.RefreshMailAsync(new Uri("https://auth.kautuka.io/"),
            "google", id, 1, id, id, token, ConnectorDpop.CreatePrivateJwk(), CancellationToken.None));
        Assert.Equal(1, handler.RefreshCalls);
    }

    private sealed class ForbiddenRefreshHandler : HttpMessageHandler
    {
        public int RefreshCalls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/installation/mail-refresh") { RefreshCalls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)); }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }

    [Fact]
    public void InstallationProofBindsExactRequestAndUsesFreshIdentifiers()
    {
        string key = ConnectorDpop.CreatePrivateJwk();
        byte[] body = Encoding.UTF8.GetBytes("{\"purpose\":\"mail\"}");
        string token = ConnectorDpop.CreateInstallationProof(key, "POST", "https://auth.kautuka.io/installations", body);
        string[] parts = token.Split('.');
        using JsonDocument header = JsonDocument.Parse(Decode(parts[0]));
        using JsonDocument claims = JsonDocument.Parse(Decode(parts[1]));
        Assert.Equal("kautuka-installation+jwt", header.RootElement.GetProperty("typ").GetString());
        Assert.Equal("https://auth.kautuka.io", claims.RootElement.GetProperty("aud").GetString());
        Assert.Equal("POST", claims.RootElement.GetProperty("htm").GetString());
        Assert.Equal(Convert.ToBase64String(SHA256.HashData(body)).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            claims.RootElement.GetProperty("body_hash").GetString());
        JsonElement jwk = header.RootElement.GetProperty("jwk");
        using ECDsa verifier = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = Decode(jwk.GetProperty("x").GetString()!), Y = Decode(jwk.GetProperty("y").GetString()!) },
        });
        Assert.True(verifier.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Decode(parts[2]),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        Assert.NotEqual(token, ConnectorDpop.CreateInstallationProof(key, "POST", "https://auth.kautuka.io/installations", body));
        Assert.Throws<ArgumentException>(() => ConnectorDpop.CreateInstallationProof(key, "POST", "https://auth.kautuka.io/installations?x=1", body));
    }

    [Fact]
    public async Task RegistrationSendsExactlyTheSignedBodyAndRejectsRedirect()
    {
        var store = new MemoryStore();
        using var identities = new BrokerInstallationIdentityProvider(store);
        var handler = new RecordingHandler();
        using var registration = new BrokerInstallationRegistration(identities, new HttpClient(handler));
        await registration.RegisterAsync(new Uri("https://auth.kautuka.io/"), CancellationToken.None);
        Assert.Equal("https://auth.kautuka.io/installation/register", handler.Url);
        using JsonDocument body = JsonDocument.Parse(handler.Body!);
        BrokerInstallationIdentity identity = await identities.GetOrCreateAsync(CancellationToken.None);
        Assert.Equal(identity.Id, body.RootElement.GetProperty("id").GetString());
        using JsonDocument claims = JsonDocument.Parse(Decode(handler.Proof!.Split('.')[1]));
        Assert.Equal(Convert.ToBase64String(SHA256.HashData(handler.Body!)).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            claims.RootElement.GetProperty("body_hash").GetString());
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => registration.RegisterAsync(new Uri("https://auth.kautuka.io/"), CancellationToken.None)));
        Assert.Equal(1, handler.Calls);
        handler.Status = HttpStatusCode.Redirect;
        await registration.InvalidateAsync(CancellationToken.None);
        await Assert.ThrowsAsync<HttpRequestException>(() => registration.RegisterAsync(new Uri("https://auth.kautuka.io/"), CancellationToken.None));
    }

    [Fact]
    public async Task HandoffClientRegistersCreatesAndBindsUsingSignedRequests()
    {
        var store = new MemoryStore();
        using var identities = new BrokerInstallationIdentityProvider(store);
        using var handler = new HandoffHandler();
        using var registration = new BrokerInstallationRegistration(identities, new HttpClient(handler, disposeHandler: false));
        using var client = new BrokerHandoffClient(identities, registration, new HttpClient(handler, disposeHandler: false));
        string token = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var origin = new Uri("https://auth.kautuka.io/");
        string flow = await client.StartAsync(origin, new BrokerHandoffStart("google", "login", token, token, token,
            "http://localhost:20229"), CancellationToken.None);
        Assert.Equal(token, flow);
        await client.BindBrowserAsync(origin, flow, token, CancellationToken.None);
        Assert.Equal(["/installation/register", "/installation/flow", "/installation/bind-browser"], handler.Paths);
        Assert.Equal(3, handler.Proofs.Count);
        Assert.Equal(3, handler.Proofs.Distinct(StringComparer.Ordinal).Count());
        handler.FlowBody = new string('a', 4097);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.StartAsync(origin,
            new BrokerHandoffStart("google", "login", token, token, token, "http://localhost:20229"), CancellationToken.None));
    }

    [Fact]
    public async Task MailBrokerRequestsCarryFreshInstallationAndConnectionProofs()
    {
        using var identities = new BrokerInstallationIdentityProvider(new MemoryStore());
        using var handler = new MailHandler();
        using var registration = new BrokerInstallationRegistration(identities, new HttpClient(handler, disposeHandler: false));
        using var client = new BrokerHandoffClient(identities, registration, new HttpClient(handler, disposeHandler: false));
        string id = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string key = ConnectorDpop.CreatePrivateJwk();
        var origin = new Uri("https://auth.kautuka.io/");
        BrokerMailResult? result = await client.ExchangeMailAsync(origin, "google", id, id, key, CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal("google", result.Provider);
        Assert.DoesNotContain(result.RefreshToken, result.ToString(), StringComparison.Ordinal);
        await client.AcknowledgeMailAsync(origin, id, id, key, CancellationToken.None);
        Assert.True(await client.IsMailActiveAsync(origin, "google", id, 1, id, key, CancellationToken.None));
        BrokerMailRefreshResult? refreshed = await client.RefreshMailAsync(origin, "google", id, 1, id, id, result.RefreshToken, key, CancellationToken.None);
        Assert.NotNull(refreshed);
        Assert.Null(refreshed.ExpiresAt);
        Assert.DoesNotContain(refreshed.AccessToken, refreshed.ToString(), StringComparison.Ordinal);
        await client.AcknowledgeMailRefreshAsync(origin, "google", id, 1, id, id, key, CancellationToken.None);
        Assert.Equal(5, handler.InstallationProofs.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(5, handler.MailProofs.Select(static proof => proof.Raw).Distinct(StringComparer.Ordinal).Count());
        Assert.All(handler.MailProofs, proof => Assert.Equal(ConnectorDpop.Thumbprint(key), proof.Thumbprint));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.ExchangeMailAsync(origin, "microsoft", id, id, key, CancellationToken.None));
    }

    private static byte[] Decode(string value)
    {
        string base64 = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '='));
    }

    private sealed class MemoryStore : IBrokerInstallationIdentityStore
    {
        public string? Value { get; set; }
        public int Writes { get; private set; }
        public bool DropWrites { get; set; }

        public Task<string?> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Value);

        public Task WriteAsync(string value, CancellationToken cancellationToken)
        {
            Writes++;
            if (!DropWrites) Value = value;
            return Task.CompletedTask;
        }
    }

    private sealed class MailHandler : HttpMessageHandler
    {
        public List<string> InstallationProofs { get; } = [];
        public List<(string Raw, string Thumbprint)> MailProofs { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/installation/register") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            InstallationProofs.Add(request.Headers.GetValues("Kautuka-Installation-Proof").Single());
            string proof = request.Headers.GetValues("DPoP").Single();
            using JsonDocument header = JsonDocument.Parse(Decode(proof.Split('.')[0]));
            using JsonDocument claims = JsonDocument.Parse(Decode(proof.Split('.')[1]));
            Assert.Equal(request.RequestUri.AbsoluteUri, claims.RootElement.GetProperty("htu").GetString());
            Assert.Equal("POST", claims.RootElement.GetProperty("htm").GetString());
            JsonElement jwk = header.RootElement.GetProperty("jwk");
            string canonical = $"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{jwk.GetProperty("x").GetString()}\",\"y\":\"{jwk.GetProperty("y").GetString()}\"}}";
            string thumbprint = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            MailProofs.Add((proof, thumbprint));
            string token = "kmf1." + new string('x', 40);
            return Task.FromResult(path switch
            {
                "/installation/mail-exchange" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"provider\":\"google\",\"refreshToken\":\"{token}\",\"generation\":1}}") },
                "/installation/mail-status" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"active\":true}") },
                "/installation/mail-refresh" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"accessToken\":\"access\",\"refreshToken\":\"{token}\",\"tokenType\":\"Bearer\",\"issuedAt\":1000,\"expiresAt\":null}}") },
                _ => new HttpResponseMessage(HttpStatusCode.NoContent),
            });
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? Url { get; private set; }
        public byte[]? Body { get; private set; }
        public string? Proof { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.NoContent;
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri!.AbsoluteUri;
            Calls++;
            Body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            Proof = request.Headers.GetValues("Kautuka-Installation-Proof").Single();
            return new HttpResponseMessage(Status);
        }
    }

    private sealed class HandoffHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public List<string> Proofs { get; } = [];
        public string FlowBody { get; set; } = "{\"flowId\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"}";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            Proofs.Add(request.Headers.GetValues("Kautuka-Installation-Proof").Single());
            var response = new HttpResponseMessage(path == "/installation/flow" ? HttpStatusCode.OK : HttpStatusCode.NoContent);
            if (path == "/installation/flow") response.Content = new StringContent(FlowBody);
            return Task.FromResult(response);
        }
    }
}
