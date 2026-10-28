using System.Text.Json;
using System.Text.Json.Nodes;

using Grpc.Core;

using Kautuka.Gateway.Domain.Channels;
using Kautuka.Gateway.Domain.Memory;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Infrastructure.Channels;
using Kautuka.Gateway.Infrastructure.Credentials;
using Kautuka.Protos;

using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class ChannelAttachmentRoleTests
{
    [Fact]
    public void ParsesChatAgentAndWorkflowAttachmentIds()
    {
        Assert.True(ChannelAttachmentIds.TryParse("telegram:user-1:default", out ChannelAttachmentIdentity chat));
        Assert.Equal(ChannelAttachmentRole.Chat, chat.Role);
        Assert.True(ChannelAttachmentIds.IsChat(chat.ProviderKey + ":" + chat.OwnerUserId + ":default"));

        Assert.True(ChannelAttachmentIds.TryParse("telegram:user-1:agent:agent-1", out ChannelAttachmentIdentity agent));
        Assert.Equal(ChannelAttachmentRole.Agent, agent.Role);
        Assert.Equal("agent-1", agent.SubjectId);
        Assert.False(ChannelAttachmentIds.IsChat("telegram:user-1:agent:agent-1"));

        Assert.True(ChannelAttachmentIds.TryParse("telegram:user-1:workflows", out ChannelAttachmentIdentity shared));
        Assert.Equal(ChannelAttachmentRole.Workflows, shared.Role);

        Assert.True(ChannelAttachmentIds.TryParse("telegram:user-1:workflow:wf-1", out ChannelAttachmentIdentity one));
        Assert.Equal(ChannelAttachmentRole.Workflow, one.Role);
        Assert.Equal("wf-1", one.SubjectId);
    }

    [Fact]
    public async Task AgentAttachmentDoesNotReplaceTheUserChatRegistration()
    {
        var host = new RoleChannelHost();
        host.Store(ActiveAttachment("telegram:user-1:default", "user-1", "chat-default"));
        ChannelAttachmentRegistry registry = CreateRegistry(host);

        await registry.RefreshAttachmentAsync("telegram:user-1:default", "user-1", TestContext.Current.CancellationToken);
        host.Store(ActiveAttachment("telegram:user-1:agent:agent-1", "user-1", "chat-agent"));
        await registry.RefreshAttachmentAsync("telegram:user-1:agent:agent-1", "user-1", TestContext.Current.CancellationToken);

        Assert.True(registry.TryResolveActiveRegistration("user-1", out string? registrationId, out string? chatId));
        Assert.Equal("telegram:user-1:default", registrationId);
        Assert.Equal("chat-default", chatId);
        Assert.True(registry.TryResolveAgentBot("user-1", "agent-1", out string? agentRegistrationId, out string? agentChatId));
        Assert.Equal("telegram:user-1:agent:agent-1", agentRegistrationId);
        Assert.Equal("chat-agent", agentChatId);
        Assert.False(registry.TryResolveWorkflowReviewBot("user-1", "wf-1", out _, out _));
    }

    [Fact]
    public async Task WorkflowOverrideWinsOverTheSharedWorkflowBot()
    {
        var host = new RoleChannelHost();
        host.Store(ActiveAttachment("telegram:user-1:workflows", "user-1", "chat-shared"));
        host.Store(ActiveAttachment("telegram:user-1:workflow:wf-1", "user-1", "chat-one"));
        ChannelAttachmentRegistry registry = CreateRegistry(host);
        await registry.RefreshAttachmentAsync("telegram:user-1:workflows", "user-1", TestContext.Current.CancellationToken);
        await registry.RefreshAttachmentAsync("telegram:user-1:workflow:wf-1", "user-1", TestContext.Current.CancellationToken);

        Assert.True(registry.TryResolveWorkflowReviewBot("user-1", "wf-1", out string? overrideId, out string? overrideChat));
        Assert.Equal("telegram:user-1:workflow:wf-1", overrideId);
        Assert.Equal("chat-one", overrideChat);
        Assert.True(registry.TryResolveWorkflowReviewBot("user-1", "wf-other", out string? sharedId, out string? sharedChat));
        Assert.Equal("telegram:user-1:workflows", sharedId);
        Assert.Equal("chat-shared", sharedChat);
        Assert.False(registry.TryResolveActiveRegistration("user-1", out _, out _));
    }

    [Fact]
    public async Task SavingAnAgentBotRejectsATokenAlreadyUsedByTheUserChatBot()
    {
        GatewayState state = CreateState();
        state.Users["user-1"] = new GatewayUser("user-1", "owner", "x", "user", true, null, null);
        state.Agents["agent-1"] = new GatewayAgent(
            "agent-1",
            "Agent",
            "model-1",
            "prompt",
            "disabled",
            new JsonObject(),
            new JsonObject(),
            new JsonObject(),
            [],
            "user-1",
            DateTimeOffset.UtcNow.ToString("O"),
            Scope: GatewayOwnershipScopes.User);
        var host = new RoleChannelHost();
        host.Store(ActiveAttachment("telegram:user-1:default", "user-1", "chat-default"));
        var credentials = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["channel.telegram:user-1:default.bot_token"] = "same-token",
        };
        ChannelAttachmentRegistry registry = CreateRegistry(host, state, credentials);

        DuplicateChannelBotTokenException exception = await Assert.ThrowsAsync<DuplicateChannelBotTokenException>(() =>
            registry.UpdateUserChannelSettingsAsync(
                state.Users["user-1"],
                "telegram",
                JsonSerializer.SerializeToElement(new Dictionary<string, object>
                {
                    ["attachmentRole"] = "agent",
                    ["attachmentSubject"] = "agent-1",
                    ["settings"] = new Dictionary<string, string>
                    {
                        ["channel_device_id"] = "chat-agent",
                        ["bot_token"] = "same-token",
                    },
                }),
                TestContext.Current.CancellationToken));

        Assert.Equal(DuplicateChannelBotTokenException.Detail, exception.Message);
        Assert.Equal(0, host.UpsertCount);
        Assert.False(credentials.ContainsKey("channel.telegram:user-1:agent:agent-1.bot_token"));
    }

    [Fact]
    public async Task SavingAnAgentBotLeavesTheUserChatRegistrationInPlace()
    {
        GatewayState state = CreateState();
        state.Users["user-1"] = new GatewayUser("user-1", "owner", "x", "user", true, null, null);
        state.Agents["agent-1"] = new GatewayAgent(
            "agent-1",
            "Agent",
            "model-1",
            "prompt",
            "disabled",
            new JsonObject(),
            new JsonObject(),
            new JsonObject(),
            [],
            "user-1",
            DateTimeOffset.UtcNow.ToString("O"),
            Scope: GatewayOwnershipScopes.User);
        var host = new RoleChannelHost();
        host.Store(ActiveAttachment("telegram:user-1:default", "user-1", "chat-default"));
        ChannelAttachmentRegistry registry = CreateRegistry(host, state, []);
        await registry.RefreshAttachmentAsync("telegram:user-1:default", "user-1", TestContext.Current.CancellationToken);

        ChannelSettingsUpdateResult? updated = await registry.UpdateUserChannelSettingsAsync(
            state.Users["user-1"],
            "telegram",
            JsonSerializer.SerializeToElement(new Dictionary<string, object>
            {
                ["attachmentRole"] = "agent",
                ["attachmentSubject"] = "agent-1",
                ["settings"] = new Dictionary<string, string>
                {
                    ["channel_device_id"] = "chat-agent",
                    ["bot_token"] = "agent-token",
                },
            }),
            TestContext.Current.CancellationToken);

        Assert.NotNull(updated);
        Assert.Equal("telegram:user-1:agent:agent-1", updated.RegistrationId);
        Assert.Equal("chat-default", updated.ExternalUserId);
        Assert.True(registry.TryResolveAgentBot("user-1", "agent-1", out _, out string? agentChatId));
        Assert.Equal("chat-default", agentChatId);
        Assert.True(registry.TryResolveActiveRegistration("user-1", out string? registrationId, out string? chatId));
        Assert.Equal("telegram:user-1:default", registrationId);
        Assert.Equal("chat-default", chatId);
        GatewayChannelTargetBinding[] bindings = registry.GetBindings("telegram:user-1:agent:agent-1");
        Assert.Single(bindings);
        Assert.Equal("agent-1", bindings[0].TargetId);
        Assert.True(bindings[0].IsDefault);
    }

    [Fact]
    public async Task SavingAWorkflowBotCopiesTheAccountDeviceId()
    {
        GatewayState state = CreateState();
        state.Users["user-1"] = new GatewayUser("user-1", "owner", "x", "user", true, null, null);
        var host = new RoleChannelHost();
        host.Store(ActiveAttachment("telegram:user-1:default", "user-1", "chat-default"));
        ChannelAttachmentRegistry registry = CreateRegistry(host, state, []);

        ChannelSettingsUpdateResult? updated = await registry.UpdateUserChannelSettingsAsync(
            state.Users["user-1"],
            "telegram",
            JsonSerializer.SerializeToElement(new Dictionary<string, object>
            {
                ["attachmentRole"] = "workflows",
                ["settings"] = new Dictionary<string, string>
                {
                    ["channel_device_id"] = "someone-else",
                    ["bot_token"] = "workflow-token",
                },
            }),
            TestContext.Current.CancellationToken);

        Assert.NotNull(updated);
        Assert.Equal("chat-default", updated.ExternalUserId);
        Assert.True(registry.TryResolveWorkflowReviewBot("user-1", "wf-other", out _, out string? sharedChat));
        Assert.Equal("chat-default", sharedChat);
    }

    [Fact]
    public async Task SavingAScopedBotFailsWhenTheAccountHasNoDeviceId()
    {
        GatewayState state = CreateState();
        state.Users["user-1"] = new GatewayUser("user-1", "owner", "x", "user", true, null, null);
        state.Agents["agent-1"] = new GatewayAgent(
            "agent-1",
            "Agent",
            "model-1",
            "prompt",
            "disabled",
            new JsonObject(),
            new JsonObject(),
            new JsonObject(),
            [],
            "user-1",
            DateTimeOffset.UtcNow.ToString("O"),
            Scope: GatewayOwnershipScopes.User);
        var host = new RoleChannelHost();
        ChannelAttachmentRegistry registry = CreateRegistry(host, state, []);

        ChannelSettingsUpdateResult? updated = await registry.UpdateUserChannelSettingsAsync(
            state.Users["user-1"],
            "telegram",
            JsonSerializer.SerializeToElement(new Dictionary<string, object>
            {
                ["attachmentRole"] = "agent",
                ["attachmentSubject"] = "agent-1",
                ["settings"] = new Dictionary<string, string>
                {
                    ["channel_device_id"] = "someone-else",
                    ["bot_token"] = "agent-token",
                },
            }),
            TestContext.Current.CancellationToken);

        Assert.Null(updated);
        Assert.Equal(0, host.UpsertCount);
    }

    [Fact]
    public async Task SavingTheAccountBotKeepsTheRequestedDeviceId()
    {
        GatewayState state = CreateState();
        state.Users["user-1"] = new GatewayUser("user-1", "owner", "x", "user", true, null, null);
        var host = new RoleChannelHost();
        ChannelAttachmentRegistry registry = CreateRegistry(host, state, []);

        ChannelSettingsUpdateResult? updated = await registry.UpdateUserChannelSettingsAsync(
            state.Users["user-1"],
            "telegram",
            JsonSerializer.SerializeToElement(new Dictionary<string, object>
            {
                ["settings"] = new Dictionary<string, string>
                {
                    ["channel_device_id"] = "chat-default",
                    ["bot_token"] = "account-token",
                },
            }),
            TestContext.Current.CancellationToken);

        Assert.NotNull(updated);
        Assert.Equal("chat-default", updated.ExternalUserId);
        Assert.True(registry.TryResolveActiveRegistration("user-1", out _, out string? chatId));
        Assert.Equal("chat-default", chatId);
    }

    private static ChannelAttachmentRegistry CreateRegistry(
        RoleChannelHost host,
        GatewayState? state = null,
        Dictionary<string, string>? credentials = null)
        => new(
            state ?? CreateState(),
            host,
            CreateCredentialResolver(credentials ?? []),
            NullLogger<ChannelAttachmentRegistry>.Instance);

    private static GatewayState CreateState()
        => GatewayState.Create(
            null!,
            NullLogger<GatewayState>.Instance,
            null!,
            null!,
            null!,
            null!,
            new GatewayMemoryStore());

    private static GetAttachmentResponse ActiveAttachment(string attachmentId, string ownerUserId, string chatId)
    {
        GetAttachmentResponse response = new()
        {
            Attachment = new AttachmentSummary
            {
                AttachmentId = attachmentId,
                ProviderKey = "telegram",
                DisplayName = "Telegram",
                Status = AttachmentStatus.Active,
                SchemaVersion = "telegram/v1",
                CapabilityVersion = "telegram/v1",
            },
        };
        response.Settings.Add(new AttachmentSettingValue { FieldKey = "owner_user_id", StringValue = ownerUserId });
        response.Settings.Add(new AttachmentSettingValue { FieldKey = "channel_device_id", StringValue = chatId });
        return response;
    }

    private static CredentialResolverService CreateCredentialResolver(Dictionary<string, string> credentials)
    {
        var client = new CredentialsService.CredentialsServiceClient(new CredentialCallInvoker(credentials));
        return new CredentialResolverService(
            new Lazy<CredentialsService.CredentialsServiceClient>(() => client),
            "https://127.0.0.1:8181");
    }

    private sealed class RoleChannelHost : IChannelHostGrpcService
    {
        private readonly Dictionary<string, GetAttachmentResponse> attachments = new(StringComparer.Ordinal);

        public int UpsertCount { get; private set; }

        public void Store(GetAttachmentResponse response)
            => attachments[response.Attachment.AttachmentId] = response;

        public ValueTask<GetAttachmentResponse> GetAttachmentAsync(GetAttachmentRequest request, CancellationToken cancellationToken = default)
            => attachments.TryGetValue(request.AttachmentId, out GetAttachmentResponse? response)
                ? ValueTask.FromResult(response)
                : ValueTask.FromException<GetAttachmentResponse>(new RpcException(new Status(StatusCode.NotFound, "missing")));

        public ValueTask<GetAttachmentsResponse> GetAttachmentsAsync(GetAttachmentsRequest request, CancellationToken cancellationToken = default)
        {
            GetAttachmentsResponse response = new();
            foreach (GetAttachmentResponse attachment in attachments.Values)
            {
                response.Attachments.Add(attachment.Attachment);
            }

            return ValueTask.FromResult(response);
        }

        public ValueTask<ListChannelProvidersResponse> ListProvidersAsync(ListChannelProvidersRequest request, CancellationToken cancellationToken = default)
        {
            ListChannelProvidersResponse response = new();
            response.Providers.Add(new ChannelProviderDefinition
            {
                ProviderKey = "telegram",
                DisplayName = "Telegram",
                SchemaVersion = "telegram/v1",
                CapabilityVersion = "telegram/v1",
                Fields =
                {
                    new AttachmentSchemaField { FieldKey = "channel_device_id", FieldType = AttachmentSchemaFieldType.Text, Required = true },
                    new AttachmentSchemaField { FieldKey = "bot_token", FieldType = AttachmentSchemaFieldType.SecretText, Required = true, Secret = true },
                },
            });
            return ValueTask.FromResult(response);
        }

        public ValueTask<UpsertAttachmentResponse> UpsertAttachmentAsync(UpsertAttachmentRequest request, CancellationToken cancellationToken = default)
        {
            UpsertCount++;
            GetAttachmentResponse stored = new()
            {
                Attachment = new AttachmentSummary
                {
                    AttachmentId = request.AttachmentId,
                    ProviderKey = request.ProviderKey,
                    DisplayName = "Telegram",
                    Status = AttachmentStatus.Active,
                    SchemaVersion = request.ExpectedSchemaVersion,
                    CapabilityVersion = "telegram/v1",
                },
            };
            stored.Settings.AddRange(request.Settings);
            stored.CredentialReferences.AddRange(request.CredentialReferences);
            attachments[request.AttachmentId] = stored;
            return ValueTask.FromResult(new UpsertAttachmentResponse());
        }

        public ValueTask<SetRuntimeCredentialsResponse> SetRuntimeCredentialsAsync(SetRuntimeCredentialsRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SetRuntimeCredentialsResponse());

        public ValueTask<ProvisionAttachmentResponse> ProvisionAttachmentAsync(ProvisionAttachmentRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new ProvisionAttachmentResponse { Status = ProvisioningStatus.Succeeded });

        public ValueTask<ChannelHostPingResponse> PingAsync(ChannelHostPingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<GetAttachmentSchemaResponse> GetAttachmentSchemaAsync(GetAttachmentSchemaRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SyncOwnerTargetsResponse> SyncOwnerTargetsAsync(SyncOwnerTargetsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<DeactivateAttachmentResponse> DeactivateAttachmentAsync(DeactivateAttachmentRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SendOutboundMessageResponse> SendOutboundMessageAsync(SendOutboundMessageRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async IAsyncEnumerable<ReadInboundMediaChunk> ReadInboundMediaAsync(ReadInboundMediaRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
        public async IAsyncEnumerable<ChannelInboundEventMessage> StreamIngressAsync(ChannelIngressSubscribeRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
        public ValueTask<AcknowledgeIngressResponse> AcknowledgeIngressAsync(AcknowledgeIngressRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class CredentialCallInvoker(Dictionary<string, string> credentials) : CallInvoker
    {
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            object response = method.Name switch
            {
                "ListUserCredentialKeys" => ListKeys(),
                "GetResolvedCredentials" => Resolve(request),
                "SetUserCredentials" => Store(request),
                "DeleteUserCredential" => new DeleteCredentialResponse { Deleted = true },
                _ => new CredentialMapResponse(),
            };
            return new AsyncUnaryCall<TResponse>(
                Task.FromResult((TResponse)response),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { });
        }

        private CredentialKeyListResponse ListKeys()
        {
            CredentialKeyListResponse response = new();
            response.Keys.AddRange(credentials.Keys);
            return response;
        }

        private CredentialMapResponse Resolve(object? request)
        {
            CredentialMapResponse response = new();
            if (request is GetResolvedCredentialsRequest resolved)
            {
                foreach (string key in resolved.RequiredKeys)
                {
                    if (credentials.TryGetValue(key, out string? value))
                    {
                        response.Entries[key] = value;
                    }
                }
            }

            return response;
        }

        private SetCredentialsResponse Store(object? request)
        {
            if (request is SetUserCredentialsRequest set)
            {
                foreach (KeyValuePair<string, string> entry in set.Entries)
                {
                    credentials[entry.Key] = entry.Value;
                }
            }

            return new SetCredentialsResponse { UpsertedCount = 1 };
        }

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
    }
}
