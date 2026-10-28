using System.Text.Json;
using System.Text.Json.Nodes;

using Google.Protobuf;

using Grpc.Core;

using Kautuka.Protos;

namespace Kautuka.ChannelHost.Providers.Telegram;

internal sealed partial class TelegramLongPollingWorker
{
    public async IAsyncEnumerable<ReadInboundMediaChunk> ReadInboundMediaAsync(
        string attachmentId,
        ReadInboundMediaRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!attachmentStore.TryGetTelegramRegistration(attachmentId, out TelegramAttachmentRegistration? registration)
            || registration is null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "registration_not_found"));
        }

        HttpClient telegramClient = httpClientFactory.CreateClient("telegram-api");
        JsonObject body = new()
        {
            ["file_id"] = request.MediaId,
        };

        using HttpResponseMessage getFileResponse = await telegramClient.PostAsJsonAsync(
            $"https://api.telegram.org/bot{registration.BotToken}/getFile",
            body,
            TelegramJsonContext.Default.JsonObject,
            cancellationToken).ConfigureAwait(false);

        if (!getFileResponse.IsSuccessStatusCode)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "media_not_found"));
        }

        using Stream getFileStream = await getFileResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument getFileDocument = await JsonDocument.ParseAsync(getFileStream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!getFileDocument.RootElement.TryGetProperty("ok", out JsonElement okElement)
            || okElement.ValueKind != JsonValueKind.True
            || !getFileDocument.RootElement.TryGetProperty("result", out JsonElement result)
            || !result.TryGetProperty("file_path", out JsonElement filePathElement)
            || string.IsNullOrWhiteSpace(filePathElement.GetString()))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "media_not_found"));
        }

        string filePath = filePathElement.GetString()!;
        long sizeBytes = result.TryGetProperty("file_size", out JsonElement fileSizeElement)
            && fileSizeElement.TryGetInt64(out long parsedSize)
            ? parsedSize
            : 0;

        using HttpResponseMessage mediaResponse = await telegramClient.GetAsync(
            new Uri($"https://api.telegram.org/file/bot{registration.BotToken}/{filePath}"),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (!mediaResponse.IsSuccessStatusCode)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "media_download_failed"));
        }

        using Stream mediaStream = await mediaResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        byte[] buffer = new byte[64 * 1024];
        while (true)
        {
            int read = await mediaStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            yield return new ReadInboundMediaChunk
            {
                SizeBytes = sizeBytes,
                Content = ByteString.CopyFrom(buffer, 0, read),
                IsFinal = false,
            };
        }

        yield return new ReadInboundMediaChunk
        {
            SizeBytes = sizeBytes,
            Content = ByteString.Empty,
            IsFinal = true,
        };
    }

    public async Task<ProvisionAttachmentResponse> ProvisionAttachmentAsync(
        string attachmentId,
        ProvisionAttachmentRequest request,
        CancellationToken cancellationToken)
    {
        if (!attachmentStore.TryGetTelegramRegistrationForProvisioning(
                attachmentId,
                out TelegramAttachmentRegistration? registration)
            || registration is null)
        {
            return new ProvisionAttachmentResponse
            {
                AttachmentId = attachmentId,
                Status = ProvisioningStatus.Failed,
                Detail = "attachment_not_found",
            };
        }

        ChannelRegistrationProvisioningAction payload = new(
            registration.Settings.ValueKind == JsonValueKind.Object
                && registration.Settings.TryGetProperty("channel_device_id", out JsonElement channelDeviceElement)
                && channelDeviceElement.ValueKind == JsonValueKind.String
                    ? channelDeviceElement.GetString() ?? string.Empty
                    : string.Empty,
            request.BotName,
            request.Description,
            request.ShortDescription,
            [.. request.Commands.Select(static item => new ChannelCommandDescriptor(item.Command, item.Description))],
            [.. request.Targets.Select(static item => new ChannelTargetDescriptor(item.TargetKey, item.DisplayName))]);

        OutboxExecutionResult result = await ExecuteProvisionRegistrationAsync(
            new PollingRegistration(registration.AttachmentId, registration.OwnerUserId, registration.BotToken, registration.PollTimeoutSeconds, registration.Settings),
            System.Text.Json.JsonSerializer.Serialize(payload, ChannelContractsJsonContext.Default.ChannelRegistrationProvisioningAction),
            cancellationToken).ConfigureAwait(false);

        if (result.Success)
        {
            attachmentStore.MarkProvisioned(attachmentId, "provisioned", request.Context?.RequestId ?? Guid.NewGuid().ToString("N"));
        }

        return new ProvisionAttachmentResponse
        {
            AttachmentId = attachmentId,
            Status = result.Success ? ProvisioningStatus.Succeeded : ProvisioningStatus.Failed,
            Detail = result.Error ?? (result.Success ? "provisioned" : "provisioning_failed"),
            OperationId = request.Context?.RequestId ?? string.Empty,
        };
    }

    public async Task<SendOutboundMessageResponse> SendOutboundMessageAsync(
        string attachmentId,
        SendOutboundMessageRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Content is null || string.IsNullOrWhiteSpace(request.Content.ActionType))
        {
            return new SendOutboundMessageResponse
            {
                Accepted = false,
                DeliveryStatus = DeliveryStatus.Rejected,
                Detail = "invalid_payload",
            };
        }

        if (!attachmentStore.TryGetTelegramRegistration(attachmentId, out TelegramAttachmentRegistration? registration)
            || registration is null)
        {
            return new SendOutboundMessageResponse
            {
                Accepted = false,
                DeliveryStatus = DeliveryStatus.Rejected,
                Detail = "attachment_not_found",
            };
        }

        OutboxExecutionResult result;
        try
        {
            result = await ExecuteOutboxActionAsync(
                new PollingRegistration(registration.AttachmentId, registration.OwnerUserId, registration.BotToken, registration.PollTimeoutSeconds, registration.Settings),
                request.Content.ActionType,
                request.Content.PayloadJson,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return new SendOutboundMessageResponse { Accepted = false, DeliveryStatus = DeliveryStatus.Rejected, Detail = "invalid_payload" };
        }
        catch (InvalidOperationException)
        {
            return new SendOutboundMessageResponse { Accepted = false, DeliveryStatus = DeliveryStatus.Rejected, Detail = "invalid_payload" };
        }
        catch (HttpRequestException)
        {
            return new SendOutboundMessageResponse { Accepted = false, DeliveryStatus = DeliveryStatus.RetryableFailure, Detail = "provider_unavailable" };
        }

        attachmentStore.MarkDeliveryStatus(attachmentId, result.Success ? "delivered" : result.Error ?? "delivery_failed");

        return new SendOutboundMessageResponse
        {
            Accepted = result.Success,
            DeliveryStatus = result.Success ? DeliveryStatus.Delivered
                : result.PermanentFailure ? DeliveryStatus.Rejected
                : result.Error?.StartsWith("unsupported_action:", StringComparison.Ordinal) == true ? DeliveryStatus.Rejected
                : DeliveryStatus.RetryableFailure,
            Detail = result.Error ?? string.Empty,
            TransportMessageId = result.TransportMessageId ?? string.Empty,
        };
    }
}
