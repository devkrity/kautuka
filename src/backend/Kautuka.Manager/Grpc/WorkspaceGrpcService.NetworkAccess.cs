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
    {
        ArgumentNullException.ThrowIfNull(request);
        return NetworkCallAsync(() =>
        {
            NetworkAccessPolicyView view = NetworkRuntime.GetNetworkAccessPolicy(Actor(request.Actor));
            if (request.EnforceRevision && request.ExpectedRevision != view.Policy.Revision)
                throw new InvalidOperationException("Network policy changed while paging. Refresh the policy.");
            return Task.FromResult(view);
        }, request.Offset, request.SummaryOnly);
    }

    public override Task<NetworkAuditResponse> GetNetworkAudit(NetworkAuditReadRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            NetworkAuditPage page = NetworkRuntime.GetNetworkAudit(Actor(request.Actor), request.Before, request.Limit == 0 ? 50 : request.Limit);
            var response = new NetworkAuditResponse
            {
                NextBefore = page.NextBefore,
                CoverageGaps = page.CoverageGaps,
                UnattributedConnections = page.UnattributedConnections,
                LastError = page.LastError ?? string.Empty
            };
            response.Events.AddRange(page.Events.Select(item => new NetworkAuditEventMessage
            {
                Sequence = item.Sequence,
                Id = item.Id,
                StartedAtUtc = item.StartedAt.ToString("O", CultureInfo.InvariantCulture),
                ObservedAtUtc = item.ObservedAt.ToString("O", CultureInfo.InvariantCulture),
                Identity = item.Identity is null ? null : Identity(item.Identity),
                ContainerId = item.ContainerId ?? string.Empty,
                SourceAddress = item.SourceAddress,
                Destination = item.Destination,
                Port = item.Port,
                Outcome = item.Outcome,
                Connected = item.Connected,
                Closed = item.Closed,
                BytesSent = item.BytesSent,
                BytesReceived = item.BytesReceived,
                DurationMilliseconds = item.DurationMilliseconds,
                Publication = item.Publication,
            }));
            return Task.FromResult(response);
        }
        catch (ArgumentException ex) { throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message)); }
        catch (IOException) { throw new RpcException(new Status(StatusCode.Unavailable, "Network audit storage is unavailable.")); }
    }

    public override Task<NetworkAccessPolicyResponse> SetNetworkAccessRule(NetworkAccessRuleRequest request, ServerCallContext context)
        => NetworkCallAsync(() =>
        {
            NetworkAccessRuleMessage rule = request.Rule ?? throw new ArgumentException("A network rule is required.");
            if (rule.AllowPrivateNetwork) throw new ArgumentException("Private-network entitlement is read-only and cannot be supplied by a client.");
            return NetworkRuntime.SetNetworkAccessRuleAsync(Actor(request.Actor), Identity(rule.Identity), rule.Destination,
                rule.Port, rule.Allowed, request.ExpectedRevision, context.CancellationToken, request.AllowPrivateNetwork);
        });

    public override Task<NetworkAccessPolicyResponse> DecideNetworkAccessRequest(NetworkAccessDecisionRequest request, ServerCallContext context)
        => NetworkCallAsync(() => NetworkRuntime.DecideNetworkAccessRequestAsync(Actor(request.Actor), request.RequestId,
            request.Allowed, request.ExpectedRevision, context.CancellationToken, request.AllowPrivateNetwork));

    private static NetworkAccessActor Actor(NetworkAccessActorMessage? value)
        => value is null ? throw new ArgumentException("An authenticated actor is required.") : new(value.UserId, value.IsAdmin);

    private static NetworkAccessIdentity Identity(NetworkAccessIdentityMessage? value)
        => value is null ? throw new ArgumentException("A scoped component identity is required.")
            : new(value.ScopeType, value.UserId, value.AgentId, value.Role, value.ComponentId);

    private static NetworkAccessIdentityMessage Identity(NetworkAccessIdentity value)
        => new() { ScopeType = value.ScopeType, UserId = value.UserId, AgentId = value.AgentId, Role = value.Role, ComponentId = value.ComponentId };

    private static async Task<NetworkAccessPolicyResponse> NetworkCallAsync(Func<Task<NetworkAccessPolicyView>> call, int offset = 0, bool summaryOnly = false)
    {
        try { return Response(await call().ConfigureAwait(false), offset, summaryOnly); }
        catch (UnauthorizedAccessException) { throw new RpcException(new Status(StatusCode.PermissionDenied, "The network scope is not accessible to this caller.")); }
        catch (KeyNotFoundException) { throw new RpcException(new Status(StatusCode.NotFound, "The network request no longer exists.")); }
        catch (ArgumentException ex) { throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message)); }
        catch (InvalidOperationException ex) { throw new RpcException(new Status(StatusCode.Aborted, ex.Message)); }
        catch (IOException) { throw new RpcException(new Status(StatusCode.Unavailable, "Network policy storage is unavailable.")); }
    }

    private static NetworkAccessPolicyResponse Response(NetworkAccessPolicyView view, int offset, bool summaryOnly)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        const int pageSize = 50;
        int maximum = Math.Max(Math.Max(view.Policy.Rules.Count, view.Policy.Requests.Count), Math.Max(view.Sources.Count, view.Defaults.Count));
        var response = new NetworkAccessPolicyResponse
        {
            Enabled = view.ActivationState != "disabled",
            Revision = view.Policy.Revision,
            ActiveRevision = view.ActiveRevision,
            ActiveVersion = view.ActiveVersion ?? string.Empty,
            ActivationState = view.ActivationState,
            LastError = view.LastError ?? string.Empty,
            RuleCount = view.Policy.Rules.Count,
            RequestCount = view.Policy.Requests.Count,
            SourceCount = view.Sources.Count,
            DefaultCount = view.Defaults.Count,
            PendingRequestCount = view.Policy.Requests.Count(item => item.Status == "pending"),
            RequestCapacityReached = view.RequestCapacityReached,
            MaxPrivateNetworks = view.MaxPrivateNetworks,
            ActivePrivateNetworks = view.ActivePrivateNetworks,
            DnsRefreshError = view.DnsRefreshError ?? string.Empty,
            NextOffset = !summaryOnly && offset < maximum - pageSize ? offset + pageSize : 0,
        };
        if (summaryOnly) return response;
        response.Rules.AddRange(view.Policy.Rules.Skip(offset).Take(pageSize).Select(rule => new NetworkAccessRuleMessage
        {
            Id = rule.Id,
            Identity = Identity(rule.Identity),
            Destination = rule.Destination,
            Port = rule.Port,
            Allowed = NetworkAccessPolicyStore.IsEffectiveAllow(rule)
                && view.IneffectiveRuleIds?.Contains(rule.Id) != true,
            UpdatedAtUtc = rule.UpdatedAt.ToString("O", CultureInfo.InvariantCulture),
            AllowPrivateNetwork = rule.AllowPrivateNetwork,
        }));
        response.Requests.AddRange(view.Policy.Requests.OrderBy(item => item.Status == "pending" ? 0 : 1)
            .ThenBy(item => item.CreatedAt).ThenBy(item => item.Id, StringComparer.Ordinal)
            .Skip(offset).Take(pageSize).Select(request => new NetworkAccessRequestMessage
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
        response.Sources.AddRange(view.Sources.Skip(offset).Take(pageSize).Select(Identity));
        response.Defaults.AddRange(view.Defaults.Skip(offset).Take(pageSize).Select(grant => new NetworkAccessRuleMessage
        {
            Identity = new NetworkAccessIdentityMessage { ScopeType = grant.ScopeType, UserId = grant.UserId, AgentId = grant.AgentId, Role = grant.Role, ComponentId = grant.ComponentId },
            Destination = grant.Destination,
            Port = grant.Port,
            Allowed = true,
            AllowPrivateNetwork = grant.AllowPrivateNetwork,
        }));
        if (response.CalculateSize() > 2 * 1024 * 1024)
            throw new RpcException(new Status(StatusCode.ResourceExhausted, "Network policy page exceeds its supported response size."));
        return response;
    }
}
