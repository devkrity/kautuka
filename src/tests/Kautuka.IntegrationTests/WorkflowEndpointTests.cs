using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Auth;
using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Domain.Workflows;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

using Xunit.Sdk;

namespace Kautuka.IntegrationTests;

public sealed class WorkflowEndpointTests
{
    [Fact]
    public async Task TestStepUsesTemporaryWorkflowTestSessionAndCreatesNoWorkflowRun()
    {
        var chat = new WorkflowTestChatGateway();
        await using var fixture = await WorkflowApiFixture.CreateAsync(chat);
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        using JsonDocument created = await fixture.SendJsonAsync(HttpMethod.Post, "/api/workflows", SampleDefinition("Test step"), accessToken);
        string workflowId = created.RootElement.GetProperty("id").GetString()!;

        using HttpResponseMessage response = await fixture.SendAsync(HttpMethod.Post, $"/api/workflows/{workflowId}/steps/review/test", new { input = new { title = "Draft" } }, accessToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("The step could not finish. Try again.", body.RootElement.GetProperty("error").GetString());
        Assert.Equal("workflow_test", chat.Request!.Value.GetProperty("source").GetString());
        Assert.False(chat.Request.Value.GetProperty("memoryWrites").GetBoolean());
        Assert.DoesNotContain(fixture.Services.GetRequiredService<IGatewaySessionStore>().ListRecentSessions(
            fixture.Services.GetRequiredService<GatewayState>().Users["admin"].Id, 100), session => session.Id == chat.SessionId);
        Assert.Empty(fixture.Services.GetRequiredService<IGatewayApprovalStore>().ListBySession(chat.SessionId!));
        Assert.Empty(fixture.Services.GetRequiredService<IGatewayWorkflowStore>().ListInstances(
            fixture.Services.GetRequiredService<GatewayState>().Users["admin"].Id, workflowId, 50, 0));
    }

    [Fact]
    public async Task TestStepCanUseSystemAgentWithoutAnOwner()
    {
        var chat = new WorkflowTestChatGateway();
        await using var fixture = await WorkflowApiFixture.CreateAsync(chat);
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        GatewayState state = fixture.Services.GetRequiredService<GatewayState>();
        state.Agents["agent-a"] = state.Agents["agent-a"] with { OwnerUserId = string.Empty, Scope = "system" };
        using JsonDocument created = await fixture.SendJsonAsync(HttpMethod.Post, "/api/workflows", SampleDefinition("System agent test"), accessToken);
        string workflowId = created.RootElement.GetProperty("id").GetString()!;

        using HttpResponseMessage response = await fixture.SendAsync(HttpMethod.Post, $"/api/workflows/{workflowId}/steps/review/test", new { input = new { title = "Draft" } }, accessToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(chat.Request);
    }

    [Fact]
    public async Task StoppingPausedWorkflowResolvesItsOpenReviewAsStopped()
    {
        await using var fixture = await WorkflowApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        using JsonDocument created = await fixture.SendJsonAsync(HttpMethod.Post, "/api/workflows", SampleDefinition("Paused review"), accessToken);
        string workflowId = created.RootElement.GetProperty("id").GetString()!;
        IGatewayWorkflowStore workflows = fixture.Services.GetRequiredService<IGatewayWorkflowStore>();
        IGatewayApprovalStore approvals = fixture.Services.GetRequiredService<IGatewayApprovalStore>();
        GatewayWorkflowDefinition definition = workflows.GetDefinition(workflowId)!;
        string now = DateTimeOffset.UtcNow.ToString("O");
        var attempt = new GatewayWorkflowStepAttempt("review-attempt", "paused-instance", "review", 1,
            "review-run", "review-session", WorkflowStepAttemptStatuses.Finished, null, now, now, now);
        var instance = new GatewayWorkflowInstance("paused-instance", workflowId, definition.Revision,
            definition.OwnerUserId, null, WorkflowInstanceStatuses.Paused, false, "review", new JsonObject(),
            "hash", 0, definition, [attempt], null, null, now, now, now, null);
        workflows.CreateInstance(instance);
        approvals.CreateRequest(new GatewayToolApprovalRequest(
            "review-approval", "review-session", "review-run", null, "agent-a", definition.OwnerUserId,
            "review-call", "workflow_review", "workflow_review", null,
            "{\"kind\":\"workflow_review\"}", now, null, null, null, null, null,
            Kind: "workflow_review", WorkflowId: workflowId, InstanceId: instance.Id, NodeId: "review"));

        using HttpResponseMessage response = await fixture.SendAsync(HttpMethod.Post,
            $"/api/workflows/{workflowId}/instances/{instance.Id}/stop", null, accessToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("stopped", approvals.GetById("review-approval")?.Decision);
        Assert.Equal(WorkflowInstanceStatuses.Cancelled, workflows.GetInstance(instance.Id)?.Status);
    }

    [Fact]
    public async Task TestStepRefusesExternalSideEffectsWithoutStartingWorkflow()
    {
        await using var fixture = await WorkflowApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        using JsonDocument created = await fixture.SendJsonAsync(HttpMethod.Post, "/api/workflows", SampleDefinition("External step", sideEffects: "external"), accessToken);
        string workflowId = created.RootElement.GetProperty("id").GetString()!;

        using HttpResponseMessage response = await fixture.SendAsync(HttpMethod.Post, $"/api/workflows/{workflowId}/steps/review/test", new { input = new { title = "Draft" } }, accessToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("This step does real things. Try it in a real run.", body.RootElement.GetProperty("error").GetString());
        using JsonDocument instances = await fixture.SendJsonAsync(HttpMethod.Get, $"/api/workflows/{workflowId}/instances", null, accessToken);
        Assert.Empty(instances.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task TestStepStopsPendingToolApprovalBeforeReturningPlainToolMessage()
    {
        var chat = new WorkflowTestChatGateway { EmitRunError = false };
        await using var fixture = await WorkflowApiFixture.CreateAsync(chat);
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        using JsonDocument created = await fixture.SendJsonAsync(HttpMethod.Post, "/api/workflows", SampleDefinition("Tool test"), accessToken);
        string workflowId = created.RootElement.GetProperty("id").GetString()!;
        IGatewayApprovalStore approvalStore = fixture.Services.GetRequiredService<IGatewayApprovalStore>();
        GatewayState state = fixture.Services.GetRequiredService<GatewayState>();
        chat.BeforeRun = (sessionId, runId) => approvalStore.CreateRequest(new GatewayToolApprovalRequest(
            Id: "test-tool-approval",
            SessionId: sessionId,
            RunId: runId,
            CheckpointId: null,
            AgentId: "agent-a",
            UserId: state.Users["admin"].Id,
            ToolCallId: "tool-call-1",
            ToolName: "mail_send",
            ToolType: "connector",
            McpServerId: null,
            ArgumentsJson: "{}",
            RequestedAt: DateTimeOffset.UtcNow.ToString("O"),
            Decision: null,
            DecidedBy: null,
            DecidedAt: null,
            Reason: null,
            ExpiresAt: null));

        using HttpResponseMessage response = await fixture.SendAsync(HttpMethod.Post, $"/api/workflows/{workflowId}/steps/review/test", new { input = new { title = "Draft" } }, accessToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("This step wants to use a tool (mail_send). Tests stop before any tool runs.", body.RootElement.GetProperty("error").GetString());
        Assert.DoesNotContain(approvalStore.ListBySession(chat.SessionId!), request => request.Decision is null);
    }

    [Fact]
    public async Task WorkflowCanDeleteBeforeFirstRunOrArchiveAndRestoreAfterRuns()
    {
        await using var fixture = await WorkflowApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();

        using JsonDocument unrun = await fixture.SendJsonAsync(HttpMethod.Post, "/api/workflows", SampleDefinition("Unrun workflow"), accessToken);
        string unrunId = unrun.RootElement.GetProperty("id").GetString()!;
        using HttpResponseMessage deleted = await fixture.SendAsync(HttpMethod.Delete, $"/api/workflows/{unrunId}", null, accessToken);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        using JsonDocument created = await fixture.SendJsonAsync(HttpMethod.Post, "/api/workflows", SampleDefinition("Workflow with runs"), accessToken);
        string workflowId = created.RootElement.GetProperty("id").GetString()!;
        using JsonDocument run = await fixture.SendJsonAsync(HttpMethod.Post, $"/api/workflows/{workflowId}/instances", new { input = new { title = "Saved" } }, accessToken);
        string runId = run.RootElement.GetProperty("id").GetString()!;

        using HttpResponseMessage deleteWithRun = await fixture.SendAsync(HttpMethod.Delete, $"/api/workflows/{workflowId}", null, accessToken);
        Assert.Equal(HttpStatusCode.Conflict, deleteWithRun.StatusCode);
        using JsonDocument deleteError = JsonDocument.Parse(await deleteWithRun.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("This workflow has runs, so it can't be deleted. Archive it instead.", deleteError.RootElement.GetProperty("detail").GetString());

        using HttpResponseMessage archiveWhileRunning = await fixture.SendAsync(HttpMethod.Post, $"/api/workflows/{workflowId}/archive", null, accessToken);
        Assert.Equal(HttpStatusCode.Conflict, archiveWhileRunning.StatusCode);
        using JsonDocument archiveError = JsonDocument.Parse(await archiveWhileRunning.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("This workflow has a run in progress. Stop it first.", archiveError.RootElement.GetProperty("detail").GetString());
        await fixture.SendJsonAsync(HttpMethod.Post, $"/api/workflows/{workflowId}/instances/{runId}/stop", null, accessToken);

        using HttpResponseMessage archived = await fixture.SendAsync(HttpMethod.Post, $"/api/workflows/{workflowId}/archive", null, accessToken);
        Assert.Equal(HttpStatusCode.NoContent, archived.StatusCode);
        using JsonDocument active = await fixture.SendJsonAsync(HttpMethod.Get, "/api/workflows", null, accessToken);
        Assert.DoesNotContain(active.RootElement.EnumerateArray(), item => item.GetProperty("id").GetString() == workflowId);
        using JsonDocument archiveList = await fixture.SendJsonAsync(HttpMethod.Get, "/api/workflows?archived=true", null, accessToken);
        Assert.Contains(archiveList.RootElement.EnumerateArray(), item => item.GetProperty("id").GetString() == workflowId);
        using JsonDocument pastRuns = await fixture.SendJsonAsync(HttpMethod.Get, $"/api/workflows/{workflowId}/instances", null, accessToken);
        Assert.Contains(pastRuns.RootElement.EnumerateArray(), item => item.GetProperty("id").GetString() == runId);

        using HttpResponseMessage startArchived = await fixture.SendAsync(HttpMethod.Post, $"/api/workflows/{workflowId}/instances", new { input = new { title = "Again" } }, accessToken);
        Assert.Equal(HttpStatusCode.Conflict, startArchived.StatusCode);
        using JsonDocument startError = JsonDocument.Parse(await startArchived.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("This workflow is archived. Restore it to run it.", startError.RootElement.GetProperty("detail").GetString());

        IGatewayWorkflowStore workflowStore = fixture.Services.GetRequiredService<IGatewayWorkflowStore>();
        WorkflowInstanceLauncher launcher = fixture.Services.GetRequiredService<WorkflowInstanceLauncher>();
        string ownerId = fixture.Services.GetRequiredService<GatewayState>().Users["admin"].Id;
        WorkflowInstanceStartResult alternateStart = launcher.Start(
            workflowStore.GetDefinition(workflowId)!,
            ownerId,
            new JsonObject(),
            "alternate-start");
        Assert.False(alternateStart.Succeeded);
        Assert.Equal(HttpStatusCode.Conflict, (HttpStatusCode)alternateStart.StatusCode!.Value);
        Assert.Equal("This workflow is archived. Restore it to run it.", alternateStart.Error);

        using HttpResponseMessage restored = await fixture.SendAsync(HttpMethod.Post, $"/api/workflows/{workflowId}/unarchive", null, accessToken);
        Assert.Equal(HttpStatusCode.NoContent, restored.StatusCode);
        using JsonDocument activeAfterRestore = await fixture.SendJsonAsync(HttpMethod.Get, "/api/workflows", null, accessToken);
        Assert.Contains(activeAfterRestore.RootElement.EnumerateArray(), item => item.GetProperty("id").GetString() == workflowId);
    }

    [Fact]
    public async Task WorkflowCatalogAndInstanceRoundTripOnDedicatedApi()
    {
        await using var fixture = await WorkflowApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();

        using JsonDocument created = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/workflows",
            SampleDefinition("Review draft"),
            accessToken);

        string workflowId = created.RootElement.GetProperty("id").GetString()
            ?? throw new XunitException("Workflow creation did not return an id.");
        Assert.Equal("Review draft", created.RootElement.GetProperty("name").GetString());
        Assert.Equal("review", created.RootElement.GetProperty("nodes")[0].GetProperty("nodeId").GetString());

        using JsonDocument listed = await fixture.SendJsonAsync(HttpMethod.Get, "/api/workflows", null, accessToken);
        Assert.Contains(listed.RootElement.EnumerateArray(), item =>
            string.Equals(item.GetProperty("id").GetString(), workflowId, StringComparison.Ordinal));

        using JsonDocument started = await fixture.SendJsonAsync(
            HttpMethod.Post,
            $"/api/workflows/{workflowId}/instances",
            new { input = new { title = "Draft" }, correlationKey = "case-1" },
            accessToken);
        string instanceId = started.RootElement.GetProperty("id").GetString()
            ?? throw new XunitException("Instance start did not return an id.");
        Assert.Equal("running", started.RootElement.GetProperty("status").GetString());
        Assert.Equal("review", started.RootElement.GetProperty("currentNodeId").GetString());
        Assert.Equal("Draft", started.RootElement.GetProperty("envelope").GetProperty("title").GetString());
        Assert.False(string.IsNullOrWhiteSpace(started.RootElement.GetProperty("envelopeHash").GetString()));

        using HttpResponseMessage overlap = await fixture.SendAsync(
            HttpMethod.Post,
            $"/api/workflows/{workflowId}/instances",
            new { input = new { title = "Other" }, correlationKey = "case-1" },
            accessToken);
        Assert.True(
            overlap.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.Created,
            $"Overlap start returned {(int)overlap.StatusCode}.");

        using JsonDocument loaded = await fixture.SendJsonAsync(
            HttpMethod.Get,
            $"/api/workflows/{workflowId}/instances/{instanceId}",
            null,
            accessToken);
        Assert.Equal("Draft", loaded.RootElement.GetProperty("envelope").GetProperty("title").GetString());
        Assert.False(string.IsNullOrWhiteSpace(loaded.RootElement.GetProperty("steps")[0].GetProperty("runId").GetString()));

        string[] eventTypes = [];
        bool journalHasRunId = false;
        string? instanceError = null;
        for (int attempt = 0; attempt < 40; attempt++)
        {
            using JsonDocument current = await fixture.SendJsonAsync(
                HttpMethod.Get,
                $"/api/workflows/{workflowId}/instances/{instanceId}",
                null,
                accessToken);
            instanceError = current.RootElement.TryGetProperty("error", out JsonElement errorElement)
                && errorElement.ValueKind == JsonValueKind.String
                ? errorElement.GetString()
                : null;

            using JsonDocument naradEvents = await fixture.SendJsonAsync(
                HttpMethod.Get,
                $"/api/narad/v1/workflows/{workflowId}/instances/{instanceId}/events",
                null,
                accessToken);
            JsonElement[] events = [.. naradEvents.RootElement.GetProperty("events").EnumerateArray()];
            eventTypes = [.. events.Select(item => item.GetProperty("type").GetString() ?? string.Empty)];
            journalHasRunId = events.Any(item => item.TryGetProperty("runId", out _));
            if (eventTypes.Contains("workflow.started", StringComparer.Ordinal))
            {
                break;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.Contains("workflow.started", eventTypes);
        Assert.False(journalHasRunId);
        Assert.DoesNotContain("indexed envelope column", instanceError ?? string.Empty, StringComparison.Ordinal);

        using JsonDocument workflowCapabilities = await fixture.SendJsonAsync(
            HttpMethod.Get,
            $"/api/narad/v1/workflows/{workflowId}/instances/{instanceId}/capabilities",
            null,
            accessToken);
        Assert.True(workflowCapabilities.RootElement.GetProperty("features").GetProperty("workflow").GetBoolean());
        Assert.Contains(
            workflowCapabilities.RootElement.GetProperty("activeProfiles").EnumerateArray().Select(static item => item.GetString()),
            static profile => string.Equals(profile, "narad.workflow/v1", StringComparison.Ordinal));

        using JsonDocument stopped = await fixture.SendJsonAsync(
            HttpMethod.Post,
            $"/api/workflows/{workflowId}/instances/{instanceId}/stop",
            null,
            accessToken);
        Assert.True(stopped.RootElement.GetProperty("stopRequested").GetBoolean());
        string stoppedStatus = stopped.RootElement.GetProperty("status").GetString() ?? string.Empty;
        Assert.True(
            stoppedStatus is "cancelled" or "error" or "finished",
            $"Unexpected stop status '{stoppedStatus}'.");
    }

    [Fact]
    public async Task GovernedWorkflowRejectsUngatedExternalStep()
    {
        await using var fixture = await WorkflowApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();

        using HttpResponseMessage response = await fixture.SendAsync(
            HttpMethod.Post,
            "/api/workflows",
            SampleDefinition("Unsafe", governed: true, requiresReview: false, sideEffects: "external"),
            accessToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SharedSaveWorkflowRequestsIncludingDiamondSaveThroughUpdateEndpoint()
    {
        await using var fixture = await WorkflowApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        using JsonDocument created = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/workflows",
            SampleDefinition("Fixture update target"),
            accessToken);
        string workflowId = created.RootElement.GetProperty("id").GetString()
            ?? throw new XunitException("Workflow creation did not return an id.");

        string fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "workflow-save-request-shapes.json");
        using JsonDocument fixtures = JsonDocument.Parse(
            await File.ReadAllTextAsync(fixturePath, TestContext.Current.CancellationToken));
        int expectedRevision = 2;
        foreach (JsonElement saveRequest in fixtures.RootElement.EnumerateArray())
        {
            using JsonDocument saved = await fixture.SendJsonAsync(
                HttpMethod.Put,
                $"/api/workflows/{workflowId}",
                saveRequest.Clone(),
                accessToken);
            Assert.Equal(expectedRevision++, saved.RootElement.GetProperty("revision").GetInt32());
            if (!saveRequest.GetProperty("name").GetString()!.Contains("diamond", StringComparison.OrdinalIgnoreCase))
                continue;

            JsonElement[] nodes = [.. saved.RootElement.GetProperty("nodes").EnumerateArray()];
            Assert.Equal(new[] { "check", "ask", "auto", "file", "email" }, nodes.Select(node => node.GetProperty("nodeId").GetString()));
            Assert.Equal("ask", nodes[0].GetProperty("switchCases")[0].GetProperty("goTo").GetString());
            Assert.Equal("auto", nodes[0].GetProperty("defaultGoTo").GetString());
            Assert.Equal("file", nodes[1].GetProperty("defaultGoTo").GetString());
            Assert.Equal("file", nodes[2].GetProperty("defaultGoTo").GetString());
        }
    }

    private static object SampleDefinition(
        string name,
        bool governed = false,
        bool requiresReview = false,
        string sideEffects = "none") =>
        new
        {
            name,
            governed,
            inputSchema = SampleSchema(),
            workingSchema = SampleSchema(),
            outputSchema = SampleSchema(),
            nodes = new[]
            {
                new
                {
                    nodeId = "review",
                    agentId = "agent-a",
                    promptTemplate = "Review {{input.title}}",
                    outputKind = "schema",
                    outputSchema = SampleSchema(),
                    maps = new[] { new { from = "/title", to = "/title" } },
                    sideEffects,
                    memoryWrites = false,
                    requiresReview,
                    maxRetries = 0,
                },
            },
        };

    private static object SampleSchema() =>
        new
        {
            type = "object",
            required = new[] { "title" },
            properties = new
            {
                title = new { type = "string" },
            },
        };

    private sealed class WorkflowApiFixture : IAsyncDisposable
    {
        private readonly string root;
        private readonly string? previousEnvironment;
        private readonly string? previousBootstrapPassword;
        private readonly WebApplication app;

        private WorkflowApiFixture(
            string root,
            string? previousEnvironment,
            string? previousBootstrapPassword,
            WebApplication app)
        {
            this.root = root;
            this.previousEnvironment = previousEnvironment;
            this.previousBootstrapPassword = previousBootstrapPassword;
            this.app = app;
            Client = app.GetTestClient();
        }

        public HttpClient Client { get; }
        public IServiceProvider Services => app.Services;

        public static async Task<WorkflowApiFixture> CreateAsync(IChatGatewayService? chat = null)
        {
            string root = Path.Combine(Path.GetTempPath(), "kautuka-workflow-api-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            string sqlitePath = Path.Combine(root, "gateway.db");
            string? previousEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            string? previousBootstrapPassword = Environment.GetEnvironmentVariable("KAUTUKA_BOOTSTRAP_PASSWORD");
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", Environments.Development);
            Environment.SetEnvironmentVariable("KAUTUKA_BOOTSTRAP_PASSWORD", "admin");

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
            if (chat is not null)
            {
                builder.Services.RemoveAll<IChatGatewayService>();
                builder.Services.AddSingleton(chat);
            }
            builder.Services.Configure<HostOptions>(o => o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

            WebApplication app = builder.Build();
            var database = app.Services.GetRequiredService<KautukaDatabase>();
            GatewayAppBootstrap.ConfigureApp(app, database);
            await app.StartAsync();

            return new WorkflowApiFixture(root, previousEnvironment, previousBootstrapPassword, app);
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

            string accessToken = json.RootElement.GetProperty("accessToken").GetString()
                ?? throw new XunitException("Login response did not include an access token.");
            string userId = json.RootElement.GetProperty("user").GetProperty("id").GetString()
                ?? throw new XunitException("Login response did not include a user id.");

            GatewayState state = app.Services.GetRequiredService<GatewayState>();
            PersistOwnedAgent(state, "agent-a", userId, "Workflow Agent");

            return accessToken;
        }

        public async Task<JsonDocument> SendJsonAsync(HttpMethod method, string path, object? body, string? accessToken = null, bool authenticated = true)
        {
            using HttpRequestMessage request = new(method, path);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            if (authenticated)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken ?? throw new ArgumentNullException(nameof(accessToken)));
            }

            using HttpResponseMessage response = await Client.SendAsync(request);
            string content = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new XunitException($"Request to '{path}' failed with {(int)response.StatusCode}: {content}");
            }

            return JsonDocument.Parse(content);
        }

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, string accessToken)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            return await Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();

            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previousEnvironment);
            Environment.SetEnvironmentVariable("KAUTUKA_BOOTSTRAP_PASSWORD", previousBootstrapPassword);
            SqliteTestPoolCleanup.ClearPoolsUnderDirectory(root);

            if (Directory.Exists(root))
            {
                await TestDirectoryCleanup.DeleteDirectoryWithRetryAsync(root).ConfigureAwait(false);
            }
        }

        private static void PersistOwnedAgent(GatewayState state, string agentId, string ownerUserId, string name)
        {
            state.Agents[agentId] = new GatewayAgent(
                agentId,
                name,
                "model-a",
                "system",
                "disabled",
                new JsonObject(),
                new JsonObject(),
                new JsonObject(),
                [],
                ownerUserId,
                DateTimeOffset.UtcNow.ToString("O"));
        }
    }

    private sealed class WorkflowTestChatGateway : IChatGatewayService
    {
        public JsonElement? Request { get; private set; }
        public string? SessionId { get; private set; }
        public bool EmitRunError { get; init; } = true;
        public Action<string, string>? BeforeRun { get; set; }

        public async IAsyncEnumerable<NaradPersistedEvent> StreamChatAsync(JsonElement body, AgentRequestContext? requestContext = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _ = requestContext;
            Request = body;
            SessionId = body.GetProperty("session_id").GetString();
            string runId = body.GetProperty("run_id").GetString()!;
            BeforeRun?.Invoke(SessionId!, runId);
            if (EmitRunError)
                yield return new NaradPersistedEvent("evt-test-error", NaradProtocolVersions.V1, NaradEventTypes.RunError, SessionId!, 1, NaradRecordClasses.Authority, "{}", DateTimeOffset.UtcNow.ToString("O"), RunId: runId);
            await Task.CompletedTask;
        }

        public IAsyncEnumerable<NaradPersistedEvent> StreamChatWithApprovalAsync(string targetType, string targetId, string sessionId, ToolApprovalResponseInfo approvalResponse, string? userId, Func<NaradPersistedEvent, CancellationToken, ValueTask>? beforeWaitTerminal = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void PersistRun(string sessionId, string userMessage, GatewayChatResult result, IReadOnlyList<string>? userAssetIds = null, string? userId = null, string? runId = null, string? sourceChannel = null) => throw new NotSupportedException();
        public void RecordUserTurnStart(string sessionId, string userMessage, IReadOnlyList<string>? userAssetIds = null, string? userId = null, string? sourceChannel = null) => throw new NotSupportedException();
        public void PersistAssistantRunResult(string sessionId, GatewayChatResult result, string? userId = null) => throw new NotSupportedException();
    }
}
