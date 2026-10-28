using System.Net;

using Microsoft.Extensions.Primitives;

namespace Kautuka.Gateway.Domain.Auth;

internal static class AuthClientAddress
{
    public static string ResolvePartitionKey(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IPAddress? remote = context.Connection.RemoteIpAddress;
        if ((remote is null || IPAddress.IsLoopback(remote))
            && TryReadSingleForwardedAddress(context, out string forwarded))
        {
            return forwarded;
        }

        return remote?.ToString() ?? "unknown";
    }

    private static bool TryReadSingleForwardedAddress(HttpContext context, out string address)
    {
        address = string.Empty;
        if (!context.Request.Headers.TryGetValue("X-Forwarded-For", out StringValues values) || values.Count != 1)
        {
            return false;
        }

        string candidate = values[0]?.Trim() ?? string.Empty;
        if (candidate.Length == 0 || candidate.Contains(',', StringComparison.Ordinal) || !IPAddress.TryParse(candidate, out IPAddress? parsed))
        {
            return false;
        }

        address = parsed.ToString();
        return true;
    }
}
