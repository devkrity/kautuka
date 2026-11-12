using System.Globalization;
using System.Security.Cryptography.X509Certificates;

using Grpc.Core;

using Kautuka.Manager.Protos;
using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.NetworkAccessController;

using NacController = Kautuka.NetworkAccessController.NetworkAccessController;

namespace Kautuka.Manager.Grpc;

public sealed partial class WorkspaceGrpcService
{
    private NacController Nac => networkAccessController
        ?? throw new RpcException(new Status(StatusCode.Unavailable, "Network access controller is not available."));

    public override Task<NetworkAccessCheckOutboundResponse> CheckOutbound(
        NetworkAccessCheckOutboundRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        using IDisposable scope = BeginGrpcScope(context, "CheckOutbound", ("Host", request.Host), ("Port", request.Port));
        TagCurrentActivity(("rpc.method", "CheckOutbound"));

        return ExecuteNacRpc(() =>
        {
            string origin = ResolveClientOrigin(context);
            string effectiveUserId;
            string effectiveAgentId;
            bool effectiveSystemwide;

            if (origin == "channelhost")
            {
                if (!string.IsNullOrWhiteSpace(request.UserId) || !string.IsNullOrWhiteSpace(request.AgentId) || request.Systemwide)
                {
                    throw new UnauthorizedAccessException("PermissionDenied: ChannelHost identity is pinned to system/channelhost and cannot specify custom userId, agentId, or systemwide.");
                }

                effectiveUserId = "system";
                effectiveAgentId = "channelhost";
                effectiveSystemwide = false;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(request.UserId))
                {
                    throw new ArgumentException("UserId is required for outbound check.", nameof(request));
                }

                effectiveUserId = request.UserId.Trim();
                effectiveAgentId = string.IsNullOrWhiteSpace(request.AgentId) ? "gateway" : request.AgentId.Trim();
                effectiveSystemwide = request.Systemwide;
            }

            NetworkAccessDecisionResult decision = Nac.EvaluateOutboundCheck(new OutboundCheckInput(
                effectiveUserId,
                effectiveAgentId,
                string.IsNullOrWhiteSpace(request.ConnectorId) ? null : request.ConnectorId.Trim(),
                effectiveSystemwide,
                request.Host,
                request.Port,
                string.IsNullOrWhiteSpace(request.Purpose) ? null : request.Purpose,
                string.IsNullOrWhiteSpace(request.CorrelationId) ? null : request.CorrelationId,
                origin));

            return new NetworkAccessCheckOutboundResponse
            {
                Allowed = decision.Allowed,
                Reason = decision.Reason,
                RequestId = decision.RequestId ?? string.Empty,
                OpenInternetBadge = decision.OpenInternetBadge,
            };
        });
    }

    public override Task<NetworkAccessListRequestsResponse> ListRequests(
        NetworkAccessListRequestsRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        using IDisposable scope = BeginGrpcScope(context, "ListRequests");

        return ExecuteNacRpc(() =>
        {
            (long revision, IReadOnlyList<NetworkAccessRequestView> requests, UserPrivileges privs, BrakeState brake) =
                Nac.PolicyStore.ListRequests(
                    ToActor(request.Actor),
                    request.HasSinceRevision ? request.SinceRevision : null);

            var response = new NetworkAccessListRequestsResponse
            {
                Revision = revision,
                Privileges = ToMessage(privs),
                Brake = ToMessage(brake),
            };
            response.Requests.AddRange(requests.Select(ToMessage));
            return response;
        });
    }

    public override Task<NetworkAccessDecideResponse> DecideRequest(
        NetworkAccessDecideRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        using IDisposable scope = BeginGrpcScope(context, "DecideRequest", ("RequestId", request.RequestId));

        return ExecuteNacRpc(() =>
        {
            DecideRequestResult result = Nac.PolicyStore.DecideRequest(
                ToActor(request.Actor),
                request.RequestId,
                request.Allow,
                string.IsNullOrWhiteSpace(request.Scope) ? null : request.Scope,
                request.ExpectedRevision,
                request.AllowLan);

            return new NetworkAccessDecideResponse
            {
                Revision = result.Revision,
                Rule = ToMessage(new NetworkAccessRuleView(result.Rule, result.Paused, result.OpenInternetBadge)),
                Request = ToMessage(new NetworkAccessRequestView(
                    result.Request,
                    WaitingForAdmin: false,
                    CompatibilityMode: !string.IsNullOrEmpty(result.Request.CompatProfile))),
            };
        });
    }

    public override Task<NetworkAccessListRulesResponse> ListRules(
        NetworkAccessListRulesRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        using IDisposable scope = BeginGrpcScope(context, "ListRules");

        return ExecuteNacRpc(() =>
        {
            (long revision, IReadOnlyList<NetworkAccessRuleView> rules) = Nac.PolicyStore.ListRules(
                ToActor(request.Actor),
                string.IsNullOrWhiteSpace(request.FilterUserId) ? null : request.FilterUserId);

            var response = new NetworkAccessListRulesResponse
            {
                Revision = revision,
            };
            response.Rules.AddRange(rules.Select(ToMessage));
            return response;
        });
    }

    public override Task<NetworkAccessSetRuleResponse> SetRule(
        NetworkAccessSetRuleRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        using IDisposable scope = BeginGrpcScope(context, "SetRule");

        return ExecuteNacRpc(() =>
        {
            string? targetUserId = string.IsNullOrWhiteSpace(request.UserId) ? null : request.UserId;
            string? targetAgentId = string.IsNullOrWhiteSpace(request.AgentId) ? null : request.AgentId;
            string? targetConnectorId = string.IsNullOrWhiteSpace(request.ConnectorId) ? null : request.ConnectorId;
            (string? execContext, string? attribution) = ResolveAuthoritativeRuleContextAndAttribution(
                targetConnectorId,
                targetUserId,
                string.IsNullOrWhiteSpace(request.ExecContext) ? null : request.ExecContext,
                string.IsNullOrWhiteSpace(request.Attribution) ? null : request.Attribution);

            (long revision, NetworkAccessRuleView rule) = Nac.PolicyStore.SetRule(
                ToActor(request.Actor),
                new RuleWriteSpec(
                    request.ScopeKind,
                    targetUserId,
                    targetAgentId,
                    targetConnectorId,
                    request.Destination,
                    request.DestKind,
                    request.Port,
                    request.Allowed,
                    request.AllowLan,
                    execContext,
                    attribution),
                request.ExpectedRevision);

            return new NetworkAccessSetRuleResponse
            {
                Revision = revision,
                Rule = ToMessage(rule),
            };
        });
    }

    private (string? ExecContext, string? Attribution) ResolveAuthoritativeRuleContextAndAttribution(
        string? connectorId,
        string? userId,
        string? requestedExecContext,
        string? requestedAttribution)
    {
        if (string.IsNullOrWhiteSpace(connectorId))
        {
            return (requestedExecContext, requestedAttribution);
        }

        string trimmedConnectorId = connectorId.Trim();

        // 1. Authoritative live container registry metadata takes precedence over caller-supplied values.
        RegistryEntry[] registryMatches =
        [
            .. Nac.Registry.GetAll().Where(entry =>
                string.Equals(entry.ConnectorId, trimmedConnectorId, StringComparison.Ordinal)
                && (string.IsNullOrWhiteSpace(userId)
                    || string.IsNullOrWhiteSpace(entry.UserId)
                    || string.Equals(entry.UserId, userId, StringComparison.Ordinal))),
        ];
        if (registryMatches.Length > 0)
        {
            RegistryEntry? restrictive = registryMatches.FirstOrDefault(static entry =>
                entry.ExecContext is "agent-sandbox" or "skill" or "skills" or "app" or "system"
                || string.Equals(entry.Attribution, "none", StringComparison.Ordinal));
            RegistryEntry authoritative = restrictive ?? registryMatches[0];
            return (authoritative.ExecContext, authoritative.Attribution);
        }

        // 2. Desired state snapshot synced from Gateway (sandbox bindings and internal connectors).
        if (desiredStateStore is not null)
        {
            WorkspaceDesiredStateSnapshot snapshot = desiredStateStore.Snapshot;
            bool isInternal = snapshot.DesiredContainerConnectors.Any(connector =>
                connector.IsInternalSystemConnector
                && (string.Equals(connector.ConnectorId, trimmedConnectorId, StringComparison.Ordinal)
                    || (!string.IsNullOrWhiteSpace(userId)
                        && string.Equals(connector.ConnectorId, $"{userId}::{trimmedConnectorId}", StringComparison.Ordinal))));
            if (isInternal)
            {
                return ("system", "none");
            }

            bool boundToSandbox = snapshot.DesiredSandboxConnectorBindings.Any(binding =>
                binding.ConnectorIds.Any(id => string.Equals(id, trimmedConnectorId, StringComparison.Ordinal)));
            bool hasStandaloneIsolated = snapshot.DesiredContainerConnectors.Any(connector =>
                !connector.IsInternalSystemConnector
                && (string.Equals(connector.ConnectorId, trimmedConnectorId, StringComparison.Ordinal)
                    || (!string.IsNullOrWhiteSpace(userId)
                        && string.Equals(connector.ConnectorId, $"{userId}::{trimmedConnectorId}", StringComparison.Ordinal))));
            if (boundToSandbox && !hasStandaloneIsolated)
            {
                return ("agent-sandbox", requestedAttribution);
            }
        }

        // 3. Previously observed network requests in policy store for unattributed connectors.
        bool recordedUnattributed = Nac.PolicyStore.GetRawStateSnapshot().Requests.Any(req =>
            string.Equals(req.ConnectorId, trimmedConnectorId, StringComparison.Ordinal)
            && string.Equals(req.Attribution, "none", StringComparison.Ordinal)
            && (string.IsNullOrWhiteSpace(userId) || req.Systemwide || string.Equals(req.UserId, userId, StringComparison.Ordinal)));
        if (recordedUnattributed)
        {
            return (requestedExecContext, "none");
        }

        return (requestedExecContext, requestedAttribution);
    }

    public override Task<NetworkAccessDeleteRuleResponse> DeleteRule(
        NetworkAccessDeleteRuleRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        using IDisposable scope = BeginGrpcScope(context, "DeleteRule", ("RuleId", request.RuleId));

        return ExecuteNacRpc(() =>
        {
            long revision = Nac.PolicyStore.DeleteRule(
                ToActor(request.Actor),
                request.RuleId,
                request.ExpectedRevision);

            return new NetworkAccessDeleteRuleResponse
            {
                Revision = revision,
            };
        });
    }

    public override Task<NetworkAccessListActivityResponse> ListActivity(
        NetworkAccessListActivityRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        using IDisposable scope = BeginGrpcScope(context, "ListActivity");

        return ExecuteNacRpc(() =>
        {
            IReadOnlyList<NetworkAccessActivityEntry> entries = Nac.AuditRing.List(ToActor(request.Actor));
            var response = new NetworkAccessListActivityResponse();
            response.Entries.AddRange(entries.Select(static e => new NetworkAccessActivityEntryMessage
            {
                TimestampUtc = e.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
                SourceIp = e.SourceIp ?? string.Empty,
                UserId = e.UserId ?? string.Empty,
                AgentId = e.AgentId ?? string.Empty,
                ConnectorId = e.ConnectorId ?? string.Empty,
                Destination = e.Destination,
                Port = e.Port,
                Decision = e.Decision,
                Reason = e.Reason,
                Cluster = e.Cluster ?? string.Empty,
                OpenInternetBadge = e.OpenInternetBadge,
                CompatibilityMode = e.CompatibilityMode,
            }));
            return response;
        });
    }

    public override Task<NetworkAccessUserPrivilegesMessage> GetUserPrivileges(
        NetworkAccessGetUserPrivilegesRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        using IDisposable scope = BeginGrpcScope(context, "GetUserPrivileges");

        return ExecuteNacRpc(() =>
        {
            UserPrivileges privs = Nac.PolicyStore.GetUserPrivileges(
                ToActor(request.Actor),
                string.IsNullOrWhiteSpace(request.UserId) ? null : request.UserId,
                request.TargetIsAdmin);
            return ToMessage(privs);
        });
    }

    public override Task<NetworkAccessUserPrivilegesMessage> SetUserPrivileges(
        NetworkAccessSetUserPrivilegesRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        using IDisposable scope = BeginGrpcScope(context, "SetUserPrivileges");

        return ExecuteNacRpc(() =>
        {
            UserPrivileges privs = Nac.PolicyStore.SetUserPrivileges(
                ToActor(request.Actor),
                request.UserId,
                request.CanApproveOwn,
                request.CanAllowAllAgents,
                request.HasTargetIsAdmin ? request.TargetIsAdmin : null);
            return ToMessage(privs);
        });
    }

    public override Task<NetworkAccessBrakeMessage> GetBrake(
        NetworkAccessGetBrakeRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        using IDisposable scope = BeginGrpcScope(context, "GetBrake");

        return ExecuteNacRpc(() =>
        {
            BrakeState brake = Nac.PolicyStore.GetBrake(ToActor(request.Actor));
            return ToMessage(brake);
        });
    }

    public override async Task<NetworkAccessBrakeMessage> SetBrake(
        NetworkAccessSetBrakeRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        using IDisposable scope = BeginGrpcScope(context, "SetBrake");

        BrakeState brake = await ExecuteNacRpc(() =>
        {
            return Nac.PolicyStore.SetBrake(
                ToActor(request.Actor),
                request.Scope,
                string.IsNullOrWhiteSpace(request.UserId) ? null : request.UserId,
                request.On);
        }).ConfigureAwait(false);

        if (request.On && egressTopologyManager is not null)
        {
            string? targetUser = string.Equals(request.Scope, "system", StringComparison.OrdinalIgnoreCase)
                ? null
                : (string.IsNullOrWhiteSpace(request.UserId) ? request.Actor.UserId : request.UserId);
            await egressTopologyManager.EngageBrakeAsync(targetUser, Nac.Registry, context.CancellationToken).ConfigureAwait(false);
        }

        return ToMessage(brake);
    }

    public override Task<NetworkAccessStatusResponse> GetNetworkAccessStatus(
        WorkspacePingRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        using IDisposable scope = BeginGrpcScope(context, "GetNetworkAccessStatus");

        return ExecuteNacRpc(() =>
        {
            NetworkAccessStatusSnapshot status = Nac.GetStatus();
            return new NetworkAccessStatusResponse
            {
                Revision = status.Revision,
                RegistrySize = status.RegistrySize,
                Checks = status.Checks,
                Allowed = status.Allowed,
                Denied = status.Denied,
                Errors = status.Errors,
                RequestOverflow = status.RequestOverflow,
                RegistryConflicts = status.RegistryConflicts,
                EnvoyListenerHealthy = status.EnvoyListenerHealthy,
            };
        });
    }

    public override Task<ImageCompatStateResponse> GetImageCompatibility(
        NetworkAccessActorMessage request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        using IDisposable scope = BeginGrpcScope(context, "GetImageCompatibility");

        return ExecuteNacRpc(() =>
        {
            ImageCompatibilityStore store = this.imageCompatibilityStore
                ?? throw new RpcException(new Status(StatusCode.Unavailable, "Image compatibility store is not configured."));
            ImageCompatState state = store.GetState(ToActor(request));
            var response = new ImageCompatStateResponse();
            response.PendingRequests.AddRange(state.PendingRequests.Select(static r => new ImageCompatRequestMessage
            {
                ImageDigest = r.ImageDigest,
                ConnectorId = r.ConnectorId,
                ConnectorDisplayName = r.ConnectorDisplayName ?? string.Empty,
                FailureReason = r.FailureReason,
                FirstSeenUtc = r.FirstSeenUtc.ToString("O", CultureInfo.InvariantCulture),
                LastSeenUtc = r.LastSeenUtc.ToString("O", CultureInfo.InvariantCulture),
            }));
            response.Profiles.AddRange(state.Profiles.Select(ToMessage));
            return response;
        });
    }

    public override Task<ImageCompatProfileMessage> SetImageCompatibility(
        ImageCompatSetProfileRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        using IDisposable scope = BeginGrpcScope(context, "SetImageCompatibility");

        return ExecuteNacRpc(() =>
        {
            ImageCompatibilityStore store = this.imageCompatibilityStore
                ?? throw new RpcException(new Status(StatusCode.Unavailable, "Image compatibility store is not configured."));
            ImageCompatProfile profile = store.SetProfile(
                ToActor(request.Actor),
                request.ImageDigest,
                [.. request.Relaxations],
                string.IsNullOrWhiteSpace(request.ConnectorId) ? null : request.ConnectorId,
                string.IsNullOrWhiteSpace(request.ConnectorDisplayName) ? null : request.ConnectorDisplayName,
                string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason);
            return ToMessage(profile);
        });
    }

    public override Task<WorkspaceBoolResponse> DeleteImageCompatibility(
        ImageCompatDeleteProfileRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        using IDisposable scope = BeginGrpcScope(context, "DeleteImageCompatibility");

        return ExecuteNacRpc(() =>
        {
            ImageCompatibilityStore store = this.imageCompatibilityStore
                ?? throw new RpcException(new Status(StatusCode.Unavailable, "Image compatibility store is not configured."));
            bool deleted = store.DeleteProfile(ToActor(request.Actor), request.ImageDigest);
            return new WorkspaceBoolResponse
            {
                Ok = true,
                Message = deleted ? "Compatibility profile removed." : "Compatibility profile was not present.",
                Outcome = deleted ? WorkspaceOperationOutcome.Succeeded : WorkspaceOperationOutcome.NotFound,
            };
        });
    }

    private static string ResolveClientOrigin(ServerCallContext context)
    {
        try
        {
            X509Certificate2? cert = context.GetHttpContext()?.Connection.ClientCertificate;
            if (cert is not null)
            {
                string cn = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
                if (string.Equals(cn, ManagerClientIdentities.ChannelHostCommonName, StringComparison.Ordinal))
                {
                    return "channelhost";
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Unit tests without an HttpContext fall back to gateway.
        }

        return "gateway";
    }

    private static NetworkAccessActor ToActor(NetworkAccessActorMessage? actor)
        => actor is null || string.IsNullOrWhiteSpace(actor.UserId)
            ? throw new ArgumentException("Authenticated actor userId is required.")
            : new NetworkAccessActor(actor.UserId, actor.IsAdmin);

    private static NetworkAccessRequestMessage ToMessage(NetworkAccessRequestView view)
        => new()
        {
            Id = view.Request.Id,
            UserId = view.Request.UserId ?? string.Empty,
            AgentId = view.Request.AgentId ?? string.Empty,
            ConnectorId = view.Request.ConnectorId ?? string.Empty,
            Systemwide = view.Request.Systemwide,
            Attribution = view.Request.Attribution,
            Destination = view.Request.Destination,
            DestKind = view.Request.DestKind,
            Port = view.Request.Port,
            ActorKind = view.Request.ActorKind,
            FirstSeenUtc = view.Request.FirstSeenUtc.ToString("O", CultureInfo.InvariantCulture),
            LastSeenUtc = view.Request.LastSeenUtc.ToString("O", CultureInfo.InvariantCulture),
            Attempts = view.Request.Attempts,
            Status = view.Request.Status,
            Origin = view.Request.Origin,
            Purpose = view.Request.Purpose ?? string.Empty,
            CompatProfile = view.Request.CompatProfile ?? string.Empty,
            WaitingForAdmin = view.WaitingForAdmin,
            CompatibilityMode = view.CompatibilityMode,
        };

    private static NetworkAccessRuleMessage ToMessage(NetworkAccessRuleView view)
        => new()
        {
            Id = view.Rule.Id,
            ScopeKind = view.Rule.ScopeKind,
            UserId = view.Rule.UserId ?? string.Empty,
            AgentId = view.Rule.AgentId ?? string.Empty,
            ConnectorId = view.Rule.ConnectorId ?? string.Empty,
            Destination = view.Rule.Destination,
            DestKind = view.Rule.DestKind,
            Port = view.Rule.Port,
            Allowed = view.Rule.Allowed,
            AllowLan = view.Rule.AllowLan,
            DecidedBy = view.Rule.DecidedBy,
            DecidedAtUtc = view.Rule.DecidedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            Source = view.Rule.Source,
            Paused = view.Paused,
            OpenInternetBadge = view.OpenInternetBadge,
        };

    private static NetworkAccessUserPrivilegesMessage ToMessage(UserPrivileges privs)
        => new()
        {
            UserId = privs.UserId,
            CanApproveOwn = privs.CanApproveOwn,
            CanAllowAllAgents = privs.CanAllowAllAgents,
            SetBy = privs.SetBy,
            SetAtUtc = privs.SetAtUtc.ToString("O", CultureInfo.InvariantCulture),
        };

    private static NetworkAccessBrakeMessage ToMessage(BrakeState brake)
    {
        var msg = new NetworkAccessBrakeMessage
        {
            System = brake.System,
            SystemSetBy = brake.SystemSetBy ?? string.Empty,
            SystemSetAtUtc = brake.SystemSetAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
        };
        msg.Users.AddRange(brake.Users.Select(static u => new NetworkAccessUserBrakeMessage
        {
            UserId = u.UserId,
            SetBy = u.SetBy,
            SetAtUtc = u.SetAtUtc.ToString("O", CultureInfo.InvariantCulture),
            LockedByAdmin = u.LockedByAdmin,
        }));
        return msg;
    }

    private static ImageCompatProfileMessage ToMessage(ImageCompatProfile profile)
    {
        var msg = new ImageCompatProfileMessage
        {
            ImageDigest = profile.ImageDigest,
            ConnectorId = profile.ConnectorId ?? string.Empty,
            ConnectorDisplayName = profile.ConnectorDisplayName ?? string.Empty,
            GrantedBy = profile.GrantedBy,
            GrantedAtUtc = profile.GrantedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            Reason = profile.Reason ?? string.Empty,
        };
        msg.Relaxations.AddRange(profile.Relaxations);
        return msg;
    }

    private static Task<T> ExecuteNacRpc<T>(Func<T> action)
    {
        try
        {
            return Task.FromResult(action());
        }
        catch (RpcException)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
        }
        catch (KeyNotFoundException ex)
        {
            throw new RpcException(new Status(StatusCode.NotFound, ex.Message));
        }
        catch (ArgumentException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            throw new RpcException(new Status(StatusCode.Aborted, ex.Message));
        }
        catch (IOException ex)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, ex.Message));
        }
    }
}
