using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Grpc.Core;

using Kautuka.Manager.Protos;

namespace Kautuka.Gateway.Endpoints.Common;

internal static partial class GatewayEndpointUtilities
{
    [LoggerMessage(
        EventId = 8501,
        Level = LogLevel.Warning,
        Message = "Could not sync network access role privileges to Manager for user '{UserId}' (isAdmin={TargetIsAdmin})")]
    private static partial void LogPrivilegeSyncUnavailable(ILogger logger, Exception exception, string userId, bool targetIsAdmin);

    internal static async Task TrySyncUserRolePrivilegesAsync(
        IManagerGrpcService managerService,
        string actorUserId,
        string targetUserId,
        bool targetIsAdmin,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        var request = new NetworkAccessSetUserPrivilegesRequest
        {
            Actor = new NetworkAccessActorMessage { UserId = actorUserId, IsAdmin = true },
            UserId = targetUserId,
            CanApproveOwn = targetIsAdmin,
            CanAllowAllAgents = targetIsAdmin,
            TargetIsAdmin = targetIsAdmin,
        };

        try
        {
            _ = await managerService.SetUserPrivilegesAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or RpcException)
        {
            if (logger is not null)
            {
                LogPrivilegeSyncUnavailable(logger, ex, targetUserId, targetIsAdmin);
            }
        }
    }

    internal static string Timestamp() => DateTimeOffset.UtcNow.ToString("O");

    internal static GatewayUserPayload ToUserPayload(GatewayUser user, string[]? externalProviders = null) => new(
        user.Id,
        user.Username,
        user.Email,
        user.DisplayName,
        user.Role,
        user.IsActive,
        user.CanImportConnectors,
        user.CanImportSkills,
        user.HasPassword,
        externalProviders ?? []);

    internal static async Task WriteSse<T>(HttpContext context, string evt, T data, JsonTypeInfo<T> jsonTypeInfo)
    {
        string payload = JsonSerializer.Serialize(data, jsonTypeInfo);
        await context.Response.WriteAsync($"event: {evt}\n", Encoding.UTF8).ConfigureAwait(false);
        await context.Response.WriteAsync($"data: {payload}\n\n", Encoding.UTF8).ConfigureAwait(false);
        await context.Response.Body.FlushAsync().ConfigureAwait(false);
    }

    internal static IEnumerable<string> Chunk(string text, int chunkSize)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield return string.Empty;
            yield break;
        }

        for (int idx = 0; idx < text.Length; idx += chunkSize)
        {
            int take = Math.Min(chunkSize, text.Length - idx);
            yield return text.Substring(idx, take);
        }
    }
}
