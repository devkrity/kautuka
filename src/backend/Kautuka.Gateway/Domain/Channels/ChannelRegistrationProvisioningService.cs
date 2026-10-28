using System.Text.Json.Nodes;

using Kautuka.Gateway.Infrastructure.Channels;
using Kautuka.Protos;

namespace Kautuka.Gateway.Domain.Channels;

internal sealed partial class ChannelRegistrationProvisioningService(
    GatewayState state,
    IChannelAttachmentRegistry attachmentRegistry,
    IChannelHostGrpcService channelHost,
    ILogger<ChannelRegistrationProvisioningService> logger) : IChannelRegistrationProvisioningService
{
    private async Task<ChannelRegistrationProvisioningResult> ProvisionRegistrationCoreAsync(
        string registrationId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!attachmentRegistry.TryGetRegistration(registrationId, out GatewayChannelRegistration? registration)
                || registration is null)
            {
                return new ChannelRegistrationProvisioningResult(false, "registration_not_found", "Active channel registration was not found.");
            }
            await attachmentRegistry.RefreshAttachmentAsync(registrationId, registration.OwnerUserId, cancellationToken).ConfigureAwait(false);
            if (!attachmentRegistry.TryGetRegistration(registrationId, out registration))
            {
                return new ChannelRegistrationProvisioningResult(false, "registration_not_found", "Active channel registration was not found.");
            }

            if (registration is null || !registration.IsActive)
            {
                return new ChannelRegistrationProvisioningResult(false, "registration_not_found", "Active channel registration was not found.");
            }

            return await ProvisionCoreAsync(registration, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return new ChannelRegistrationProvisioningResult(false, "channel_host_unavailable", "ChannelHost is unavailable.");
        }
    }

    private async Task<ChannelRegistrationProvisioningResult> ProvisionCoreAsync(
        GatewayChannelRegistration registration,
        CancellationToken cancellationToken)
    {
        GatewayChannelTargetBinding[] bindings = [.. attachmentRegistry.GetBindings(registration.Id)
            .OrderBy(item => item.TargetType, StringComparer.Ordinal)
            .ThenBy(item => item.TargetId, StringComparer.Ordinal)];

        string targetsSummary = bindings.Length == 0
            ? "No agent targets granted yet."
            : string.Join(", ", bindings.Select(binding => ResolveBotDescriptionLabel(binding)));

        GatewayChannelTargetBinding? defaultBinding = bindings.FirstOrDefault(static item => item.IsDefault);
        string defaultSummary = defaultBinding is null
            ? "none"
            : ResolveBotDescriptionLabel(defaultBinding);

        ProvisionAttachmentRequest request = new()
        {
            Context = BuildSystemContext(registration.OwnerUserId),
            AttachmentId = registration.Id,
            Reason = "gateway_settings_sync",
            BotName = "Kautuka",
            Description = $"Kautuka bot. Granted targets: {targetsSummary}",
            ShortDescription = $"Default target: {defaultSummary}",
        };
        request.Commands.AddRange(ChannelBotCommandCatalogue.Default.Select(static command => new ChannelCommandMessage
        {
            Command = command.Command,
            Description = command.Description,
        }));
        request.Targets.AddRange(bindings.Select(binding => new ChannelTargetMessage
        {
            TargetKey = $"{binding.TargetType}:{binding.TargetId}",
            DisplayName = ResolveTargetName(binding),
        }));

        ProvisionAttachmentResponse provisionResponse = await channelHost.ProvisionAttachmentAsync(request, cancellationToken).ConfigureAwait(false);

#pragma warning disable CA1848, CA1873 // LoggerMessage refactor deferred; non-hot path
        logger.LogInformation("Provisioned channel attachment {AttachmentId} status={Status}", registration.Id, provisionResponse.Status);
#pragma warning restore CA1848, CA1873
        return new ChannelRegistrationProvisioningResult(
            provisionResponse.Status == ProvisioningStatus.Succeeded || provisionResponse.Status == ProvisioningStatus.Accepted,
            MapProvisioningStatus(provisionResponse.Status),
            string.IsNullOrWhiteSpace(provisionResponse.Detail) ? "Channel provisioning completed." : provisionResponse.Detail);
    }

    private string ResolveTargetName(GatewayChannelTargetBinding binding)
    {
        if (state.Agents.TryGetValue(binding.TargetId, out GatewayAgent? agent)
            && !string.IsNullOrWhiteSpace(agent.Name))
        {
            return agent.Name.Trim();
        }

        return $"{binding.TargetType}: {binding.TargetId}";
    }

    /// <summary>Bot profile text uses display names — never raw <c>agent:id</c> when a name is known.</summary>
    private string ResolveBotDescriptionLabel(GatewayChannelTargetBinding binding)
    {
        if (state.Agents.TryGetValue(binding.TargetId, out GatewayAgent? agent)
            && !string.IsNullOrWhiteSpace(agent.Name))
        {
            return agent.Name.Trim();
        }

        return $"{binding.TargetType}:{binding.TargetId}";
    }

    private static IEnumerable<AttachmentSettingValue> ToSettingValues(JsonObject settings)
    {
        foreach (KeyValuePair<string, JsonNode?> entry in settings)
        {
            if (entry.Value is null)
            {
                continue;
            }

            AttachmentSettingValue value = new() { FieldKey = entry.Key };
            switch (entry.Value)
            {
                case JsonArray array:
                    foreach (JsonNode? item in array)
                    {
                        value.MultiValues.Add(item?.ToString() ?? string.Empty);
                    }
                    break;
                case JsonValue jsonValue when jsonValue.TryGetValue<string>(out string? stringValue):
                    if (string.IsNullOrWhiteSpace(stringValue))
                    {
                        continue;
                    }
                    value.StringValue = stringValue;
                    break;
                case JsonValue jsonValue when jsonValue.TryGetValue<bool>(out bool boolValue):
                    value.BoolValue = boolValue;
                    break;
                case JsonValue jsonValue when jsonValue.TryGetValue<int>(out int intValue):
                    value.NumberValue = intValue;
                    break;
                case JsonValue jsonValue when jsonValue.TryGetValue<long>(out long longValue):
                    value.NumberValue = longValue;
                    break;
                case JsonValue jsonValue when jsonValue.TryGetValue<double>(out double doubleValue):
                    value.NumberValue = doubleValue;
                    break;
                default:
                    value.JsonValue = entry.Value.ToJsonString();
                    break;
            }

            yield return value;
        }
    }

    private static ScopedRequestContext BuildSystemContext(string actorUserId)
    {
        ScopedRequestContext context = new()
        {
            RequestId = Guid.NewGuid().ToString("N"),
            ActorUserId = string.IsNullOrWhiteSpace(actorUserId) ? "gateway-system" : actorUserId,
            SubmittedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            GatewaySessionId = "gateway-channel-provisioning",
        };
        context.ActorCapabilities.Add("channels.write");
        return context;
    }

    private static string MapProvisioningStatus(ProvisioningStatus status) => status switch
    {
        ProvisioningStatus.Accepted => "accepted",
        ProvisioningStatus.Running => "running",
        ProvisioningStatus.Succeeded => "succeeded",
        ProvisioningStatus.Failed => "failed",
        _ => "unspecified",
    };
}
