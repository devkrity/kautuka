namespace Kautuka.Gateway.Domain.Hitl;

/// <summary>
/// Channel sync policy while a channel attachment is enabled. Both modes fan the originating
/// user prompt, and neither progressive-streams tokens to providers (no word-by-word edits).
/// <list type="bullet">
/// <item><see cref="Hitl"/> — user prompt, approval requests/status, and the terminal assistant reply.</item>
/// <item><see cref="Mirror"/> — also fan completed assistant messages as one-shot channel messages.</item>
/// </list>
/// </summary>
internal enum ChannelHitlTetherMode
{
    Hitl = 0,
    Mirror = 1,
}

internal static class ChannelHitlTetherModeExtensions
{
    public static ChannelHitlTetherMode Parse(string? value)
#pragma warning disable CA1308 // ToLowerInvariant is intentional for switch-based parsing of mode strings
        => value?.Trim().ToLowerInvariant() switch
#pragma warning restore CA1308
        {
            "hitl" => ChannelHitlTetherMode.Hitl,
            _ => ChannelHitlTetherMode.Mirror,
        };

    public static string ToStorageValue(this ChannelHitlTetherMode mode)
        => mode switch
        {
            ChannelHitlTetherMode.Mirror => "mirror",
            _ => "hitl",
        };
}
