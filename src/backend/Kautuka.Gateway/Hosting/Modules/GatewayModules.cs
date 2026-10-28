using System.Threading.RateLimiting;

using Kautuka.Gateway.Domain.AgentFiles;
using Kautuka.Gateway.Domain.Assets;
using Kautuka.Gateway.Domain.Autopilot;
using Kautuka.Gateway.Domain.Channels;
using Kautuka.Gateway.Domain.Configuration;
using Kautuka.Gateway.Domain.Connectors;
using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Domain.Memory;
using Kautuka.Gateway.Domain.Providers;
using Kautuka.Gateway.Domain.Sandbox;
using Kautuka.Gateway.Domain.Sessions;
using Kautuka.Gateway.Domain.Skills;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Domain.Usage;
using Kautuka.Gateway.Domain.Workflows;
using Kautuka.Gateway.Endpoints.Auth;
using Kautuka.Gateway.Endpoints.Catalog;
using Kautuka.Gateway.Endpoints.Connectors;
using Kautuka.Gateway.Endpoints.Credentials;
using Kautuka.Gateway.Endpoints.Internal;
using Kautuka.Gateway.Endpoints.Runtime;
using Kautuka.Gateway.Endpoints.Settings;
using Kautuka.Gateway.Endpoints.Skills;
using Kautuka.Gateway.Infrastructure.Auth;
using Kautuka.Gateway.Infrastructure.Catalog;
using Kautuka.Gateway.Infrastructure.Channels;
using Kautuka.Gateway.Infrastructure.Connectors;

using Mediator;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Kautuka.Gateway.Hosting.Modules;

internal static class GatewayModuleCatalog
{
    public static void AddGatewayModules(this WebApplicationBuilder builder)
    {
        GatewaySecurityModule.AddServices(builder);
        GatewayUsageModule.AddServices(builder.Services);
        GatewayMemoryModule.AddServices(builder);
        GatewayOperationsModule.AddServices(builder.Services);
        GatewayChannelsModule.AddServices(builder.Services);
        GatewayCatalogModule.AddServices(builder.Services);
        GatewayExecutionModule.AddServices(builder);
        GatewayHitlModule.AddServices(builder.Services);
    }

    public static void MapGatewayModules(this WebApplication app)
    {
        GatewaySecurityModule.MapEndpoints(app);
        GatewayCatalogModule.MapEndpoints(app);
        GatewayExecutionModule.MapEndpoints(app);
        GatewayHitlModule.MapEndpoints(app);
        GatewayChannelsModule.MapEndpoints(app);
        GatewayMemoryModule.MapEndpoints(app);
        GatewayUsageModule.MapEndpoints(app);
        GatewayOperationsModule.MapEndpoints(app);
    }
}

internal static class GatewaySecurityModule
{
    public static void AddServices(WebApplicationBuilder builder)
    {
        builder.Services.AddAuthentication(GatewayBearerAuthenticationHandler.AuthenticationSchemeName)
            .AddScheme<AuthenticationSchemeOptions, GatewayBearerAuthenticationHandler>(GatewayBearerAuthenticationHandler.AuthenticationSchemeName, _ => { });
        builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .AddAuthenticationSchemes(GatewayBearerAuthenticationHandler.AuthenticationSchemeName)
                .RequireAuthenticatedUser()
                .Build())
            .AddPolicy("AdminDiagnostics", policy => policy.RequireRole("admin"))
            .AddPolicy("AdminManagement", policy => policy.RequireAuthenticatedUser().RequireRole("admin"));
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            // Unset stays at 10 attempts per minute. Tests set Kautuka:Auth:AttemptLimit.
            int permitLimit = 10;
            if (int.TryParse(builder.Configuration["Kautuka:Auth:AttemptLimit"], out int configuredLimit) && configuredLimit > 0)
            {
                permitLimit = configuredLimit;
            }

            AddAuthPolicy("AuthLogin");
            AddAuthPolicy("AuthRefresh");
            AddAuthPolicy("AuthSetup");
            AddAuthPolicy("AuthExternal");

            void AddAuthPolicy(string name)
            {
                options.AddPolicy(name, context => RateLimitPartition.GetFixedWindowLimiter(
                    name + "\n" + AuthClientAddress.ResolvePartitionKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = permitLimit,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    }));
            }
        });
        builder.Services.AddSingleton<IGatewayTokenStore, GatewayTokenStore>();
        builder.Services.AddSingleton<SetupPossession>();
        builder.Services.AddSingleton<IGatewayUserStore, GatewayUserStore>();
        builder.Services.AddSingleton<IGatewayAuthGuard, GatewayAuthGuard>();
        builder.Services.AddHttpClient(HttpExternalIdentityClient.HttpClientName);
        builder.Services.AddSingleton(static provider => ExternalLoginProviders.Create(
            provider.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(),
            provider.GetRequiredService<ConnectorAuthEndpoint>()));
        builder.Services.AddSingleton<ExternalLoginSessionStore>();
        builder.Services.AddSingleton<IExternalIdentityClient, HttpExternalIdentityClient>();
    }

    public static void MapEndpoints(WebApplication app)
    {
        app.MapGatewayManagementEndpoints();
        app.MapGatewayAuthEndpoints();
    }
}

internal static class GatewayCatalogModule
{
    public static void AddServices(IServiceCollection services)
    {
        services.AddSingleton<IGatewaySettingsStore, GatewaySettingsStore>();
        services.AddSingleton<IGatewayCatalogStore, GatewayCatalogStore>();
        services.AddSingleton<IGatewayHostedCatalogProvider, UpdateServiceHostedCatalogProvider>();
        services.AddSingleton<IGatewayHostedCatalogInstaller, GatewayHostedCatalogInstaller>();
        services.AddHostedService<BuiltInMcpPackageBootstrapService>();
        services.AddSingleton(static provider => ConnectorAuthEndpoint.Parse(
            provider.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["Kautuka:ConnectorAuth:Host"]));
        services.AddSingleton(static provider => ConnectorAuthCatalog.Load(provider.GetRequiredService<IWebHostEnvironment>().ContentRootPath));
        services.AddSingleton<ConnectorOAuthSessionStore>();
        services.AddSingleton(static _ => new ConnectorAuthorizationClient());
        services.AddSingleton<IConnectorRefreshTokenStore, CredentialsHostConnectorRefreshTokenStore>();
        services.AddSingleton<ConnectorOAuthService>();
        services.AddHostedService<BuiltInConnectorCatalogService>();
        services.AddHostedService<BuiltInSkillPackageBootstrapService>();
        services.AddSingleton<IModelProbeService, ModelProbeService>();
        services.AddSingleton<IProviderValidationService, ProviderValidationService>();
        services.AddSingleton<IMcpConnectorTestService, McpConnectorTestService>();
    }

    public static void MapEndpoints(WebApplication app)
    {
        app.MapGatewayCatalogEndpoints();
        app.MapGatewayConnectorEndpoints();
        app.MapConnectorOAuthEndpoints();
        app.MapGatewaySkillEndpoints();
        app.MapGatewayCredentialEndpoints();
        app.MapGatewaySettingsEndpoints();
    }
}

internal static class GatewayExecutionModule
{
    public static void AddServices(WebApplicationBuilder builder)
    {
        builder.Services.AddKautukaAiClients();
        builder.Services.AddKautukaMAF();
        builder.Services.AddSingleton<IModelTurnOrdinalCounter, ModelTurnOrdinalCounter>();
        builder.Services.AddSingleton<IAgentChatService, AgentChatService>();
        builder.Services.AddSingleton<ISessionTitleService, SessionTitleService>();
        builder.Services.AddSingleton<IAgentFilePathAuthorizationService, AgentFilePathAuthorizationService>();
        builder.Services.AddSingleton<IAgentFileExchangeService, AgentFileExchangeService>();
        builder.Services.AddSingleton<IGatewayContinuationStore, GatewayContinuationStore>();
        builder.Services.AddSingleton<NaradWorkspaceWatchHub>();
        builder.Services.AddSingleton<INaradWorkspaceWatchHub>(static services => services.GetRequiredService<NaradWorkspaceWatchHub>());
        builder.Services.AddSingleton<GatewaySessionStore>();
        builder.Services.AddSingleton<IGatewaySessionStore>(static services => new WorkspaceNotifyingGatewaySessionStore(
            services.GetRequiredService<GatewaySessionStore>(),
            services.GetRequiredService<INaradWorkspaceWatchHub>()));
        builder.Services.AddSingleton<ISessionCompactionService, SessionCompactionService>();
        builder.Services.AddSingleton<IRunWaitStateStore, SqliteRunWaitStateStore>();
        builder.Services.AddSingleton<IRuntimeEventStreamHub, RuntimeEventStreamHub>();
        builder.Services.AddSingleton<RuntimeEventPublisher>(static services => new RuntimeEventPublisher(
            services.GetRequiredService<IAgentDefinitionStore>(),
            services.GetRequiredService<IRuntimeEventStreamHub>(),
            services.GetRequiredService<ILogger<RuntimeEventPublisher>>(),
            services.GetServices<IRuntimeEventPublicationObserver>()));
        builder.Services.AddSingleton<IRuntimeEventPublisher>(static services => services.GetRequiredService<RuntimeEventPublisher>());
        builder.Services.AddSingleton<IRuntimeEventLifecycle>(static services => services.GetRequiredService<RuntimeEventPublisher>());
        builder.Services.AddSingleton<IRuntimeEventHistoryReader, RuntimeEventHistoryReader>();
        builder.Services.AddSingleton<IAgentToolInvocationStore, GatewayAgentToolInvocationStore>();
        builder.Services.AddSingleton<IAgentToolInvocationLinkStore, GatewayAgentToolInvocationLinkStore>();
        builder.Services.AddSingleton<IExecutionQuarantineStore, ExecutionQuarantineStore>();
        builder.Services.AddHostedService<ExecutionReconciliationService>();
        builder.Services.AddSingleton<ConversationAssetStore>();
        builder.Services.AddSingleton<IConversationAssetStore>(static services => services.GetRequiredService<ConversationAssetStore>());
        builder.Services.AddHostedService<ConversationAssetRecoveryService>();
        builder.Services.Configure<DocumentIngestionOptions>(builder.Configuration.GetSection(DocumentIngestionOptions.Section));
        builder.Services.AddSingleton<IParsedAssetStore, SqliteParsedAssetStore>();
        builder.Services.AddSingleton<IDocumentParsingClient, MarkItDownMcpClient>();
        builder.Services.AddSingleton<IConversationAssetIngestionService, ConversationAssetIngestionService>();
        builder.Services.AddSingleton<IGatewayAutopilotStore, GatewayAutopilotStore>();
        builder.Services.AddSingleton<IGatewayWorkflowStore, GatewayWorkflowStore>();
        builder.Services.AddSingleton<WorkflowInstanceLauncher>();
        builder.Services.AddSingleton<IWorkflowNaradStreamHub, WorkflowNaradStreamHub>();
        builder.Services.AddSingleton<IWorkflowNaradPublisher, WorkflowNaradPublisher>();
        builder.Services.AddSingleton<IWorkflowExecutionService, WorkflowExecutionService>();
        builder.Services.AddHostedService<WorkflowRecoveryHostedService>();
        builder.Services.AddSingleton<IAutopilotNotificationDispatcher, AutopilotNotificationDispatcher>();
        builder.Services.AddSingleton<IAutopilotRunExecutionService, AutopilotRunExecutionService>();
        builder.Services.AddSingleton<IAutopilotRunLaunchService, AutopilotRunLaunchService>();
        builder.Services.AddHostedService<AutopilotSchedulerService>();
        builder.Services.AddSingleton<ISessionLifecycleService, SessionLifecycleService>();
        builder.Services.AddSingleton<ChatGatewayService>();
        builder.Services.AddSingleton<IChatGatewayService>(static services => services.GetRequiredService<ChatGatewayService>());
        builder.Services.AddSingleton<SessionTurnQueueStore>(static services => new(services.GetRequiredService<GatewayState>().Database));
        builder.Services.AddSingleton<SessionTurnExecution>();
        builder.Services.AddHostedService<SessionTurnQueueWorker>();
        builder.Services.AddSingleton<INaradClientLeaseService, NaradClientLeaseService>();
        builder.Services.AddSingleton<NaradIntentExecutionQueue>();
        builder.Services.AddSingleton<INaradIntentExecutionQueue>(static services => services.GetRequiredService<NaradIntentExecutionQueue>());
        builder.Services.AddSingleton<INaradIntentRequestHandler, NaradIntentRequestHandler>();
        builder.Services.AddSingleton<IThreadCommandCoordinator, ThreadCommandCoordinator>();
        builder.Services.AddSingleton<IThreadCommandService, ThreadCommandService>();
        builder.Services.AddSingleton<IWorkflowCommandService, WorkflowCommandService>();
        builder.Services.AddSingleton<IRootRunAdmissionGate, RootRunAdmissionGate>();
        builder.Services.AddSingleton<ISessionRetentionQueue, SessionRetentionQueue>();
        builder.Services.AddHostedService<SessionRetentionDispatcherService>();
    }

    public static void MapEndpoints(WebApplication app)
    {
        GatewayRuntimeEndpointMappings.MapGatewayRuntimeSessionEndpoints(app);
        GatewayRuntimeEndpointMappings.MapGatewayRuntimeAssetEndpoints(app);
        GatewayRuntimeEndpointMappings.MapGatewayRuntimeAgentFileEndpoints(app);
        GatewayRuntimeEndpointMappings.MapGatewayRuntimeAutopilotEndpoints(app);
        GatewayRuntimeEndpointMappings.MapGatewayRuntimeWorkflowEndpoints(app);
        GatewayRuntimeEndpointMappings.MapGatewayRuntimeBehaviorEndpoints(app);
        app.MapNaradEndpoints();
        app.MapNaradWorkflowEndpoints();
    }
}

internal static class GatewayHitlModule
{
    public static void AddServices(IServiceCollection services)
    {
        services.AddSingleton<GatewayApprovalStore>();
        services.AddSingleton<IGatewayApprovalStore>(static provider => provider.GetRequiredService<GatewayApprovalStore>());
        services.AddSingleton<IApprovalResolutionEventStore>(static provider => provider.GetRequiredService<GatewayApprovalStore>());
        services.AddSingleton<IApprovalResolutionSideEffectApplier, ApprovalResolutionSideEffectApplier>();
        services.AddSingleton<ApprovalResolutionEventDispatcher>();
        services.AddSingleton<IApprovalResolutionEventDispatcher>(static provider => provider.GetRequiredService<ApprovalResolutionEventDispatcher>());
        services.AddHostedService<ApprovalResolutionEventDispatcher>(static provider => provider.GetRequiredService<ApprovalResolutionEventDispatcher>());
        services.AddSingleton<IApprovalResolutionService, ApprovalResolutionService>();
        services.AddSingleton<IApprovalBypassRegistry, ApprovalBypassRegistry>();
        services.AddSingleton<IRunStopRegistry, RunStopRegistry>();
        services.AddSingleton<IRunStopCommandService, RunStopCommandService>();
        services.AddSingleton<ISamplingPolicyResolver>(_ => new SamplingPolicyResolver());
        services.AddSingleton<IResumeTokenProvider, ResumeTokenProvider>();
        services.AddSingleton<IApprovalContinuationOrchestrator, ApprovalContinuationOrchestrator>();
        services.AddSingleton<IPackedApprovedToolInvoker, PackedApprovedToolInvoker>();
        services.TryAddSingleton<AgentToolApprovalWaiter>();
        services.TryAddSingleton<IAgentToolApprovalWaiter>(static provider => provider.GetRequiredService<AgentToolApprovalWaiter>());
        services.AddSingleton<INotificationHandler<HitlApprovalResolved>>(static provider => provider.GetRequiredService<AgentToolApprovalWaiter>());
        services.AddMediator(options => options.Assemblies = [typeof(HitlApprovalRequested).Assembly]);
        services.AddSingleton<IApprovalContinuationQueue, ApprovalContinuationQueue>();
        services.AddHostedService<ApprovalContinuationDispatcherService>();
        services.AddHostedService<HitlExpirySweeperService>();
        services.AddHostedService<ChannelApprovalRecoveryService>();
    }

    public static void MapEndpoints(WebApplication app) => app.MapGatewayApprovalEndpoints();
}

internal static class GatewayChannelsModule
{
    public static void AddServices(IServiceCollection services)
    {
        services.AddSingleton<IPersonaSettingsReader, PersonaSettingsReader>();
        services.AddSingleton<IChannelAttachmentRegistry, ChannelAttachmentRegistry>();
        services.AddSingleton<IChannelRoutingCoordinator, ChannelRoutingCoordinator>();
        services.AddSingleton<IChannelOutboxStore, SqliteChannelOutboxStore>();
        services.AddSingleton<IChannelOutboundMessagePublisher, ChannelOutboundMessagePublisher>();
        services.AddSingleton<IChannelConversationMapStore, SqliteChannelConversationMapStore>();
        services.AddSingleton<IChannelRuntimeEventProjector, ChannelRuntimeEventProjector>();
        services.AddSingleton<IRuntimeEventPublicationObserver>(static services =>
            (IRuntimeEventPublicationObserver)services.GetRequiredService<IChannelRuntimeEventProjector>());
        services.AddSingleton<IChannelHostOwnerTargetSyncQueue, ChannelHostOwnerTargetSyncQueue>();
        services.AddSingleton<IChannelHostOwnerTargetSyncNotifier, ChannelHostOwnerTargetSyncNotifier>();
        services.AddSingleton<ChannelIngressInfrastructure>();
        services.AddSingleton<ChannelIngressRoutingInfrastructure>();
        services.AddSingleton<ChannelIngressService>();
        services.AddHostedService<ChannelSessionUiWorker>();
        services.AddSingleton<IChannelIngressService>(static services => services.GetRequiredService<ChannelIngressService>());
        services.AddSingleton<ISessionTurnProjection>(static services => services.GetRequiredService<ChannelIngressService>());
        services.AddSingleton<IChannelRegistrationProvisioningService, ChannelRegistrationProvisioningService>();
        services.AddSingleton<IChannelOutboundAssetUploadService, ChannelOutboundAssetUploadService>();
        services.AddSingleton<IChannelInteractionTransportUpdater, ChannelInteractionTransportUpdater>();
        services.AddHostedService<ChannelHostOutboxWorker>();
        services.AddHostedService<ChannelHostIngressWorker>();
        services.AddHostedService<ChannelHostOwnerTargetSyncDispatcherService>();
        services.AddHostedService<ChannelHostOwnerTargetReconciliationService>();
    }

    public static void MapEndpoints(WebApplication app)
    {
        // ChannelHost owns its authenticated transport surface; Gateway has no public channel routes.
        _ = app;
    }
}

internal static class GatewayMemoryModule
{
    public static void AddServices(WebApplicationBuilder builder)
    {
        builder.Services.AddHostedService<AgentMemoryEmbeddingDispatcherService>();
        builder.Services.AddHostedService<AgentMemoryEmbeddingReconciliationService>();
        builder.Services.AddSingleton<IGatewayMemoryStore, GatewayMemoryStore>();
        builder.Services.AddSingleton<MemoryExtractionJobStore>();
        builder.Services.AddHostedService<MemoryExtractionDispatcherService>();
        builder.Services.AddSingleton<JsonMemoryResponseParser>();
        builder.Services.AddSingleton<ModelMemoryFactExtractor>();
        builder.Services.AddSingleton<HeuristicMemoryFactExtractor>();
        builder.Services.AddSingleton<IMemoryFactExtractor, ChatMemoryFactExtractor>();
        builder.Services.AddSingleton<IMemoryScopePolicy, DefaultMemoryScopePolicy>();
        builder.Services.AddSingleton<IChatMemoryAssembler, ChatMemoryAssembler>();
    }

    public static void MapEndpoints(WebApplication app)
        => GatewayRuntimeEndpointMappings.MapGatewayRuntimeMemoryEndpoints(app);
}

internal static class GatewayUsageModule
{
    public static void AddServices(IServiceCollection services)
    {
        services.AddSingleton<KautukaUsageDatabase>();
        services.AddSingleton<IGatewayUsageQueue, GatewayUsageQueue>();
        services.AddSingleton<IGatewayUsageStore, GatewayUsageStore>();
        services.AddSingleton<IGatewayUsageQueryStore, GatewayUsageQueryStore>();
        services.AddHostedService<GatewayUsageWriterService>();
    }

    public static void MapEndpoints(WebApplication app)
        => GatewayRuntimeEndpointMappings.MapGatewayRuntimeUsageEndpoints(app);
}

internal static class GatewayOperationsModule
{
    public static void AddServices(IServiceCollection services)
    {
        services.AddHttpClient();
        services.AddSingleton<IWorkspaceRuntimeTool, GrpcWorkspaceRuntimeTool>();
        services.AddSingleton<IWorkspaceUserIdentityResolver, GatewayWorkspaceUserIdentityResolver>();
        services.AddSingleton(static provider => GatewayState.FromDatabase(
            provider.GetRequiredService<KautukaDatabase>(),
            provider.GetRequiredService<ILogger<GatewayState>>(),
            provider.GetRequiredService<IGatewayMemoryStore>(),
            provider.GetRequiredService<IAgentDefinitionStore>(),
            provider.GetRequiredService<IConfiguration>(),
            provider.GetRequiredService<IWorkspaceRuntimeTool>()));
    }

    public static void MapEndpoints(WebApplication app)
        => GatewayRuntimeEndpointMappings.MapGatewayRuntimeSystemEndpoints(app);
}
