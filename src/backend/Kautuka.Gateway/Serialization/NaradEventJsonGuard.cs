namespace Kautuka.Gateway.Serialization;

internal static class NaradEventJsonGuard
{
    public const int MaxEventJsonChars = 256_000;

    public static string ValidatePersistedEventJson(string eventJson, string context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventJson);
        if (eventJson.Length > MaxEventJsonChars)
        {
            throw new InvalidOperationException($"{context} exceeds the maximum persisted Narad event size of {MaxEventJsonChars} characters.");
        }

        return GatewayRuntimePayloadGuard.ValidatePersistedJson(eventJson, context);
    }
}
