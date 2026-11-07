using System.Globalization;

using Grpc.Core;

using Kautuka.Manager.Protos;
using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.Manager.Grpc;

public sealed partial class WorkspaceGrpcService
{
    private ContainerEgressRuntime NetworkRuntime => egressRuntime
        ?? throw new RpcException(new Status(StatusCode.Unavailable, "Network access control is unavailable."));

    public override Task<NetworkAccessPolicyResponse> GetNetworkAccessPolicy(NetworkAccessReadRequest request, ServerCallContext context)
        => NetworkCallAsync(() => Task.FromResult(NetworkRuntime.GetNetworkAccessPolicy(Actor(request.Actor))));

    public override Task<NetworkAccessPolicyResponse> SetNetworkAccessRule(NetworkAccessRuleRequest request, ServerCallContext context)
        => NetworkCallAsync(() =>
        {
            NetworkAccessRuleMessage rule = request.Rule ?? throw new ArgumentException("A network rule is required.");
            return NetworkRuntime.SetNetworkAccessRuleAsync(Actor(request.Actor), Identity(rule.Identity), rule.Destination,
                rule.Port, rule.Allowed, request.ExpectedRevision, context.CancellationToken);
        });

    public override Task<NetworkAccessPolicyResponse> DecideNetworkAccessRequest(NetworkAccessDecisionRequest request, ServerCallContext context)
        => NetworkCallAsync(() => NetworkRuntime.DecideNetworkAccessRequestAsync(Actor(request.Actor), request.RequestId,
            request.Allowed, request.ExpectedRevision, context.CancellationToken));

    private static NetworkAccessActor Actor(NetworkAccessActorMessage? value)
        => value is null ? throw new ArgumentException("An authenticated actor is required.") : new(value.UserId, value.IsAdmin);

    private static NetworkAccessIdentity Identity(NetworkAccessIdentityMessage? value)
        => value is null ? throw new ArgumentException("A scoped component identity is required.")
            : new(value.ScopeType, value.UserId, value.AgentId, value.Role, value.ComponentId);

    private static NetworkAccessIdentityMessage Identity(NetworkAccessIdentity value)
        => new() { ScopeType = value.ScopeType, UserId = value.UserId, AgentId = value.AgentId, Role = value.Role, ComponentId = value.ComponentId };

    private static async Task<NetworkAccessPolicyResponse> NetworkCallAsync(Func<Task<NetworkAccessPolicyView>> call)
    {
        try { return Response(await call().ConfigureAwait(false)); }
        catch (UnauthorizedAccessException) { throw new RpcException(new Status(StatusCode.PermissionDenied, "The network scope is not accessible to this caller.")); }
        catch (KeyNotFoundException) { throw new RpcException(new Status(StatusCode.NotFound, "The network request no longer exists.")); }
        catch (ArgumentException ex) { throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message)); }
        catch (InvalidOperationException ex) { throw new RpcException(new Status(StatusCode.Aborted, ex.Message)); }
        catch (IOException) { throw new RpcException(new Status(StatusCode.Unavailable, "Network policy storage is unavailable.")); }
    }

    private static NetworkAccessPolicyResponse Response(NetworkAccessPolicyView view)
    {
        var response = new NetworkAccessPolicyResponse
        {
            Enabled = view.ActivationState != "disabled",
            Revision = view.Policy.Revision,
            ActiveRevision = view.ActiveRevision,
            ActiveVersion = view.ActiveVersion ?? string.Empty,
            ActivationState = view.ActivationState,
            LastError = view.LastError ?? string.Empty,
        };
        response.Rules.AddRange(view.Policy.Rules.Select(rule => new NetworkAccessRuleMessage
        {
            Id = rule.Id,
            Identity = Identity(rule.Identity),
            Destination = rule.Destination,
            Port = rule.Port,
            Allowed = rule.Allowed,
            UpdatedAtUtc = rule.UpdatedAt.ToString("O", CultureInfo.InvariantCulture),
        }));
        response.Requests.AddRange(view.Policy.Requests.Select(request => new NetworkAccessRequestMessage
        {
            Id = request.Id,
            Identity = Identity(request.Identity),
            Destination = request.Destination,
            Port = request.Port,
            CreatedAtUtc = request.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            LastSeenAtUtc = request.LastSeenAt.ToString("O", CultureInfo.InvariantCulture),
            Attempts = request.Attempts,
            Status = request.Status,
        }));
        response.Sources.AddRange(view.Sources.Select(Identity));
        response.Defaults.AddRange(view.Defaults.Select(grant => new NetworkAccessRuleMessage
        {
            Identity = new NetworkAccessIdentityMessage { ScopeType = grant.ScopeType, UserId = grant.UserId, AgentId = grant.AgentId, Role = grant.Role, ComponentId = grant.ComponentId },
            Destination = grant.Destination,
            Port = grant.Port,
            Allowed = true,
        }));
        return response;
    }
}
