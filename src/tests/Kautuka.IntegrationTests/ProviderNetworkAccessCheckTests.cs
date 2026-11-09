using System.Globalization;
using System.Text.Json.Nodes;

using Grpc.Core;

using Kautuka.Gateway.Domain.Providers;
using Kautuka.Manager.Protos;

namespace Kautuka.IntegrationTests;

public sealed class ProviderNetworkAccessCheckTests
{
    [Fact]
    public async Task ChatDiagnosticAlwaysFallsBackOnCancellationOrStalledManager()
    {
        var never = new TaskCompletionSource<NetworkAccessPolicyResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var failure = new HttpRequestException("original transport failure");
        Assert.Equal(failure.Message, await ProviderNetworkAccessCheck.DescribeChatFailureAsync((_, _) => never.Task,
            "https://api.openai.com", DateTimeOffset.UtcNow, failure, cancelled.Token));
        Assert.Equal(failure.Message, await ProviderNetworkAccessCheck.DescribeChatFailureAsync((_, _) => never.Task,
            "https://api.openai.com", DateTimeOffset.UtcNow, failure, TestContext.Current.CancellationToken));
        Assert.Equal(failure.Message, await ProviderNetworkAccessCheck.DescribeChatFailureAsync((_, _) =>
            Task.FromException<NetworkAccessPolicyResponse>(new InvalidOperationException("lookup failed")),
            "https://api.openai.com", DateTimeOffset.UtcNow, failure, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("openai", "api.openai.com")]
    [InlineData("openai-responses", "api.openai.com")]
    [InlineData("gemini", "generativelanguage.googleapis.com")]
    public async Task ChatUsesKnownDefaultEndpointForRecordedDenial(string providerType, string host)
    {
        var provider = new ProviderDefinition { Id = "provider", Type = providerType, Settings = new(StringComparer.OrdinalIgnoreCase) };
        Uri endpoint = KautukaAiClientFactory.GetBaseUrl(provider);
        Assert.Equal(host, endpoint.Host);
        DateTimeOffset started = DateTimeOffset.UtcNow;
        var policy = new NetworkAccessPolicyResponse();
        policy.Requests.Add(new NetworkAccessRequestMessage
        {
            Id = "request",
            Identity = Gateway(),
            Destination = host,
            Port = 443,
            Status = "pending",
            LastSeenAtUtc = started.ToString("O", CultureInfo.InvariantCulture)
        });
        string message = await ProviderNetworkAccessCheck.DescribeChatFailureAsync((_, _) => Task.FromResult(policy),
            endpoint.AbsoluteUri, started, new HttpRequestException("closed"), TestContext.Current.CancellationToken);
        Assert.Contains("Manager recorded", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChatReportsOnlyRecordedRecentDenialAndPreservesStatusAndUnknownFailures()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow;
        var policy = new NetworkAccessPolicyResponse();
        var request = new NetworkAccessRequestMessage
        {
            Id = "request-1",
            Identity = Gateway(),
            Destination = "192.168.1.20",
            Port = 11434,
            Status = "pending",
            LastSeenAtUtc = start.AddSeconds(1).ToString("O", CultureInfo.InvariantCulture)
        };
        int reads = 0;
        Task<NetworkAccessPolicyResponse> Read(NetworkAccessReadRequest _, CancellationToken __) { reads++; return Task.FromResult(policy); }
        const string endpoint = "http://192.168.1.20:11434";
        var transport = new HttpRequestException("connection closed");
        Assert.Equal("connection closed", await ProviderNetworkAccessCheck.DescribeChatFailureAsync(Read, endpoint, start, transport, TestContext.Current.CancellationToken));
        policy.Requests.Add(request);
        string message = await ProviderNetworkAccessCheck.DescribeChatFailureAsync(Read, endpoint, start, transport, TestContext.Current.CancellationToken);
        Assert.Contains("Manager recorded", message, StringComparison.Ordinal);
        Assert.Contains("Network access > Permissions", message, StringComparison.Ordinal);
        var unauthorized = new HttpRequestException("unauthorized", null, System.Net.HttpStatusCode.Unauthorized);
        Assert.Equal("unauthorized", await ProviderNetworkAccessCheck.DescribeChatFailureAsync(Read, endpoint, start, unauthorized, TestContext.Current.CancellationToken));
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task OnlyRecentMatchingManagerDenialProducesApprovalHint()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow;
        var policy = new NetworkAccessPolicyResponse();
        policy.Requests.Add(new NetworkAccessRequestMessage
        {
            Id = "request-1",
            Identity = Gateway(),
            Destination = "192.168.1.20",
            Port = 11434,
            Status = "pending",
            LastSeenAtUtc = start.AddSeconds(1).ToString("O", CultureInfo.InvariantCulture),
        });
        var settings = new JsonObject { ["base_url"] = "http://192.168.1.20:11434/secret?token=private" };
        GatewayProviderTestResult? result = await ProviderNetworkAccessCheck.CheckRecordedDenialAsync((_, _) => Task.FromResult(policy), settings, start, TestContext.Current.CancellationToken);
        Assert.Equal("request-1", result?.NetworkBlock?.RequestId);
        Assert.Contains("Manager recorded", result!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", result.Message, StringComparison.Ordinal);
        policy.Requests[0].LastSeenAtUtc = start.AddSeconds(-1).ToString("O", CultureInfo.InvariantCulture);
        Assert.Null(await ProviderNetworkAccessCheck.CheckRecordedDenialAsync((_, _) => Task.FromResult(policy), settings, start, TestContext.Current.CancellationToken));
        policy.Requests[0].LastSeenAtUtc = start.AddSeconds(1).ToString("O", CultureInfo.InvariantCulture);
        policy.Requests[0].Identity.ComponentId = "channelhost";
        Assert.Null(await ProviderNetworkAccessCheck.CheckRecordedDenialAsync((_, _) => Task.FromResult(policy), settings, start, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnavailableManagerDoesNotMislabelFailure()
    {
        Assert.Null(await ProviderNetworkAccessCheck.CheckRecordedDenialAsync((_, _) => Task.FromException<NetworkAccessPolicyResponse>(new RpcException(new Status(StatusCode.Unavailable, "offline"))),
            new JsonObject { ["base_url"] = "https://example.com" }, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));
    }

    private static NetworkAccessIdentityMessage Gateway() => new() { ScopeType = "core", UserId = "system", AgentId = "gateway", Role = "core", ComponentId = "gateway" };
}
