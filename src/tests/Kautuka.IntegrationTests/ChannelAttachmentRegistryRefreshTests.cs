using System.Security.Cryptography;
using System.Text;

using Grpc.Core;

using Kautuka.Gateway.Domain.Channels;
using Kautuka.Gateway.Domain.Memory;
using Kautuka.Gateway.Infrastructure.Channels;
using Kautuka.Gateway.Infrastructure.Credentials;
using Kautuka.Protos;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class ChannelAttachmentRegistryRefreshTests
{
    private const string attachmentId = "telegram:user-1:default";
    private const string ownerUserId = "user-1";

    [Fact]
    public async Task ReadFailureRemovesPreviouslyCachedAttachment()
    {
        var channelHost = new StubChannelHost(BuildAttachment());
        ChannelAttachmentRegistry registry = CreateRegistry(channelHost, SuccessfulCredentialResolver());

        await registry.RefreshAttachmentAsync(attachmentId, ownerUserId, TestContext.Current.CancellationToken);
        Assert.True(registry.TryGetRegistration(attachmentId, out _));

        channelHost.ReadFailure = new InvalidOperationException("ChannelHost unavailable.");
        await registry.RefreshAttachmentAsync(attachmentId, ownerUserId, TestContext.Current.CancellationToken);

        Assert.False(registry.TryGetRegistration(attachmentId, out _));
    }

    [Fact]
    public async Task ProjectionFailureRemovesPreviouslyCachedAttachment()
    {
        var channelHost = new StubChannelHost(BuildAttachment());
        ChannelAttachmentRegistry registry = CreateRegistry(channelHost, SuccessfulCredentialResolver());

        await registry.RefreshAttachmentAsync(attachmentId, ownerUserId, TestContext.Current.CancellationToken);
        Assert.True(registry.TryGetRegistration(attachmentId, out _));

        GetAttachmentResponse malformed = BuildAttachment();
        malformed.Settings.Add(new AttachmentSettingValue
        {
            FieldKey = "malformed",
            JsonValue = "{not-json",
        });
        channelHost.Response = malformed;
        await registry.RefreshAttachmentAsync(attachmentId, ownerUserId, TestContext.Current.CancellationToken);

        Assert.False(registry.TryGetRegistration(attachmentId, out _));
    }

    [Fact]
    public async Task CredentialHydrationFailurePreservesDurableAttachmentAndRemainsActionableWithoutLoggingSecret()
    {
        const string secret = "ultra-secret-token";
        GetAttachmentResponse response = BuildAttachment(includeCredentialReference: true);
        var channelHost = new StubChannelHost(response)
        {
            ProvisionResponse = new ProvisionAttachmentResponse
            {
                Status = ProvisioningStatus.Failed,
                Detail = "Runtime credentials are unavailable. Reconnect the channel and retry.",
            },
        };
        var logger = new RecordingLogger<ChannelAttachmentRegistry>();
        ChannelAttachmentRegistry registry = CreateRegistry(
            channelHost,
            FailingCredentialResolver(new InvalidOperationException(secret)),
            logger: logger);

        await registry.RefreshAttachmentAsync(attachmentId, ownerUserId, TestContext.Current.CancellationToken);

        Assert.True(registry.TryGetRegistration(attachmentId, out GatewayChannelRegistration? registration));
        Assert.NotNull(registration);
        Assert.True(registration.IsActive);
        Assert.Contains(logger.Entries, entry => entry.Contains("credential_hydration", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, entry => entry.Contains(secret, StringComparison.Ordinal));

        var provisioning = new ChannelRegistrationProvisioningService(
            CreateState(),
            registry,
            channelHost,
            NullLogger<ChannelRegistrationProvisioningService>.Instance);
        ChannelRegistrationProvisioningResult result = await provisioning.ProvisionRegistrationAsync(
            attachmentId,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("failed", result.Status);
        Assert.Contains("Reconnect", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SlackPairingStatusReadsCachedAttachmentWithoutRefreshingChannelHost()
    {
        const string slackAttachmentId = "slack:user-1:default";
        var channelHost = new StubChannelHost(BuildAttachment(
            providerKey: "slack",
            id: slackAttachmentId,
            paired: true));
        ChannelAttachmentRegistry registry = CreateRegistry(channelHost, SuccessfulCredentialResolver());
        await registry.RefreshAttachmentAsync(slackAttachmentId, ownerUserId, TestContext.Current.CancellationToken);

        bool firstStatus = await registry.IsSlackPairedAsync(ownerUserId, TestContext.Current.CancellationToken);
        bool secondStatus = await registry.IsSlackPairedAsync(ownerUserId, TestContext.Current.CancellationToken);

        Assert.True(firstStatus);
        Assert.True(secondStatus);
        Assert.Equal(1, channelHost.GetAttachmentCalls);
    }

    [Fact]
    public async Task CompletingSlackPairingDoesNotReloadAttachmentBeforeSavingLink()
    {
        const string slackAttachmentId = "slack:user-1:default";
        const string code = "612495";
        var channelHost = new StubChannelHost(BuildAttachment(
            providerKey: "slack",
            id: slackAttachmentId,
            pairingCode: code));
        ChannelAttachmentRegistry registry = CreateRegistry(channelHost, SuccessfulCredentialResolver());
        await registry.RefreshAttachmentAsync(slackAttachmentId, ownerUserId, TestContext.Current.CancellationToken);

        bool completed = await registry.CompleteSlackPairingAsync(
            slackAttachmentId,
            ownerUserId,
            "U123",
            "D123",
            code,
            TestContext.Current.CancellationToken);

        Assert.True(completed);
        Assert.Equal(2, channelHost.GetAttachmentCalls);
        Assert.True(await registry.IsSlackPairedAsync(ownerUserId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SlackPairingRejectsAnotherUserAfterSuccessfulPairingWithoutChangingOwner()
    {
        const string slackAttachmentId = "slack:user-1:default";
        const string code = "612495";
        var channelHost = new StubChannelHost(BuildAttachment(providerKey: "slack", id: slackAttachmentId, pairingCode: code));
        ChannelAttachmentRegistry registry = CreateRegistry(channelHost, SuccessfulCredentialResolver());
        await registry.RefreshAttachmentAsync(slackAttachmentId, ownerUserId, TestContext.Current.CancellationToken);

        Assert.True(await registry.CompleteSlackPairingAsync(slackAttachmentId, ownerUserId, "U123", "D123", code, TestContext.Current.CancellationToken));
        Assert.False(await registry.CompleteSlackPairingAsync(slackAttachmentId, ownerUserId, "U-ATTACKER", "D-ATTACKER", code, TestContext.Current.CancellationToken));
        Assert.Contains(channelHost.Response.Settings, setting => setting.FieldKey == "slack_owner_user_id" && setting.StringValue == "U123");
        Assert.DoesNotContain(channelHost.Response.Settings, setting => setting.FieldKey == "slack_pairing_failed_attempts");
    }

    [Fact]
    public async Task SlackPairingIgnoresNonCodeTextWithoutCountingFailedAttempts()
    {
        const string slackAttachmentId = "slack:user-1:default";
        var channelHost = new StubChannelHost(BuildAttachment(providerKey: "slack", id: slackAttachmentId, pairingCode: "612495"));
        ChannelAttachmentRegistry registry = CreateRegistry(channelHost, SuccessfulCredentialResolver());
        await registry.RefreshAttachmentAsync(slackAttachmentId, ownerUserId, TestContext.Current.CancellationToken);

        Assert.False(await registry.CompleteSlackPairingAsync(slackAttachmentId, ownerUserId, "U123", "D123", "hello", TestContext.Current.CancellationToken));
        Assert.False(await registry.CompleteSlackPairingAsync(slackAttachmentId, ownerUserId, "U123", "D123", "12345", TestContext.Current.CancellationToken));

        Assert.Contains(channelHost.Response.Settings, setting => setting.FieldKey == "slack_pairing_failed_attempts" && setting.StringValue == "0");
    }

    [Fact]
    public async Task SlackPairingInvalidatesCodeAfterFiveSixDigitFailures()
    {
        const string slackAttachmentId = "slack:user-1:default";
        const string correctCode = "612495";
        var channelHost = new StubChannelHost(BuildAttachment(providerKey: "slack", id: slackAttachmentId, pairingCode: correctCode));
        ChannelAttachmentRegistry registry = CreateRegistry(channelHost, SuccessfulCredentialResolver());
        await registry.RefreshAttachmentAsync(slackAttachmentId, ownerUserId, TestContext.Current.CancellationToken);

        for (int i = 0; i < 5; i++)
        {
            Assert.False(await registry.CompleteSlackPairingAsync(slackAttachmentId, ownerUserId, "U-ATTACKER", "D-ATTACKER", "000000",
                TestContext.Current.CancellationToken));
        }

        Assert.False(await registry.CompleteSlackPairingAsync(slackAttachmentId, ownerUserId, "U-ATTACKER", "D-ATTACKER", correctCode,
            TestContext.Current.CancellationToken));
        Assert.DoesNotContain(channelHost.Response.Settings, setting => setting.FieldKey == "slack_pairing_code_hash");
        Assert.Equal("Too many incorrect codes. Generate a new pairing code to try again.",
            await registry.GetSlackPairingFailureReasonAsync(ownerUserId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MixedTelegramAndSlackUserPayloadKeepsEachAttachmentsOwnTetherMode()
    {
        const string slackId = "slack:user-1:default";
        var conversationMaps = new AttachmentConversationMapStore();
        conversationMaps.Add(new ChannelConversationMapEntry("telegram-map", "telegram", attachmentId, "agent", "agent-1",
            "device-1", "target:AGENT:agent-1", "permanent_topic", "telegram-session", "created", "updated", null));
        conversationMaps.Add(new ChannelConversationMapEntry("slack-map", "slack", slackId, "agent", "agent-1",
            "D123", "target:AGENT:agent-1", "permanent_topic", "slack-session", "created", "updated", null));
        var channelHost = new StubChannelHost(BuildAttachment(hitlTetherMode: "hitl"));
        ChannelAttachmentRegistry registry = CreateRegistry(channelHost, SuccessfulCredentialResolver(), conversationMaps);
        await registry.RefreshAttachmentAsync(attachmentId, ownerUserId, TestContext.Current.CancellationToken);

        channelHost.Response = BuildAttachment(providerKey: "slack", id: slackId, paired: true, hitlTetherMode: "mirror");
        await registry.RefreshAttachmentAsync(slackId, ownerUserId, TestContext.Current.CancellationToken);

        Assert.Equal(Kautuka.Gateway.Domain.Hitl.ChannelHitlTetherMode.Hitl, registry.GetChannelHitlTetherMode(ownerUserId, "telegram-session"));
        Assert.Equal(Kautuka.Gateway.Domain.Hitl.ChannelHitlTetherMode.Mirror, registry.GetChannelHitlTetherMode(ownerUserId, "slack-session"));
        GatewayUserListItem payload = registry.BuildUserPayload(new GatewayUser(ownerUserId, "owner", "password", "admin", true, null, "Owner"));
        Assert.Collection(payload.ChannelTetherModes.OrderBy(static item => item.ProviderKey, StringComparer.Ordinal),
            item => { Assert.Equal("slack", item.ProviderKey); Assert.Equal("mirror", item.HitlTetherMode); },
            item => { Assert.Equal("telegram", item.ProviderKey); Assert.Equal("hitl", item.HitlTetherMode); });
    }

    [Fact]
    public async Task AttachmentWithoutStoredTetherModeDefaultsToMirror()
    {
        GetAttachmentResponse response = BuildAttachment();
        response.Settings.Remove(response.Settings.Single(setting => setting.FieldKey == "hitl_tether_mode"));
        var channelHost = new StubChannelHost(response);
        ChannelAttachmentRegistry registry = CreateRegistry(channelHost, SuccessfulCredentialResolver());

        await registry.RefreshAttachmentAsync(attachmentId, ownerUserId, TestContext.Current.CancellationToken);

        GatewayUser user = new(ownerUserId, "owner", "password", "admin", true, null, "Owner");
        GatewayUserListItem userPayload = registry.BuildUserPayload(user);
        Assert.Equal("mirror", Assert.Single(userPayload.ChannelTetherModes).HitlTetherMode);

        GatewayUserChannelsResponse channelsPayload = await registry.BuildUserChannelCatalogAsync(
            user,
            null,
            TestContext.Current.CancellationToken);
        Assert.Equal("mirror", Assert.Single(channelsPayload.Channels).HitlTetherMode);
    }

    private static ChannelAttachmentRegistry CreateRegistry(
        StubChannelHost channelHost,
        CredentialResolverService credentialResolver,
        IChannelConversationMapStore? conversationMaps = null,
        ILogger<ChannelAttachmentRegistry>? logger = null)
        => new(CreateState(), channelHost, credentialResolver, logger ?? NullLogger<ChannelAttachmentRegistry>.Instance, null, conversationMaps);

    private static GatewayState CreateState()
        => GatewayState.Create(
            null!,
            NullLogger<GatewayState>.Instance,
            null!,
            null!,
            null!,
            null!,
            new GatewayMemoryStore());

    private static GetAttachmentResponse BuildAttachment(
        bool includeCredentialReference = false,
        string providerKey = "telegram",
        string id = attachmentId,
        bool paired = false,
        string? pairingCode = null,
        string hitlTetherMode = "hitl")
    {
        GetAttachmentResponse response = new()
        {
            Attachment = new AttachmentSummary
            {
                AttachmentId = id,
                ProviderKey = providerKey,
                DisplayName = providerKey,
                Status = AttachmentStatus.Active,
                SchemaVersion = providerKey + "/v1",
                CapabilityVersion = providerKey + "/v1",
            },
        };
        response.Settings.Add(new AttachmentSettingValue { FieldKey = "owner_user_id", StringValue = ownerUserId });
        response.Settings.Add(new AttachmentSettingValue { FieldKey = "hitl_tether_mode", StringValue = hitlTetherMode });
        if (providerKey == "telegram")
        {
            response.Settings.Add(new AttachmentSettingValue { FieldKey = "channel_device_id", StringValue = "device-1" });
        }

        if (paired)
        {
            response.Settings.Add(new AttachmentSettingValue { FieldKey = "slack_owner_user_id", StringValue = "U123" });
            response.Settings.Add(new AttachmentSettingValue { FieldKey = "slack_dm_channel_id", StringValue = "D123" });
        }

        if (pairingCode is not null)
        {
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{id}\0{ownerUserId}\0{pairingCode}")));
            response.Settings.Add(new AttachmentSettingValue { FieldKey = "slack_pairing_code_hash", StringValue = hash });
            response.Settings.Add(new AttachmentSettingValue
            {
                FieldKey = "slack_pairing_expires_at",
                StringValue = DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"),
            });
            response.Settings.Add(new AttachmentSettingValue { FieldKey = "slack_pairing_failed_attempts", StringValue = "0" });
        }

        if (includeCredentialReference)
        {
            response.CredentialReferences.Add(new AttachmentCredentialReference
            {
                FieldKey = "bot_token",
                CredentialKey = $"channel.{id}.bot_token",
            });
        }

        return response;
    }

    private static CredentialResolverService SuccessfulCredentialResolver()
        => CreateCredentialResolver(new CredentialMapResponse());

    private static CredentialResolverService FailingCredentialResolver(Exception exception)
        => CreateCredentialResolver(exception);

    private static CredentialResolverService CreateCredentialResolver(object response)
    {
        var client = new CredentialsService.CredentialsServiceClient(new StubCallInvoker(_ => response));
        return new CredentialResolverService(
            new Lazy<CredentialsService.CredentialsServiceClient>(() => client),
            "https://127.0.0.1:8181");
    }

    private sealed class StubChannelHost(GetAttachmentResponse response) : IChannelHostGrpcService
    {
        public GetAttachmentResponse Response { get; set; } = response;
        public Exception? ReadFailure { get; set; }
        public ProvisionAttachmentResponse ProvisionResponse { get; set; } = new() { Status = ProvisioningStatus.Succeeded };
        public int GetAttachmentCalls { get; private set; }

        public ValueTask<GetAttachmentResponse> GetAttachmentAsync(GetAttachmentRequest request, CancellationToken cancellationToken = default)
        {
            GetAttachmentCalls++;
            return ReadFailure is null
                ? ValueTask.FromResult(Response)
                : ValueTask.FromException<GetAttachmentResponse>(ReadFailure);
        }

        public ValueTask<ProvisionAttachmentResponse> ProvisionAttachmentAsync(ProvisionAttachmentRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(ProvisionResponse);

        public ValueTask<SetRuntimeCredentialsResponse> SetRuntimeCredentialsAsync(SetRuntimeCredentialsRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SetRuntimeCredentialsResponse());

        public ValueTask<ChannelHostPingResponse> PingAsync(ChannelHostPingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<ListChannelProvidersResponse> ListProvidersAsync(ListChannelProvidersRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new ListChannelProvidersResponse());
        public ValueTask<GetAttachmentsResponse> GetAttachmentsAsync(GetAttachmentsRequest request, CancellationToken cancellationToken = default)
        {
            GetAttachmentsResponse attachments = new();
            attachments.Attachments.Add(Response.Attachment.Clone());
            return ValueTask.FromResult(attachments);
        }
        public ValueTask<GetAttachmentSchemaResponse> GetAttachmentSchemaAsync(GetAttachmentSchemaRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<UpsertAttachmentResponse> UpsertAttachmentAsync(UpsertAttachmentRequest request, CancellationToken cancellationToken = default)
        {
            GetAttachmentResponse updated = new()
            {
                Attachment = Response.Attachment.Clone(),
            };
            updated.Settings.AddRange(request.Settings);
            updated.CredentialReferences.AddRange(request.CredentialReferences);
            Response = updated;
            return ValueTask.FromResult(new UpsertAttachmentResponse { ValidationStatus = ValidationStatus.Accepted });
        }
        public ValueTask<SyncOwnerTargetsResponse> SyncOwnerTargetsAsync(SyncOwnerTargetsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<DeactivateAttachmentResponse> DeactivateAttachmentAsync(DeactivateAttachmentRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SendOutboundMessageResponse> SendOutboundMessageAsync(SendOutboundMessageRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ReadInboundMediaChunk> ReadInboundMediaAsync(ReadInboundMediaRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
        public async IAsyncEnumerable<ChannelInboundEventMessage> StreamIngressAsync(ChannelIngressSubscribeRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
        public ValueTask<AcknowledgeIngressResponse> AcknowledgeIngressAsync(AcknowledgeIngressRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class AttachmentConversationMapStore : IChannelConversationMapStore
    {
        private readonly Dictionary<string, ChannelConversationMapEntry> maps = new(StringComparer.Ordinal);
        public void Add(ChannelConversationMapEntry entry) => maps[$"{entry.SessionId}:{entry.ChannelType}"] = entry;
        public ChannelConversationMapEntry? TryResolveBySession(string sessionId, string channelType)
            => maps.TryGetValue($"{sessionId}:{channelType}", out ChannelConversationMapEntry? entry) ? entry : null;
        public ChannelConversationMapEntry GetOrCreatePermanentTopic(string channelType, string registrationId, string targetType, string targetId, string providerConversationId, string providerTopicId, string? sessionId) => throw new NotSupportedException();
        public ChannelConversationMapEntry CreateTransientThread(string channelType, string registrationId, string targetType, string targetId, string providerConversationId, string providerThreadId, string? sessionId, DateTimeOffset expiresAtUtc) => throw new NotSupportedException();
        public ChannelConversationMapEntry? TryResolve(string channelType, string registrationId, string providerConversationId, string providerThreadId) => null;
        public ChannelConversationMapEntry? TryResolveLatestByRegistration(string channelType, string registrationId) => null;
        public ChannelConversationMapEntry? AttachSession(string entryId, string sessionId, DateTimeOffset? expiresAtUtc = null) => null;
        public int CleanupExpiredTransientThreads(DateTimeOffset nowUtc) => 0;
    }

    private sealed class StubCallInvoker(Func<IMethod, object> responseFactory) : CallInvoker
    {
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            object response = responseFactory(method);
            Task<TResponse> responseTask = response is Exception exception
                ? Task.FromException<TResponse>(exception)
                : Task.FromResult((TResponse)response);
            return new AsyncUnaryCall<TResponse>(responseTask, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
        }

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add(formatter(state, exception));
    }
}
