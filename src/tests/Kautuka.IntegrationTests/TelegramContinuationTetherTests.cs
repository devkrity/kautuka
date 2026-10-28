using System.Text.Json;

using Kautuka.ChannelContracts.Channels;
using Kautuka.Gateway.Domain.Channels;
using Kautuka.Gateway.Domain.Hitl;

namespace Kautuka.IntegrationTests;

public sealed class TelegramContinuationTetherTests
{
    [Fact]
    public async Task ContinuationCompletedNoLongerDeliversFromGateway()
    {
        var ingress = new CapturingChannelIngressService();
        var handler = new ChannelContinuationResultHandler();

        await handler.Handle(new HitlContinuationCompleted(
            "req-hitl",
            "session-1",
            "agent",
            "agent-1",
            "user-1",
            true,
            HitlResolutionSource.Browser,
            false,
            "final reply from chat approval"), CancellationToken.None);

        Assert.Empty(ingress.Continuations);
    }

    [Theory]
    [InlineData(null, ChannelHitlTetherMode.Mirror)]
    [InlineData("", ChannelHitlTetherMode.Mirror)]
    [InlineData("none", ChannelHitlTetherMode.Mirror)]
    [InlineData("single", ChannelHitlTetherMode.Mirror)]
    [InlineData("hitl", ChannelHitlTetherMode.Hitl)]
    [InlineData("mirror", ChannelHitlTetherMode.Mirror)]
    [InlineData("full", ChannelHitlTetherMode.Mirror)]
    [InlineData("unsupported", ChannelHitlTetherMode.Mirror)]
    internal void TetherModeParserAcceptsOnlyHitlAndMirror(string? stored, ChannelHitlTetherMode expected)
    {
        Assert.Equal(expected, ChannelHitlTetherModeExtensions.Parse(stored));
        Assert.Equal(
            expected == ChannelHitlTetherMode.Mirror ? "mirror" : "hitl",
            expected.ToStorageValue());
    }

    [Fact]
    public void StructuredApprovalCallbackPayloadParsesAlongsideLegacyHitlTokens()
    {
        string structured = """{"kind":"approval.resolve","interactionId":"hitl-json-1","decision":"approve"}""";
        string legacy = "hitl:hitl-legacy-1:deny";

        Assert.Equal("APPROVE", InvokeParseCallbackAction(structured));
        Assert.Equal("hitl-json-1", InvokeParseCallbackInteractionId(structured));
        Assert.Equal("DENY", InvokeParseCallbackAction(legacy));
        Assert.Equal("hitl-legacy-1", InvokeParseCallbackInteractionId(legacy));
    }

    private static string InvokeParseCallbackAction(string callbackData)
    {
        var method = typeof(ChannelIngressService).GetMethod(
            "ParseCallbackAction",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<string>(method!.Invoke(null, [callbackData]));
    }

    private static string? InvokeParseCallbackInteractionId(string callbackData)
    {
        var method = typeof(ChannelIngressService).GetMethod(
            "ParseCallbackInteractionId",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (string?)method!.Invoke(null, [callbackData]);
    }

    private sealed class CapturingChannelIngressService : IChannelIngressService
    {
        public List<(string RequestId, string Text)> Continuations { get; } = [];

        public Task<ChannelIngressResult> ProcessInboundEventAsync(ChannelInboundEvent evt, CancellationToken cancellationToken)
        {
            _ = evt;
            _ = cancellationToken;
            return Task.FromResult(new ChannelIngressResult(true, "ok", "ok"));
        }

        public Task<ChannelInteractionDispatchResult> DispatchChannelInteractionAsync(string registrationId, JsonElement payload, CancellationToken cancellationToken)
            => Task.FromResult(new ChannelInteractionDispatchResult(true, "ok", "ok"));

        public Task NotifyChannelApprovalAsync(
            string userId,
            string sessionId,
            string? targetType,
            string? targetId,
            GatewayChatApprovalRequest approvalReq,
            CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task NotifyChannelContinuationAsync(string requestId, string continuationText, CancellationToken cancellationToken)
        {
            Continuations.Add((requestId, continuationText));
            return Task.CompletedTask;
        }

        public Task SyncChannelApprovalResolutionAsync(
            ChannelApprovalResolution resolution,
            CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
