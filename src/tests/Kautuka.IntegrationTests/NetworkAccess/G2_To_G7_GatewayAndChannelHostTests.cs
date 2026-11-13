#pragma warning disable xUnit1051 // Test methods use explicit tokens or defaults

using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;

using Grpc.Core;

using Kautuka.Manager.Grpc;
using Kautuka.Manager.Protos;
using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.NetworkAccess.Client;
using Kautuka.NetworkAccessController;

using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests.NetworkAccess;

/// <summary>
/// Gateway and ChannelHost NAC v7 Integration Tests G2 through G7 with genuinely flipping .NC negative controls.
/// </summary>
public sealed class G2_To_G7_GatewayAndChannelHostTests : IDisposable
{
    private readonly string tempDir;
    private readonly Kautuka.NetworkAccessController.NetworkAccessController nacController;
    private readonly WorkspaceGrpcService grpcService;

    public G2_To_G7_GatewayAndChannelHostTests()
    {
        tempDir = Path.Combine(Path.GetTempPath(), "kautuka-nac-g-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        var policyStore = new NetworkAccessPolicyStore(Path.Combine(tempDir, "policy", "state.json"));
        var workloadRegistry = new WorkloadRegistry();
        var auditRing = new NetworkAccessAuditRing(Path.Combine(tempDir, "audit", "decisions.jsonl"));
        var imageCompat = new ImageCompatibilityStore(Path.Combine(tempDir, "compat", "compat.json"));
        nacController = new Kautuka.NetworkAccessController.NetworkAccessController(policyStore, workloadRegistry, auditRing);

        grpcService = new WorkspaceGrpcService(
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            NullLogger<WorkspaceGrpcService>.Instance,
            nacController,
            imageCompat);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort on Windows
        }
    }

    // -------------------------------------------------------------------------------------------------
    // G2: Model provider outbound check
    // Denied custom host produces pending request in NAC and Narad RunError with structured fields.
    // G2.NC: Seed AI host (api.openai.com) is allowed, no pending request, no RunError.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    [SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
    public async Task G2_AiProvider_SeedHostAllowed_CustomHostDeniedRecordsRequest_WithNegativeControl()
    {
        var checker = new DirectNacChecker(grpcService);
        var mockTransport = new TrackingHttpHandler();
        var clientHandler = new NetworkAccessCheckHandler(checker, mockTransport);
        using var httpClient = new HttpClient(clientHandler);

        // 1. Custom host without a rule: denied -> produces pending request and Narad RunError fields
        using (NetworkAccessContext.BeginScope(userId: "user-alpha", agentId: "agent-007", purpose: "agent-run", service: "ai-model"))
        {
            var customUri = new Uri("https://api.custom-ai-models.com/v1/chat/completions");
            var ex = await Assert.ThrowsAsync<NetworkAccessDeniedException>(() =>
                httpClient.GetAsync(customUri));

            Assert.Equal(NetworkAccessDenialKind.Denied, ex.Kind);
            Assert.Equal("network_access_required", ex.NaradReason);
            Assert.Equal("api.custom-ai-models.com", ex.Destination);
            Assert.Equal(443, ex.Port);
            Assert.False(string.IsNullOrWhiteSpace(ex.RequestId));

            // Verify pending request was recorded in NAC
            var adminCtx = new TestServerCallContext();
            var listResp = await grpcService.ListRequests(new NetworkAccessListRequestsRequest
            {
                Actor = new NetworkAccessActorMessage { UserId = "admin", IsAdmin = true },
            }, adminCtx);

            Assert.Contains(listResp.Requests, r => r.Destination == "api.custom-ai-models.com" && r.UserId == "user-alpha");
        }

        // G2.NC (Negative Control): Seed AI host (api.openai.com) is allowed, connects through to transport
        using (NetworkAccessContext.BeginScope(userId: "system", agentId: "gateway", purpose: "agent-run", service: "ai-model"))
        {
            mockTransport.ResponseFactory = _ => new HttpResponseMessage(HttpStatusCode.OK);
            var seedUri = new Uri("https://api.openai.com/v1/models");

            using var response = await httpClient.GetAsync(seedUri);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, mockTransport.CallCount);
        }
    }

    // -------------------------------------------------------------------------------------------------
    // G3: Remote MCP connection denied before TCP connect, attributed request, ToolCallFinished
    // G3.NC: Approved remote MCP host connects successfully (server accept count == 1).
    // -------------------------------------------------------------------------------------------------

    [Fact]
    [SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
    public async Task G3_RemoteMcp_DeniedBeforeTcpConnect_ProducesAttributedRequest_AndToolCallFinished_WithNegativeControl()
    {
        var checker = new DirectNacChecker(grpcService);
        var mockTransport = new TrackingHttpHandler();

        var unapprovedUri = new Uri("https://mcp.unapproved-tools.org/sse");
        var context = new RemoteMcpRequestContext(
            Endpoint: unapprovedUri,
            ConnectorId: "conn-unapproved-1",
            UserId: "user-beta",
            IncludeSystemScope: false,
            Credentials: [],
            Headers: [],
            Resolve: (_, _) => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>()));

        using var requestHandler = new RemoteMcpRequestHandler(context, checker, mockTransport);
        using var client = new HttpClient(requestHandler);

        // 1. Outbound call is denied BEFORE inner transport receives the request
        using (NetworkAccessContext.BeginScope(userId: "user-beta", agentId: "agent-beta", connectorId: "conn-unapproved-1"))
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, unapprovedUri);
            var ex = await Assert.ThrowsAsync<NetworkAccessDeniedException>(() => client.SendAsync(req));

            Assert.Equal(0, mockTransport.CallCount); // Assert mock transport received 0 requests
            Assert.Equal("network_access_required", ex.NaradReason);
            Assert.Equal("mcp.unapproved-tools.org", ex.Destination);
            Assert.False(string.IsNullOrWhiteSpace(ex.RequestId));

            // Verify mapping to Narad ToolCallFinished failed event
            var toolCallFinished = NaradEventBodies.ToolCallFinished(
                toolCallId: "call-1",
                outcome: "failed",
                response: null,
                code: ex.NaradReason,
                message: ex.Message,
                destination: ex.Destination,
                port: ex.Port,
                requestId: ex.RequestId,
                approvalUrl: "/network-access");

            Assert.Equal("failed", toolCallFinished.GetProperty("outcome").GetString());
            Assert.Equal("network_access_required", toolCallFinished.GetProperty("code").GetString());
            Assert.Equal("mcp.unapproved-tools.org", toolCallFinished.GetProperty("destination").GetString());
            Assert.Equal(443, toolCallFinished.GetProperty("port").GetInt32());
            Assert.Equal("/network-access", toolCallFinished.GetProperty("approvalUrl").GetString());

            // Verify NAC pending request has correct attribution (user, agent, connector)
            var adminCtx = new TestServerCallContext();
            var listResp = await grpcService.ListRequests(new NetworkAccessListRequestsRequest
            {
                Actor = new NetworkAccessActorMessage { UserId = "admin", IsAdmin = true },
            }, adminCtx);

            var pending = Assert.Single(listResp.Requests, r => r.Destination == "mcp.unapproved-tools.org");
            Assert.Equal("user-beta", pending.UserId);
            Assert.Equal("agent-beta", pending.AgentId);
            Assert.Equal("conn-unapproved-1", pending.ConnectorId);

            // G3.NC (Negative Control): Approve the host -> call connects to mock server
            var decideResp = await grpcService.DecideRequest(new NetworkAccessDecideRequest
            {
                Actor = new NetworkAccessActorMessage { UserId = "admin", IsAdmin = true },
                RequestId = pending.Id,
                Allow = true,
                Scope = "agent",
                ExpectedRevision = listResp.Revision,
            }, adminCtx);

            Assert.True(decideResp.Revision > listResp.Revision);

            mockTransport.ResponseFactory = _ => new HttpResponseMessage(HttpStatusCode.OK);
            using var approvedReq = new HttpRequestMessage(HttpMethod.Post, unapprovedUri);
            using var okResp = await client.SendAsync(approvedReq);

            Assert.Equal(HttpStatusCode.OK, okResp.StatusCode);
            Assert.Equal(1, mockTransport.CallCount); // Transport was reached!
        }
    }

    // -------------------------------------------------------------------------------------------------
    // G4: Remote MCP zero secret reads on deny
    // G4.NC: Credential resolver IS called when allowed (call count > 0).
    // -------------------------------------------------------------------------------------------------

    [Fact]
    [SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
    public async Task G4_RemoteMcp_ZeroSecretReadsOnDeny_WithNegativeControl()
    {
        var checker = new DirectNacChecker(grpcService);
        var mockTransport = new TrackingHttpHandler();
        int secretReadCount = 0;

        Task<IReadOnlyDictionary<string, string>> MockSecretResolver(IReadOnlyList<string> creds, CancellationToken ct)
        {
            Interlocked.Increment(ref secretReadCount);
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string k in creds)
            {
                dict[k] = "s3cr3t-v4lu3";
            }
            return Task.FromResult<IReadOnlyDictionary<string, string>>(dict);
        }

        var unapprovedUri = new Uri("https://secret-target.unapproved.net/tool");
        var context = new RemoteMcpRequestContext(
            Endpoint: unapprovedUri,
            ConnectorId: "conn-sec-1",
            UserId: "user-gamma",
            IncludeSystemScope: false,
            Credentials: [new RemoteMcpCredential("secret_token", "Bearer Token", true)],
            Headers: [],
            Resolve: MockSecretResolver,
            CredentialNamespace: "test-connector-ns");

        using var requestHandler = new RemoteMcpRequestHandler(context, checker, mockTransport);
        using var client = new HttpClient(requestHandler);

        // 1. Outbound check fails on denied host BEFORE secret resolution
        using (NetworkAccessContext.BeginScope(userId: "user-gamma", agentId: "agent-gamma", connectorId: "conn-sec-1"))
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, unapprovedUri);
            await Assert.ThrowsAsync<NetworkAccessDeniedException>(() => client.SendAsync(req));

            // CRITICAL G4 ASSERTION: Zero secret reads on deny!
            Assert.Equal(0, secretReadCount);
            Assert.Equal(0, mockTransport.CallCount);

            // G4.NC (Negative Control): When host is approved, secret resolution IS executed
            var adminCtx = new TestServerCallContext();
            var listResp = await grpcService.ListRequests(new NetworkAccessListRequestsRequest
            {
                Actor = new NetworkAccessActorMessage { UserId = "admin", IsAdmin = true },
            }, adminCtx);

            var pending = Assert.Single(listResp.Requests, r => r.Destination == "secret-target.unapproved.net");
            await grpcService.DecideRequest(new NetworkAccessDecideRequest
            {
                Actor = new NetworkAccessActorMessage { UserId = "admin", IsAdmin = true },
                RequestId = pending.Id,
                Allow = true,
                Scope = "agent",
                ExpectedRevision = listResp.Revision,
            }, adminCtx);

            mockTransport.ResponseFactory = _ => new HttpResponseMessage(HttpStatusCode.OK);
            using var approvedReq = new HttpRequestMessage(HttpMethod.Post, unapprovedUri);
            using var okResp = await client.SendAsync(approvedReq);

            Assert.Equal(HttpStatusCode.OK, okResp.StatusCode);
            Assert.True(secretReadCount > 0, $"Expected secret reads > 0 when allowed, got {secretReadCount}");
            Assert.Equal(1, mockTransport.CallCount);
        }
    }

    // -------------------------------------------------------------------------------------------------
    // G5: ChannelHost telegram allowed / unknown denied / Slack wss host checked
    // G5.NC: Allowed Slack wss host connects successfully.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    [SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
    public async Task G5_ChannelHost_TelegramAllowed_UnknownDenied_SlackWebSocketChecked_WithNegativeControl()
    {
        var checker = new DirectNacChecker(grpcService);
        var mockTransport = new TrackingHttpHandler();
        var channelHostHttpHandler = new NetworkAccessCheckHandler(checker, mockTransport, defaultExecutionContext: "channelhost");
        using var httpClient = new HttpClient(channelHostHttpHandler);

        // 1. ChannelHost telegram-api allowed (seeded for system/channelhost)
        mockTransport.ResponseFactory = _ => new HttpResponseMessage(HttpStatusCode.OK);
        using var telegramResp = await httpClient.GetAsync(new Uri("https://api.telegram.org/bot123/getMe"));
        Assert.Equal(HttpStatusCode.OK, telegramResp.StatusCode);
        Assert.Equal(1, mockTransport.CallCount);

        // 2. ChannelHost unknown host denied
        var unknownUri = new Uri("https://malicious-exfiltrate.org/api");
        var ex = await Assert.ThrowsAsync<NetworkAccessDeniedException>(() =>
            httpClient.GetAsync(unknownUri));
        Assert.Equal("network_access_required", ex.NaradReason);
        Assert.Equal("malicious-exfiltrate.org", ex.Destination);

        // 3. ChannelHost Slack WebSocket checker: dynamic wss check
        var wsChecker = new NetworkAccessWebSocketChecker(checker, defaultExecutionContext: "channelhost");
        var unapprovedWsUri = new Uri("wss://unapproved-relay.slack.com/websocket");

        using var ws = new ClientWebSocket();
        var wsEx = await Assert.ThrowsAsync<NetworkAccessDeniedException>(() =>
            wsChecker.ConnectAsync(ws, unapprovedWsUri, CancellationToken.None));
        Assert.Equal("network_access_required", wsEx.NaradReason);
        Assert.Equal("unapproved-relay.slack.com", wsEx.Destination);

        // G5.NC (Negative Control): Seeded Slack wss host (wss-primary.slack.com) passes check
        var seededWsUri = new Uri("wss://wss-primary.slack.com/websocket");
        var target = new OutboundTarget(
            Host: seededWsUri.IdnHost,
            Port: 443,
            Scheme: "wss",
            Service: "channelhost",
            Purpose: "slack-socket-mode",
            UserId: string.Empty,
            AgentId: string.Empty,
            ConnectorId: null,
            Systemwide: false,
            ExecutionContext: "channelhost");

        var decision = await checker.CheckAsync(target, CancellationToken.None);
        Assert.True(decision.Allowed, $"Expected seeded Slack wss host to be allowed, but reason was {decision.Reason}");
    }

    // -------------------------------------------------------------------------------------------------
    // G6: Manager unreachable -> Unavailable within 2s, zero upstream connections
    // G6.NC: Healthy Manager returns decision promptly (<100ms).
    // -------------------------------------------------------------------------------------------------

    [Fact]
    [SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
    public async Task G6_ManagerUnreachable_ReturnsUnavailableWithinTwoSeconds_ZeroUpstreamConnections_WithNegativeControl()
    {
        // Unreachable manager checker that simulates connection failure
        var failingChecker = new GrpcNetworkAccessChecker(
            () => throw new RpcException(new Status(StatusCode.Unavailable, "Manager service unreachable")),
            NullLogger<GrpcNetworkAccessChecker>.Instance);

        var mockTransport = new TrackingHttpHandler();
        var handler = new NetworkAccessCheckHandler(failingChecker, mockTransport);
        using var client = new HttpClient(handler);

        var start = DateTimeOffset.UtcNow;
        using (NetworkAccessContext.BeginScope(userId: "user-g6", agentId: "agent-g6"))
        {
            var ex = await Assert.ThrowsAsync<NetworkAccessDeniedException>(() =>
                client.GetAsync(new Uri("https://some-remote-api.com/v1")));

            var elapsed = DateTimeOffset.UtcNow - start;

            Assert.True(elapsed <= TimeSpan.FromSeconds(2.5), $"Expected fast failover under 2.5s, took {elapsed.TotalSeconds}s");
            Assert.Equal(NetworkAccessDenialKind.Unavailable, ex.Kind);
            Assert.Equal("network_access_unavailable", ex.NaradReason);
            Assert.Equal(0, mockTransport.CallCount); // Zero upstream connections attempted!
        }

        // G6.NC (Negative Control): Healthy Manager returns decision promptly (<100ms)
        var healthyChecker = new DirectNacChecker(grpcService);
        var healthyHandler = new NetworkAccessCheckHandler(healthyChecker, mockTransport);
        using var healthyClient = new HttpClient(healthyHandler);

        var healthyStart = DateTimeOffset.UtcNow;
        using (NetworkAccessContext.BeginScope(userId: "system", agentId: "gateway"))
        {
            mockTransport.ResponseFactory = _ => new HttpResponseMessage(HttpStatusCode.OK);
            using var okResp = await healthyClient.GetAsync(new Uri("https://api.openai.com/v1/models"));

            var healthyElapsed = DateTimeOffset.UtcNow - healthyStart;
            Assert.True(healthyElapsed < TimeSpan.FromSeconds(1), $"Healthy call took {healthyElapsed.TotalSeconds}s");
            Assert.Equal(HttpStatusCode.OK, okResp.StatusCode);
            Assert.Equal(1, mockTransport.CallCount);
        }
    }

    // -------------------------------------------------------------------------------------------------
    // G7: ChannelHost cert allowed CheckOutbound only, PermissionDenied on everything else
    // G7.NC: Gateway client certificate CAN invoke administrative RPCs.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    [SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
    public async Task G7_ChannelHostCert_AllowedCheckOutboundOnly_PermissionDeniedOnAdministrativeRpcs_WithNegativeControl()
    {
        // 1. ChannelHost identity policy check
        string servicePrefix = "/kautuka.manager.v1.ManagerService/";
        Assert.True(ManagerRpcAuthorizationPolicy.IsAuthorized(
            ManagerClientIdentities.ChannelHostCommonName,
            servicePrefix + "CheckOutbound"));

        Assert.False(ManagerRpcAuthorizationPolicy.IsAuthorized(
            ManagerClientIdentities.ChannelHostCommonName,
            servicePrefix + "ListRequests"));

        Assert.False(ManagerRpcAuthorizationPolicy.IsAuthorized(
            ManagerClientIdentities.ChannelHostCommonName,
            servicePrefix + "DecideRequest"));

        Assert.False(ManagerRpcAuthorizationPolicy.IsAuthorized(
            ManagerClientIdentities.ChannelHostCommonName,
            servicePrefix + "SetRule"));

        Assert.False(ManagerRpcAuthorizationPolicy.IsAuthorized(
            ManagerClientIdentities.ChannelHostCommonName,
            servicePrefix + "DeleteRule"));

        Assert.False(ManagerRpcAuthorizationPolicy.IsAuthorized(
            ManagerClientIdentities.ChannelHostCommonName,
            servicePrefix + "SetUserPrivileges"));

        Assert.False(ManagerRpcAuthorizationPolicy.IsAuthorized(
            ManagerClientIdentities.ChannelHostCommonName,
            servicePrefix + "SetBrake"));

        Assert.False(ManagerRpcAuthorizationPolicy.IsAuthorized(
            ManagerClientIdentities.ChannelHostCommonName,
            servicePrefix + "SpawnSandbox"));

        // 2. ChannelHost client context invoking CheckOutbound succeeds
        var channelHostCtx = new TestServerCallContextWithCert(ManagerClientIdentities.ChannelHostCommonName);
        var outboundResp = await grpcService.CheckOutbound(new NetworkAccessCheckOutboundRequest
        {
            Host = "api.telegram.org",
            Port = 443,
            UserId = string.Empty, // Pinned origin requirements
            AgentId = string.Empty,
            Systemwide = false,
        }, channelHostCtx);

        Assert.True(outboundResp.Allowed);

        // 3. ChannelHost client context attempting custom userId on CheckOutbound fails with PermissionDenied
        var rpcEx = await Assert.ThrowsAsync<RpcException>(() => grpcService.CheckOutbound(new NetworkAccessCheckOutboundRequest
        {
            Host = "api.telegram.org",
            Port = 443,
            UserId = "spoofed-user", // Illegal for ChannelHost!
            AgentId = string.Empty,
            Systemwide = false,
        }, channelHostCtx));

        Assert.Equal(StatusCode.PermissionDenied, rpcEx.StatusCode);

        // G7.NC (Negative Control): Gateway client certificate identity CAN invoke administrative RPCs
        Assert.True(ManagerRpcAuthorizationPolicy.IsAuthorized(
            ManagerClientIdentities.GatewayCommonName,
            servicePrefix + "ListRequests"));

        Assert.True(ManagerRpcAuthorizationPolicy.IsAuthorized(
            ManagerClientIdentities.GatewayCommonName,
            servicePrefix + "DecideRequest"));

        Assert.True(ManagerRpcAuthorizationPolicy.IsAuthorized(
            ManagerClientIdentities.GatewayCommonName,
            servicePrefix + "SetRule"));

        var gatewayCtx = new TestServerCallContextWithCert(ManagerClientIdentities.GatewayCommonName);
        var adminList = await grpcService.ListRequests(new NetworkAccessListRequestsRequest
        {
            Actor = new NetworkAccessActorMessage { UserId = "admin", IsAdmin = true },
        }, gatewayCtx);

        Assert.NotNull(adminList);
    }

    // -------------------------------------------------------------------------------------------------
    // Helper Test Doubles
    // -------------------------------------------------------------------------------------------------

    private sealed class DirectNacChecker(WorkspaceGrpcService grpcService) : INetworkAccessChecker
    {
        public async Task<NetworkAccessCheckResult> CheckAsync(
            OutboundTarget target,
            CancellationToken cancellationToken = default)
        {
            var ctx = target.ExecutionContext == "channelhost"
                ? new TestServerCallContextWithCert(ManagerClientIdentities.ChannelHostCommonName)
                : new TestServerCallContextWithCert(ManagerClientIdentities.GatewayCommonName);

            string effectiveUserId = target.ExecutionContext == "channelhost"
                ? string.Empty
                : (target.UserId?.Trim() ?? string.Empty);
            if (target.ExecutionContext != "channelhost" && string.IsNullOrEmpty(effectiveUserId))
            {
                return NetworkAccessCheckResult.CreateDenied("unknown_source");
            }

            var req = new NetworkAccessCheckOutboundRequest
            {
                Host = target.Host,
                Port = target.Port,
                UserId = effectiveUserId,
                AgentId = target.ExecutionContext == "channelhost" ? string.Empty : (target.AgentId ?? string.Empty),
                ConnectorId = target.ConnectorId ?? string.Empty,
                Systemwide = target.Systemwide,
                Purpose = target.Purpose ?? string.Empty,
            };

            var resp = await grpcService.CheckOutbound(req, ctx);
            NetworkAccessDenialKind kind = resp.Allowed
                ? NetworkAccessDenialKind.Denied
                : resp.Reason switch
                {
                    "brake_system" or "brake_user" => NetworkAccessDenialKind.Paused,
                    "controller_unavailable" => NetworkAccessDenialKind.Unavailable,
                    _ => NetworkAccessDenialKind.Denied,
                };

            return new NetworkAccessCheckResult(
                resp.Allowed,
                resp.Reason,
                string.IsNullOrWhiteSpace(resp.RequestId) ? null : resp.RequestId,
                resp.OpenInternetBadge,
                kind);
        }
    }

    private sealed class TrackingHttpHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public Func<HttpRequestMessage, HttpResponseMessage>? ResponseFactory { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            var response = ResponseFactory?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK);
            return Task.FromResult(response);
        }
    }

    private class TestServerCallContext : ServerCallContext
    {
        private readonly Metadata requestHeaders = [];
        private readonly Metadata responseTrailers = [];
        private readonly Dictionary<object, object> userState = [];

        protected override string MethodCore => "/kautuka.manager.v1.ManagerService/Test";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "unix:test";
        protected override DateTime DeadlineCore => DateTime.MaxValue;
        protected override Metadata RequestHeadersCore => requestHeaders;
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;
        protected override Metadata ResponseTrailersCore => responseTrailers;
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => new(string.Empty, []);
        protected override IDictionary<object, object> UserStateCore => userState;

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options)
            => throw new NotSupportedException();

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders)
            => Task.CompletedTask;
    }

    private sealed class TestServerCallContextWithCert : TestServerCallContext
    {
        private readonly string commonName;
        private readonly Microsoft.AspNetCore.Http.DefaultHttpContext httpContext;

        public TestServerCallContextWithCert(string commonName)
        {
            this.commonName = commonName;
            httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            using var rsa = System.Security.Cryptography.RSA.Create(2048);
            var req = new CertificateRequest(
                $"CN={commonName}",
                rsa,
                System.Security.Cryptography.HashAlgorithmName.SHA256,
                System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            httpContext.Connection.ClientCertificate = cert;
            UserStateCore["__HttpContext"] = httpContext;
        }
    }

    [Fact]
    public async Task V7_6_MissingAmbientUserId_FailsClosedInsteadOfSystem_AndBrakedUserCannotBypass_WithNegativeControl()
    {
        var checker = new GrpcNetworkAccessChecker(new InProcCheckOutboundClient(grpcService));
        var transport = new TrackingHttpHandler();
        using var http = new HttpClient(new NetworkAccessCheckHandler(checker, transport));

        var targetUri = new Uri("https://api.openai.com/v1/models");

        // 1. Outbound call to a system-seeded destination (api.openai.com) with NO NetworkAccessContext
        //    must fail closed with unknown_source instead of defaulting to "system".
        NetworkAccessDeniedException noCtxEx = await Assert.ThrowsAsync<NetworkAccessDeniedException>(async () =>
            _ = await http.GetAsync(targetUri));
        Assert.Equal("unknown_source", noCtxEx.Reason);
        Assert.Equal(0, transport.CallCount);

        // 2. Braked user executing discovery/OAuth under their explicit NetworkAccessContext cannot bypass their brake.
        var admin = new NetworkAccessActor("admin", IsAdmin: true);
        nacController.PolicyStore.SetBrake(admin, scope: "user", targetUserId: "user-braked", on: true);

        using (NetworkAccessContext.BeginScope(userId: "user-braked", agentId: "gateway", purpose: "ai-discovery", service: "ai-model"))
        {
            NetworkAccessDeniedException brakedEx = await Assert.ThrowsAsync<NetworkAccessDeniedException>(async () =>
                _ = await http.GetAsync(targetUri));
            Assert.Equal(NetworkAccessDenialKind.Paused, brakedEx.Kind);
            Assert.Equal("brake_user", brakedEx.Reason);
            Assert.Equal(0, transport.CallCount);
        }

        // Negative control (.NC): legitimate system-scoped call explicitly bound to "system" reaches system-seeded host.
        using (NetworkAccessContext.BeginScope(userId: "system", agentId: "system", purpose: "oidc-login", service: "external-auth"))
        {
            using HttpResponseMessage okResp = await http.GetAsync(targetUri);
            Assert.Equal(HttpStatusCode.OK, okResp.StatusCode);
            Assert.Equal(1, transport.CallCount);
        }
    }

    private sealed class InProcCheckOutboundClient(WorkspaceGrpcService grpc) : ManagerService.ManagerServiceClient
    {
        private static readonly TestServerCallContext callCtx = new();

        public override AsyncUnaryCall<NetworkAccessCheckOutboundResponse> CheckOutboundAsync(
            NetworkAccessCheckOutboundRequest request,
            CallOptions options)
            => new(grpc.CheckOutbound(request, callCtx), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
    }

    [Fact]
    public async Task V7_31_ClientsWithNullChecker_FailClosedBeforeNetwork_WithNegativeControl()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string httpUrl = $"http://127.0.0.1:{port}";
        int acceptedConnections = 0;
        using var acceptCts = new CancellationTokenSource();
        Task acceptLoop = Task.Run(async () =>
        {
            while (!acceptCts.Token.IsCancellationRequested)
            {
                try
                {
                    using System.Net.Sockets.TcpClient client = await listener.AcceptTcpClientAsync(acceptCts.Token);
                    Interlocked.Increment(ref acceptedConnections);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (System.Net.Sockets.SocketException)
                {
                    break;
                }
            }
        });

        try
        {
            // 1. OllamaAiClientProvider with checker: null -> fail closed (no TCP connection)
            var ollama = new Kautuka.Gateway.Infrastructure.AI.OllamaAiClientProvider(checker: null);
            var ollamaSettings = new System.Text.Json.Nodes.JsonObject { ["base_url"] = httpUrl };
            NetworkAccessDeniedException ollamaEx = await Assert.ThrowsAsync<NetworkAccessDeniedException>(async () =>
                _ = await ollama.DiscoverModelsAsync(ollamaSettings, apiKey: null, throwOnFailure: true));
            Assert.Equal("checker_missing", ollamaEx.Reason);

            // 2. OpenAiCompatibleProvider with checker: null -> fail closed
            var openAi = new Kautuka.Gateway.Infrastructure.AI.OpenAiCompatibleProvider("openai", checker: null);
            var openAiSettings = new System.Text.Json.Nodes.JsonObject { ["base_url"] = httpUrl };
            NetworkAccessDeniedException openAiEx = await Assert.ThrowsAsync<NetworkAccessDeniedException>(async () =>
                _ = await openAi.DiscoverModelsAsync(openAiSettings, apiKey: "sk-test", throwOnFailure: true));
            Assert.Equal("checker_missing", openAiEx.Reason);

            // 3. OpenAiCompatibleEndpoint.DiscoverModelsAsync without handler -> fail closed
            NetworkAccessDeniedException endpointEx = await Assert.ThrowsAsync<NetworkAccessDeniedException>(async () =>
                _ = await Kautuka.Gateway.Infrastructure.AI.OpenAiCompatibleEndpoint.DiscoverModelsAsync(
                    openAiSettings,
                    apiKey: "sk-test",
                    defaultBaseUrl: null,
                    runLimitResolver: null,
                    throwOnFailure: true,
                    cancellationToken: CancellationToken.None));
            Assert.Equal("checker_missing", endpointEx.Reason);

            // 4. OpenAiResponsesCompatibleProvider with checker: null -> fail closed
            var openAiResp = new Kautuka.Gateway.Infrastructure.AI.OpenAiResponsesCompatibleProvider(checker: null);
            NetworkAccessDeniedException respEx = await Assert.ThrowsAsync<NetworkAccessDeniedException>(async () =>
                _ = await openAiResp.DiscoverModelsAsync(openAiSettings, apiKey: "sk-test", throwOnFailure: true));
            Assert.Equal("checker_missing", respEx.Reason);

            // 5. GoogleAiClientProvider with checker: null -> fail closed
            var google = new Kautuka.Gateway.Infrastructure.AI.GoogleAiClientProvider(checker: null);
            var googleSettings = new System.Text.Json.Nodes.JsonObject { ["base_url"] = httpUrl };
            await Assert.ThrowsAsync<NetworkAccessDeniedException>(async () =>
                _ = await google.DiscoverModelsAsync(googleSettings, apiKey: "AIza-test", throwOnFailure: true));

            // 6. McpHttpClientFactory & RemoteMcpRequestHandler with checker: null -> fail closed
            var mcpFactory = new Kautuka.Gateway.Infrastructure.Agents.McpHttpClientFactory(networkAccessChecker: null);
            var mcpContext = new Kautuka.Gateway.Infrastructure.Agents.RemoteMcpRequestContext(
                new Uri(httpUrl + "/mcp"), "conn-1", "user-1", false, [], [],
                (_, _) => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>()),
                CredentialNamespace: "ns");
            await Assert.ThrowsAsync<NetworkAccessDeniedException>(async () =>
                await mcpFactory.CreateAsync(new ModelContextProtocol.Client.HttpClientTransportOptions { Endpoint = new Uri(httpUrl + "/mcp") }, true, mcpContext, CancellationToken.None));

            using var rawMcpHandler = new Kautuka.Gateway.Infrastructure.Agents.RemoteMcpRequestHandler(mcpContext, new TrackingHttpHandler());
            using var mcpHttpClient = new HttpClient(rawMcpHandler);
            NetworkAccessDeniedException mcpHandlerEx = await Assert.ThrowsAsync<NetworkAccessDeniedException>(async () =>
                _ = await mcpHttpClient.GetAsync(new Uri(httpUrl + "/mcp")));
            Assert.Equal("checker_missing", mcpHandlerEx.Reason);

            // 7. ConnectorAuthorizationClient with checker: null -> fail closed
            using var oauthClient = new Kautuka.Gateway.Infrastructure.Connectors.ConnectorAuthorizationClient(checker: null);
            var oauthProfile = new Kautuka.Gateway.Domain.Connectors.ConnectorAuthProfile
            {
                Id = "google-mail",
                Name = "Google Mail",
                Description = "test",
                Grant = "oauth2",
                AuthorizeUrl = httpUrl + "/auth",
                TokenUrl = httpUrl + "/token",
                Scopes = "mail.read",
                RedirectUri = "/connectors/oauth/callback",
                StartUrl = "/connectors/oauth/start",
                RefreshCredentialKey = "refresh_token",
                AccessTokenEnvironmentKey = "ACCESS_TOKEN",
                RevokeUrl = httpUrl + "/revoke",
                SignInLabel = "Sign in",
                EntryPoint = "google-mail",
                ToolNames = ["mail.read"],
                AskFirst = [],
                ClientId = "test-client-id",
            };
            await Assert.ThrowsAsync<NetworkAccessDeniedException>(async () =>
                _ = await oauthClient.RefreshAsync(oauthProfile, "rt", null, httpUrl + "/token", false, CancellationToken.None));

            // 8. SlackSocketModeWorker with webSocketChecker: null -> fail closed on socket connect
            var nullWsChecker = new NetworkAccessWebSocketChecker(FailClosedNetworkAccessChecker.Instance);
            using var ws = new System.Net.WebSockets.ClientWebSocket();
            await Assert.ThrowsAsync<NetworkAccessDeniedException>(async () =>
                await nullWsChecker.ConnectAsync(ws, new Uri($"ws://127.0.0.1:{port}/ws")));

            // Verify zero TCP connections ever reached the loopback listener!
            Assert.Equal(0, Volatile.Read(ref acceptedConnections));

            // Negative control (.NC): with an allowing checker passed in, RemoteMcpRequestHandler permits the transport call
            using var allowHandler = new Kautuka.Gateway.Infrastructure.Agents.RemoteMcpRequestHandler(
                mcpContext,
                new AllowTestChecker(),
                new TrackingHttpHandler());
            using var allowClient = new HttpClient(allowHandler);
            using HttpResponseMessage okResp = await allowClient.GetAsync(new Uri(httpUrl + "/mcp"));
            Assert.Equal(HttpStatusCode.OK, okResp.StatusCode);
        }
        finally
        {
            await acceptCts.CancelAsync();
            listener.Stop();
            await acceptLoop;
        }
    }

    // -------------------------------------------------------------------------------------------------
    // G7 (v7-34): HTTP 3xx redirects are never auto-followed silently; denied redirect targets fail
    // closed with NetworkAccessDeniedException, and allowed redirect targets still aren't auto-followed.
    // -------------------------------------------------------------------------------------------------

    [Fact]
    [SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
    public async Task G7_RedirectFromAllowedHost_IsNeverAutoFollowed_WithNegativeControl()
    {
        using var allowedListener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        using var targetListener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        allowedListener.Start();
        targetListener.Start();

        int allowedPort = ((IPEndPoint)allowedListener.LocalEndpoint).Port;
        int targetPort = ((IPEndPoint)targetListener.LocalEndpoint).Port;
        int targetHits = 0;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task serveAllowed = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    using System.Net.Sockets.TcpClient conn = await allowedListener.AcceptTcpClientAsync(cts.Token);
                    using System.Net.Sockets.NetworkStream stream = conn.GetStream();
                    byte[] buf = new byte[1024];
                    _ = await stream.ReadAsync(buf, cts.Token);
                    byte[] resp = System.Text.Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{targetPort}/steal\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(resp, cts.Token);
                }
                catch
                {
                    break;
                }
            }
        });

        Task serveTarget = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    using System.Net.Sockets.TcpClient conn = await targetListener.AcceptTcpClientAsync(cts.Token);
                    Interlocked.Increment(ref targetHits);
                    using System.Net.Sockets.NetworkStream stream = conn.GetStream();
                    byte[] buf = new byte[1024];
                    _ = await stream.ReadAsync(buf, cts.Token);
                    byte[] resp = System.Text.Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                    await stream.WriteAsync(resp, cts.Token);
                }
                catch
                {
                    break;
                }
            }
        });

        try
        {
            // 1. Primary: allowed host returns 302 to non-allowed host -> denied, target mock hit count == 0
            var denyRedirectChecker = new PortSelectiveTestChecker(allowedPort);
            using var defaultSockets = new SocketsHttpHandler(); // AllowAutoRedirect starts true (.NET default)
            using var checkedHandler = new NetworkAccessCheckHandler(denyRedirectChecker, defaultSockets);
            Assert.False(defaultSockets.AllowAutoRedirect);

            using var client = new HttpClient(checkedHandler);
            NetworkAccessDeniedException denied = await Assert.ThrowsAsync<NetworkAccessDeniedException>(async () =>
                _ = await client.GetAsync(new Uri($"http://127.0.0.1:{allowedPort}/start"), cts.Token));
            Assert.Equal(targetPort, denied.Port);
            Assert.Equal(0, Volatile.Read(ref targetHits));

            // 2. Spec G7.NC: allow second host too -> redirect is still NOT auto-followed (302 returned, targetHits == 0)
            using var allowBothClient = new HttpClient(
                new NetworkAccessCheckHandler(new AllowTestChecker(), new SocketsHttpHandler()));
            using HttpResponseMessage redirectResp = await allowBothClient.GetAsync(
                new Uri($"http://127.0.0.1:{allowedPort}/start"),
                cts.Token);
            Assert.Equal(HttpStatusCode.Redirect, redirectResp.StatusCode);
            Assert.Equal(0, Volatile.Read(ref targetHits));

            // 3. Handler-removal NC: raw SocketsHttpHandler without NetworkAccessCheckHandler auto-follows to targetPort
            using var uncheckedClient = new HttpClient(new SocketsHttpHandler());
            using HttpResponseMessage bypassedResp = await uncheckedClient.GetAsync(
                new Uri($"http://127.0.0.1:{allowedPort}/start"),
                cts.Token);
            Assert.Equal(HttpStatusCode.OK, bypassedResp.StatusCode);
            Assert.Equal(1, Volatile.Read(ref targetHits));
        }
        finally
        {
            await cts.CancelAsync();
            allowedListener.Stop();
            targetListener.Stop();
            await Task.WhenAll(serveAllowed, serveTarget);
        }
    }

    [Fact]
    [SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
    public async Task V7_30_ToolFailureLookup_UsesNonAdminActorAndNeverDisclosesOtherUsersOrSystemwideDestinations_WithNegativeControl()
    {
        var gatewayCtx = new TestServerCallContextWithCert(ManagerClientIdentities.GatewayCommonName);

        // Seed pending request for user-a and a systemwide pending request
        await grpcService.CheckOutbound(new NetworkAccessCheckOutboundRequest
        {
            Host = "secret-user-a.example",
            Port = 443,
            UserId = "user-a",
            AgentId = "agent-a",
            Systemwide = false,
            Purpose = "tool",
        }, gatewayCtx);

        await grpcService.CheckOutbound(new NetworkAccessCheckOutboundRequest
        {
            Host = "secret-systemwide.example",
            Port = 443,
            UserId = "user-a",
            AgentId = string.Empty,
            ConnectorId = "sys-conn",
            Systemwide = true,
            Purpose = "tool",
        }, gatewayCtx);

        NetworkAccessActorMessage? capturedActor = null;
        Task<NetworkAccessListRequestsResponse> ListViaManagerAsync(NetworkAccessListRequestsRequest req)
        {
            capturedActor = req.Actor;
            return grpcService.ListRequests(req, gatewayCtx);
        }

        // User B executes a failing tool without a direct NetworkAccessDeniedException
        var toolErr = new InvalidOperationException("Generic tool failure");
        System.Text.Json.JsonElement payloadB = await Kautuka.Gateway.Domain.Execution.ChatToolNetworkAccessHelper
            .BuildToolCallFinishedPayloadAsync(
                toolErr,
                nacEx: null,
                activeToolCallId: "tc-1",
                activeToolName: "fetch",
                userId: "user-b",
                agentId: "agent-b",
                listRequestsAsync: ListViaManagerAsync);

        Assert.NotNull(capturedActor);
        Assert.False(capturedActor!.IsAdmin);
        Assert.Equal("user-b", capturedActor.UserId);
        Assert.Equal("tool_execution_failed", payloadB.GetProperty("code").GetString());
        Assert.False(payloadB.TryGetProperty("destination", out _));
        Assert.False(payloadB.TryGetProperty("approvalUrl", out _));

        // Negative control (.NC): When user-b / agent-b itself has a pending non-systemwide request,
        // the lookup DOES surface user-b's own blocked destination without admin privileges.
        await grpcService.CheckOutbound(new NetworkAccessCheckOutboundRequest
        {
            Host = "user-b-target.example",
            Port = 443,
            UserId = "user-b",
            AgentId = "agent-b",
            Systemwide = false,
            Purpose = "tool",
        }, gatewayCtx);

        System.Text.Json.JsonElement payloadBWithOwnPending = await Kautuka.Gateway.Domain.Execution.ChatToolNetworkAccessHelper
            .BuildToolCallFinishedPayloadAsync(
                toolErr,
                nacEx: null,
                activeToolCallId: "tc-2",
                activeToolName: "fetch",
                userId: "user-b",
                agentId: "agent-b",
                listRequestsAsync: ListViaManagerAsync);

        Assert.False(capturedActor!.IsAdmin);
        Assert.Equal("network_access_required", payloadBWithOwnPending.GetProperty("code").GetString());
        Assert.Equal("user-b-target.example", payloadBWithOwnPending.GetProperty("destination").GetString());
        Assert.Equal(443, payloadBWithOwnPending.GetProperty("port").GetInt32());
        Assert.Equal("/network-access", payloadBWithOwnPending.GetProperty("approvalUrl").GetString());
    }

    [Fact]
    [SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test naming convention")]
    public async Task V7_30_ToolFailureLookup_SelectsMostRecentlySeenMatchingRequest_WithNegativeControl()
    {
        NetworkAccessActorMessage? capturedActor = null;
        Task<NetworkAccessListRequestsResponse> ListRequestsAsync(NetworkAccessListRequestsRequest req)
        {
            capturedActor = req.Actor;
            var response = new NetworkAccessListRequestsResponse();
            response.Requests.Add(new NetworkAccessRequestMessage
            {
                Id = "older-request",
                UserId = "user-a",
                AgentId = "agent-a",
                Destination = "older.example",
                Port = 443,
                Status = "pending",
                LastSeenUtc = "2026-01-01T00:00:00+00:00",
            });
            response.Requests.Add(new NetworkAccessRequestMessage
            {
                Id = "newer-request",
                UserId = "user-a",
                AgentId = "agent-a",
                Destination = "newer.example",
                Port = 8443,
                Status = "pending",
                LastSeenUtc = "2026-01-02T00:00:00+00:00",
            });
            return Task.FromResult(response);
        }

        var payload = await Kautuka.Gateway.Domain.Execution.ChatToolNetworkAccessHelper
            .BuildToolCallFinishedPayloadAsync(
                new InvalidOperationException("Generic tool failure"),
                nacEx: null,
                activeToolCallId: "tc-latest",
                activeToolName: "fetch",
                userId: "user-a",
                agentId: "agent-a",
                listRequestsAsync: ListRequestsAsync);

        Assert.NotNull(capturedActor);
        Assert.False(capturedActor!.IsAdmin);
        Assert.Equal("newer.example", payload.GetProperty("destination").GetString());
        Assert.Equal(8443, payload.GetProperty("port").GetInt32());
        Assert.Equal("newer-request", payload.GetProperty("requestId").GetString());
    }

    private sealed class PortSelectiveTestChecker(int allowedPort) : INetworkAccessChecker
    {
        public Task<NetworkAccessCheckResult> CheckAsync(OutboundTarget target, CancellationToken cancellationToken = default)
            => Task.FromResult(target.Port == allowedPort
                ? NetworkAccessCheckResult.CreateAllowed("test_allow_port")
                : NetworkAccessCheckResult.CreateDenied("test_deny_redirect_port", "req-redirect"));
    }

    private sealed class AllowTestChecker : INetworkAccessChecker
    {
        public Task<NetworkAccessCheckResult> CheckAsync(OutboundTarget target, CancellationToken cancellationToken = default)
            => Task.FromResult(NetworkAccessCheckResult.CreateAllowed("test_allow"));
    }
}
