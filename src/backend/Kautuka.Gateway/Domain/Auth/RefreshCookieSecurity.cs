using System.Net;

using Microsoft.Extensions.Primitives;

namespace Kautuka.Gateway.Domain.Auth;

internal static class RefreshCookieSecurity
{
    public static bool IsSecure(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.IsHttps)
        {
            return true;
        }

        IPAddress? remote = request.HttpContext.Connection.RemoteIpAddress;
        if (remote is not null && !IPAddress.IsLoopback(remote))
        {
            return false;
        }

        if (!request.Headers.TryGetValue("X-Forwarded-Proto", out StringValues values) || values.Count != 1)
        {
            return false;
        }

        return string.Equals(values[0]?.Trim(), "https", StringComparison.OrdinalIgnoreCase);
    }
}
