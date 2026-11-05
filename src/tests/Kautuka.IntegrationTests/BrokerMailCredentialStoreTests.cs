using Kautuka.Gateway.Domain.Connectors;
using Kautuka.Gateway.Infrastructure.Connectors;

namespace Kautuka.IntegrationTests;

public sealed class BrokerMailCredentialStoreTests
{
    [Fact]
    public async Task RetainedStateEnumerationIgnoresEmptyBundlesAndFindsActiveCredentials()
    {
        using var store = new BrokerMailCredentialStore(new MemoryStorage());
        Assert.False(await store.HasRetainedStateAsync(CancellationToken.None));
        var credential = new BrokerMailCredential("google", new string('A', 43), new string('A', 43), 1,
            "kmf1." + new string('x', 40), ConnectorDpop.CreatePrivateJwk(), "https://auth.kautuka.io");
        await store.StageActivationAsync("user", "connector", new(credential, new string('A', 43)), CancellationToken.None);
        Assert.True(await store.HasRetainedStateAsync(CancellationToken.None));
        await store.DiscardActivationAsync("user", "connector", credential.FlowId, CancellationToken.None);
        Assert.False(await store.HasRetainedStateAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReplacementFromDifferentBrokerCannotBeStaged()
    {
        using var store = new BrokerMailCredentialStore(new MemoryStorage());
        string id = new('A', 43);
        var original = new BrokerMailCredential("google", id, id, 1, "kmf1." + new string('x', 40), ConnectorDpop.CreatePrivateJwk(), "https://auth.kautuka.io");
        await store.StageActivationAsync("user", "mail", new(original, id), CancellationToken.None);
        await store.PromoteActivationAsync("user", "mail", id, CancellationToken.None);
        var other = original with { FlowId = new string('B', 43), Generation = 2, BrokerOrigin = "https://other.example" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.StageActivationAsync("user", "mail", new(other, id), CancellationToken.None));
    }
    private static readonly string Id = new('A', 43);
    private static readonly string OtherId = new('B', 43);
    private static readonly string Credential = "kmf1." + new string('x', 40);
    private static readonly string Replacement = "kmf1." + new string('y', 40);

    [Fact]
    public async Task PendingActivationSurvivesRestartWithoutReplacingTheActiveCredential()
    {
        var storage = new MemoryStorage();
        using var first = new BrokerMailCredentialStore(storage);
        var original = new BrokerMailCredential("google", Id, Id, 1, Credential, ConnectorDpop.CreatePrivateJwk(), "https://auth.kautuka.io");
        await first.StageActivationAsync("user", "mail", new(original, Id), CancellationToken.None);
        Assert.Null((await first.ReadAsync("user", "mail", CancellationToken.None)).Active);
        await first.PromoteActivationAsync("user", "mail", Id, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.StageActivationAsync("user", "mail",
            new(original with { ConnectionId = OtherId, FlowId = OtherId, Generation = 2 }, Id), CancellationToken.None));
        var replacement = original with { FlowId = OtherId, Generation = 2, RefreshToken = Replacement };
        await first.StageActivationAsync("user", "mail", new(replacement, Id), CancellationToken.None);
        using var restarted = new BrokerMailCredentialStore(storage);
        BrokerMailStoredState state = await restarted.ReadAsync("user", "mail", CancellationToken.None);
        Assert.Equal(original, state.Active);
        Assert.Equal(replacement, state.PendingActivation!.Credential);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.PromoteActivationAsync("user", "mail", Id, CancellationToken.None));
        await restarted.PromoteActivationAsync("user", "mail", OtherId, CancellationToken.None);
        Assert.Equal(replacement, (await restarted.ReadAsync("user", "mail", CancellationToken.None)).Active);
        Assert.DoesNotContain(replacement.RefreshToken, state.ToString(), StringComparison.Ordinal);
        Assert.Null((await restarted.ReadAsync("other", "mail", CancellationToken.None)).Active);
    }

    [Fact]
    public async Task RefreshOperationAndReplacementSurviveRestartUntilAcknowledged()
    {
        var storage = new MemoryStorage();
        using var first = new BrokerMailCredentialStore(storage);
        var active = new BrokerMailCredential("google", Id, Id, 1, Credential, ConnectorDpop.CreatePrivateJwk(), "https://auth.kautuka.io");
        await first.StageActivationAsync("user", "mail", new(active, Id), CancellationToken.None);
        await first.PromoteActivationAsync("user", "mail", Id, CancellationToken.None);
        await first.StageRefreshAsync("user", "mail", OtherId, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.StageRefreshAsync("user", "mail", Id, CancellationToken.None));
        using var restarted = new BrokerMailCredentialStore(storage);
        BrokerMailStoredState pending = await restarted.ReadAsync("user", "mail", CancellationToken.None);
        Assert.Equal(Credential, pending.PendingRefresh!.InputRefreshToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.ClearRefreshAsync("user", "mail", OtherId, CancellationToken.None));
        await restarted.SaveRefreshResultAsync("user", "mail", OtherId, Replacement, CancellationToken.None);
        using var restartedAgain = new BrokerMailCredentialStore(storage);
        BrokerMailStoredState saved = await restartedAgain.ReadAsync("user", "mail", CancellationToken.None);
        Assert.Equal(Replacement, saved.Active!.RefreshToken);
        Assert.Equal(OtherId, saved.PendingRefresh!.OperationId);
        await restartedAgain.ClearRefreshAsync("user", "mail", OtherId, CancellationToken.None);
        Assert.Null((await restartedAgain.ReadAsync("user", "mail", CancellationToken.None)).PendingRefresh);
    }

    [Fact]
    public async Task CommittedWritesCanBeRetriedAfterReadbackFailure()
    {
        var storage = new MemoryStorage();
        using var store = new BrokerMailCredentialStore(storage);
        var active = new BrokerMailCredential("google", Id, Id, 1, Credential, ConnectorDpop.CreatePrivateJwk(), "https://auth.kautuka.io");
        storage.FailNextReadback = true;
        await Assert.ThrowsAsync<IOException>(() => store.StageActivationAsync("user", "mail", new(active, Id), CancellationToken.None));
        await store.StageActivationAsync("user", "mail", new(active, Id), CancellationToken.None);
        storage.FailNextReadback = true;
        await Assert.ThrowsAsync<IOException>(() => store.PromoteActivationAsync("user", "mail", Id, CancellationToken.None));
        await store.PromoteActivationAsync("user", "mail", Id, CancellationToken.None);
        await store.StageRefreshAsync("user", "mail", OtherId, CancellationToken.None);
        storage.FailNextReadback = true;
        await Assert.ThrowsAsync<IOException>(() => store.SaveRefreshResultAsync("user", "mail", OtherId, Replacement, CancellationToken.None));
        await store.SaveRefreshResultAsync("user", "mail", OtherId, Replacement, CancellationToken.None);
        storage.FailNextReadback = true;
        await Assert.ThrowsAsync<IOException>(() => store.ClearRefreshAsync("user", "mail", OtherId, CancellationToken.None));
        await store.ClearRefreshAsync("user", "mail", OtherId, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.StageRefreshAsync("user", "mail", OtherId, CancellationToken.None));
    }

    [Fact]
    public async Task UnknownRefreshRequiresReconnectAndCancelledActivationPreservesOldGeneration()
    {
        using var store = new BrokerMailCredentialStore(new MemoryStorage());
        var active = new BrokerMailCredential("google", Id, Id, 1, Credential, ConnectorDpop.CreatePrivateJwk(), "https://auth.kautuka.io");
        await store.StageActivationAsync("user", "mail", new(active, Id), CancellationToken.None);
        await store.PromoteActivationAsync("user", "mail", Id, CancellationToken.None);
        await store.StageRefreshAsync("user", "mail", OtherId, CancellationToken.None);
        await store.MarkRefreshUnknownAsync("user", "mail", OtherId, CancellationToken.None);
        BrokerMailStoredState unknown = await store.ReadAsync("user", "mail", CancellationToken.None);
        Assert.True(unknown.RequiresReconnect);
        Assert.Equal(active, unknown.Active);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.StageRefreshAsync("user", "mail", Id, CancellationToken.None));
        var replacement = active with { FlowId = OtherId, Generation = 2, RefreshToken = Replacement };
        await store.StageActivationAsync("user", "mail", new(replacement, Id), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ClearConnectionAsync("user", "mail", unknown, CancellationToken.None));
        await store.DiscardActivationAsync("user", "mail", OtherId, CancellationToken.None);
        Assert.Equal(active, (await store.ReadAsync("user", "mail", CancellationToken.None)).Active);
        await store.StageActivationAsync("user", "mail", new(replacement, Id), CancellationToken.None);
        await store.PromoteActivationAsync("user", "mail", OtherId, CancellationToken.None);
        Assert.False((await store.ReadAsync("user", "mail", CancellationToken.None)).RequiresReconnect);
    }

    [Fact]
    public async Task InvalidPersistedStateIsRejectedAndConcurrentStagingDoesNotOverwrite()
    {
        var storage = new MemoryStorage();
        using var store = new BrokerMailCredentialStore(storage);
        var credential = new BrokerMailCredential("google", Id, Id, 1, Credential, ConnectorDpop.CreatePrivateJwk(), "https://auth.kautuka.io");
        Task[] attempts = Enumerable.Range(0, 2).Select(async index =>
        {
            var candidate = credential with { FlowId = index == 0 ? Id : OtherId };
            try { await store.StageActivationAsync("user", "mail", new(candidate, Id), CancellationToken.None); }
            catch (InvalidOperationException) { }
        }).ToArray();
        await Task.WhenAll(attempts);
        Assert.NotNull((await store.ReadAsync("user", "mail", CancellationToken.None)).PendingActivation);
        storage.ReplaceOnlyValue("{\"Active\":null,\"PendingRefresh\":{\"OperationId\":\"bad\"}}");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync("user", "mail", CancellationToken.None));
    }

    private sealed class MemoryStorage : IBrokerMailCredentialStorage
    {
        private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
        public Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([.. values.Keys]);
        public bool FailNextReadback { get; set; }
        private bool failRead;
        public void ReplaceOnlyValue(string value) => values[values.Keys.Single()] = value;
        public Task<string?> ReadAsync(string key, CancellationToken ct)
        {
            if (failRead) { failRead = false; throw new IOException("Simulated readback failure."); }
            return Task.FromResult(values.GetValueOrDefault(key));
        }
        public Task WriteAsync(string key, string value, CancellationToken ct)
        {
            values[key] = value;
            if (FailNextReadback) { FailNextReadback = false; failRead = true; }
            return Task.CompletedTask;
        }
    }
}
