using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Kautuka.ChannelHost.Providers.Telegram;

internal static class TelegramBotIdentity
{
    public static string FromToken(string token)
    {
        int separator = token.IndexOf(':', StringComparison.Ordinal);
        if (separator > 0 && long.TryParse(token.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out long id) && id > 0)
            return id.ToString(CultureInfo.InvariantCulture);
        // Opaque identities also support simulator credentials without exposing their secrets.
        return "opaque-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }
}
