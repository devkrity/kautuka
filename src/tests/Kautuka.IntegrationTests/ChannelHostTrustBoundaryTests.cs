using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

using Grpc.Core;

using Kautuka.ChannelContracts.Channels;
using Kautuka.ChannelContracts.Serialization;
using Kautuka.ChannelHost;
using Kautuka.ChannelHost.Grpc;
using Kautuka.ChannelHost.Hosting;
using Kautuka.ChannelHost.Protos;
using Kautuka.Hosting.Hosting;
using Kautuka.Protos;
using Kautuka.Telemetry.Security;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class ChannelHostTrustBoundaryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "kautuka-channel-security-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void AttachmentStorePersistsCredentialReferenceButNeverPlaintextSecret()
    {
        ChannelHostDatabase database = CreateDatabase();
        var store = new ChannelAttachmentStore(database, new ChannelRuntimeCredentialStore());
        UpsertAttachmentRequest request = AttachmentRequest();
        request.CredentialReferences.Add(new AttachmentCredentialReference
        {
            FieldKey = "bot_token",
            CredentialKey = "channel.telegram:user-1:default.bot_token",
        });

        _ = store.UpsertAttachment(request);

        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT settings_json,credential_references_json FROM runtime_channel_attachments WHERE attachment_id=$id";
        command.Parameters.AddWithValue("$id", request.AttachmentId);
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.DoesNotContain("super-secret", reader.GetString(0), StringComparison.Ordinal);
        Assert.Contains("channel.telegram:user-1:default.bot_token", reader.GetString(1), StringComparison.Ordinal);
    }

    [Fact]
    public void AttachmentStoreRejectsPlaintextSecretEvenWhenCalledWithoutGrpcService()
    {
        var store = new ChannelAttachmentStore(CreateDatabase(), new ChannelRuntimeCredentialStore());
        UpsertAttachmentRequest request = AttachmentRequest();
        request.Settings.Add(new AttachmentSettingValue { FieldKey = "bot_token", StringValue = "super-secret" });

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => store.UpsertAttachment(request));
        Assert.Contains("CredentialsHost", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequestAuthorizerRejectsMissingForgedAndReplayedDelegation()
    {
        using X509Certificate2 signer = LocalCaMtlsBootstrap.EnsureDelegationSigningCertificate(
            root, "Channel", ".delegation.pfx", ".delegation.cer");
        ChannelRequestAuthorizer authorizer = CreateAuthorizer(signer);
        var request = new ListChannelProvidersRequest();
        RpcException unauthenticated = Assert.Throws<RpcException>(() =>
            authorizer.Authorize(request, null, "ListProviders", ChannelRequestAuthorizer.Read));
        Assert.Equal(StatusCode.Unauthenticated, unauthenticated.StatusCode);

        request.Context = Context();
        ChannelDelegationEnvelope.Sign(request, request.Context, ChannelRequestAuthorizer.Read, signer, DateTimeOffset.UtcNow);
        request.Context.ActorUserId = "substituted-user";
        RpcException forged = Assert.Throws<RpcException>(() =>
            authorizer.Authorize(request, request.Context, "ListProviders", ChannelRequestAuthorizer.Read));
        Assert.Equal("request_digest_invalid", forged.Status.Detail);

        var shortDigest = new ListChannelProvidersRequest { Context = Context() };
        ChannelDelegationEnvelope.Sign(shortDigest, shortDigest.Context, ChannelRequestAuthorizer.Read, signer, DateTimeOffset.UtcNow);
        shortDigest.Context.RequestDigest = "ab";
        RpcException shortDigestError = Assert.Throws<RpcException>(() =>
            authorizer.Authorize(shortDigest, shortDigest.Context, "ListProviders", ChannelRequestAuthorizer.Read));
        Assert.Equal("request_digest_invalid", shortDigestError.Status.Detail);

        request.Context = Context();
        ChannelDelegationEnvelope.Sign(request, request.Context, ChannelRequestAuthorizer.Read, signer, DateTimeOffset.UtcNow);
        _ = authorizer.Authorize(request, request.Context, "ListProviders", ChannelRequestAuthorizer.Read);
        RpcException replay = Assert.Throws<RpcException>(() =>
            authorizer.Authorize(request, request.Context, "ListProviders", ChannelRequestAuthorizer.Read));
        Assert.Equal("delegation_replay_detected", replay.Status.Detail);

        RpcException ownerMismatch = Assert.Throws<RpcException>(() => ChannelRequestAuthorizer.RequireOwner(request.Context, "user-2"));
        Assert.Equal(StatusCode.PermissionDenied, ownerMismatch.StatusCode);
        ChannelRequestAuthorizer.RequireOwner(request.Context, "user-1");
    }

    [Fact]
    public void PingRequiresSignedShortLivedDelegationAndDurablyAuditsMissingEnvelope()
    {
        using X509Certificate2 signer = LocalCaMtlsBootstrap.EnsureDelegationSigningCertificate(
            root, "PingBoundary", ".delegation.pfx", ".delegation.cer");
        ChannelHostDatabase database = CreateDatabase();
        ChannelRequestAuthorizer authorizer = CreateAuthorizer(signer, database);
        var unsigned = new ChannelHostPingRequest();

        RpcException denied = Assert.Throws<RpcException>(() => authorizer.Authorize(
            unsigned, unsigned.Context, "Ping", ChannelRequestAuthorizer.Read));
        Assert.Equal(StatusCode.Unauthenticated, denied.StatusCode);

        var signed = new ChannelHostPingRequest { Context = Context() };
        ChannelDelegationEnvelope.Sign(signed, signed.Context, ChannelRequestAuthorizer.Read, signer, DateTimeOffset.UtcNow);
        ChannelAuthorizationResult allowed = authorizer.Authorize(
            signed, signed.Context, "Ping", ChannelRequestAuthorizer.Read);
        Assert.NotEmpty(allowed.RequestDigest);

        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand audit = connection.CreateCommand();
        audit.CommandText = "SELECT decision,reason_code FROM channel_security_authorization_audit WHERE method_name='Ping' ORDER BY audit_id";
        using SqliteDataReader reader = audit.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("denied", reader.GetString(0));
        Assert.Equal("delegation_key_invalid", reader.GetString(1));
        Assert.True(reader.Read());
        Assert.Equal("allowed", reader.GetString(0));
    }

    [Theory]
    [InlineData("channel_resource_outside_actor_scope")]
    [InlineData("channel_tenant_scope_mismatch")]
    [InlineData("attachment_not_found")]
    [InlineData("conversation_not_bound_to_attachment")]
    [InlineData("inbound_media_descriptor_not_admitted")]
    public void PostAuthorizationResourceSubstitutionDenialIsDurablePrivacySafeAndLoggedOnce(string reason)
    {
        using X509Certificate2 signer = LocalCaMtlsBootstrap.EnsureDelegationSigningCertificate(
            root, "ScopeAudit" + Guid.NewGuid().ToString("N"), ".delegation.pfx", ".delegation.cer");
        ChannelHostDatabase database = CreateDatabase();
        ChannelRequestAuthorizer authorizer = CreateAuthorizer(signer, database);
        ScopedRequestContext context = Context();
        context.ActorUserId = "raw-scope-attacker";
        context.TenantId = "raw-scope-tenant";
        var request = new SendOutboundMessageRequest
        {
            Context = context,
            AttachmentId = "telegram:raw-scope-attacker:default",
            ConversationId = "substituted-conversation",
            MessageId = Guid.NewGuid().ToString("N"),
            Content = new OutboundContentEnvelope { ActionType = "send_text", PayloadJson = "{}" },
        };
        ChannelDelegationEnvelope.Sign(request, context, ChannelRequestAuthorizer.Runtime, signer, DateTimeOffset.UtcNow);
        ChannelAuthorizationResult authorization = authorizer.Authorize(
            request, context, "SendOutboundMessage", ChannelRequestAuthorizer.Runtime);

        RpcException denied = Assert.Throws<RpcException>(() => authorizer.RequireResourceScope(
            authorization, context, "SendOutboundMessage", admitted: false,
            StatusCode.PermissionDenied, reason));
        Assert.Equal(reason, denied.Status.Detail);

        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand audit = connection.CreateCommand();
        audit.CommandText = "SELECT actor_user_id,tenant_id,request_digest,COUNT(*) FROM channel_security_authorization_audit WHERE method_name='SendOutboundMessage' AND decision='denied' AND reason_code=$reason";
        audit.Parameters.AddWithValue("$reason", reason);
        using SqliteDataReader reader = audit.ExecuteReader();
        Assert.True(reader.Read());
        Assert.DoesNotContain("raw-scope-attacker", reader.GetString(0), StringComparison.Ordinal);
        Assert.DoesNotContain("raw-scope-tenant", reader.GetString(1), StringComparison.Ordinal);
        Assert.Equal(authorization.RequestDigest, reader.GetString(2));
        Assert.Equal(1, reader.GetInt64(3));
    }

    [Fact]
    public void StructuredSecurityLogsContainNoRawIdentityTokenOrPayloadSecret()
    {
        using X509Certificate2 signer = LocalCaMtlsBootstrap.EnsureDelegationSigningCertificate(
            root, "AuditPrivacy", ".delegation.pfx", ".delegation.cer");
        var logger = new RecordingLogger<ChannelRequestAuthorizer>();
        ChannelHostDatabase database = CreateDatabase();
        var authorizer = new ChannelRequestAuthorizer(database, new ChannelDelegationVerificationKeys([signer]),
            CreatePolicyRegistry(signer), TimeProvider.System, new ChannelResourceGovernor(TimeProvider.System), logger);
        ScopedRequestContext context = Context();
        context.ActorUserId = "raw-user-canary";
        context.TenantId = "raw-tenant-canary";
        context.RequestId = "raw-request-canary";
        SendOutboundMessageRequest request = IdempotentSend(context, "audit-1", "raw-secret-canary");
        ChannelDelegationEnvelope.Sign(request, context, ChannelRequestAuthorizer.Runtime, signer, DateTimeOffset.UtcNow);
        string rawToken = context.DelegationTokenId;

        _ = authorizer.Authorize(request, context, "SendOutboundMessage", ChannelRequestAuthorizer.Runtime);
        request.Content.PayloadJson = "{\"text\":\"forged-raw-secret-canary\"}";
        _ = Assert.Throws<RpcException>(() => authorizer.Authorize(
            request, context, "SendOutboundMessage", ChannelRequestAuthorizer.Runtime));

        string captured = string.Join('\n', logger.Entries);
        Assert.DoesNotContain("raw-user-canary", captured, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-tenant-canary", captured, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-request-canary", captured, StringComparison.Ordinal);
        Assert.DoesNotContain(rawToken, captured, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-secret-canary", captured, StringComparison.Ordinal);
        Assert.Contains("request_digest_invalid", captured, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalPublicContractWorkloadIsAcceptedOnlyInsideRegisteredPolicyScope()
    {
        using X509Certificate2 signer = LocalCaMtlsBootstrap.EnsureDelegationSigningCertificate(
            root, "ExternalChannel", ".external-delegation.pfx", ".external-delegation.cer");
        const string issuer = "kautuka-reference-workload";
        const string audience = "kautuka-channel-gateway";
        var policy = new ChannelWorkloadPolicy(
            "reference-workload", new HashSet<string>(StringComparer.OrdinalIgnoreCase), "unused", new Uri("spiffe://kautuka.local/workload/reference"),
            new HashSet<string>(StringComparer.Ordinal) { "SendOutboundMessage" },
            new HashSet<string>(StringComparer.Ordinal) { ChannelRequestAuthorizer.Runtime },
            issuer, audience, [signer],
            new HashSet<string>(StringComparer.Ordinal) { "external-user" },
            OwnerScopeWildcard: false, AllowCredentialOperations: false, AllowAdministrativeOperations: false, AllowCanonicalIngress: false);
        ChannelHostDatabase policyDatabase = CreateDatabase();
        var authorizer = new ChannelRequestAuthorizer(
            policyDatabase, new ChannelDelegationVerificationKeys([signer]),
            new ChannelWorkloadPolicyRegistry(signer, [policy]), TimeProvider.System,
            new ChannelResourceGovernor(TimeProvider.System), NullLogger<ChannelRequestAuthorizer>.Instance);
        ChannelSemanticMessageAction semantic = new("conversation-1", null,
            ChannelMessageTemplates.AgentResponse("controlled external delivery", "Reference agent"));

        SendOutboundMessageRequest allowed = CreateSignedSemanticMessage(
            "telegram:external-user:default", semantic, signer, issuer, audience,
            actorUserId: "external-user", tenantId: "external-tenant");
        ChannelAuthorizationResult authorization = authorizer.Authorize(
            allowed, allowed.Context, "SendOutboundMessage", ChannelRequestAuthorizer.Runtime);
        Assert.False(authorization.IsIdempotentRetry);

        SendOutboundMessageRequest wrongOwner = CreateSignedSemanticMessage(
            "telegram:other-user:default", semantic, signer, issuer, audience,
            actorUserId: "other-user", tenantId: "external-tenant");
        Assert.Equal("external_workload_owner_scope_denied", Assert.Throws<RpcException>(() => authorizer.Authorize(
            wrongOwner, wrongOwner.Context, "SendOutboundMessage", ChannelRequestAuthorizer.Runtime)).Status.Detail);

        SendOutboundMessageRequest wrongOperation = CreateSignedSemanticMessage(
            "telegram:external-user:default", semantic, signer, issuer, audience,
            actorUserId: "external-user", tenantId: "external-tenant");
        Assert.Equal("delegated_operation_invalid", Assert.Throws<RpcException>(() => authorizer.Authorize(
            wrongOperation, wrongOperation.Context, "SendOutboundMessage", ChannelRequestAuthorizer.Write)).Status.Detail);

        using SqliteConnection auditConnection = policyDatabase.OpenConnection();
        using SqliteCommand audit = auditConnection.CreateCommand();
        audit.CommandText = "SELECT actor_user_id,tenant_id,decision,reason_code FROM channel_security_authorization_audit WHERE decision='denied' ORDER BY audit_id LIMIT 1";
        using SqliteDataReader reader = audit.ExecuteReader();
        Assert.True(reader.Read());
        Assert.DoesNotContain("other-user", reader.GetString(0), StringComparison.Ordinal);
        Assert.DoesNotContain("external-tenant", reader.GetString(1), StringComparison.Ordinal);
        Assert.Equal("denied", reader.GetString(2));
        Assert.Equal("external_workload_owner_scope_denied", reader.GetString(3));
    }

    private static SendOutboundMessageRequest CreateSignedSemanticMessage(
        string attachmentId,
        ChannelSemanticMessageAction message,
        X509Certificate2 delegationCertificate,
        string issuer,
        string audience,
        string actorUserId,
        string tenantId)
    {
        var context = new ScopedRequestContext
        {
            RequestId = Guid.NewGuid().ToString("N"),
            ActorUserId = actorUserId,
            TenantId = tenantId,
            SubmittedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
        };
        var request = new SendOutboundMessageRequest
        {
            Context = context,
            AttachmentId = attachmentId,
            ConversationId = message.ConversationId,
            MessageId = Guid.NewGuid().ToString("N"),
            RequestedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            Content = new OutboundContentEnvelope
            {
                ActionType = "send_semantic_message",
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(message,
                    ChannelContractsJsonContext.Default.ChannelSemanticMessageAction),
            },
        };
        ChannelDelegationEnvelope.Sign(request, context, ChannelRequestAuthorizer.Runtime,
            delegationCertificate, DateTimeOffset.UtcNow, issuer, audience);
        return request;
    }

    [Fact]
    public void ExternalWorkloadPolicyReloadPreservesExactIdentityAndDisableRevokesIt()
    {
        Uri workloadIdentity = new("spiffe://kautuka.local/workload/reference-channel");
        var (server, trustRoot) = LocalCaMtlsBootstrap.EnsureAndLoad(root, "ExternalPolicy",
            [new LocalMtlsClientCertificate("Reference Channel Workload", ".reference-client.pfx", workloadIdentity)]);
        using (server)
        using (trustRoot)
        using (X509Certificate2 client = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(root, ".reference-client.pfx"), null))
        using (X509Certificate2 delegation = LocalCaMtlsBootstrap.EnsureDelegationSigningCertificate(
            root, "ExternalPolicy", ".reference-signing.pfx", ".reference-signing.cer"))
        {
            ChannelWorkloadPolicy gateway = new(
                "gateway", new HashSet<string>(StringComparer.OrdinalIgnoreCase), "unused", new Uri("spiffe://kautuka.local/workload/gateway"),
                new HashSet<string>(StringComparer.Ordinal) { "ListProviders" },
                new HashSet<string>(StringComparer.Ordinal) { ChannelRequestAuthorizer.Read },
                ChannelDelegationEnvelope.Issuer, ChannelDelegationEnvelope.Audience,
                [delegation], new HashSet<string>(StringComparer.Ordinal), true, true, true, true);
            Dictionary<string, string?> enabled = ExternalPolicyConfiguration(client, workloadIdentity, enabled: true);

            ChannelWorkloadPolicyRegistry first = ChannelWorkloadPolicyRegistry.Create(
                trustRoot, gateway, new ConfigurationBuilder().AddInMemoryCollection(enabled).Build());
            ChannelWorkloadPolicyRegistry restarted = ChannelWorkloadPolicyRegistry.Create(
                trustRoot, gateway, new ConfigurationBuilder().AddInMemoryCollection(enabled).Build());

            Assert.Equal("reference", first.Resolve(client)?.WorkloadId);
            Assert.Equal("reference", restarted.Resolve(client)?.WorkloadId);
            Assert.False(restarted.Resolve(client)!.AllowCredentialOperations);
            Assert.False(restarted.Resolve(client)!.AllowAdministrativeOperations);

            Dictionary<string, string?> disabled = ExternalPolicyConfiguration(client, workloadIdentity, enabled: false);
            ChannelWorkloadPolicyRegistry revoked = ChannelWorkloadPolicyRegistry.Create(
                trustRoot, gateway, new ConfigurationBuilder().AddInMemoryCollection(disabled).Build());
            Assert.Null(revoked.Resolve(client));
        }
    }

    [Fact]
    public void ExactWorkloadIdentityRejectsAlternateCaIssuedClientAndDelegationKeySurvivesRestart()
    {
        var (server, ca) = LocalCaMtlsBootstrap.EnsureAndLoad(root, "Channel", ".gateway.pfx");
        using (server)
        using (ca)
        using (X509Certificate2 expected = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(root, ".gateway.pfx"), null))
        {
            Assert.True(LocalCaMtlsBootstrap.ValidateExactServerCert(server, ca, "Channel Server", "localhost"));
            Assert.False(LocalCaMtlsBootstrap.ValidateExactServerCert(server, ca, "Channel Server", "attacker.invalid"));
            Assert.True(LocalCaMtlsBootstrap.ValidateExactClientCert(expected, ca, "Kautuka Gateway Client",
                LocalCaMtlsBootstrap.GatewayChannelWorkloadIdentity));

            var (alternateServer, alternateCa) = LocalCaMtlsBootstrap.EnsureAndLoad(root, "Channel",
                [new LocalMtlsClientCertificate("Unexpected Client", ".unexpected.pfx", new Uri("spiffe://kautuka.local/workload/unexpected"))]);
            alternateServer.Dispose();
            alternateCa.Dispose();
            using X509Certificate2 unexpected = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(root, ".unexpected.pfx"), null);
            Assert.True(LocalCaMtlsBootstrap.ValidateClientCert(unexpected, ca));
            Assert.False(LocalCaMtlsBootstrap.ValidateExactClientCert(unexpected, ca, "Kautuka Gateway Client",
                LocalCaMtlsBootstrap.GatewayChannelWorkloadIdentity));
        }

        using X509Certificate2 first = LocalCaMtlsBootstrap.EnsureDelegationSigningCertificate(root, "Channel", ".delegation.pfx", ".delegation.cer");
        using X509Certificate2 restarted = LocalCaMtlsBootstrap.EnsureDelegationSigningCertificate(root, "Channel", ".delegation.pfx", ".delegation.cer");
        Assert.Equal(first.Thumbprint, restarted.Thumbprint);
    }

    [Fact]
    public void InstallerProvisioningProducesDisjointPublicOnlyRuntimeInventories()
    {
        var directories = new ChannelSecurityMaterialDirectories(
            Path.Combine(root, "gateway-private"),
            Path.Combine(root, "gateway-trust"),
            Path.Combine(root, "channel-private"),
            Path.Combine(root, "channel-trust"));

        ChannelSecurityMaterialProvisioner.Provision(directories);
        ChannelSecurityMaterialProvisioner.ValidateRuntimeInventories(directories);
        AssertOwnerOnlyPrivateFile(Path.Combine(directories.GatewayPrivateRoot, ChannelSecurityMaterialProvisioner.GatewayClientPfx));
        AssertOwnerOnlyPrivateFile(Path.Combine(directories.ChannelPrivateRoot, ChannelSecurityMaterialProvisioner.ServerPfx));
        ChannelSecurityMaterialProvisioner.ValidateGatewayRuntimeInventory(directories.GatewayPrivateRoot, directories.GatewayTrustRoot);
        ChannelSecurityMaterialProvisioner.ValidateChannelRuntimeInventory(directories.ChannelPrivateRoot, directories.ChannelTrustRoot);

        Assert.Equal<string>(
            new[] { ChannelSecurityMaterialProvisioner.GatewayClientPfx, ChannelSecurityMaterialProvisioner.GatewayDelegationPfx },
            Directory.GetFiles(directories.GatewayPrivateRoot).Select(static path => Path.GetFileName(path)!).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal<string>(
            new[] { ChannelSecurityMaterialProvisioner.ServerPfx },
            Directory.GetFiles(directories.ChannelPrivateRoot).Select(static path => Path.GetFileName(path)!).ToArray());
        Assert.All(Directory.GetFiles(directories.ChannelTrustRoot), path =>
        {
            using X509Certificate2 certificate = X509CertificateLoader.LoadCertificateFromFile(path);
            Assert.False(certificate.HasPrivateKey);
        });
        Assert.DoesNotContain(Directory.EnumerateFiles(root, "*.key", SearchOption.AllDirectories),
            path => !path.Contains("kautuka-channel-bootstrap-", StringComparison.Ordinal));
    }

    [Fact]
    public void ProductionRotationStagesActivatesBoundedOverlapAndRejectsStaleIdentityAfterCutover()
    {
        var directories = new ChannelSecurityMaterialDirectories(
            Path.Combine(root, "rotation-gateway-private"),
            Path.Combine(root, "rotation-gateway-trust"),
            Path.Combine(root, "rotation-channel-private"),
            Path.Combine(root, "rotation-channel-trust"),
            Path.Combine(root, "rotation-offline-authority"));
        ChannelSecurityMaterialProvisioner.Provision(directories);
        using X509Certificate2 originalClient = X509CertificateLoader.LoadPkcs12FromFile(
            Path.Combine(directories.GatewayPrivateRoot, ChannelSecurityMaterialProvisioner.GatewayClientPfx), null);
        using X509Certificate2 originalDelegation = X509CertificateLoader.LoadPkcs12FromFile(
            Path.Combine(directories.GatewayPrivateRoot, ChannelSecurityMaterialProvisioner.GatewayDelegationPfx), null);

        ChannelSecurityMaterialProvisioner.StageGeneratedRotation(directories);
        Assert.True(File.Exists(Path.Combine(directories.GatewayPrivateRoot,
            ChannelSecurityMaterialProvisioner.GatewayClientPfx + ChannelSecurityMaterialProvisioner.NextSuffix)));
        ChannelSecurityMaterialProvisioner.ValidateRuntimeInventories(directories);

        DateTimeOffset activatedAt = DateTimeOffset.UtcNow;
        ChannelSecurityRotationState state = ChannelSecurityMaterialProvisioner.ActivateStagedRotation(
            directories, activatedAt, TimeSpan.FromMinutes(10));
        Assert.Equal("ACTIVE", state.Phase);
        Assert.Equal(activatedAt.AddMinutes(10), state.PreviousAcceptedUntilUtc);
        Assert.Equal(originalClient.Thumbprint, state.PreviousClientThumbprint);
        Assert.Equal(originalDelegation.Thumbprint, state.PreviousDelegationThumbprint);
        using X509Certificate2 currentClient = X509CertificateLoader.LoadPkcs12FromFile(
            Path.Combine(directories.GatewayPrivateRoot, ChannelSecurityMaterialProvisioner.GatewayClientPfx), null);
        Assert.NotEqual(originalClient.Thumbprint, currentClient.Thumbprint);
        Assert.Throws<InvalidOperationException>(() => ChannelSecurityMaterialProvisioner.FinalizeRotation(
            directories, activatedAt.AddMinutes(9)));

        ChannelSecurityMaterialProvisioner.FinalizeRotation(directories, activatedAt.AddMinutes(11));
        Assert.False(File.Exists(Path.Combine(directories.ChannelTrustRoot,
            ChannelSecurityMaterialProvisioner.GatewayClientCertificate + ChannelSecurityMaterialProvisioner.PreviousSuffix)));
        Assert.Null(ChannelSecurityMaterialProvisioner.ReadRotationState(directories.ChannelTrustRoot));
        ChannelSecurityMaterialProvisioner.ValidateRuntimeInventories(directories);

        using X509Certificate2 ca = X509CertificateLoader.LoadCertificateFromFile(
            Path.Combine(directories.ChannelTrustRoot, ChannelSecurityMaterialProvisioner.CaCertificate));
        ChannelWorkloadPolicy currentPolicy = CreatePolicyRegistry(currentClient).DefaultPolicy with
        {
            CertificateThumbprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { currentClient.Thumbprint },
            CertificateCommonName = "Kautuka Gateway Client",
            WorkloadIdentityUri = LocalCaMtlsBootstrap.GatewayChannelWorkloadIdentity,
        };
        var cutoverRegistry = new ChannelWorkloadPolicyRegistry(ca, [currentPolicy]);
        Assert.NotNull(cutoverRegistry.Resolve(currentClient));
        Assert.Null(cutoverRegistry.Resolve(originalClient));
    }

    [Fact]
    public void ProductionRotationRollbackRestoresBothIdentitiesAndExactInventoryRejectsExtras()
    {
        var directories = new ChannelSecurityMaterialDirectories(
            Path.Combine(root, "rollback-gateway-private"),
            Path.Combine(root, "rollback-gateway-trust"),
            Path.Combine(root, "rollback-channel-private"),
            Path.Combine(root, "rollback-channel-trust"),
            Path.Combine(root, "rollback-offline-authority"));
        ChannelSecurityMaterialProvisioner.Provision(directories);
        using X509Certificate2 originalClient = X509CertificateLoader.LoadPkcs12FromFile(
            Path.Combine(directories.GatewayPrivateRoot, ChannelSecurityMaterialProvisioner.GatewayClientPfx), null);
        ChannelSecurityMaterialProvisioner.StageGeneratedRotation(directories);
        _ = ChannelSecurityMaterialProvisioner.ActivateStagedRotation(
            directories, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));

        ChannelSecurityMaterialProvisioner.RollbackRotation(directories);
        using X509Certificate2 restored = X509CertificateLoader.LoadPkcs12FromFile(
            Path.Combine(directories.GatewayPrivateRoot, ChannelSecurityMaterialProvisioner.GatewayClientPfx), null);
        Assert.Equal(originalClient.Thumbprint, restored.Thumbprint);
        Assert.Null(ChannelSecurityMaterialProvisioner.ReadRotationState(directories.ChannelTrustRoot));
        Assert.True(File.Exists(Path.Combine(directories.GatewayPrivateRoot,
            ChannelSecurityMaterialProvisioner.GatewayClientPfx + ChannelSecurityMaterialProvisioner.NextSuffix)));

        File.WriteAllText(Path.Combine(directories.ChannelTrustRoot, "attacker.cer"), "not-a-certificate");
        Assert.Throws<InvalidOperationException>(() =>
            ChannelSecurityMaterialProvisioner.ValidateChannelRuntimeInventory(directories.ChannelPrivateRoot, directories.ChannelTrustRoot));
    }

    [Fact]
    public void IdempotentRetryReturnsDurableOutcomeAndChangedPayloadIsDeniedWithoutSecondEffect()
    {
        using X509Certificate2 signer = LocalCaMtlsBootstrap.EnsureDelegationSigningCertificate(
            root, "Channel", ".delegation.pfx", ".delegation.cer");
        ChannelRequestAuthorizer authorizer = CreateAuthorizer(signer);
        var request = new SendOutboundMessageRequest
        {
            Context = Context(),
            AttachmentId = "telegram:user-1:default",
            MessageId = "message-1",
            ConversationId = "chat-1",
            IdempotencyKey = "delivery-1",
            Content = new OutboundContentEnvelope { ActionType = "text", PayloadJson = "{\"text\":\"hello\"}" },
        };
        ChannelDelegationEnvelope.Sign(request, request.Context, ChannelRequestAuthorizer.Runtime, signer, DateTimeOffset.UtcNow);
        ChannelAuthorizationResult first = authorizer.BeginOperation("SendOutboundMessage",
            authorizer.Authorize(request, request.Context, "SendOutboundMessage", ChannelRequestAuthorizer.Runtime));
        authorizer.MarkEffectStarted("SendOutboundMessage", first);
        var outcome = new SendOutboundMessageResponse { Accepted = true, TransportMessageId = "transport-1" };
        authorizer.StoreResponse("SendOutboundMessage", first, outcome);

        var reconstructed = new SendOutboundMessageRequest
        {
            Context = Context(),
            AttachmentId = request.AttachmentId,
            MessageId = request.MessageId,
            ConversationId = request.ConversationId,
            IdempotencyKey = request.IdempotencyKey,
            Content = request.Content.Clone(),
        };
        reconstructed.Context.GatewaySessionId = "restarted-gateway-session";
        reconstructed.Context.SubmittedAtUtc = DateTimeOffset.Parse(request.Context.SubmittedAtUtc, System.Globalization.CultureInfo.InvariantCulture)
            .AddSeconds(1).ToString("O");
        Assert.NotEqual(request.Context.RequestId, reconstructed.Context.RequestId);
        Assert.NotEqual(request.Context.SubmittedAtUtc, reconstructed.Context.SubmittedAtUtc);
        ChannelDelegationEnvelope.Sign(reconstructed, reconstructed.Context, ChannelRequestAuthorizer.Runtime, signer, DateTimeOffset.UtcNow);
        ChannelAuthorizationResult retry = authorizer.BeginOperation("SendOutboundMessage",
            authorizer.Authorize(reconstructed, reconstructed.Context, "SendOutboundMessage", ChannelRequestAuthorizer.Runtime));
        Assert.True(retry.IsIdempotentRetry);
        Assert.True(authorizer.TryGetCachedResponse("SendOutboundMessage", retry, out byte[] cached));
        Assert.Equal("transport-1", SendOutboundMessageResponse.Parser.ParseFrom(cached).TransportMessageId);

        reconstructed.Content.PayloadJson = "{\"text\":\"changed\"}";
        reconstructed.Context = Context();
        ChannelDelegationEnvelope.Sign(reconstructed, reconstructed.Context, ChannelRequestAuthorizer.Runtime, signer, DateTimeOffset.UtcNow);
        RpcException conflict = Assert.Throws<RpcException>(() =>
            authorizer.BeginOperation("SendOutboundMessage",
                authorizer.Authorize(reconstructed, reconstructed.Context, "SendOutboundMessage", ChannelRequestAuthorizer.Runtime)));
        Assert.Equal("idempotency_key_digest_conflict", conflict.Status.Detail);
    }

    [Fact]
    public void EverySecurityCriticalMutationHasDurableRestartSafeOutcomeIdentity()
    {
        using X509Certificate2 signer = LocalCaMtlsBootstrap.EnsureDelegationSigningCertificate(
            root, "MutationCoverage", ".delegation.pfx", ".delegation.cer");
        ChannelRequestAuthorizer authorizer = CreateAuthorizer(signer);
        (string Method, string Operation, Func<ScopedRequestContext, Google.Protobuf.IMessage> Create)[] cases =
        [
            ("UpsertAttachment", ChannelRequestAuthorizer.Write, context => new UpsertAttachmentRequest { Context = context, AttachmentId = "a", ProviderKey = "telegram", IdempotencyKey = "upsert-1" }),
            ("SyncOwnerTargets", ChannelRequestAuthorizer.Write, context => new SyncOwnerTargetsRequest { Context = context, OwnerUserId = "user-1", IdempotencyKey = "owner-sync-1" }),
            ("ProvisionAttachment", ChannelRequestAuthorizer.Write, context => new ProvisionAttachmentRequest { Context = context, AttachmentId = "a", IdempotencyKey = "provision-1" }),
            ("DeactivateAttachment", ChannelRequestAuthorizer.Write, context => new DeactivateAttachmentRequest { Context = context, AttachmentId = "a", IdempotencyKey = "deactivate-1" }),
            ("TransitionAttachmentLifecycle", ChannelRequestAuthorizer.Write, context => new TransitionAttachmentLifecycleRequest { Context = context, AttachmentId = "a", Transition = "SUSPEND", OperationId = "lifecycle-1" }),
            ("SetRuntimeCredentials", ChannelRequestAuthorizer.Runtime, context => new SetRuntimeCredentialsRequest { Context = context, AttachmentId = "a", IdempotencyKey = "credentials-1" }),
            ("AcknowledgeIngress", ChannelRequestAuthorizer.Ingress, context => new AcknowledgeIngressRequest { Context = context, SubscriberId = "gateway-ingress", EventId = "event-1", DeliveryLeaseId = "lease-1", IdempotencyKey = "ack-1" }),
        ];

        foreach ((string method, string operation, Func<ScopedRequestContext, Google.Protobuf.IMessage> create) in cases)
        {
            Google.Protobuf.IMessage firstRequest = create(Context());
            ScopedRequestContext firstContext = RequestContext(firstRequest);
            ChannelDelegationEnvelope.Sign(firstRequest, firstContext, operation, signer, DateTimeOffset.UtcNow);
            ChannelAuthorizationResult first = authorizer.BeginOperation(method,
                authorizer.Authorize(firstRequest, firstContext, method, operation));
            authorizer.StoreResponse(method, first, new AcknowledgeIngressResponse { Acknowledged = true });

            Google.Protobuf.IMessage rebuilt = create(Context());
            ScopedRequestContext rebuiltContext = RequestContext(rebuilt);
            ChannelDelegationEnvelope.Sign(rebuilt, rebuiltContext, operation, signer, DateTimeOffset.UtcNow);
            ChannelAuthorizationResult retry = authorizer.BeginOperation(method,
                authorizer.Authorize(rebuilt, rebuiltContext, method, operation));
            Assert.True(retry.IsIdempotentRetry);
            Assert.True(authorizer.TryGetCachedResponse(method, retry, out _));
        }
    }

    [Fact]
    public void OperationLedgerRecoversBeforeEffectAndRequiresReconciliationAfterEffect()
    {
        using X509Certificate2 signer = LocalCaMtlsBootstrap.EnsureDelegationSigningCertificate(
            root, "CrashWindows", ".delegation.pfx", ".delegation.cer");
        ChannelHostDatabase database = CreateDatabase();
        var time = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        var authorizer = new ChannelRequestAuthorizer(database, new ChannelDelegationVerificationKeys([signer]),
            CreatePolicyRegistry(signer), time, new ChannelResourceGovernor(time), NullLogger<ChannelRequestAuthorizer>.Instance);

        SendOutboundMessageRequest firstRequest = IdempotentSend(Context(), "crash-window-1", "hello");
        ChannelDelegationEnvelope.Sign(firstRequest, firstRequest.Context, ChannelRequestAuthorizer.Runtime, signer, time.GetUtcNow());
        ChannelAuthorizationResult reserved = authorizer.BeginOperation("SendOutboundMessage",
            authorizer.Authorize(firstRequest, firstRequest.Context, "SendOutboundMessage", ChannelRequestAuthorizer.Runtime));
        Assert.False(reserved.IsIdempotentRetry);

        time.Advance(TimeSpan.FromSeconds(31));
        SendOutboundMessageRequest recoveredRequest = IdempotentSend(Context(), "crash-window-1", "hello");
        ChannelDelegationEnvelope.Sign(recoveredRequest, recoveredRequest.Context, ChannelRequestAuthorizer.Runtime, signer, time.GetUtcNow());
        ChannelAuthorizationResult recovered = authorizer.BeginOperation("SendOutboundMessage",
            authorizer.Authorize(recoveredRequest, recoveredRequest.Context, "SendOutboundMessage", ChannelRequestAuthorizer.Runtime));
        Assert.False(recovered.IsIdempotentRetry);
        Assert.NotEqual(reserved.LeaseId, recovered.LeaseId);

        authorizer.MarkEffectStarted("SendOutboundMessage", recovered);
        time.Advance(TimeSpan.FromSeconds(31));
        SendOutboundMessageRequest uncertainRequest = IdempotentSend(Context(), "crash-window-1", "hello");
        ChannelDelegationEnvelope.Sign(uncertainRequest, uncertainRequest.Context, ChannelRequestAuthorizer.Runtime, signer, time.GetUtcNow());
        ChannelAuthorizationResult uncertain = authorizer.BeginOperation("SendOutboundMessage",
            authorizer.Authorize(uncertainRequest, uncertainRequest.Context, "SendOutboundMessage", ChannelRequestAuthorizer.Runtime));
        Assert.True(uncertain.IsIdempotentRetry);
        Assert.True(uncertain.RequiresReconciliation);
    }

    [Fact]
    public void OwnerTargetSyncRetryReturnsCachedResultAndCrashAfterProviderEffectRequiresReconciliation()
    {
        using X509Certificate2 signer = LocalCaMtlsBootstrap.EnsureDelegationSigningCertificate(
            root, "OwnerSyncCrash", ".delegation.pfx", ".delegation.cer");
        ChannelHostDatabase database = CreateDatabase();
        var time = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        var authorizer = new ChannelRequestAuthorizer(database, new ChannelDelegationVerificationKeys([signer]),
            CreatePolicyRegistry(signer), time, new ChannelResourceGovernor(time), NullLogger<ChannelRequestAuthorizer>.Instance);

        SyncOwnerTargetsRequest completedRequest = OwnerSyncRequest(Context(), "owner-sync-completed");
        ChannelDelegationEnvelope.Sign(completedRequest, completedRequest.Context, ChannelRequestAuthorizer.Write, signer, time.GetUtcNow());
        ChannelAuthorizationResult completed = authorizer.BeginOperation("SyncOwnerTargets",
            authorizer.Authorize(completedRequest, completedRequest.Context, "SyncOwnerTargets", ChannelRequestAuthorizer.Write));
        authorizer.MarkEffectStarted("SyncOwnerTargets", completed);
        authorizer.StoreResponse("SyncOwnerTargets", completed,
            new SyncOwnerTargetsResponse { Accepted = true, RegistrationsSynced = 1, Detail = "owner_targets_synced" });

        SyncOwnerTargetsRequest retryRequest = OwnerSyncRequest(Context(), "owner-sync-completed");
        ChannelDelegationEnvelope.Sign(retryRequest, retryRequest.Context, ChannelRequestAuthorizer.Write, signer, time.GetUtcNow());
        ChannelAuthorizationResult retry = authorizer.BeginOperation("SyncOwnerTargets",
            authorizer.Authorize(retryRequest, retryRequest.Context, "SyncOwnerTargets", ChannelRequestAuthorizer.Write));
        Assert.True(retry.IsIdempotentRetry);
        Assert.True(authorizer.TryGetCachedResponse("SyncOwnerTargets", retry, out byte[] cached));
        Assert.Equal(1, SyncOwnerTargetsResponse.Parser.ParseFrom(cached).RegistrationsSynced);

        SyncOwnerTargetsRequest uncertainRequest = OwnerSyncRequest(Context(), "owner-sync-uncertain");
        ChannelDelegationEnvelope.Sign(uncertainRequest, uncertainRequest.Context, ChannelRequestAuthorizer.Write, signer, time.GetUtcNow());
        ChannelAuthorizationResult uncertainFirst = authorizer.BeginOperation("SyncOwnerTargets",
            authorizer.Authorize(uncertainRequest, uncertainRequest.Context, "SyncOwnerTargets", ChannelRequestAuthorizer.Write));
        authorizer.MarkEffectStarted("SyncOwnerTargets", uncertainFirst);
        time.Advance(TimeSpan.FromSeconds(31));

        SyncOwnerTargetsRequest afterCrashRequest = OwnerSyncRequest(Context(), "owner-sync-uncertain");
        ChannelDelegationEnvelope.Sign(afterCrashRequest, afterCrashRequest.Context, ChannelRequestAuthorizer.Write, signer, time.GetUtcNow());
        ChannelAuthorizationResult afterCrash = authorizer.BeginOperation("SyncOwnerTargets",
            authorizer.Authorize(afterCrashRequest, afterCrashRequest.Context, "SyncOwnerTargets", ChannelRequestAuthorizer.Write));
        Assert.True(afterCrash.IsIdempotentRetry);
        Assert.True(afterCrash.RequiresReconciliation);
        Assert.False(authorizer.TryGetCachedResponse("SyncOwnerTargets", afterCrash, out _));
    }

    [Fact]
    public void ValidationBeforeReservationCannotPoisonKeyAndScopesDoNotCollide()
    {
        using X509Certificate2 signer = LocalCaMtlsBootstrap.EnsureDelegationSigningCertificate(
            root, "ValidationWindow", ".delegation.pfx", ".delegation.cer");
        ChannelHostDatabase database = CreateDatabase();
        ChannelRequestAuthorizer firstWorkload = CreateScopedAuthorizer(database, signer, "gateway-a");
        ChannelRequestAuthorizer secondWorkload = CreateScopedAuthorizer(database, signer, "gateway-b");

        SendOutboundMessageRequest rejectedBeforeReservation = IdempotentSend(Context(), "shared-key", "invalid-before-service-validation");
        ChannelDelegationEnvelope.Sign(rejectedBeforeReservation, rejectedBeforeReservation.Context, ChannelRequestAuthorizer.Runtime, signer, DateTimeOffset.UtcNow);
        _ = firstWorkload.Authorize(rejectedBeforeReservation, rejectedBeforeReservation.Context, "SendOutboundMessage", ChannelRequestAuthorizer.Runtime);

        SendOutboundMessageRequest valid = IdempotentSend(Context(), "shared-key", "valid-business-payload");
        ChannelDelegationEnvelope.Sign(valid, valid.Context, ChannelRequestAuthorizer.Runtime, signer, DateTimeOffset.UtcNow);
        ChannelAuthorizationResult first = firstWorkload.BeginOperation("SendOutboundMessage",
            firstWorkload.Authorize(valid, valid.Context, "SendOutboundMessage", ChannelRequestAuthorizer.Runtime));
        Assert.False(first.IsIdempotentRetry);

        SendOutboundMessageRequest sameKeyOtherWorkload = IdempotentSend(Context(), "shared-key", "valid-business-payload");
        ChannelDelegationEnvelope.Sign(sameKeyOtherWorkload, sameKeyOtherWorkload.Context, ChannelRequestAuthorizer.Runtime, signer, DateTimeOffset.UtcNow);
        ChannelAuthorizationResult second = secondWorkload.BeginOperation("SendOutboundMessage",
            secondWorkload.Authorize(sameKeyOtherWorkload, sameKeyOtherWorkload.Context, "SendOutboundMessage", ChannelRequestAuthorizer.Runtime));
        Assert.False(second.IsIdempotentRetry);

        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM channel_security_operations_v2 WHERE idempotency_key='shared-key'";
        Assert.Equal(2L, (long)(count.ExecuteScalar() ?? 0L));
    }

    [Fact]
    public void DelegationRejectsExpiryWrongOperationAndClientSuppliedAuthority()
    {
        using X509Certificate2 signer = LocalCaMtlsBootstrap.EnsureDelegationSigningCertificate(
            root, "Channel", ".delegation.pfx", ".delegation.cer");
        ChannelRequestAuthorizer authorizer = CreateAuthorizer(signer);

        var expiredRequest = new ListChannelProvidersRequest { Context = Context() };
        ChannelDelegationEnvelope.Sign(expiredRequest, expiredRequest.Context, ChannelRequestAuthorizer.Read,
            signer, DateTimeOffset.UtcNow.Subtract(TimeSpan.FromMinutes(2)));
        Assert.Equal("delegation_expired_or_not_yet_valid", Assert.Throws<RpcException>(() =>
            authorizer.Authorize(expiredRequest, expiredRequest.Context, "ListProviders", ChannelRequestAuthorizer.Read)).Status.Detail);

        var wrongOperation = new ListChannelProvidersRequest { Context = Context() };
        ChannelDelegationEnvelope.Sign(wrongOperation, wrongOperation.Context, ChannelRequestAuthorizer.Read,
            signer, DateTimeOffset.UtcNow);
        Assert.Equal("delegated_operation_invalid", Assert.Throws<RpcException>(() =>
            authorizer.Authorize(wrongOperation, wrongOperation.Context, "ListProviders", ChannelRequestAuthorizer.Write)).Status.Detail);

        var capabilityRequest = new ListChannelProvidersRequest { Context = Context() };
        ChannelDelegationEnvelope.Sign(capabilityRequest, capabilityRequest.Context, ChannelRequestAuthorizer.Read,
            signer, DateTimeOffset.UtcNow);
        capabilityRequest.Context.ActorCapabilities.Add(ChannelRequestAuthorizer.Admin);
        Assert.Equal("client_capabilities_forbidden", Assert.Throws<RpcException>(() =>
            authorizer.Authorize(capabilityRequest, capabilityRequest.Context, "ListProviders", ChannelRequestAuthorizer.Read)).Status.Detail);
    }

    [Fact]
    public void RegistrationLifecycleIsIdempotentAuditedAndSuspensionRemovesRuntimeAuthority()
    {
        ChannelHostDatabase database = CreateDatabase();
        var credentials = new ChannelRuntimeCredentialStore();
        var store = new ChannelAttachmentStore(database, credentials);
        UpsertAttachmentRequest request = AttachmentRequest();
        request.Context = Context();
        _ = store.UpsertAttachment(request);
        credentials.Replace(request.AttachmentId,
            [new RuntimeCredentialValue { FieldKey = "bot_token", SecretValue = "secret-canary" }]);

        Assert.True(store.SuspendAttachment(request.AttachmentId, "operator", "operation-suspend-1"));
        Assert.True(store.SuspendAttachment(request.AttachmentId, "operator", "operation-suspend-1"));
        Assert.Equal(string.Empty, credentials.Get(request.AttachmentId, "bot_token"));
        Assert.False(store.TryGetActiveAttachment(request.AttachmentId, out _));

        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand audit = connection.CreateCommand();
        audit.CommandText = "SELECT COUNT(*) FROM channel_registration_lifecycle_audit WHERE operation_id='operation-suspend-1'";
        Assert.Equal(1L, (long)(audit.ExecuteScalar() ?? 0L));
        using SqliteCommand status = connection.CreateCommand();
        status.CommandText = "SELECT status FROM runtime_channel_attachments WHERE attachment_id=$id";
        status.Parameters.AddWithValue("$id", request.AttachmentId);
        Assert.Equal("SUSPENDED", status.ExecuteScalar());
        Assert.True(store.RecoverAttachment(request.AttachmentId, "operation-recover-1"));
        store.MarkProvisioned(request.AttachmentId, "provisioned", "operation-provision-1");
        store.MarkProvisioned(request.AttachmentId, "provisioned", "operation-provision-2");
        using SqliteCommand provisioningAudit = connection.CreateCommand();
        provisioningAudit.CommandText = "SELECT COUNT(*) FROM channel_registration_lifecycle_audit WHERE reason LIKE 'provision_succeeded:%'";
        Assert.Equal(2L, (long)(provisioningAudit.ExecuteScalar() ?? 0L));
        UpsertAttachmentRequest suspendedUpdate = AttachmentRequest();
        suspendedUpdate.Context = Context();
        _ = store.UpsertAttachment(suspendedUpdate);
        Assert.Equal(AttachmentStatus.Active, store.GetAttachmentSummary(request.AttachmentId)?.Status);
        Assert.Equal("tenant-1", store.GetAttachmentTenant(request.AttachmentId));
    }

    [Fact]
    public void ConfiguredProviderDestinationIsDurablyBoundWithoutTrustingPayloadJson()
    {
        ChannelHostDatabase database = CreateDatabase();
        var store = new ChannelAttachmentStore(database, new ChannelRuntimeCredentialStore());
        UpsertAttachmentRequest request = AttachmentRequest();
        request.Context = Context();
        request.Settings.Add(new AttachmentSettingValue
        {
            FieldKey = "channel_device_id",
            StringValue = "configured-destination-1",
        });

        _ = store.UpsertAttachment(request);

        Assert.True(store.IsConversationBound(request.AttachmentId, "configured-destination-1"));
        Assert.False(store.IsConversationBound(request.AttachmentId, "payload-substitution"));
        var restarted = new ChannelAttachmentStore(database, new ChannelRuntimeCredentialStore());
        Assert.True(restarted.IsConversationBound(request.AttachmentId, "configured-destination-1"));
    }

    [Fact]
    public void ConfiguredProviderDestinationRejectsControlCharacters()
    {
        var store = new ChannelAttachmentStore(CreateDatabase(), new ChannelRuntimeCredentialStore());
        UpsertAttachmentRequest request = AttachmentRequest();
        request.Context = Context();
        request.Settings.Add(new AttachmentSettingValue
        {
            FieldKey = "channel_device_id",
            StringValue = "destination\nsubstitution",
        });

        Assert.Equal("configured_conversation_id_invalid",
            Assert.Throws<InvalidOperationException>(() => store.UpsertAttachment(request)).Message);
        Assert.False(store.IsConversationBound(request.AttachmentId, "destination\nsubstitution"));
    }

    [Fact]
    public void ResourceGovernorIsolatesConversationRateLimit()
    {
        var governor = new ChannelResourceGovernor(TimeProvider.System);
        ScopedRequestContext context = Context();
        for (int index = 0; index < 120; index++)
        {
            governor.Enforce(context, "ReadConversation", new SendOutboundMessageRequest
            {
                AttachmentId = "telegram:user-1:default",
                ConversationId = "flooded",
            });
        }
        Assert.Equal(StatusCode.ResourceExhausted, Assert.Throws<RpcException>(() => governor.Enforce(
            context, "ReadConversation", new SendOutboundMessageRequest
            {
                AttachmentId = "telegram:user-1:default",
                ConversationId = "flooded",
            })).StatusCode);

        ScopedRequestContext secondContext = Context();
        secondContext.ActorUserId = "user-2";
        secondContext.TenantId = "tenant-2";
        governor.Enforce(secondContext, "ReadConversation",
            new SendOutboundMessageRequest { AttachmentId = "telegram:user-2:default", ConversationId = "healthy" });
        Assert.Equal(1L, governor.DeniedCount);
    }

    [Fact]
    public void ChannelStateSchemaIsIndependentAndNewerSchemaFailsClosed()
    {
        ChannelHostDatabase database = CreateDatabase();
        using (SqliteConnection connection = database.OpenConnection())
        using (SqliteCommand version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version";
            Assert.Equal(ChannelHostDatabase.CurrentSchemaVersion, Convert.ToInt32(version.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
            version.CommandText = "PRAGMA user_version = 99";
            _ = version.ExecuteNonQuery();
        }

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(database.EnsureReady);
        Assert.Contains("newer than supported", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FreshChannelStateOperationalHealthIsReadyBeforeProviderTraffic()
    {
        ChannelHostDatabase database = CreateDatabase();
        using X509Certificate2 signer = LocalCaMtlsBootstrap.EnsureDelegationSigningCertificate(
            root, "Channel", ".delegation.pfx", ".delegation.cer");
        var governor = new ChannelResourceGovernor(TimeProvider.System);
        ChannelRequestAuthorizer authorizer = new(
            database,
            new ChannelDelegationVerificationKeys([signer]),
            CreatePolicyRegistry(signer),
            TimeProvider.System,
            governor,
            NullLogger<ChannelRequestAuthorizer>.Instance);
        var reader = new ChannelOperationalHealthReader(
            database, new ChannelDelegationVerificationKeys([signer]), CreatePolicyRegistry(signer), authorizer, governor);

        ChannelGatewayOperationalHealth health = reader.Read();
        Assert.Equal(ChannelHostDatabase.CurrentSchemaVersion, health.SchemaVersion);
        Assert.True(health.StateIntegrityVerified);
        Assert.Equal(0, health.ActiveRegistrations);
        Assert.Equal(0, health.IngressDepth);
    }

    [Fact]
    public void OperationalHealthReportsActualCallerAndExpiredRotationIdentityAsRevoked()
    {
        ChannelHostDatabase database = CreateDatabase();
        using X509Certificate2 signer = LocalCaMtlsBootstrap.EnsureDelegationSigningCertificate(
            root, "RotationHealth", ".delegation.pfx", ".delegation.cer");
        var governor = new ChannelResourceGovernor(TimeProvider.System);
        ChannelRequestAuthorizer authorizer = new(
            database, new ChannelDelegationVerificationKeys([signer]), CreatePolicyRegistry(signer),
            TimeProvider.System, governor, NullLogger<ChannelRequestAuthorizer>.Instance);
        var security = new ChannelSecurityRuntimeHealth(
            DateTimeOffset.UtcNow.AddHours(2), DateTimeOffset.UtcNow.AddHours(-1),
            "OVERLAP_EXPIRED_PENDING_FINALIZE", DateTimeOffset.UtcNow.AddMinutes(-5), ["stale-client", "stale-delegation"]);
        var reader = new ChannelOperationalHealthReader(
            database, new ChannelDelegationVerificationKeys([signer]), CreatePolicyRegistry(signer), authorizer, governor, security);
        var authorization = new ChannelAuthorizationResult(
            "digest", null, false, false, "external-workload", "spiffe://example.test/workload/external",
            "actor", "tenant", string.Empty);

        ChannelGatewayOperationalHealth health = reader.Read(authorization);
        Assert.Equal("external-workload", health.AuthenticatedWorkloadId);
        Assert.Equal("spiffe://example.test/workload/external", health.AuthenticatedWorkloadIdentity);
        Assert.True(health.ClientCertificateValid);
        Assert.False(health.DelegationCertificateValid);
        Assert.Equal("OVERLAP_EXPIRED_PENDING_FINALIZE", health.RotationState);
        Assert.Equal(2, health.RevokedIdentityKeyIds.Count);
    }

    [Fact]
    public async Task OutboundAssetSpoolVerifiesDigestBindsResourceAndReplaysIdempotently()
    {
        byte[] bytes = "verified asset"u8.ToArray();
        ChannelHostDatabase database = CreateDatabase();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Kautuka:ContainerStateRoot"] = root })
            .Build();
        var store = new ChannelOutboundAssetStore(database, configuration);
        UploadOutboundAssetMetadata metadata = AssetMetadata(bytes);

        ChannelOutboundAssetUploadResult first = await store.ReceiveAsync(
            metadata, Chunks(bytes), TestContext.Current.CancellationToken);
        ChannelOutboundAssetUploadResult replay = await store.ReceiveAsync(
            metadata, Chunks(bytes), TestContext.Current.CancellationToken);
        Assert.Equal(first.UploadId, replay.UploadId);
        Assert.Null(store.OpenAvailable(first.UploadId, metadata.AttachmentId, "wrong-conversation"));
        using (ChannelOutboundAssetLease? lease = store.OpenAvailable(first.UploadId, metadata.AttachmentId, metadata.ConversationId))
        {
            Assert.NotNull(lease);
            using var content = new MemoryStream();
            await lease.Content.CopyToAsync(content, TestContext.Current.CancellationToken);
            Assert.Equal(bytes, content.ToArray());
        }

        AssertOwnerOnlyPrivateFile(Path.Combine(
            KautukaHostPathPolicy.ResolveServiceStateRoot(configuration, "channelHost"),
            "outbound-assets",
            first.UploadId + ".asset"));

        store.MarkConsumed(first.UploadId);
        Assert.Null(store.OpenAvailable(first.UploadId, metadata.AttachmentId, metadata.ConversationId));
    }

    [Fact]
    public async Task OutboundAssetDigestFailureLeavesNoAvailableStateOrSpool()
    {
        byte[] bytes = "tampered"u8.ToArray();
        ChannelHostDatabase database = CreateDatabase();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Kautuka:ContainerStateRoot"] = root })
            .Build();
        var store = new ChannelOutboundAssetStore(database, configuration);
        UploadOutboundAssetMetadata metadata = AssetMetadata(bytes);
        metadata.Sha256Hex = new string('A', 64);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReceiveAsync(
            metadata, Chunks(bytes), TestContext.Current.CancellationToken));
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM channel_outbound_assets";
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync(TestContext.Current.CancellationToken) ?? -1L));
        string spoolPath = Path.Combine(root, "channelHost", "outbound-assets");
        Assert.True(!Directory.Exists(spoolPath) || Directory.GetFiles(spoolPath).Length == 0);
    }

    [Fact]
    public void ExternalWorkloadPolicyIsDistinctLeastPrivilegedRestartSafeAndRevocable()
    {
        Uri externalIdentity = new("spiffe://kautuka.local/workload/reference-consumer/channel-read");
        var (server, ca) = LocalCaMtlsBootstrap.EnsureAndLoad(root, "Channel",
            [new LocalMtlsClientCertificate("Reference Consumer", ".reference-client.pfx", externalIdentity)]);
        using (server)
        using (ca)
        using (X509Certificate2 client = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(root, ".reference-client.pfx"), null))
        using (X509Certificate2 delegation = LocalCaMtlsBootstrap.EnsureDelegationSigningCertificate(
            Path.Combine(root, "reference-delegation"), "Reference", ".signer.pfx", ".signer.cer"))
        {
            var external = new ChannelWorkloadPolicy(
                "reference-consumer", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { client.Thumbprint },
                "Reference Consumer", externalIdentity,
                new HashSet<string>(StringComparer.Ordinal) { "ListProviders" },
                new HashSet<string>(StringComparer.Ordinal) { ChannelRequestAuthorizer.Read },
                "reference-consumer", "kautuka-channel-gateway", [delegation],
                new HashSet<string>(StringComparer.Ordinal) { "external-owner" }, false, false, false, false);
            ChannelWorkloadPolicy gateway = CreatePolicyRegistry(delegation).DefaultPolicy;
            var registry = new ChannelWorkloadPolicyRegistry(ca, [gateway, external]);

            Assert.Equal("reference-consumer", registry.RequireAuthorized(client, "ListProviders", ChannelRequestAuthorizer.Read).WorkloadId);
            Assert.Throws<UnauthorizedAccessException>(() => registry.RequireAuthorized(client, "SetRuntimeCredentials", ChannelRequestAuthorizer.Runtime));
            Assert.False(external.AllowCredentialOperations);
            Assert.False(external.AllowAdministrativeOperations);
            ScopedRequestContext externalOwner = Context();
            externalOwner.ActorUserId = "external-owner";
            ChannelRequestAuthorizer.EnforceWorkloadResourcePolicy(external, externalOwner, "GetAttachments");
            Assert.Equal("external_workload_credentials_forbidden", Assert.Throws<RpcException>(() =>
                ChannelRequestAuthorizer.EnforceWorkloadResourcePolicy(external, externalOwner, "SetRuntimeCredentials")).Status.Detail);
            Assert.Equal("external_workload_administration_forbidden", Assert.Throws<RpcException>(() =>
                ChannelRequestAuthorizer.EnforceWorkloadResourcePolicy(external, externalOwner, "TransitionAttachmentLifecycle")).Status.Detail);
            Assert.Equal("external_workload_canonical_ingress_forbidden", Assert.Throws<RpcException>(() =>
                ChannelRequestAuthorizer.EnforceWorkloadResourcePolicy(external, externalOwner, "StreamIngress")).Status.Detail);
            externalOwner.ActorUserId = "other-owner";
            Assert.Equal("external_workload_owner_scope_denied", Assert.Throws<RpcException>(() =>
                ChannelRequestAuthorizer.EnforceWorkloadResourcePolicy(external, externalOwner, "GetAttachments")).Status.Detail);
            ChannelWorkloadPolicy noOwners = external with { AllowedOwnerUserIds = new HashSet<string>(StringComparer.Ordinal) };
            Assert.Equal("external_workload_owner_scope_denied", Assert.Throws<RpcException>(() =>
                ChannelRequestAuthorizer.EnforceWorkloadResourcePolicy(noOwners, Context(), "GetAttachments")).Status.Detail);

            var restarted = new ChannelWorkloadPolicyRegistry(ca, [gateway, external]);
            Assert.NotNull(restarted.Resolve(client));
            var revoked = new ChannelWorkloadPolicyRegistry(ca, [gateway]);
            Assert.Null(revoked.Resolve(client));
        }
    }

    [Fact]
    public async Task OutboundAssetCrashWindowsFailClosedAndCleanupOrphans()
    {
        byte[] bytes = "crash-safe"u8.ToArray();
        ChannelHostDatabase database = CreateDatabase();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Kautuka:ContainerStateRoot"] = root })
            .Build();
        var firstStore = new ChannelOutboundAssetStore(database, configuration);
        UploadOutboundAssetMetadata metadata = AssetMetadata(bytes);
        ChannelOutboundAssetUploadResult upload = await firstStore.ReceiveAsync(
            metadata, Chunks(bytes), TestContext.Current.CancellationToken);

        using (ChannelOutboundAssetLease? claimed = firstStore.OpenAvailable(upload.UploadId, metadata.AttachmentId, metadata.ConversationId))
        {
            Assert.NotNull(claimed);
        }
        var restartedAfterProviderWindow = new ChannelOutboundAssetStore(database, configuration);
        Assert.Null(restartedAfterProviderWindow.OpenAvailable(upload.UploadId, metadata.AttachmentId, metadata.ConversationId));

        using (SqliteConnection connection = database.OpenConnection())
        using (SqliteCommand expire = connection.CreateCommand())
        {
            expire.CommandText = "UPDATE channel_outbound_assets SET expires_at_utc=$expired WHERE upload_id=$upload";
            expire.Parameters.AddWithValue("$expired", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"));
            expire.Parameters.AddWithValue("$upload", upload.UploadId);
            _ = await expire.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        var cleanupAfterRestart = new ChannelOutboundAssetStore(database, configuration);
        using SqliteConnection verifyConnection = database.OpenConnection();
        using SqliteCommand verify = verifyConnection.CreateCommand();
        verify.CommandText = "SELECT COUNT(*) FROM channel_outbound_assets WHERE upload_id=$upload";
        verify.Parameters.AddWithValue("$upload", upload.UploadId);
        Assert.Equal(0L, (long)(await verify.ExecuteScalarAsync(TestContext.Current.CancellationToken) ?? -1L));
    }

    [Theory]
    [InlineData("telegram")]
    [InlineData("slack")]
    public async Task IngressOutboxReplaysAfterStoreRestartUntilAcknowledgedAndDeduplicatesProviderReplay(string providerKey)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ChannelHostDatabase database = CreateDatabase();
        string attachmentId = $"{providerKey}:user-1:default";
        string eventId = $"{providerKey}-event-1";
        using (var first = new ChannelIngressStore(database))
        {
            ChannelInboundEventMessage inbound = Inbound(eventId, providerKey, attachmentId);
            inbound.InboundMedia.Add(new InboundMediaDescriptorMessage { MediaId = "media-1" });
            Assert.True(first.Enqueue(inbound, "42"));
            Assert.False(first.Enqueue(Inbound(eventId, providerKey, attachmentId), "42"));
            Assert.Equal("42", first.GetCursor(providerKey, attachmentId));
            Assert.True(first.ContainsAdmittedMedia(attachmentId, eventId, "media-1"));
            Assert.False(first.ContainsAdmittedMedia(attachmentId, eventId, "substituted-media"));
        }

        using var restarted = new ChannelIngressStore(database);
        string eventLeaseId;
        using (var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        await using (IAsyncEnumerator<ChannelInboundEventMessage> enumerator = restarted.StreamAsync("gateway-ingress", receiveCts.Token).GetAsyncEnumerator(receiveCts.Token))
        {
            receiveCts.CancelAfter(TimeSpan.FromSeconds(2));
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(eventId, enumerator.Current.EventId);
            Assert.False(string.IsNullOrWhiteSpace(enumerator.Current.DeliveryLeaseId));
            eventLeaseId = enumerator.Current.DeliveryLeaseId;
        }

        Assert.False(restarted.Acknowledge("gateway-ingress", eventId, "stale-lease"));
        Assert.True(restarted.Acknowledge("gateway-ingress", eventId, eventLeaseId));
        Assert.False(restarted.Acknowledge("other-subscriber", eventId, eventLeaseId));
        using (SqliteConnection connection = database.OpenConnection())
        using (SqliteCommand count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM channel_ingress_outbox WHERE event_id=$event";
            count.Parameters.AddWithValue("$event", eventId);
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync(cancellationToken) ?? -1L));
        }
        using var emptyCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        emptyCts.CancelAfter(TimeSpan.FromMilliseconds(150));
        await using IAsyncEnumerator<ChannelInboundEventMessage> empty = restarted.StreamAsync("gateway-ingress", emptyCts.Token).GetAsyncEnumerator(emptyCts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => _ = await empty.MoveNextAsync().AsTask());
    }

    [Fact]
    public void ChannelHostDatabaseDropsAWorldReadRuleAndStillOpens()
    {
        ChannelHostDatabase database = CreateDatabase();
        if (OperatingSystem.IsWindows())
        {
            var info = new FileInfo(database.DatabasePath);
            FileSecurity security = info.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.Read,
                AccessControlType.Allow));
            info.SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(
                database.DatabasePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        database.EnsureReady();
        AssertOwnerOnlyPrivateFile(database.DatabasePath);
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        Assert.Equal("ok", command.ExecuteScalar() as string);
    }

    private ChannelHostDatabase CreateDatabase()
    {
        Directory.CreateDirectory(root);
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Kautuka:ContainerStateRoot"] = root })
            .Build();
        var database = new ChannelHostDatabase(configuration);
        database.EnsureReady();
        return database;
    }

    private ChannelRequestAuthorizer CreateAuthorizer(X509Certificate2 signer) => new(
        CreateDatabase(),
        new ChannelDelegationVerificationKeys([signer]),
        CreatePolicyRegistry(signer),
        TimeProvider.System,
        new ChannelResourceGovernor(TimeProvider.System),
        NullLogger<ChannelRequestAuthorizer>.Instance);

    private static ChannelRequestAuthorizer CreateAuthorizer(X509Certificate2 signer, ChannelHostDatabase database) => new(
        database,
        new ChannelDelegationVerificationKeys([signer]),
        CreatePolicyRegistry(signer),
        TimeProvider.System,
        new ChannelResourceGovernor(TimeProvider.System),
        NullLogger<ChannelRequestAuthorizer>.Instance);

    private static ChannelRequestAuthorizer CreateScopedAuthorizer(ChannelHostDatabase database, X509Certificate2 signer, string workloadId)
    {
        ChannelWorkloadPolicy baseline = CreatePolicyRegistry(signer).DefaultPolicy;
        ChannelWorkloadPolicy policy = baseline with { WorkloadId = workloadId };
        return new ChannelRequestAuthorizer(database, new ChannelDelegationVerificationKeys([signer]),
            new ChannelWorkloadPolicyRegistry(signer, [policy]), TimeProvider.System,
            new ChannelResourceGovernor(TimeProvider.System), NullLogger<ChannelRequestAuthorizer>.Instance);
    }

    private static ScopedRequestContext Context() => new()
    {
        RequestId = Guid.NewGuid().ToString("N"),
        ActorUserId = "user-1",
        TenantId = "tenant-1",
        SubmittedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
    };

    private static ScopedRequestContext RequestContext(Google.Protobuf.IMessage request) => request switch
    {
        UpsertAttachmentRequest value => value.Context,
        SyncOwnerTargetsRequest value => value.Context,
        ProvisionAttachmentRequest value => value.Context,
        DeactivateAttachmentRequest value => value.Context,
        TransitionAttachmentLifecycleRequest value => value.Context,
        SetRuntimeCredentialsRequest value => value.Context,
        AcknowledgeIngressRequest value => value.Context,
        _ => throw new InvalidOperationException("mutation_request_context_not_supported"),
    };

    private static UploadOutboundAssetMetadata AssetMetadata(byte[] content) => new()
    {
        Context = Context(),
        AssetId = "asset-1",
        AttachmentId = "telegram:user-1:default",
        ConversationId = "conversation-1",
        SafeFileName = "report.txt",
        ContentType = "text/plain",
        DeclaredSizeBytes = content.Length,
        Sha256Hex = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)),
        IdempotencyKey = "asset-upload-1",
    };

    private static SendOutboundMessageRequest IdempotentSend(ScopedRequestContext context, string key, string text) => new()
    {
        Context = context,
        AttachmentId = "telegram:user-1:default",
        MessageId = "message-1",
        ConversationId = "chat-1",
        IdempotencyKey = key,
        Content = new OutboundContentEnvelope { ActionType = "text", PayloadJson = "{\"text\":\"" + text + "\"}" },
    };

    private static SyncOwnerTargetsRequest OwnerSyncRequest(ScopedRequestContext context, string key) => new()
    {
        Context = context,
        OwnerUserId = "user-1",
        BotName = "Kautuka",
        Reason = "test",
        IdempotencyKey = key,
        Targets = { new ChannelTargetMessage { TargetKey = "agent:1", DisplayName = "Agent One" } },
    };

    private static ChannelWorkloadPolicyRegistry CreatePolicyRegistry(X509Certificate2 signer)
    {
        var policy = new ChannelWorkloadPolicy(
            "test-gateway", new HashSet<string>(StringComparer.OrdinalIgnoreCase), "unused", new Uri("spiffe://kautuka.local/test"),
            new HashSet<string>(StringComparer.Ordinal) { "Ping", "ListProviders", "SendOutboundMessage" },
            new HashSet<string>(StringComparer.Ordinal)
            {
                ChannelRequestAuthorizer.Read, ChannelRequestAuthorizer.Write,
                ChannelRequestAuthorizer.Runtime, ChannelRequestAuthorizer.Ingress,
            },
            ChannelDelegationEnvelope.Issuer,
            ChannelDelegationEnvelope.Audience,
            [signer], new HashSet<string>(StringComparer.Ordinal), true, true, true, true);
        return new ChannelWorkloadPolicyRegistry(signer, [policy]);
    }

    private string DictionaryCertificatePath() => Path.Combine(root, ".reference-signing.cer");

    private Dictionary<string, string?> ExternalPolicyConfiguration(X509Certificate2 client, Uri workloadIdentity, bool enabled) => new()
    {
        ["Kautuka:ChannelGateway:ExternalWorkloads:reference:Enabled"] = enabled.ToString(),
        ["Kautuka:ChannelGateway:ExternalWorkloads:reference:DelegationPublicCertificatePath"] = DictionaryCertificatePath(),
        ["Kautuka:ChannelGateway:ExternalWorkloads:reference:WorkloadIdentityUri"] = workloadIdentity.AbsoluteUri,
        ["Kautuka:ChannelGateway:ExternalWorkloads:reference:CertificateCommonName"] = "Reference Channel Workload",
        ["Kautuka:ChannelGateway:ExternalWorkloads:reference:CertificateThumbprints:0"] = client.Thumbprint,
        ["Kautuka:ChannelGateway:ExternalWorkloads:reference:AllowedMethods:0"] = "SendOutboundMessage",
        ["Kautuka:ChannelGateway:ExternalWorkloads:reference:AllowedOperations:0"] = ChannelRequestAuthorizer.Runtime,
        ["Kautuka:ChannelGateway:ExternalWorkloads:reference:DelegationIssuer"] = "reference-issuer",
        ["Kautuka:ChannelGateway:ExternalWorkloads:reference:DelegationAudience"] = "kautuka-channel-gateway",
        ["Kautuka:ChannelGateway:ExternalWorkloads:reference:AllowedOwnerUserIds:0"] = "external-user",
    };

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Chunks(byte[] content)
    {
        await Task.Yield();
        yield return content;
    }

    private static UpsertAttachmentRequest AttachmentRequest() => new()
    {
        AttachmentId = "telegram:user-1:default",
        ProviderKey = "telegram",
        ExpectedSchemaVersion = "telegram/v1",
        Settings =
        {
            new AttachmentSettingValue { FieldKey = "owner_user_id", StringValue = "user-1" },
            new AttachmentSettingValue { FieldKey = "display_name", StringValue = "Telegram user-1" },
        },
    };

    private static ChannelInboundEventMessage Inbound(string eventId, string providerKey, string attachmentId) => new()
    {
        EventId = eventId,
        ChannelType = providerKey,
        RegistrationId = attachmentId,
        OwnerUserId = "user-1",
        ExternalUserId = "external-1",
        ConversationId = "chat-1",
        EventType = "message",
        Text = "hello",
        PayloadJson = "{}",
    };

    public void Dispose()
    {
        SqliteTestPoolCleanup.ClearPoolsUnderDirectory(root);
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private static void AssertOwnerOnlyPrivateFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            FileSecurity security = new FileInfo(path).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected);
            AuthorizationRuleCollection rules = security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier));
            Assert.NotEmpty(rules);
            foreach (FileSystemAccessRule rule in rules)
            {
                Assert.Equal(WindowsIdentity.GetCurrent().User, (SecurityIdentifier)rule.IdentityReference);
            }

            return;
        }

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset value) : TimeProvider
    {
        private DateTimeOffset value = value;
        public override DateTimeOffset GetUtcNow() => value;
        public void Advance(TimeSpan duration) => value = value.Add(duration);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NoopDisposable.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(formatter(state, exception));
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
                Entries.Add(string.Join('|', values.Select(static item => $"{item.Key}={item.Value}")));
        }

        private sealed class NoopDisposable : IDisposable
        {
            public static NoopDisposable Instance { get; } = new();
            public void Dispose() { }
        }
    }
}
