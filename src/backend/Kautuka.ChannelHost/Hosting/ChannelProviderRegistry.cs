using Kautuka.Protos;

namespace Kautuka.ChannelHost.Hosting;

internal sealed class ChannelProviderRegistry(IEnumerable<IChannelAdapter> adapters)
{
    private readonly Dictionary<string, IChannelAdapter> adaptersByKey = adapters.ToDictionary(
        static item => item.ProviderKey,
        StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<IChannelAdapter> GetAll()
        => [.. adaptersByKey.Values
            .Where(static item => ChannelProviderAdmissionPolicy.Evaluate(item.ProviderKey).Admitted)
            .OrderBy(static item => item.ProviderKey, StringComparer.OrdinalIgnoreCase)];

    public bool TryGet(string providerKey, out IChannelAdapter? adapter)
    {
        if (ChannelProviderAdmissionPolicy.Evaluate(providerKey).Admitted)
        {
            return adaptersByKey.TryGetValue(providerKey, out adapter);
        }

        adapter = null;
        return false;
    }

    public bool TryGetForAttachment(ChannelAttachmentStore attachmentStore, string attachmentId, out IChannelAdapter? adapter)
    {
        adapter = null;
        AttachmentSummary? attachment = attachmentStore.GetAttachmentSummary(attachmentId);
        return attachment is not null
            && TryGet(attachment.ProviderKey, out adapter);
    }

    public ListChannelProvidersResponse BuildProviderCatalog()
    {
        ListChannelProvidersResponse response = new();
        foreach (IChannelAdapter adapter in GetAll())
        {
            ChannelProviderDefinition definition = new()
            {
                ProviderKey = adapter.ProviderKey,
                DisplayName = adapter.DisplayName,
                SchemaVersion = adapter.SchemaVersion,
                CapabilityVersion = adapter.CapabilityVersion,
                Capabilities = BuildCapabilitiesDocument(adapter.Capabilities),
            };
            definition.Fields.AddRange(adapter.GetSchema());
            response.Providers.Add(definition);
        }

        return response;
    }

    internal static ChannelProviderCapabilitiesDocument BuildCapabilitiesDocument(ChannelProviderCapabilities capabilities)
        => new()
        {
            Version = capabilities.Version,
            Streaming = capabilities.Streaming,
            Edits = capabilities.Edits,
            Deletes = capabilities.Deletes,
            Typing = capabilities.Typing,
            Reactions = capabilities.Reactions,
            Modals = capabilities.Modals,
            EphemeralMessages = capabilities.EphemeralMessages,
            Files = capabilities.Files,
            InboundFiles = capabilities.InboundFiles,
            History = capabilities.History,
            UserLookup = capabilities.UserLookup,
            Commands = capabilities.Commands,
            SuggestedPrompts = capabilities.SuggestedPrompts,
            ThreadTitles = capabilities.ThreadTitles,
            MaximumMessageCharacters = capabilities.MaximumMessageCharacters,
            MaximumActions = capabilities.MaximumActions,
            MaximumActionLabelCharacters = capabilities.MaximumActionLabelCharacters,
            MaximumCallbackTokenBytes = capabilities.MaximumCallbackTokenBytes,
            InteractionAcknowledgementDeadlineSeconds = capabilities.InteractionAcknowledgementDeadlineSeconds,
            MaximumBlocks = capabilities.MaximumBlocks,
            MaximumActionRows = capabilities.MaximumActionRows,
            MaximumSelectOptions = capabilities.MaximumSelectOptions,
            MaximumFileBytes = capabilities.MaximumFileBytes,
        };
}
