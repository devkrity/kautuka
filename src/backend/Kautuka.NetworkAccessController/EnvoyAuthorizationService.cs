using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using Kautuka.NetworkAccess.Protos.Envoy.Service.Auth.V3;

using RpcStatus = global::Grpc.Core.Status;
using Status = Kautuka.NetworkAccess.Protos.Envoy.Service.Auth.V3.Status;

namespace Kautuka.NetworkAccessController;

public delegate Task EnvoyAuthorizationFaultHook(
    CheckRequest request,
    ServerCallContext context,
    CancellationToken cancellationToken);

/// <summary>
/// Envoy <c>envoy.service.auth.v3.Authorization</c> gRPC implementation mapped exclusively
/// on the second minimal Kestrel host (<see cref="EnvoyAuthorizationHost"/>).
/// Admission concurrency is bounded to 32 concurrent calls; overflow returns <c>Unavailable</c>
/// (<c>capacity_exceeded</c>), never status code 7 (C14).
/// </summary>
public sealed partial class EnvoyAuthorizationService : Authorization.AuthorizationBase
{
    public const int MaxConcurrency = 32;
    public const string ExtAuthzMetadataNamespace = "envoy.filters.network.ext_authz";
    public const string ClusterMetadataKey = "cluster";

    // Allow for the Podman inspection and rules read within one bounded wait.
    private static readonly TimeSpan packageVerificationTimeout = TimeSpan.FromSeconds(30);
    private readonly NetworkAccessController controller;
    private readonly SemaphoreSlim admissionCapacity;
    private int packageVerificationWaiters;
    private volatile EnvoyAuthorizationFaultHook? faultHook;

    public EnvoyAuthorizationService(
        NetworkAccessController controller,
        SemaphoreSlim? admissionCapacity = null,
        EnvoyAuthorizationFaultHook? faultHook = null)
    {
        ArgumentNullException.ThrowIfNull(controller);
        this.controller = controller;
        this.admissionCapacity = admissionCapacity ?? new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
        this.faultHook = faultHook;
    }

    public EnvoyAuthorizationFaultHook? FaultHook
    {
        get => faultHook;
        set => faultHook = value;
    }

    public override async Task<CheckResponse> Check(CheckRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        if (context.CancellationToken.IsCancellationRequested)
        {
            controller.RecordError();
            throw new RpcException(new RpcStatus(StatusCode.Cancelled, "cancelled"));
        }

        if (context.Deadline <= DateTime.UtcNow)
        {
            controller.RecordError();
            throw new RpcException(new RpcStatus(StatusCode.DeadlineExceeded, "deadline_exceeded"));
        }

        if (!admissionCapacity.Wait(0))
        {
            controller.RecordError();
            throw new RpcException(new RpcStatus(StatusCode.Unavailable, "capacity_exceeded"));
        }

        bool ownsAdmission = true;
        try
        {
            EnvoyAuthorizationFaultHook? hook = faultHook;
            if (hook is not null)
            {
                await hook(request, context, context.CancellationToken).ConfigureAwait(false);
            }

            AttributeContext? attributes = request.Attributes;
            if (attributes is not null && (attributes.Request is not null || attributes.ContextExtensions.ContainsKey(GatewayConnectRouteKey)))
            {
                return await CheckGatewayConnectAsync(attributes, context.CancellationToken).ConfigureAwait(false);
            }

            SocketAddress? source = attributes?.Source?.Address?.SocketAddress;
            SocketAddress? destination = attributes?.Destination?.Address?.SocketAddress;

            if (source is null
                || destination is null
                || source.Protocol != 0
                || destination.Protocol != 0
                || destination.PortValue is < 1 or > 65535)
            {
                NetworkAccessDecisionResult badDecision = controller.EvaluateContainerConnection(
                    source?.Address,
                    destination?.Address,
                    (int)(destination?.PortValue ?? 0),
                    isTls: attributes?.TlsSession is not null,
                    sni: attributes?.TlsSession?.Sni);
                return CreateDenyResponse(badDecision.Reason);
            }

            bool isTls = attributes?.TlsSession is not null;
            string? sni = attributes?.TlsSession?.Sni;

            NetworkAccessDecisionResult decision = controller.EvaluateContainerConnection(
                source.Address,
                destination.Address,
                (int)destination.PortValue,
                isTls,
                sni,
                expectedSource: null,
                recordPendingRequest: true,
                waitForPackageVerification: true);

            if (decision.PendingPackageProxy is { } originalProxy)
            {
                RegistryEntry? packageCaller = decision.PendingSource;
                try
                {
                    if (Interlocked.Increment(ref packageVerificationWaiters) <= MaxConcurrency)
                    {
                        using var budget = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
                        budget.CancelAfter(packageVerificationTimeout);
                        while (decision.PendingPackageProxy?.Verification is { } verification)
                        {
                            // Live rules inspection can run while other connections are authorized.
                            admissionCapacity.Release();
                            ownsAdmission = false;
                            bool verified = false;
                            try
                            {
                                verified = await verification.WaitAsync(budget.Token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested) { }

                            ownsAdmission = admissionCapacity.Wait(0);
                            if (!ownsAdmission)
                            {
                                try
                                {
                                    await admissionCapacity.WaitAsync(budget.Token).ConfigureAwait(false);
                                    ownsAdmission = true;
                                }
                                catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
                                {
                                    throw new RpcException(new RpcStatus(StatusCode.Unavailable, "capacity_exceeded"));
                                }
                            }

                            context.CancellationToken.ThrowIfCancellationRequested();
                            verified = verified && !budget.IsCancellationRequested;
                            decision = controller.EvaluateContainerConnection(source.Address, destination.Address,
                                (int)destination.PortValue, isTls, sni, expectedSource: packageCaller,
                                recordPendingRequest: false, waitForPackageVerification: verified,
                                expectedPackageProxyGeneration: originalProxy.Generation, allowVerifiedPackageProxy: verified);
                        }
                    }
                    else
                    {
                        decision = controller.EvaluateContainerConnection(source.Address, destination.Address,
                            (int)destination.PortValue, isTls, sni, expectedSource: packageCaller,
                            recordPendingRequest: false, waitForPackageVerification: false, allowVerifiedPackageProxy: false);
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref packageVerificationWaiters);
                }
            }

            if (decision.PendingSource is { } originalSource)
                decision = await controller.RecheckAfterApprovalAsync(decision, originalSource.UserId ?? "system",
                    () => controller.EvaluateContainerConnection(source.Address, destination.Address, (int)destination.PortValue,
                        isTls, sni, expectedSource: originalSource, recordPendingRequest: false), context.CancellationToken).ConfigureAwait(false);

            if (!decision.Allowed)
            {
                return CreateDenyResponse(decision.Reason);
            }

            var dynamicMetadata = new Struct();
            dynamicMetadata.Fields[ClusterMetadataKey] = Value.ForString(decision.Cluster ?? NetworkAccessController.ClusterDfpPublic);

            return new CheckResponse
            {
                Status = new Status { Code = 0 },
                DynamicMetadata = dynamicMetadata,
            };
        }
        catch (RpcException)
        {
            controller.RecordError();
            throw;
        }
        catch (OperationCanceledException)
        {
            controller.RecordError();
            StatusCode code = context.Deadline <= DateTime.UtcNow
                ? StatusCode.DeadlineExceeded
                : StatusCode.Cancelled;
            throw new RpcException(new RpcStatus(code, "check_cancelled"));
        }
        catch (Exception)
        {
            controller.RecordError();
            throw new RpcException(new RpcStatus(StatusCode.Unavailable, "internal_error"));
        }
        finally
        {
            if (ownsAdmission)
                admissionCapacity.Release();
        }
    }

    private static CheckResponse CreateDenyResponse(string reason)
        => new()
        {
            Status = new Status
            {
                Code = 7,
                Message = reason,
            },
        };
}
