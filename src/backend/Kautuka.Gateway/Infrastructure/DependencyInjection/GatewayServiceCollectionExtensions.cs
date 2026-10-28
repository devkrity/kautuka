using Kautuka.Gateway.Domain.Assets;
using Kautuka.Gateway.Domain.Connectors;
using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Domain.Memory;
using Kautuka.Gateway.Domain.Runtime;
using Kautuka.Gateway.Domain.Skills;
using Kautuka.Gateway.Domain.Usage;
using Kautuka.Gateway.Infrastructure.Credentials;

using Microsoft.Extensions.DependencyInjection.Extensions;


namespace Kautuka.Gateway.Infrastructure.DependencyInjection;

internal static class GatewayServiceCollectionExtensions
{
    public static IServiceCollection AddKautukaAiClients(this IServiceCollection services)
    {
        services.AddSingleton<CredentialResolverService>();
        services.AddSingleton<IAgentMemoryEmbeddingService, AgentMemoryEmbeddingService>();
        services.AddSingleton<IAgentMemoryEmbeddingQueue, AgentMemoryEmbeddingQueue>();
        services.AddSingleton<Kautuka.Gateway.Hosting.GatewayMeterFactory>();
        services.AddSingleton<IAgentRunMetrics, AgentRunMetrics>();
        services.AddSingleton<IGatewayUsageCaptureAccessor, GatewayUsageCaptureAccessor>();
        services.AddSingleton<IMemoryFactFormatter, DefaultMemoryFactFormatter>();
        services.AddSingleton<IInjectedMemoryContextBuilder, SourceAwareMemoryContextBuilder>();
        services.AddSingleton<IChatClientMemoryDecorator, ChatClientMemoryDecorator>();
        services.AddSingleton<IProviderPathTelemetryChatClientDecorator, ProviderPathTelemetryChatClientDecorator>();
        services.AddSingleton<IRunLimitResolver, RunLimitResolver>();
        services.AddSingleton<IToolInvocationSettingsResolver, ToolInvocationSettingsResolver>();
        services.AddSingleton<IAiClientProvider, OllamaAiClientProvider>();
        services.AddSingleton<IAiClientProvider, GoogleAiClientProvider>();
        services.AddSingleton<IAiClientProvider>(sp => new OpenAiCompatibleProvider("openai", runLimitResolver: sp.GetRequiredService<IRunLimitResolver>()));
        services.AddSingleton<IAiClientProvider>(sp => new OpenAiCompatibleProvider("azure-openai", runLimitResolver: sp.GetRequiredService<IRunLimitResolver>()));
        services.AddSingleton<IAiClientProvider>(sp => new OpenAiCompatibleProvider("foundry", runLimitResolver: sp.GetRequiredService<IRunLimitResolver>()));
        services.AddSingleton<IAiClientProvider>(sp => new OpenAiCompatibleProvider("anthropic", runLimitResolver: sp.GetRequiredService<IRunLimitResolver>()));
        services.AddSingleton<IAiClientProvider>(sp => new OpenAiCompatibleProvider("groq", runLimitResolver: sp.GetRequiredService<IRunLimitResolver>()));
        services.AddSingleton<IAiClientProvider>(sp => new OpenAiCompatibleProvider("mistral", runLimitResolver: sp.GetRequiredService<IRunLimitResolver>()));
        services.AddSingleton<IAiClientProvider>(sp => new OpenAiCompatibleProvider("openrouter", runLimitResolver: sp.GetRequiredService<IRunLimitResolver>()));
        services.AddSingleton<IAiClientProvider>(sp => new OpenAiResponsesCompatibleProvider(sp.GetRequiredService<IRunLimitResolver>()));
        services.AddSingleton<KautukaAiClientFactory>();

        return services;
    }

    public static IServiceCollection AddKautukaMAF(this IServiceCollection services)
    {
        services.AddSingleton<MafSessionStore>();
        services.AddSingleton<IToolExecutionPolicyService, ToolExecutionPolicyService>();
        services.AddSingleton<IAgentToolSurfaceStore, SqliteAgentToolSurfaceStore>();
        services.AddSingleton<IAgentToolCatalog, AgentToolCatalog>();
        services.AddSingleton<IEffectiveSkillManifestResolver, EffectiveSkillManifestResolver>();
        services.AddSingleton<ISkillPackageService, SkillPackageService>();
        services.AddSingleton<IMcpbPackageService, McpbPackageService>();
        services.AddSingleton<MailImageHandoff>();
        services.AddSingleton<IAgentToolProvider, RunShellAgentToolProvider>();
        services.AddSingleton<IAgentToolProvider, McpAgentToolProvider>();
        services.AddSingleton<IAgentToolProvider, SkillAgentToolProvider>();
        services.AddSingleton<Lazy<IAgentChatService>>(sp => new Lazy<IAgentChatService>(sp.GetRequiredService<IAgentChatService>));
        services.AddSingleton<Lazy<IChatGatewayService>>(sp => new Lazy<IChatGatewayService>(sp.GetRequiredService<IChatGatewayService>));
        services.TryAddSingleton<AgentToolApprovalWaiter>();
        services.TryAddSingleton<IAgentToolApprovalWaiter>(sp => sp.GetRequiredService<AgentToolApprovalWaiter>());
        services.AddSingleton<IAgentToolInvocationOrchestrator, AgentToolInvocationOrchestrator>();
        services.AddSingleton<IAgentToolProvider, AgentAsToolProvider>();
        services.AddSingleton<IMcpHttpClientFactory, McpHttpClientFactory>();
        services.AddSingleton<IModelCapabilityStore, SqliteModelCapabilityStore>();
        services.AddSingleton<IModelCapabilityProber, DefaultModelCapabilityProber>();
        services.AddSingleton<IModelCapabilityService, ModelCapabilityService>();
        services.AddSingleton<IProviderPathObservationStore, SqliteProviderPathObservationStore>();
        services.AddSingleton<IProviderPathPolicyResolver, ProviderPathPolicyResolver>();
        services.AddSingleton<AgentRuntimeFactory>();
        services.AddSingleton<IAgentRuntimeFactory>(static serviceProvider =>
            serviceProvider.GetRequiredService<AgentRuntimeFactory>());
        return services;
    }
}
