namespace Kautuka.ChannelHost.Hosting;

/// <summary>
/// Holds decrypted channel credentials only for the lifetime of this process. The Gateway
/// resolves the durable references through CredentialsHost and refreshes this store over mTLS.
/// </summary>
internal sealed class ChannelRuntimeCredentialStore
{
    private readonly Lock sync = new();
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> values = new(StringComparer.Ordinal);

    public void Replace(string attachmentId, IEnumerable<Kautuka.Protos.RuntimeCredentialValue> credentials)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attachmentId);
        Dictionary<string, string> replacement = credentials
            .Where(static item => !string.IsNullOrWhiteSpace(item.FieldKey) && !string.IsNullOrWhiteSpace(item.SecretValue))
            .ToDictionary(static item => item.FieldKey, static item => item.SecretValue, StringComparer.Ordinal);
        lock (sync)
        {
            values[attachmentId] = replacement;
        }
    }

    public string Get(string attachmentId, string fieldKey)
    {
        lock (sync)
        {
            return values.TryGetValue(attachmentId, out IReadOnlyDictionary<string, string>? credentials)
                && credentials.TryGetValue(fieldKey, out string? value)
                ? value
                : string.Empty;
        }
    }

    public void Remove(string attachmentId)
    {
        lock (sync)
        {
            values.Remove(attachmentId);
        }
    }
}
