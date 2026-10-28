using Grpc.Core;

using Kautuka.Protos;

namespace Kautuka.CredentialsHost.Grpc;

internal static class CredentialRequestAuthorizer
{
    public static void AuthorizeResolve(CredentialAccessContext? access, string userId)
    {
        RequireActor(access);
        if (access!.Capability == CredentialCapability.RuntimeResolve
            && (string.IsNullOrEmpty(userId) || string.Equals(access.ActorId, userId, StringComparison.Ordinal)))
        {
            return;
        }

        if (access.Capability is CredentialCapability.CrossUserAdmin or CredentialCapability.SystemAdmin)
        {
            return;
        }

        Deny();
    }

    public static void AuthorizeUserAdministration(CredentialAccessContext? access, string userId)
    {
        RequireActor(access);
        if (access!.Capability == CredentialCapability.OwnUserAdmin
            && string.Equals(access.ActorId, userId, StringComparison.Ordinal))
        {
            return;
        }

        if (access.Capability == CredentialCapability.CrossUserAdmin)
        {
            return;
        }

        Deny();
    }

    public static void AuthorizeSystemAdministration(CredentialAccessContext? access)
    {
        RequireActor(access);
        if (access!.Capability == CredentialCapability.SystemAdmin)
        {
            return;
        }

        Deny();
    }

    private static void RequireActor(CredentialAccessContext? access)
    {
        if (access is null
            || string.IsNullOrWhiteSpace(access.ActorId)
            || access.Capability == CredentialCapability.Unspecified)
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Authenticated credential access context is required."));
        }
    }

    private static void Deny()
        => throw new RpcException(new Status(StatusCode.PermissionDenied, "Credential operation is not authorized for this actor and capability."));
}
