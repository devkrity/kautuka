using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

using Grpc.Core;

using Kautuka.Gateway.Domain.Auth;
using Kautuka.Gateway.Domain.Autopilot;
using Kautuka.Gateway.Domain.Channels;
using Kautuka.Gateway.Domain.Sandbox;
using Kautuka.Gateway.Hosting;
using Kautuka.Gateway.Infrastructure.Channels;
using Kautuka.Gateway.Infrastructure.Credentials;
using Kautuka.Gateway.Infrastructure.Database;
using Kautuka.Gateway.Infrastructure.Sandbox;
using Kautuka.Manager.Protos;
using Kautuka.Protos;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Xunit.Sdk;
using Xunit.v3;

[assembly: Parallelization(Mode = ParallelMode.None)]

namespace Kautuka.ApiFlowTests;

public sealed class GatewayApiFlowTests
{
    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private const string agentAlphaId = "agent-alpha";
    private const string agentBravoId = "agent-bravo";
    private const string agentTargetType = "agent";

    [Fact]
    public async Task BlankSystemFlowCreatesCatalogAndChatsThroughApi()
    {
        await using var fixture = await ApiFlowFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();

        await fixture.SavePersonaDefinitionsAsync(accessToken);
        await fixture.SaveProviderAsync(accessToken);
        await fixture.SaveModelAsync(accessToken);
        await fixture.SaveConnectorAsync(
            accessToken,
            serverId: "filesystem-mcp",
            envKeys: [],
            enabled: false);
        await fixture.SaveAgentAsync(
            accessToken,
            agentId: agentAlphaId,
            name: "Agent Alpha",
            systemPrompt: "You are Agent Alpha. Reply briefly and directly.",
            connectorIds: []);
        await fixture.SaveAgentAsync(
            accessToken,
            agentId: agentBravoId,
            name: "Agent Bravo",
            systemPrompt: "You are Agent Bravo. Reply briefly and directly.",
            connectorIds: ["filesystem-mcp"]);
        await fixture.SaveAgentPersonaProfileAsync(
            accessToken,
            agentAlphaId,
            temperature: 0.5);
        await fixture.SaveAgentPersonaProfileAsync(
            accessToken,
            agentBravoId,
            temperature: 0.7);

        await fixture.AssertCatalogStateAsync(accessToken);

        const string prompt = "Reply with exactly api-flow-ok.";

        string agentAlphaResponse = await fixture.RunAgentChatAsync(accessToken, agentAlphaId, prompt);
        AssertApiFlowOk(agentAlphaResponse);

        string agentBravoResponse = await fixture.RunAgentChatAsync(accessToken, agentBravoId, prompt);
        AssertApiFlowOk(agentBravoResponse);
    }

    [Fact]
    public async Task ConnectorCredentialEndpointStillFiltersUndeclaredKeys()
    {
        await using var fixture = await ApiFlowFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();

        await fixture.SaveConnectorAsync(
            accessToken,
            serverId: "gmail-mcp",
            envKeys: ["GOOGLE_CLIENT_ID"],
            enabled: false);

        using JsonDocument putResponse = await fixture.SendJsonAsync(
            HttpMethod.Put,
            "/api/credentials/connector/gmail-mcp",
            new
            {
                entries = new Dictionary<string, string>
                {
                    ["GOOGLE_CLIENT_ID"] = "cid",
                    ["UNDECLARED_KEY"] = "blocked",
                },
            },
            accessToken: accessToken).ConfigureAwait(true);

        Assert.Contains("1 credential", putResponse.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);

        using JsonDocument statusResponse = await fixture.SendJsonAsync(
            HttpMethod.Get,
            "/api/credentials/connector/gmail-mcp",
            body: null,
            accessToken: accessToken).ConfigureAwait(true);

        string[] configured = [.. statusResponse.RootElement.GetProperty("configuredKeys").EnumerateArray().Select(static item => item.GetString() ?? string.Empty)];
        Assert.Contains("GOOGLE_CLIENT_ID", configured, StringComparer.Ordinal);
        Assert.DoesNotContain("UNDECLARED_KEY", configured, StringComparer.Ordinal);
    }

    private static void AssertApiFlowOk(string response)
    {
        bool match = Regex.IsMatch(response, @"\bapi[-\s]?flow[-\s]?ok\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match)
        {
            throw new XunitException($"Expected response to contain 'api-flow-ok' semantic marker, but got: {response}");
        }
    }

    private sealed class ApiFlowFixture : IAsyncDisposable
    {
        private const string providerId = "ollama-local";
        private const string modelId = "gemma4-e4b";
        private const string modelName = "gemma4:e4b";
        private static readonly Uri ollamaBaseUri = new UriBuilder(Uri.UriSchemeHttp, "localhost", 11434).Uri;
        private static Task? ollamaReadinessTask;
        private static readonly Lock ollamaReadinessLock = new();

        private readonly string root;
        private readonly string? previousEnvironment;
        private readonly WebApplication app;

        private ApiFlowFixture(string root, string? previousEnvironment, WebApplication app)
        {
            this.root = root;
            this.previousEnvironment = previousEnvironment;
            this.app = app;
            Client = app.GetTestClient();
        }

        public HttpClient Client { get; }

        public static async Task<ApiFlowFixture> CreateAsync()
        {
            await EnsureOllamaReadyOnceAsync().ConfigureAwait(true);

            string root = Path.Combine(Path.GetTempPath(), "kautuka-api-flow-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            string sqlitePath = Path.Combine(root, "gateway.db");
            string? previousEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", Environments.Development);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
            });
            builder.Logging.ClearProviders();

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
            builder.Services.AddSingleton<IWorkspaceRuntimeTool>(new NoOpWorkspaceRuntimeTool());
            var workspaceGrpcService = new TestWorkspaceGrpcService();
            builder.Services.AddSingleton<IManagerGrpcService>(workspaceGrpcService);
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
            await app.StartAsync().ConfigureAwait(true);

            return new ApiFlowFixture(root, previousEnvironment, app);
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
            // First-run: create the bootstrap admin account.
            using JsonDocument setupDoc = await SendJsonAsync(
                HttpMethod.Post,
                "/api/auth/setup",
                new { username = "admin", password = "Testing1", setupToken = app.Services.GetRequiredService<SetupPossession>().Peek() },
                authenticated: false).ConfigureAwait(true);

            JsonDocument json = await SendJsonAsync(
                HttpMethod.Post,
                "/api/auth/login",
                new
                {
                    username = "admin",
                    password = "Testing1",
                },
                authenticated: false).ConfigureAwait(true);

            return json.RootElement.GetProperty("accessToken").GetString()
                ?? throw new XunitException("Login response did not include an access token.");
        }

        public async Task SaveProviderAsync(string accessToken)
        {
            using JsonDocument _ = await SendJsonAsync(
                HttpMethod.Post,
                "/api/settings/providers/save",
                new
                {
                    id = providerId,
                    type = "ollama",
                    settings = new
                    {
                        base_url = ollamaBaseUri.ToString().TrimEnd('/'),
                        default_model = modelName,
                    },
                },
                accessToken).ConfigureAwait(true);
        }

        public async Task SavePersonaDefinitionsAsync(string accessToken)
        {
            using JsonDocument _ = await SendJsonAsync(
                HttpMethod.Put,
                "/api/settings/persona/definitions",
                new
                {
                    styles = new[] { "professional", "helper" },
                    tones = new[] { "neutral", "friendly" },
                    verbosity_levels = new[] { "concise", "detailed" },
                },
                accessToken).ConfigureAwait(true);
        }

        public async Task SaveModelAsync(string accessToken)
        {
            using JsonDocument _ = await SendJsonAsync(
                HttpMethod.Post,
                "/api/settings/models/save",
                new
                {
                    id = modelId,
                    providerId = providerId,
                    modelName = modelName,
                    settings = new { },
                },
                accessToken).ConfigureAwait(true);
        }

        public async Task SaveConnectorAsync(string accessToken, string serverId, string[] envKeys, bool enabled)
        {
            using JsonDocument _ = await SendJsonAsync(
                HttpMethod.Post,
                $"/api/connectors/{serverId}",
                new
                {
                    name = serverId,
                    description = $"Provisioned by API flow test for {serverId}.",
                    iconUrl = string.Empty,
                    presetId = (string?)null,
                    transport = "http",
                    httpUrl = "https://example.invalid/mcp",
                    localCommand = (string?)null,
                    localArgs = Array.Empty<string>(),
                    containerImage = (string?)null,
                    containerCommand = (string?)null,
                    containerArgs = Array.Empty<string>(),
                    containerSidecarName = (string?)null,
                    envKeys,
                    enabled,
                },
                accessToken).ConfigureAwait(true);
        }

        public async Task SaveAgentAsync(string accessToken, string agentId, string name, string systemPrompt, string[] connectorIds)
        {
            using JsonDocument _ = await SendJsonAsync(
                HttpMethod.Put,
                $"/api/settings/agents/{agentId}",
                new
                {
                    name,
                    modelId = modelId,
                    systemPrompt = systemPrompt,
                    allowCommandExecution = false,
                    mcpBindings = connectorIds.Select(static connectorId => new
                    {
                        connectorId,
                        context = "OwnContext",
                    }).ToArray(),
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
                accessToken).ConfigureAwait(true);
        }

        public async Task SaveAgentPersonaProfileAsync(string accessToken, string agentId, double temperature)
        {
            using JsonDocument _ = await SendJsonAsync(
                HttpMethod.Put,
                $"/api/settings/persona/profiles/{agentId}",
                new
                {
                    temperature,
                    style = "professional",
                    tone = "neutral",
                    verbosity = "concise",
                },
                accessToken).ConfigureAwait(true);
        }

        public async Task AssertCatalogStateAsync(string accessToken)
        {
            using JsonDocument providers = await SendJsonAsync(HttpMethod.Get, "/api/settings/providers", null, accessToken: accessToken).ConfigureAwait(true);
            Assert.Contains(
                providers.RootElement.GetProperty("providers").EnumerateArray(),
                item => string.Equals(item.GetProperty("id").GetString(), providerId, StringComparison.Ordinal));

            using JsonDocument models = await SendJsonAsync(HttpMethod.Get, "/api/settings/models", null, accessToken: accessToken).ConfigureAwait(true);
            Assert.Contains(
                models.RootElement.GetProperty("models").EnumerateArray(),
                item => string.Equals(item.GetProperty("id").GetString(), modelId, StringComparison.Ordinal)
                    && string.Equals(item.GetProperty("modelName").GetString(), modelName, StringComparison.Ordinal));

            using JsonDocument agents = await SendJsonAsync(HttpMethod.Get, "/api/settings/agents", null, accessToken: accessToken).ConfigureAwait(true);
            JsonElement agentBravo = agents.RootElement
                .GetProperty("agents")
                .EnumerateArray()
                .First(item => string.Equals(item.GetProperty("id").GetString(), agentBravoId, StringComparison.Ordinal));

            Assert.Contains(
                agentBravo.GetProperty("mcpBindings").EnumerateArray(),
                item => string.Equals(item.GetProperty("connectorId").GetString(), "filesystem-mcp", StringComparison.Ordinal)
                    && string.Equals(item.GetProperty("context").GetString(), "OwnContext", StringComparison.Ordinal));

            JsonElement approvalConfig = agentBravo.GetProperty("approvalConfig");
            if (approvalConfig.TryGetProperty("requireMcpIds", out JsonElement requireMcpIds))
            {
                Assert.DoesNotContain(requireMcpIds.EnumerateArray(), item => string.Equals(item.GetString(), "filesystem-mcp", StringComparison.Ordinal));
            }

            using JsonDocument definitions = await SendJsonAsync(HttpMethod.Get, "/api/settings/persona/definitions", null, accessToken: accessToken).ConfigureAwait(true);
            Assert.Contains(
                definitions.RootElement.GetProperty("definitions").GetProperty("styles").EnumerateArray(),
                item => string.Equals(item.GetString(), "professional", StringComparison.Ordinal));

            using JsonDocument profiles = await SendJsonAsync(HttpMethod.Get, "/api/settings/persona/profiles", null, accessToken: accessToken).ConfigureAwait(true);
            JsonElement profileMap = profiles.RootElement.GetProperty("profiles");
            Assert.Equal(0.5d, profileMap.GetProperty(agentAlphaId).GetProperty("temperature").GetDouble());
            Assert.Equal(0.7d, profileMap.GetProperty(agentBravoId).GetProperty("temperature").GetDouble());

        }

        public async Task<string> RunAgentChatAsync(string accessToken, string agentId, string prompt)
        {
            string sessionId = await CreateSessionAsync(accessToken, agentTargetType, agentId).ConfigureAwait(true);
            using JsonDocument intent = await SendJsonAsync(
                HttpMethod.Post,
                $"/api/narad/v1/agents/{agentId}/sessions/{sessionId}/messages",
                new
                {
                    message = prompt,
                    expectedRevision = 0,
                },
                accessToken).ConfigureAwait(true);

            string status = intent.RootElement.GetProperty("status").GetString() ?? string.Empty;
            if (!string.Equals(status, "accepted", StringComparison.Ordinal))
            {
                throw new XunitException($"Narad message intent returned '{status}' instead of accepted.");
            }

            return await StreamNaradSessionAsync(accessToken, agentId, sessionId, afterSessionSequence: 0).ConfigureAwait(true);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync().ConfigureAwait(true);

            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previousEnvironment);
            SqliteConnection.ClearAllPools();

            for (int attempt = 0; attempt < 10 && Directory.Exists(root); attempt++)
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException) when (attempt < 9)
                {
                    SqliteConnection.ClearAllPools();
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    await Task.Delay(100 * (attempt + 1)).ConfigureAwait(true);
                }
            }
        }

        private async Task<string> CreateSessionAsync(string accessToken, string targetType, string targetId)
        {
            JsonDocument session = await SendJsonAsync(
                HttpMethod.Post,
                "/api/sessions",
                new
                {
                    targetType,
                    targetId,
                },
                accessToken).ConfigureAwait(true);

            return session.RootElement.GetProperty("id").GetString()
                ?? throw new XunitException("Session response did not include an id.");
        }

        private async Task<string> StreamNaradSessionAsync(
            string accessToken,
            string agentId,
            string sessionId,
            long afterSessionSequence)
        {
            using HttpRequestMessage request = new(
                HttpMethod.Get,
                $"/api/narad/v1/sessions/{sessionId}/stream?agentId={Uri.EscapeDataString(agentId)}&afterSessionSequence={afterSessionSequence}&hydrate=true");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            // Live Narad streams remain open after the run ends. Cancel reading once we see a
            // terminal root run (or hit the time budget) instead of waiting for EOF forever.
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            using HttpResponseMessage response = await Client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(true);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                string body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(true);
                throw new XunitException($"Narad stream for session '{sessionId}' failed with {(int)response.StatusCode}: {body}");
            }

            await using Stream responseStream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(true);
            using var reader = new StreamReader(responseStream);
            var text = new StringBuilder();
            var assistantMessageIds = new HashSet<string>(StringComparer.Ordinal);
            string? runError = null;
            string? dataLine = null;
            string? line;
            try
            {
                while ((line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(true)) is not null)
                {
                    if (line.Length == 0)
                    {
                        if (dataLine is null)
                        {
                            continue;
                        }

                        using JsonDocument evt = JsonDocument.Parse(dataLine);
                        dataLine = null;
                        if (!evt.RootElement.TryGetProperty("eventId", out _))
                        {
                            continue;
                        }

                        string type = evt.RootElement.GetProperty("type").GetString() ?? string.Empty;
                        switch (type)
                        {
                            case "message.text.started":
                                if (evt.RootElement.TryGetProperty("messageId", out JsonElement startMessageId)
                                    && startMessageId.ValueKind == JsonValueKind.String)
                                {
                                    assistantMessageIds.Add(startMessageId.GetString()!);
                                }
                                break;
                            case "message.text.delta":
                                if (evt.RootElement.TryGetProperty("messageId", out JsonElement contentMessageId)
                                    && contentMessageId.ValueKind == JsonValueKind.String
                                    && assistantMessageIds.Contains(contentMessageId.GetString()!))
                                {
                                    text.Append(evt.RootElement.GetProperty("delta").GetString());
                                }
                                break;
                            case "run.error":
                                runError = evt.RootElement.TryGetProperty("message", out JsonElement message)
                                    ? message.GetString()
                                    : evt.RootElement.TryGetProperty("content", out JsonElement content)
                                        ? content.GetString()
                                        : "Unknown Narad stream error.";
                                goto StreamComplete;
                            case "run.finished":
                            case "run.cancelled":
                                goto StreamComplete;
                        }

                        continue;
                    }

                    if (line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        dataLine = line.Length > 5 ? line[5..].TrimStart() : string.Empty;
                    }
                }
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                throw new XunitException(
                    $"Narad stream for session '{sessionId}' timed out after 2 minutes without a terminal run event. Partial text: '{text}'.");
            }

        StreamComplete:
            if (!string.IsNullOrWhiteSpace(runError))
            {
                throw new XunitException($"Narad stream for session '{sessionId}' failed: {runError}");
            }

            string finalText = text.ToString().Trim();
            if (string.IsNullOrWhiteSpace(finalText))
            {
                throw new XunitException($"Narad stream for session '{sessionId}' completed without assistant text.");
            }

            return finalText;
        }

        public async Task<JsonDocument> SendJsonAsync(HttpMethod method, string path, object? body, string? accessToken = null, bool authenticated = true)
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

            using HttpResponseMessage response = await Client.SendAsync(request).ConfigureAwait(true);
            string content = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
            if (!response.IsSuccessStatusCode)
            {
                throw new XunitException($"Request to '{path}' failed with {(int)response.StatusCode}: {content}");
            }

            return JsonDocument.Parse(content);
        }

        private static Task EnsureOllamaReadyOnceAsync()
        {
            lock (ollamaReadinessLock)
            {
                ollamaReadinessTask ??= EnsureOllamaReadyCoreAsync();
                return ollamaReadinessTask;
            }
        }

        private static async Task EnsureOllamaReadyCoreAsync()
        {
            using var client = new HttpClient
            {
                BaseAddress = ollamaBaseUri,
                Timeout = TimeSpan.FromSeconds(10),
            };

            HttpResponseMessage response;
            try
            {
                response = await client.GetAsync(new Uri("/api/tags", UriKind.Relative)).ConfigureAwait(true);
            }
            catch (HttpRequestException ex) when (ex.InnerException is System.Net.Sockets.SocketException)
            {
                Assert.Skip($"Local Ollama is not reachable at {ollamaBaseUri} — skipping test. Run Ollama locally to enable this test.");
                return; // unreachable; satisfies compiler
            }
            catch (TaskCanceledException)
            {
                Assert.Skip($"Local Ollama did not respond at {ollamaBaseUri} before the readiness timeout elapsed — skipping test. Run Ollama locally to enable this test.");
                return; // unreachable; satisfies compiler
            }

            using (response)
            {
                string content = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
                if (!response.IsSuccessStatusCode)
                {
                    throw new XunitException($"Local Ollama probe failed with {(int)response.StatusCode}: {content}");
                }

                using JsonDocument tags = JsonDocument.Parse(content);
                bool modelFound = tags.RootElement.TryGetProperty("models", out JsonElement models)
                    && models.EnumerateArray().Any(model => string.Equals(model.GetProperty("name").GetString(), modelName, StringComparison.Ordinal));

                if (!modelFound)
                {
                    Assert.Skip($"Local Ollama is reachable, but model '{modelName}' is not installed — skipping test.");
                }
            }
        }

        private sealed class TestWorkspaceGrpcService : IManagerGrpcService
        {
            public Task<WorkspaceBoolResponse> PingAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(new WorkspaceBoolResponse { Ok = true, Message = request.Message });

            public Task<ManagerStatusResponse> GetHostStatusAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(new ManagerStatusResponse
                {
                    Ok = true,
                    CoreReady = true,
                    AgentsPending = false,
                    AgentsReady = true,
                    Lifecycle = "reconciled",
                    Message = "api-flow-test",
                });

            public Task<ManagerRuntimeContainersResponse> ListRuntimeContainersAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(new ManagerRuntimeContainersResponse { Ok = true, Message = "api-flow-test" });

            public Task<WorkspaceContextResponse> DescribeWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(new WorkspaceContextResponse
                {
                    Ok = true,
                    Message = "api-flow-test",
                    Context = new WorkspaceContextMessage
                    {
                        ScopeType = "agent",
                        ScopeId = "api-flow",
                        UserId = string.Empty,
                        HostWorkspacePath = "/tmp/kautuka-apiflow",
                        ContainerWorkspacePath = "/workspace",
                        Image = "kautuka-base:latest",
                        DefaultTimeoutSeconds = 120,
                    },
                });

            public Task<WorkspaceContextResponse> RebuildWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
                => DescribeWorkspaceAsync(request, cancellationToken);

            public Task<WorkspaceDeleteResponse> DeleteWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(new WorkspaceDeleteResponse { Deleted = true, Message = "api-flow-test" });

            public Task<WorkspaceSandboxResponse> SpawnSandboxAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
                => throw new NotSupportedException();

            public Task<WorkspaceSandboxResponse> InspectSandboxAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
                => throw new NotSupportedException();

            public Task<WorkspaceExecutionResponse> ExecAsync(WorkspaceExecRequest request, CancellationToken cancellationToken = default)
                => throw new NotSupportedException();

            public Task<WorkspaceBoolResponse> StopSandboxAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(new WorkspaceBoolResponse { Ok = true, Message = "api-flow-test" });

            public Task<WorkspaceBoolResponse> PrimeWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(new WorkspaceBoolResponse { Ok = true, Message = "api-flow-test" });

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
                => Task.FromResult(new DesiredStateUpdateResponse { Outcome = DesiredStateUpdateOutcome.Applied, Message = "api-flow-test" });
        }

        private sealed class NoOpChannelRegistrationProvisioningService : IChannelRegistrationProvisioningService
        {
            private static readonly ChannelRegistrationProvisioningResult success =
                new(true, "skipped", "Provisioning disabled for API flow test fixture.");

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

        private sealed class FakeCredentialsServiceClient : CredentialsService.CredentialsServiceClient
        {
            private readonly Dictionary<string, string> systemStore = new(StringComparer.Ordinal);
            private readonly Dictionary<string, Dictionary<string, string>> userStore = new(StringComparer.Ordinal);

            public override AsyncUnaryCall<CredentialHealthResponse> GetHealthAsync(
                CredentialHealthRequest request,
                CallOptions options)
                => Wrap(new CredentialHealthResponse
                {
                    State = CredentialHealthState.Ready,
                    ActiveClientCertificateNotAfterUnixSeconds = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds(),
                });

            public override AsyncUnaryCall<CredentialKeyListResponse> ListSystemCredentialKeysAsync(
                ListSystemCredentialKeysRequest request,
                CallOptions options)
            {
                var response = new CredentialKeyListResponse();
                response.Keys.AddRange(systemStore.Keys);
                return Wrap(response);
            }

            public override AsyncUnaryCall<SetCredentialsResponse> SetSystemCredentialsAsync(
                SetSystemCredentialsRequest request,
                CallOptions options)
            {
                foreach ((string key, string value) in request.Entries)
                {
                    systemStore[key] = value;
                }

                return Wrap(new SetCredentialsResponse { UpsertedCount = request.Entries.Count });
            }

            public override AsyncUnaryCall<CredentialMapResponse> GetSystemCredentialAsync(
                GetSystemCredentialRequest request,
                CallOptions options)
            {
                var response = new CredentialMapResponse();
                if (systemStore.TryGetValue(request.Key, out string? value))
                {
                    response.Entries[request.Key] = value;
                }

                return Wrap(response);
            }

            public override AsyncUnaryCall<CredentialMapResponse> GetResolvedCredentialsAsync(
                GetResolvedCredentialsRequest request,
                CallOptions options)
            {
                var response = new CredentialMapResponse();

                foreach (string key in request.RequiredKeys)
                {
                    if (systemStore.TryGetValue(key, out string? value))
                    {
                        response.Entries[key] = value;
                    }

                    if (userStore.TryGetValue(request.UserId, out Dictionary<string, string>? userEntries)
                        && userEntries.TryGetValue(key, out string? userValue))
                    {
                        response.Entries[key] = userValue;
                    }
                }

                return Wrap(response);
            }

            public override AsyncUnaryCall<CredentialKeyListResponse> ListUserCredentialKeysAsync(
                ListUserCredentialKeysRequest request,
                CallOptions options)
            {
                var response = new CredentialKeyListResponse();
                if (userStore.TryGetValue(request.UserId, out Dictionary<string, string>? entries))
                {
                    response.Keys.AddRange(entries.Keys);
                }

                return Wrap(response);
            }

            public override AsyncUnaryCall<SetCredentialsResponse> SetUserCredentialsAsync(
                SetUserCredentialsRequest request,
                CallOptions options)
            {
                if (!userStore.TryGetValue(request.UserId, out Dictionary<string, string>? entries))
                {
                    entries = new Dictionary<string, string>(StringComparer.Ordinal);
                    userStore[request.UserId] = entries;
                }

                foreach ((string key, string value) in request.Entries)
                {
                    entries[key] = value;
                }

                return Wrap(new SetCredentialsResponse { UpsertedCount = request.Entries.Count });
            }

            public override AsyncUnaryCall<DeleteCredentialResponse> DeleteUserCredentialAsync(
                DeleteUserCredentialRequest request,
                CallOptions options)
            {
                bool deleted = userStore.TryGetValue(request.UserId, out Dictionary<string, string>? entries)
                    && entries.Remove(request.Key);

                return Wrap(new DeleteCredentialResponse { Deleted = deleted });
            }

            public override AsyncUnaryCall<DeleteCredentialResponse> DeleteSystemCredentialAsync(
                DeleteSystemCredentialRequest request,
                CallOptions options)
            {
                bool deleted = systemStore.Remove(request.Key);
                return Wrap(new DeleteCredentialResponse { Deleted = deleted });
            }

            private static AsyncUnaryCall<T> Wrap<T>(T response) where T : class
                => new(
                    responseAsync: Task.FromResult(response),
                    responseHeadersAsync: Task.FromResult(new Metadata()),
                    getStatusFunc: () => Status.DefaultSuccess,
                    getTrailersFunc: () => new Metadata(),
                    disposeAction: static () => { });
        }
    }

    private sealed class NoOpWorkspaceRuntimeTool : IWorkspaceRuntimeTool
    {
        WorkspaceRuntimeContext IWorkspaceRuntimeTool.DescribeAgentWorkspace(string agentId, string? userId) => CreateContext(agentTargetType, agentId, userId);

        WorkspaceRuntimeContext IWorkspaceRuntimeTool.RebuildAgentWorkspace(string agentId, string? userId) => CreateContext(agentTargetType, agentId, userId);

        bool IWorkspaceRuntimeTool.DeleteAgentWorkspace(string agentId) => true;

        Task<WorkspaceSandboxSession> IWorkspaceRuntimeTool.SpawnAgentSandboxAsync(string agentId, string? userId, CancellationToken cancellationToken)
            => Task.FromResult(CreateSession(agentTargetType, agentId, userId));

        Task<WorkspaceCommandResult> IWorkspaceRuntimeTool.RunAgentShellAsync(string agentId, string command, string? userId, int? timeoutSeconds, CancellationToken cancellationToken)
            => Task.FromResult(new WorkspaceCommandResult("completed", 0, string.Empty, string.Empty));

        Task<WorkspaceSandboxSession> IWorkspaceRuntimeTool.InspectAgentSandboxAsync(string agentId, string? userId, CancellationToken cancellationToken)
            => Task.FromResult(CreateSession(agentTargetType, agentId, userId));

        Task IWorkspaceRuntimeTool.CloseAgentSandboxAsync(string agentId, string? userId, CancellationToken cancellationToken) => Task.CompletedTask;

        private static WorkspaceRuntimeContext CreateContext(string scopeType, string scopeId, string? userId)
            => new(scopeType, scopeId, userId, Path.GetTempPath(), "/workspace", "/workspace", "kautuka-base:latest", 120, [], new Dictionary<string, string>());

        private static WorkspaceSandboxSession CreateSession(string scopeType, string scopeId, string? userId)
            => new($"{scopeType}-{scopeId}", false, scopeType, scopeId, userId);
    }
}
