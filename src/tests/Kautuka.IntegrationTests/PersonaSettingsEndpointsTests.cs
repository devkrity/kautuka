using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Kautuka.Gateway.Domain.Auth;
using Kautuka.Gateway.Domain.Autopilot;
using Kautuka.Gateway.Domain.Channels;
using Kautuka.Gateway.Infrastructure.Channels;
using Kautuka.Gateway.Infrastructure.Credentials;
using Kautuka.Manager.Protos;
using Kautuka.Protos;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

using Xunit.Sdk;

namespace Kautuka.IntegrationTests;

public sealed class PersonaSettingsEndpointsTests
{
    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    [Fact]
    public async Task PersonaDefinitionsEndpointsRoundTripAndRejectInvalidPayloads()
    {
        await using var fixture = await PersonaApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();

        using JsonDocument initial = await fixture.SendJsonAsync(HttpMethod.Get, "/api/settings/persona/definitions", null, accessToken);
        Assert.True(initial.RootElement.TryGetProperty("definitions", out JsonElement initialDefinitions));
        Assert.True(initialDefinitions.TryGetProperty("styles", out _));

        using JsonDocument updated = await fixture.SendJsonAsync(
            HttpMethod.Put,
            "/api/settings/persona/definitions",
            new
            {
                styles = new[] { "professional", "helper" },
                tones = new[] { "neutral", "friendly" },
                verbosity_levels = new[] { "concise", "detailed" },
            },
            accessToken);

        JsonElement definitions = updated.RootElement.GetProperty("definitions");
        Assert.Contains(definitions.GetProperty("styles").EnumerateArray(), item =>
            string.Equals(item.GetString(), "helper", StringComparison.Ordinal));

        using HttpResponseMessage invalid = await fixture.SendAsync(
            HttpMethod.Put,
            "/api/settings/persona/definitions",
            new
            {
                styles = "professional",
                tones = new[] { "neutral" },
                verbosity_levels = new[] { "concise" },
            },
            accessToken);

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task PersonaProfilesEndpointsPersistPerAgentAndValidateShape()
    {
        await using var fixture = await PersonaApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();

        await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/providers/save",
            new
            {
                id = "provider-persona-profiles",
                type = "ollama",
                settings = new
                {
                    base_url = "http://localhost:11434",
                    default_model = "gemma",
                },
            },
            accessToken);

        await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/models/save",
            new
            {
                id = "model-persona-profiles",
                providerId = "provider-persona-profiles",
                modelName = "gemma",
                settings = new { },
            },
            accessToken);

        await fixture.SendJsonAsync(
            HttpMethod.Put,
            "/api/settings/agents/agent-alpha",
            new
            {
                name = "Agent Alpha",
                modelId = "model-persona-profiles",
                systemPrompt = "Be helpful",
                mcpBindings = Array.Empty<object>(),
                session = new
                {
                    historyEnabled = true,
                    maxHistoryRuns = 5,
                    summariesEnabled = false,
                },
                sandbox = new
                {
                    enabled = true,
                    allowedLanguages = Array.Empty<string>(),
                    maxMemory = "2g",
                    maxTimeout = 120,
                },
                approvalConfig = new
                {
                    requireMcpIds = Array.Empty<string>(),
                    requireMcpFunctionFilters = new Dictionary<string, string[]>(),
                    disabledMcpFunctions = new Dictionary<string, string[]>(),
                    timeoutSeconds = 300,
                },
            },
            accessToken);

        using JsonDocument profiles = await fixture.SendJsonAsync(
            HttpMethod.Put,
            "/api/settings/persona/profiles/agent-alpha",
            new
            {
                temperature = 0.5,
                style = "professional",
                tone = "neutral",
                verbosity = "concise",
            },
            accessToken);

        JsonElement saved = profiles.RootElement.GetProperty("profiles").GetProperty("agent-alpha");
        Assert.Equal(0.5d, saved.GetProperty("temperature").GetDouble());
        Assert.False(saved.TryGetProperty("office_worker", out _));

        using HttpResponseMessage invalid = await fixture.SendAsync(
            HttpMethod.Put,
            "/api/settings/persona/profiles/agent-alpha",
            new
            {
                temperature = "warm",
                style = "professional",
                tone = "neutral",
                verbosity = "concise",
            },
            accessToken);

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task AgentSettingsRoundTripPersistsPersonaPayload()
    {
        await using var fixture = await PersonaApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();

        await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/providers/save",
            new
            {
                id = "provider-local",
                type = "ollama",
                settings = new
                {
                    base_url = "http://localhost:11434",
                    default_model = "gemma",
                },
            },
            accessToken);

        await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/models/save",
            new
            {
                id = "model-local",
                providerId = "provider-local",
                modelName = "gemma",
                settings = new { },
            },
            accessToken);

        await fixture.SendJsonAsync(
            HttpMethod.Put,
            "/api/settings/persona/definitions",
            new
            {
                styles = new[] { "professional" },
                tones = new[] { "neutral" },
                verbosity_levels = new[] { "concise" },
            },
            accessToken);

        await fixture.SendJsonAsync(
            HttpMethod.Put,
            "/api/settings/agents/agent-persona-roundtrip",
            new
            {
                name = "Persona Agent",
                modelId = "model-local",
                systemPrompt = "Be helpful",
                allowCommandExecution = false,
                mcpBindings = Array.Empty<object>(),
                persona = new
                {
                    temperature = 0.5,
                    style = "professional",
                    tone = "neutral",
                    verbosity = "concise",
                    icon = "✨",
                    color = "#2563EB",
                },
                session = new
                {
                    historyEnabled = true,
                    maxHistoryRuns = 5,
                    summariesEnabled = false,
                },
                sandbox = new
                {
                    enabled = true,
                    allowedLanguages = Array.Empty<string>(),
                    maxMemory = "2g",
                    maxTimeout = 120,
                },
                runtime = new
                {
                    maxRunDurationSeconds = 900,
                    hitlExpirySeconds = 240,
                    hitlExpiryAction = "abort",
                    allowMultipleToolCalls = true,
                    allowConcurrentInvocation = true,
                    maxParallelTools = 4,
                },
                approvalConfig = new
                {
                    requireMcpIds = Array.Empty<string>(),
                    requireMcpFunctionFilters = new Dictionary<string, string[]>(),
                    disabledMcpFunctions = new Dictionary<string, string[]>(),
                    timeoutSeconds = 300,
                },
            },
            accessToken);

        using JsonDocument agents = await fixture.SendJsonAsync(HttpMethod.Get, "/api/settings/agents", null, accessToken);
        JsonElement agent = agents.RootElement
            .GetProperty("agents")
            .EnumerateArray()
            .First(item => string.Equals(item.GetProperty("id").GetString(), "agent-persona-roundtrip", StringComparison.Ordinal));

        JsonElement persona = agent.GetProperty("persona");
        Assert.Equal(0.5d, persona.GetProperty("temperature").GetDouble());
        Assert.Equal("✨", persona.GetProperty("icon").GetString());
        Assert.Equal("#2563EB", persona.GetProperty("color").GetString());
        Assert.False(persona.TryGetProperty("office_worker", out _));

        await fixture.RestartAsync();
        using JsonDocument agentsAfterRestart = await fixture.SendJsonAsync(HttpMethod.Get, "/api/settings/agents", null, accessToken);
        JsonElement restartedAgent = agentsAfterRestart.RootElement
            .GetProperty("agents")
            .EnumerateArray()
            .First(item => string.Equals(item.GetProperty("id").GetString(), "agent-persona-roundtrip", StringComparison.Ordinal));
        Assert.Equal("✨", restartedAgent.GetProperty("persona").GetProperty("icon").GetString());
        Assert.Equal("#2563EB", restartedAgent.GetProperty("persona").GetProperty("color").GetString());
        JsonElement restartedRuntime = restartedAgent.GetProperty("runtime");
        Assert.Equal(900, restartedRuntime.GetProperty("maxRunDurationSeconds").GetInt32());
        Assert.Equal(240, restartedRuntime.GetProperty("hitlExpirySeconds").GetInt32());
        Assert.Equal("abort", restartedRuntime.GetProperty("hitlExpiryAction").GetString());
        Assert.True(restartedRuntime.GetProperty("allowMultipleToolCalls").GetBoolean());
        Assert.True(restartedRuntime.GetProperty("allowConcurrentInvocation").GetBoolean());
        Assert.Equal(4, restartedRuntime.GetProperty("maxParallelTools").GetInt32());

        await fixture.SendJsonAsync(
            HttpMethod.Put,
            "/api/settings/agents/agent-persona-default-company",
            new
            {
                name = "Default Company Agent",
                modelId = "model-local",
                systemPrompt = "Be helpful",
                allowCommandExecution = false,
                mcpBindings = Array.Empty<object>(),
                persona = new
                {
                    temperature = 0.7,
                    style = "professional",
                    tone = "neutral",
                    verbosity = "concise",
                },
                session = new
                {
                    historyEnabled = true,
                    maxHistoryRuns = 5,
                    summariesEnabled = false,
                },
                sandbox = new
                {
                    enabled = true,
                    allowedLanguages = Array.Empty<string>(),
                    maxMemory = "2g",
                    maxTimeout = 120,
                },
                approvalConfig = new
                {
                    requireMcpIds = Array.Empty<string>(),
                    requireMcpFunctionFilters = new Dictionary<string, string[]>(),
                    disabledMcpFunctions = new Dictionary<string, string[]>(),
                    timeoutSeconds = 300,
                },
            },
            accessToken);

        using JsonDocument agentsAfter = await fixture.SendJsonAsync(HttpMethod.Get, "/api/settings/agents", null, accessToken);
        JsonElement defaultCompanyAgent = agentsAfter.RootElement
            .GetProperty("agents")
            .EnumerateArray()
            .First(item => string.Equals(item.GetProperty("id").GetString(), "agent-persona-default-company", StringComparison.Ordinal));

        Assert.Equal(0.7d, defaultCompanyAgent.GetProperty("persona").GetProperty("temperature").GetDouble());
        Assert.False(defaultCompanyAgent.GetProperty("persona").TryGetProperty("office_worker", out _));
    }

    [Fact]
    public async Task AgentSettingsSavePersistsCamelCaseApprovalPolicyToRuntimeConfiguration()
    {
        await using var fixture = await PersonaApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();

        await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/providers/save",
            new
            {
                id = "provider-approval-runtime",
                type = "ollama",
                settings = new
                {
                    base_url = "http://localhost:11434",
                    default_model = "gemma",
                },
            },
            accessToken);

        await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/models/save",
            new
            {
                id = "model-approval-runtime",
                providerId = "provider-approval-runtime",
                modelName = "gemma",
                settings = new { },
            },
            accessToken);

        await fixture.SendJsonAsync(
            HttpMethod.Put,
            "/api/settings/agents/agent-approval-runtime",
            new
            {
                name = "Approval Runtime Agent",
                modelId = "model-approval-runtime",
                systemPrompt = "Be helpful",
                allowCommandExecution = false,
                mcpBindings = new[] { new { connectorId = "home-assistant", context = "OwnContext" } },
                approvalConfig = new
                {
                    requireMcpIds = new[] { "home-assistant" },
                    requireMcpFunctionFilters = new Dictionary<string, string[]>
                    {
                        ["home-assistant"] = ["HassTurnOn"],
                    },
                    disabledMcpFunctions = new Dictionary<string, string[]>
                    {
                        ["home-assistant"] = ["HassTurnOff"],
                    },
                    timeoutSeconds = 420,
                },
                persona = new
                {
                    category = "general",
                    style = "professional",
                    tone = "neutral",
                    verbosity = "concise",
                },
                session = new
                {
                    historyEnabled = true,
                    maxHistoryRuns = 5,
                    summariesEnabled = true,
                },
                sandbox = new
                {
                    enabled = true,
                    allowedLanguages = Array.Empty<string>(),
                    maxMemory = "2g",
                    maxTimeout = 120,
                },
            },
            accessToken);

        IPlatformConfigurationProvider configurationProvider = fixture.Services.GetRequiredService<IPlatformConfigurationProvider>();
        PlatformConfiguration configuration = configurationProvider.GetCurrent();
        AgentDefinition agent = Assert.Single(configuration.Agents, item => string.Equals(item.Id, "agent-approval-runtime", StringComparison.Ordinal));

        Assert.Contains("home-assistant", agent.ApprovalPolicy.RequireMcpIds);
        Assert.Equal(420, agent.ApprovalPolicy.TimeoutSeconds);
        Assert.Equal(["HassTurnOn"], agent.ApprovalPolicy.RequireMcpFunctionFilters["home-assistant"]);
        Assert.Equal(["HassTurnOff"], agent.ApprovalPolicy.DisabledMcpFunctions["home-assistant"]);
    }

    [Fact]
    public async Task SavingExposedAgentToolWithAgentToolTargetsReturnsBadRequest()
    {
        await using var fixture = await PersonaApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();

        await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/providers/save",
            new
            {
                id = "provider-agent-tool-validation",
                type = "ollama",
                settings = new
                {
                    base_url = "http://localhost:11434",
                    default_model = "gemma",
                },
            },
            accessToken);

        await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/models/save",
            new
            {
                id = "model-agent-tool-validation",
                providerId = "provider-agent-tool-validation",
                modelName = "gemma",
                settings = new { },
            },
            accessToken);

        using HttpResponseMessage invalid = await fixture.SendAsync(
            HttpMethod.Put,
            "/api/settings/agents/agent-leaf-validation",
            new
            {
                name = "Leaf Validation Agent",
                modelId = "model-agent-tool-validation",
                systemPrompt = "Be helpful",
                approvalConfig = new
                {
                    agentToolIds = new[] { "agent-child-tool" },
                    approvalRequiredAgentToolIds = Array.Empty<string>(),
                },
                toolExposure = new
                {
                    exposedAsTool = true,
                    description = "Leaf tool",
                    invocationTimeoutSeconds = 120,
                },
                persona = new
                {
                    category = "general",
                    style = "professional",
                    tone = "neutral",
                    verbosity = "concise",
                },
                session = new
                {
                    historyEnabled = true,
                    maxHistoryRuns = 5,
                    summariesEnabled = true,
                },
                sandbox = new
                {
                    enabled = true,
                    allowedLanguages = Array.Empty<string>(),
                    maxMemory = "2g",
                    maxTimeout = 120,
                },
            },
            accessToken);

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        string errorBody = await invalid.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains(
            "Agents exposed as tools cannot invoke other agents as tools. Remove agent tool selections or disable Expose as tool.",
            errorBody,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentRevisionEndpointsReturnConsecutiveSettingDiffs()
    {
        await using var fixture = await PersonaApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();

        await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/providers/save",
            new
            {
                id = "provider-agent-revisions",
                type = "ollama",
                settings = new
                {
                    base_url = "http://localhost:11434",
                    default_model = "gemma",
                },
            },
            accessToken);

        await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/models/save",
            new
            {
                id = "model-agent-revisions",
                providerId = "provider-agent-revisions",
                modelName = "gemma",
                settings = new { },
            },
            accessToken);

        using JsonDocument created = await fixture.SendJsonAsync(
            HttpMethod.Put,
            "/api/settings/agents/agent-revisions",
            new
            {
                name = "Revision Agent",
                modelId = "model-agent-revisions",
                systemPrompt = "Be helpful",
                mcpBindings = Array.Empty<object>(),
                session = new
                {
                    historyEnabled = true,
                    maxHistoryRuns = 5,
                    summariesEnabled = false,
                },
                sandbox = new
                {
                    enabled = true,
                    allowedLanguages = Array.Empty<string>(),
                    maxMemory = "512m",
                    maxTimeout = 30,
                },
            },
            accessToken);
        Assert.Equal(1, created.RootElement.GetProperty("agent").GetProperty("revision").GetInt64());

        using JsonDocument updated = await fixture.SendJsonAsync(
            HttpMethod.Put,
            "/api/settings/agents/agent-revisions",
            new
            {
                name = "Revision Agent v2",
                modelId = "model-agent-revisions",
                systemPrompt = "Be concise",
                mcpBindings = Array.Empty<object>(),
                session = new
                {
                    historyEnabled = true,
                    maxHistoryRuns = 5,
                    summariesEnabled = false,
                },
                sandbox = new
                {
                    enabled = true,
                    allowedLanguages = Array.Empty<string>(),
                    maxMemory = "512m",
                    maxTimeout = 30,
                },
            },
            accessToken);
        Assert.Equal(2, updated.RootElement.GetProperty("agent").GetProperty("revision").GetInt64());

        using JsonDocument revisions = await fixture.SendJsonAsync(
            HttpMethod.Get,
            "/api/settings/agents/agent-revisions/revisions",
            null,
            accessToken);
        Assert.Equal(2, revisions.RootElement.GetProperty("currentRevision").GetInt64());
        JsonElement revisionTwo = revisions.RootElement.GetProperty("revisions")[0];
        Assert.Equal(2, revisionTwo.GetProperty("revision").GetInt64());
        Assert.Contains(
            revisionTwo.GetProperty("changedSettingIds").EnumerateArray(),
            static item => string.Equals(item.GetString(), "name", StringComparison.Ordinal));
        Assert.DoesNotContain(
            revisionTwo.GetProperty("changedSettingIds").EnumerateArray(),
            static item => string.Equals(item.GetString(), "scope", StringComparison.Ordinal));

        using JsonDocument diff = await fixture.SendJsonAsync(
            HttpMethod.Get,
            "/api/settings/agents/agent-revisions/revisions/2/diff",
            null,
            accessToken);
        Assert.Equal(1, diff.RootElement.GetProperty("fromRevision").GetInt64());
        Assert.Equal(2, diff.RootElement.GetProperty("toRevision").GetInt64());
        Assert.Contains(
            diff.RootElement.GetProperty("changes").EnumerateArray(),
            static change => string.Equals(change.GetProperty("settingId").GetString(), "name", StringComparison.Ordinal));
        Assert.DoesNotContain(
            diff.RootElement.GetProperty("changes").EnumerateArray(),
            static change => string.Equals(change.GetProperty("settingId").GetString(), "scope", StringComparison.Ordinal));
    }

    private sealed class PersonaApiFixture : IAsyncDisposable
    {
        private readonly string root;
        private readonly string? previousEnvironment;
        private WebApplication app;

        private PersonaApiFixture(string root, string? previousEnvironment, WebApplication app)
        {
            this.root = root;
            this.previousEnvironment = previousEnvironment;
            this.app = app;
            Client = app.GetTestClient();
            Services = app.Services;
        }

        public HttpClient Client { get; private set; }

        public IServiceProvider Services { get; private set; }

        public static async Task<PersonaApiFixture> CreateAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "kautuka-persona-api-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            string? previousEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", Environments.Development);

            WebApplication app = await BuildAppAsync(root);
            return new PersonaApiFixture(root, previousEnvironment, app);
        }

        public async Task RestartAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
            app = await BuildAppAsync(root);
            Client = app.GetTestClient();
            Services = app.Services;
        }

        private static async Task<WebApplication> BuildAppAsync(string root)
        {
            string sqlitePath = Path.Combine(root, "gateway.db");
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
            });

            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kautuka:ContainerStateRoot"] = Path.GetDirectoryName(sqlitePath) ?? throw new InvalidOperationException("Expected sqlite path to include a parent directory."),
            });

            builder.WebHost.UseTestServer();
            GatewayAppBootstrap.ConfigureServices(builder);
            RemoveHostedService<WorkspaceDesiredStateStartupService>(builder.Services);
            RemoveHostedService<ChannelHostOutboxWorker>(builder.Services);
            RemoveHostedService<ChannelHostIngressWorker>(builder.Services);
            RemoveHostedService<AutopilotSchedulerService>(builder.Services);
            builder.Services.AddSingleton<IManagerGrpcService, TestWorkspaceGrpcService>();
            builder.Services.RemoveAll<CredentialResolverService>();
            builder.Services.AddSingleton(
                new CredentialResolverService(
                    new Lazy<CredentialsService.CredentialsServiceClient>(new FakeCredentialsServiceClient())));
            builder.Services.RemoveAll<IChannelRegistrationProvisioningService>();
            builder.Services.AddSingleton<IChannelRegistrationProvisioningService>(new NoOpChannelRegistrationProvisioningService());
            builder.Services.Configure<HostOptions>(o => o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

            WebApplication app = builder.Build();
            var database = app.Services.GetRequiredService<KautukaDatabase>();
            GatewayAppBootstrap.ConfigureApp(app, database);
            await app.StartAsync();
            return app;
        }

        private static void RemoveHostedService<THostedService>(IServiceCollection services) where THostedService : class, IHostedService
        {
            ServiceDescriptor[] descriptors = services
                .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                    && descriptor.ImplementationType == typeof(THostedService))
                .ToArray();

            foreach (ServiceDescriptor descriptor in descriptors)
            {
                services.Remove(descriptor);
            }
        }

        public async Task<string> LoginAsBootstrapAdminAsync()
        {
            using JsonDocument setup = await SendJsonAsync(
                HttpMethod.Post,
                "/api/auth/setup",
                new { username = "admin", password = "Testing1", setupToken = app.Services.GetRequiredService<SetupPossession>().Peek() },
                accessToken: null,
                authenticated: false);

            using JsonDocument json = await SendJsonAsync(
                HttpMethod.Post,
                "/api/auth/login",
                new
                {
                    username = "admin",
                    password = "Testing1",
                },
                accessToken: null,
                authenticated: false);

            return json.RootElement.GetProperty("accessToken").GetString()
                ?? throw new XunitException("Login response did not include an access token.");
        }

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, string? accessToken = null, bool authenticated = true)
        {
            using HttpRequestMessage request = new(method, path);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, options: jsonOptions);
            }

            if (authenticated)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken ?? throw new ArgumentNullException(nameof(accessToken)));
            }

            return await Client.SendAsync(request);
        }

        public async Task<JsonDocument> SendJsonAsync(HttpMethod method, string path, object? body, string? accessToken = null, bool authenticated = true)
        {
            using HttpResponseMessage response = await SendAsync(method, path, body, accessToken, authenticated);
            string content = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new XunitException($"Request to '{path}' failed with {(int)response.StatusCode}: {content}");
            }

            return JsonDocument.Parse(content);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();

            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previousEnvironment);
            SqliteTestPoolCleanup.ClearPoolsUnderDirectory(root);

            if (Directory.Exists(root))
            {
                await TestDirectoryCleanup.DeleteDirectoryWithRetryAsync(root).ConfigureAwait(false);
            }
        }

        private sealed class NoOpChannelRegistrationProvisioningService : IChannelRegistrationProvisioningService
        {
            private static readonly ChannelRegistrationProvisioningResult success =
                new(true, "skipped", "Provisioning disabled for integration test fixture.");

            public Task<ChannelRegistrationProvisioningResult> ProvisionRegistrationAsync(string registrationId, CancellationToken cancellationToken)
                => Task.FromResult(success);

            public Task<ChannelRegistrationProvisioningResult> ProvisionAsync(
                string registrationId,
                string channelDeviceId,
                string? defaultTargetType,
                string? defaultTargetId,
                CancellationToken cancellationToken)
            {
                _ = registrationId;
                _ = channelDeviceId;
                _ = defaultTargetType;
                _ = defaultTargetId;
                _ = cancellationToken;
                return Task.FromResult(success);
            }
        }

        private sealed class TestWorkspaceGrpcService : IManagerGrpcService
        {
            private static WorkspaceContextResponse SuccessWorkspaceContext()
                => new()
                {
                    Ok = true,
                    Message = "integration-test",
                    Context = new WorkspaceContextMessage
                    {
                        ScopeType = "agent",
                        ScopeId = "integration-test",
                        UserId = string.Empty,
                        HostWorkspacePath = "/tmp/kautuka-integration",
                        ContainerWorkspacePath = "/workspace",
                        Image = "kautuka-base:latest",
                        DefaultTimeoutSeconds = 120,
                    },
                };

            public Task<WorkspaceBoolResponse> PingAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(new WorkspaceBoolResponse { Ok = true, Message = request.Message });

            public Task<ManagerStatusResponse> GetHostStatusAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(new ManagerStatusResponse
                {
                    Ok = true,
                    CoreReady = false,
                    AgentsPending = false,
                    AgentsReady = false,
                    Lifecycle = "core_not_ready",
                    Message = "integration-test",
                });

            public Task<ManagerRuntimeContainersResponse> ListRuntimeContainersAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(new ManagerRuntimeContainersResponse { Ok = true, Message = "integration-test" });

            public Task<WorkspaceContextResponse> DescribeWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(SuccessWorkspaceContext());

            public Task<WorkspaceContextResponse> RebuildWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(SuccessWorkspaceContext());

            public Task<WorkspaceDeleteResponse> DeleteWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
                => throw new NotSupportedException();

            public Task<WorkspaceSandboxResponse> SpawnSandboxAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
                => throw new NotSupportedException();

            public Task<WorkspaceSandboxResponse> InspectSandboxAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
                => throw new NotSupportedException();

            public Task<WorkspaceExecutionResponse> ExecAsync(WorkspaceExecRequest request, CancellationToken cancellationToken = default)
                => throw new NotSupportedException();

            public Task<WorkspaceBoolResponse> StopSandboxAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
                => throw new NotSupportedException();

            public Task<WorkspaceBoolResponse> PrimeWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
                => throw new NotSupportedException();

            public Task<WorkspaceMcpProbeResponse> ProbeMcpContainerAsync(WorkspaceMcpContainerProbeRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(new WorkspaceMcpProbeResponse
                {
                    Ok = true,
                    ToolCount = 1,
                    Tools = { new WorkspaceMcpToolMessage { Name = "kautuka_web_fetch", Description = "Fetches web content." } },
                });

            public Task<WorkspaceMcpContainerListToolsResponse> ListMcpContainerToolsAsync(WorkspaceMcpContainerCallRequest request, CancellationToken cancellationToken = default)
                => throw new NotSupportedException();

            public Task<WorkspaceMcpContainerCallResponse> CallMcpContainerToolAsync(WorkspaceMcpContainerCallRequest request, CancellationToken cancellationToken = default)
                => throw new NotSupportedException();

            public Task<DesiredStateUpdateResponse> UpdateDesiredStateAsync(ManagerDesiredStateUpdateRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(new DesiredStateUpdateResponse { Outcome = DesiredStateUpdateOutcome.Applied, Message = "integration-test" });
        }
    }
}
