using Grpc.Core;

using Kautuka.ChannelHost.Hosting;
using Kautuka.ChannelHost.Providers.Slack;
using Kautuka.ChannelHost.Providers.Telegram;
using Kautuka.Protos;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.ChannelHost.Tests;

public sealed class ChannelAttachmentSecretHandlingTests
{
    [Fact]
    public void AdmittedProviderSecretFieldsAreAlwaysSecretText()
    {
        AssertSecretFields(
            new TelegramLongPollingWorker(null!, null!, null!, null!, null!).GetSchema(),
            "telegram",
            ["bot_token"]);
        AssertSecretFields(
            new SlackSocketModeWorker(null!, null!, null!, null!, null!, null!).GetSchema(),
            "slack",
            ["bot_token", "app_token"]);
    }

    [Theory]
    [InlineData("telegram", "telegram/v1", "bot_token")]
    [InlineData("slack", "slack/v1", "bot_token")]
    [InlineData("slack", "slack/v1", "app_token")]
    public void PlaintextSecretSettingIsRejectedBeforeAttachmentIsPersisted(
        string providerKey,
        string schemaVersion,
        string secretField)
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-attachment-secret-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Kautuka:ContainerStateRoot"] = root })
                .Build();
            ChannelHostDatabase database = new(configuration);
            database.EnsureReady();
            var store = new ChannelAttachmentStore(database);
            string attachmentId = providerKey + "-secret-rejection";

            Assert.Throws<InvalidOperationException>(() => store.UpsertAttachment(new UpsertAttachmentRequest
            {
                AttachmentId = attachmentId,
                ProviderKey = providerKey,
                ExpectedSchemaVersion = schemaVersion,
                Settings =
                {
                    new AttachmentSettingValue { FieldKey = "owner_user_id", StringValue = "owner-1" },
                    new AttachmentSettingValue { FieldKey = "display_name", StringValue = providerKey },
                    new AttachmentSettingValue { FieldKey = secretField, StringValue = "plaintext-secret-must-never-persist" },
                },
            }));

            Assert.Null(store.GetAttachment(attachmentId));
        }
        finally
        {
            ChannelHostSqliteTestCleanup.ReleaseAndDelete(root);
        }
    }

    [Fact]
    public void OptionalChannelDeviceBooleanFalseDoesNotThrowOrBindConversation()
    {
        using AttachmentStoreFixture fixture = new();
        const string attachmentId = "telegram-optional-false";

        UpsertAttachmentResponse response = fixture.Store.UpsertAttachment(new UpsertAttachmentRequest
        {
            AttachmentId = attachmentId,
            ProviderKey = "telegram",
            ExpectedSchemaVersion = "telegram/v1",
            Settings =
            {
                new AttachmentSettingValue { FieldKey = "owner_user_id", StringValue = "owner-1" },
                new AttachmentSettingValue { FieldKey = "display_name", StringValue = "Telegram" },
                new AttachmentSettingValue { FieldKey = "channel_device_id", BoolValue = false },
            },
        });

        Assert.Equal(ValidationStatus.Accepted, response.ValidationStatus);
        Assert.False(fixture.Store.IsConversationBound(attachmentId, "false"));
        Assert.DoesNotContain(fixture.Store.GetAttachment(attachmentId)!.Settings,
            setting => setting.FieldKey == "channel_device_id");
    }

    [Fact]
    public void BlankOptionalChannelDeviceIsOmittedRatherThanPersisted()
    {
        using AttachmentStoreFixture fixture = new();
        const string attachmentId = "telegram-optional-blank";

        _ = fixture.Store.UpsertAttachment(new UpsertAttachmentRequest
        {
            AttachmentId = attachmentId,
            ProviderKey = "telegram",
            ExpectedSchemaVersion = "telegram/v1",
            Settings =
            {
                new AttachmentSettingValue { FieldKey = "owner_user_id", StringValue = "owner-1" },
                new AttachmentSettingValue { FieldKey = "display_name", StringValue = "Telegram" },
                new AttachmentSettingValue { FieldKey = "channel_device_id", StringValue = "" },
            },
        });

        Assert.DoesNotContain(fixture.Store.GetAttachment(attachmentId)!.Settings,
            setting => setting.FieldKey == "channel_device_id");
    }

    [Fact]
    public void UpsertReactivatesInactiveEvenWhenLastDeliveryStatusIsNotRecoverMarker()
    {
        using AttachmentStoreFixture fixture = new();
        const string attachmentId = "telegram-inactive-reconnect";
        fixture.Upsert(attachmentId, "telegram", "telegram/v1", channelDeviceId: "8201017430");
        Assert.True(fixture.Store.SuspendAttachment(attachmentId, "user_requested", "suspend-op"));
        Assert.True(fixture.Store.RecoverAttachment(attachmentId, "recover-op"));
        Assert.Equal(AttachmentStatus.Inactive, fixture.Store.GetAttachmentSummary(attachmentId)!.Status);

        // A stale outbound delivery must not erase reconnectability.
        fixture.Store.MarkDeliveryStatus(attachmentId, "delivered");
        Assert.Equal(AttachmentStatus.Inactive, fixture.Store.GetAttachmentSummary(attachmentId)!.Status);
        Assert.Equal("recover_requires_validation", fixture.Store.GetAttachmentSummary(attachmentId)!.LastDeliveryStatus);

        UpsertAttachmentResponse response = fixture.Store.UpsertAttachment(new UpsertAttachmentRequest
        {
            AttachmentId = attachmentId,
            ProviderKey = "telegram",
            ExpectedSchemaVersion = "telegram/v1",
            Settings =
            {
                new AttachmentSettingValue { FieldKey = "owner_user_id", StringValue = "owner-1" },
                new AttachmentSettingValue { FieldKey = "display_name", StringValue = "Telegram" },
                new AttachmentSettingValue { FieldKey = "channel_device_id", StringValue = "8201017430" },
            },
        });

        Assert.Equal(AttachmentStatus.Active, response.Attachment.Status);
        Assert.Equal(AttachmentStatus.Active, fixture.Store.GetAttachmentSummary(attachmentId)!.Status);
    }

    [Fact]
    public void ProvisioningTelegramRegistrationRetainsCredentialsButLeavesActiveList()
    {
        using AttachmentStoreFixture fixture = new();
        const string attachmentId = "telegram-provisioning";
        fixture.Upsert(attachmentId, "telegram", "telegram/v1", channelDeviceId: "123456789");
        fixture.Credentials.Replace(attachmentId, [new RuntimeCredentialValue { FieldKey = "bot_token", SecretValue = "telegram-token" }]);

        Assert.True(fixture.Store.MarkProvisioningStarted(attachmentId, "provision-telegram"));
        Assert.True(fixture.Store.TryGetTelegramRegistrationForProvisioning(attachmentId, out TelegramAttachmentRegistration? registration));
        Assert.Equal("telegram-token", registration!.BotToken);
        Assert.Empty(fixture.Store.ListActiveTelegramRegistrations());
    }

    [Fact]
    public async Task TelegramProvisioningUsesProvisioningLookupWhileInboundMediaRemainsActiveOnly()
    {
        using AttachmentStoreFixture fixture = new();
        using var handler = new SuccessfulTelegramProvisioningHandler();
        using var httpClientFactory = new SingleHttpClientFactory(handler);
        const string attachmentId = "telegram-provider-provisioning";
        fixture.Upsert(attachmentId, "telegram", "telegram/v1", channelDeviceId: "123456789");
        fixture.Credentials.Replace(attachmentId, [new RuntimeCredentialValue { FieldKey = "bot_token", SecretValue = "telegram-token" }]);
        Assert.True(fixture.Store.MarkProvisioningStarted(attachmentId, "provision-telegram-provider"));
        var worker = new TelegramLongPollingWorker(
            fixture.Store,
            null!,
            null!,
            httpClientFactory,
            NullLogger<TelegramLongPollingWorker>.Instance);

        RpcException mediaException = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            await foreach (ReadInboundMediaChunk _ in worker.ReadInboundMediaAsync(
                               attachmentId,
                               new ReadInboundMediaRequest { MediaId = "file-1" },
                               CancellationToken.None).ConfigureAwait(false))
            {
            }
        });
        Assert.Equal(StatusCode.NotFound, mediaException.StatusCode);
        Assert.Equal("registration_not_found", mediaException.Status.Detail);

        ProvisionAttachmentResponse provisioned = await worker.ProvisionAttachmentAsync(
            attachmentId,
            new ProvisionAttachmentRequest { AttachmentId = attachmentId },
            CancellationToken.None);

        Assert.Equal(ProvisioningStatus.Succeeded, provisioned.Status);
        Assert.NotEqual("attachment_not_found", provisioned.Detail);
    }

    [Theory]
    [InlineData("slack", "slack/v1")]
    public void ProvisioningGenericRegistrationRetainsCredentialsButLeavesActiveList(string providerKey, string schemaVersion)
    {
        using AttachmentStoreFixture fixture = new();
        string attachmentId = providerKey + "-provisioning";
        fixture.Upsert(attachmentId, providerKey, schemaVersion);
        fixture.Credentials.Replace(attachmentId,
        [
            new RuntimeCredentialValue { FieldKey = "bot_token", SecretValue = providerKey + "-bot-token" },
            new RuntimeCredentialValue { FieldKey = "app_token", SecretValue = providerKey + "-app-token" },
        ]);

        Assert.True(fixture.Store.MarkProvisioningStarted(attachmentId, "provision-" + providerKey));
        ChannelAttachmentRecord? registration = fixture.Store.GetAttachmentForProvisioning(attachmentId, providerKey);

        Assert.NotNull(registration);
        Assert.Equal(attachmentId, registration!.AttachmentId);
        Assert.Empty(fixture.Store.ListActiveAttachments(providerKey));
    }

    private static void AssertSecretFields(
        IReadOnlyList<AttachmentSchemaField> schema,
        string providerKey,
        IReadOnlyCollection<string> expectedSecretFields)
    {
        HashSet<string> actualSecretFields = [.. schema
            .Where(static field => field.FieldType == AttachmentSchemaFieldType.SecretText)
            .Select(static field => field.FieldKey)];

        AttachmentSchemaField[] incorrectlyClassified = [.. schema
            .Where(static field => field.FieldType == AttachmentSchemaFieldType.SecretText && !field.Secret)];

        Assert.True(
            actualSecretFields.SetEquals(expectedSecretFields),
            $"{providerKey} secret schema fields changed. Expected: {string.Join(',', expectedSecretFields)}. Actual: {string.Join(',', actualSecretFields)}.");
        Assert.True(
            incorrectlyClassified.Length == 0,
            $"{providerKey} secret-text fields must also set the schema secret flag: {string.Join(',', incorrectlyClassified.Select(static field => field.FieldKey))}.");
    }

    private sealed class AttachmentStoreFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "kautuka-attachment-optional-" + Guid.NewGuid().ToString("N"));

        public AttachmentStoreFixture()
        {
            Directory.CreateDirectory(root);
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Kautuka:ContainerStateRoot"] = root })
                .Build();
            ChannelHostDatabase database = new(configuration);
            database.EnsureReady();
            Credentials = new ChannelRuntimeCredentialStore();
            Store = new ChannelAttachmentStore(database, Credentials);
        }

        public ChannelAttachmentStore Store { get; }
        public ChannelRuntimeCredentialStore Credentials { get; }

        public void Upsert(string attachmentId, string providerKey, string schemaVersion, string? channelDeviceId = null)
        {
            var request = new UpsertAttachmentRequest
            {
                AttachmentId = attachmentId,
                ProviderKey = providerKey,
                ExpectedSchemaVersion = schemaVersion,
                Settings =
                {
                    new AttachmentSettingValue { FieldKey = "owner_user_id", StringValue = "owner-1" },
                    new AttachmentSettingValue { FieldKey = "display_name", StringValue = providerKey },
                },
            };
            if (!string.IsNullOrWhiteSpace(channelDeviceId))
            {
                request.Settings.Add(new AttachmentSettingValue
                {
                    FieldKey = "channel_device_id",
                    StringValue = channelDeviceId,
                });
            }

            _ = Store.UpsertAttachment(request);
        }

        public void Dispose()
        {
            ChannelHostSqliteTestCleanup.ReleaseAndDelete(root);
        }
    }

    private sealed class SingleHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient client = new(handler, disposeHandler: false);
        public HttpClient CreateClient(string name) => client;
        public void Dispose() => client.Dispose();
    }

    private sealed class SuccessfulTelegramProvisioningHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true,\"result\":true}"),
            });
    }
}
