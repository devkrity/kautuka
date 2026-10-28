using Kautuka.ChannelHost.Grpc;
using Kautuka.Protos;

namespace Kautuka.ChannelHost.Providers.Telegram;

internal sealed partial class TelegramLongPollingWorker(
    ChannelAttachmentStore attachmentStore,
    ChannelIngressStore ingressStore,
    ChannelOutboundAssetStore outboundAssets,
    IHttpClientFactory httpClientFactory,
    ILogger<TelegramLongPollingWorker> logger,
    ChannelResourceGovernor? resourceGovernor = null) : IChannelAdapter
{
    // Process-lifetime stripes serialize each registration without an unbounded lock cache.
    private static readonly SemaphoreSlim[] provisioningGates = [.. Enumerable.Range(0, 64).Select(static _ => new SemaphoreSlim(1, 1))];

    public string ProviderKey => "telegram";
    public string DisplayName => "Telegram";
    public string SchemaVersion => "telegram/v1";
    public string CapabilityVersion => "telegram/v1";
    public ChannelProviderCapabilities Capabilities => ChannelProviderCapabilityCatalog.Telegram;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await RunSupervisedPollingAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Streams inbound Telegram events to the caller (Gateway).</summary>
    public async IAsyncEnumerable<ChannelInboundEventMessage> StreamIngressAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (ChannelInboundEventMessage message in ingressStore.StreamAsync("adapter-telegram", cancellationToken).ConfigureAwait(false))
            yield return message;
    }

    public IReadOnlyList<AttachmentSchemaField> GetSchema()
        =>
        [
            new AttachmentSchemaField { FieldKey = "channel_device_id", Label = "Telegram Chat ID", Description = "Required. The private chat or group chat ID Kautuka is allowed to use for this Telegram attachment.", FieldType = AttachmentSchemaFieldType.Text, Required = true },
            new AttachmentSchemaField { FieldKey = "bot_token", Label = "Bot Token", Description = "The Telegram Bot API token.", FieldType = AttachmentSchemaFieldType.SecretText, Required = true, Secret = true },
            new AttachmentSchemaField { FieldKey = "poll_timeout_seconds", Label = "Poll Timeout Seconds", Description = "Timeout for long polling in seconds (default 25).", FieldType = AttachmentSchemaFieldType.Number, Required = false, DefaultValue = "25" }
        ];

    public async Task<ChannelProviderHealth> GetHealthAsync(CancellationToken cancellationToken)
    {
        if (!pollingIssues.IsEmpty)
            return new ChannelProviderHealth { ProviderKey = ProviderKey, DisplayName = DisplayName, Status = "degraded", Detail = "bot_polling_requires_attention" };
        try
        {
            HttpClient client = httpClientFactory.CreateClient("telegram-api");
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using HttpResponseMessage response = await client.GetAsync(new Uri("https://api.telegram.org/"), cts.Token).ConfigureAwait(false);
            return new ChannelProviderHealth
            {
                ProviderKey = ProviderKey,
                DisplayName = DisplayName,
                Status = response.IsSuccessStatusCode ? "healthy" : "degraded",
                Detail = response.IsSuccessStatusCode ? "reachable" : $"http_{(int)response.StatusCode}",
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or TaskCanceledException)
        {
            return new ChannelProviderHealth
            {
                ProviderKey = ProviderKey,
                DisplayName = DisplayName,
                Status = "not_configured",
                Detail = ex.GetType().Name,
            };
        }
    }

}
