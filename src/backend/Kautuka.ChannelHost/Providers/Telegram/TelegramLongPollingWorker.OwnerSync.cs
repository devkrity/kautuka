using Kautuka.Protos;

namespace Kautuka.ChannelHost.Providers.Telegram;

internal sealed partial class TelegramLongPollingWorker
{
    public async Task<SyncOwnerTargetsResponse> SyncOwnerTargetsAsync(
        SyncOwnerTargetsRequest request,
        CancellationToken cancellationToken)
    {
        string ownerUserId = request.OwnerUserId.Trim();
        if (string.IsNullOrWhiteSpace(ownerUserId))
        {
            return new SyncOwnerTargetsResponse
            {
                Accepted = false,
                Detail = "owner_user_id_required",
            };
        }

        TelegramAttachmentRegistration[] registrations = [.. attachmentStore.ListActiveTelegramRegistrations(ownerUserId)];
        if (registrations.Length == 0)
        {
            return new SyncOwnerTargetsResponse
            {
                Accepted = true,
                Detail = "no_active_registrations",
                RegistrationsConsidered = 0,
                RegistrationsSynced = 0,
            };
        }

        ChannelTargetMessage[] ownerTargets = [.. request.Targets];
        HashSet<string> separatelyBotHostedTargetKeys = [.. registrations
            .Select(GetAgentTargetKeyFromRegistration)
            .Where(static targetKey => !string.IsNullOrWhiteSpace(targetKey))
            .Select(static targetKey => targetKey!)];
        int syncedCount = 0;
        foreach (TelegramAttachmentRegistration registration in registrations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(GetStringSetting(registration.Settings, "channel_device_id")))
            {
                continue;
            }

            ChannelTargetMessage[] targets = ResolveTargetsForRegistration(
                registration,
                ownerTargets,
                separatelyBotHostedTargetKeys);

            ProvisionAttachmentResponse response = await ProvisionAttachmentAsync(
                registration.AttachmentId,
                BuildProvisionRequest(request, registration, targets),
                cancellationToken).ConfigureAwait(false);

            if (response.Status is ProvisioningStatus.Accepted or ProvisioningStatus.Running or ProvisioningStatus.Succeeded)
            {
                syncedCount++;
            }
        }

        return new SyncOwnerTargetsResponse
        {
            Accepted = true,
            Detail = syncedCount == 0 ? "no_registrations_synced" : "synced",
            RegistrationsConsidered = registrations.Length,
            RegistrationsSynced = syncedCount,
        };
    }

    private static ProvisionAttachmentRequest BuildProvisionRequest(
        SyncOwnerTargetsRequest request,
        TelegramAttachmentRegistration registration,
        IReadOnlyList<ChannelTargetMessage> targets)
    {
        ProvisionAttachmentRequest provisionRequest = new()
        {
            Context = request.Context,
            AttachmentId = registration.AttachmentId,
            Reason = string.IsNullOrWhiteSpace(request.Reason) ? "owner_target_sync" : request.Reason,
            BotName = string.IsNullOrWhiteSpace(request.BotName) ? "Kautuka" : request.BotName,
            Description = BuildDescription(targets),
            ShortDescription = BuildShortDescription(registration.Settings),
        };

        // Prefer Gateway-supplied commands on SyncOwnerTargets; fall back to shared catalogue.
        if (request.Commands.Count > 0)
        {
            provisionRequest.Commands.AddRange(request.Commands);
        }
        else
        {
            provisionRequest.Commands.AddRange(ChannelBotCommandCatalogue.Default.Select(static command => new ChannelCommandMessage
            {
                Command = command.Command,
                Description = command.Description,
            }));
        }

        provisionRequest.Targets.AddRange(targets);
        return provisionRequest;
    }

    private static ChannelTargetMessage[] ResolveTargetsForRegistration(
        TelegramAttachmentRegistration registration,
        IReadOnlyList<ChannelTargetMessage> ownerTargets,
        HashSet<string> separatelyBotHostedTargetKeys)
    {
        string[] parts = registration.AttachmentId.Split(':', 4);
        if (parts.Length == 3 && parts[2] == "default")
        {
            return [.. ownerTargets.Where(target => !separatelyBotHostedTargetKeys.Contains(target.TargetKey))];
        }

        string? targetKey = GetAgentTargetKeyFromRegistration(registration);
        if (targetKey is null)
        {
            return [];
        }

        ChannelTargetMessage? target = ownerTargets.FirstOrDefault(item =>
            string.Equals(item.TargetKey, targetKey, StringComparison.Ordinal));
        return target is null ? [] : [target];
    }

    private static string? GetAgentTargetKeyFromRegistration(TelegramAttachmentRegistration registration)
    {
        string[] parts = registration.AttachmentId.Split(':', 4);
        return parts.Length == 4 && parts[2] == "agent" && !string.IsNullOrWhiteSpace(parts[3])
            && !string.IsNullOrWhiteSpace(GetStringSetting(registration.Settings, "channel_device_id")) ? $"agent:{parts[3]}" : null;
    }

    private static string BuildDescription(IReadOnlyList<ChannelTargetMessage> targets)
        => targets.Count == 0
            ? "Kautuka bot. Granted targets: No agent targets granted yet."
            : $"Kautuka bot. Granted targets: {string.Join(", ", targets.Select(static item => item.TargetKey))}";

    private static string BuildShortDescription(System.Text.Json.JsonElement settings)
    {
        string? defaultTargetType = GetStringSetting(settings, "default_target_type");
        string? defaultTargetId = GetStringSetting(settings, "default_target_id");
        return string.IsNullOrWhiteSpace(defaultTargetType) || string.IsNullOrWhiteSpace(defaultTargetId)
            ? "Default target: none"
            : $"Default target: {defaultTargetType}:{defaultTargetId}";
    }

    private static string? GetStringSetting(System.Text.Json.JsonElement settings, string propertyName)
        => settings.ValueKind == System.Text.Json.JsonValueKind.Object
            && settings.TryGetProperty(propertyName, out System.Text.Json.JsonElement property)
            && property.ValueKind == System.Text.Json.JsonValueKind.String
                ? property.GetString()?.Trim()
                : null;
}
