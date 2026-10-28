using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.AgentFiles;
using Kautuka.Gateway.Domain.Auth;
using Kautuka.Gateway.Domain.Autopilot;
using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Domain.Sandbox;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Domain.Workflows;

using Mediator;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit.Sdk;

namespace Kautuka.IntegrationTests;

public sealed class AutopilotEndpointTests
{
    private static GatewayWorkflowDefinition CreateAutopilotWorkflow(string id, string ownerUserId, JsonObject schema)
    {
        string now = DateTimeOffset.UtcNow.ToString("O");
        var node = new GatewayWorkflowNode("step-1", "agent-a", "Read the input", "text", null, [], "none", false, false, 0);
        return new GatewayWorkflowDefinition(id, ownerUserId, id, false, 1, schema, schema.DeepClone().AsObject(),
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }, [node], now, now);
    }

    private static void EnableMailConnector(GatewayState state, string connectorId)
    {
        GatewayAgent agent = state.Agents["agent-a"];
        state.Agents[agent.Id] = agent with { McpBindings = [new GatewayAgentMcpBinding(connectorId)] };
        string now = DateTimeOffset.UtcNow.ToString("O");
        state.McpServers[connectorId] = new GatewayMcpServer(
            Id: connectorId, Name: "Test mail", Description: "Mail connector for a test.", IconUrl: string.Empty,
            PresetId: null, Transport: "local", HttpUrl: null, ContainerImage: null, ContainerCommand: null,
            ContainerArgs: [], ContainerSidecarName: null, BundlePath: null, ServerType: null,
            ReadinessTimeoutSeconds: null, JitCooldownSeconds: null, HealthcheckUrl: null,
            HealthcheckIntervalMilliseconds: null, Enabled: true, IsInternalSystemConnector: false,
            CreatedAt: now, UpdatedAt: now, Scope: "system");
    }

    private static async Task<GatewayScheduledRun> WaitForRunAsync(
        IGatewayAutopilotStore autopilot,
        string scheduleId,
        Func<GatewayScheduledRun, bool> ready)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            GatewayScheduledRun? run = autopilot.GetLatestRunBySchedule(scheduleId);
            if (run is not null && ready(run)) return run;
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        throw new XunitException($"Workflow run for schedule '{scheduleId}' did not reach the expected state.");
    }

    private static async Task<GatewayAutopilotNotification> WaitForNotificationAsync(
        IGatewayAutopilotStore autopilot,
        string ownerId,
        string runId)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            GatewayAutopilotNotification? notification = autopilot.ListNotifications(ownerId, 50, 0, unreadOnly: false)
                .FirstOrDefault(item => item.ScheduledRunId == runId && item.ChannelType == "ui_inbox");
            if (notification is not null) return notification;
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        throw new XunitException($"Workflow start notification for run '{runId}' was not written.");
    }

    private static GatewayAutopilotSchedule CreateWorkflowSchedule(
        string id,
        string ownerId,
        string workflowId,
        string input = "{}",
        string mode = AutopilotScheduleModes.On,
        string overlapPolicy = AutopilotOverlapPolicies.SkipAndNotify,
        string? createdAt = null) => new(
            Id: id, OwnerUserId: ownerId, TargetType: AutopilotTargetTypes.Workflow, TargetId: workflowId,
            CronExpression: "0 * * * *", TimeZoneId: "UTC", Mode: mode,
            RequiresPreRunApproval: string.Equals(mode, AutopilotScheduleModes.NeedsApproval, StringComparison.Ordinal),
            SchedulePromptContext: input, WorkspaceFilePaths: [], NotifyUiInbox: true, NotifyChannels: false,
            PreRunApprovalExpiryPolicy: AutopilotPreRunApprovalExpiryPolicies.Never, PreRunApprovalTimeoutSeconds: null,
            OverlapPolicy: overlapPolicy, IsPaused: false,
            CreatedAt: createdAt ?? DateTimeOffset.UtcNow.ToString("O"), UpdatedAt: DateTimeOffset.UtcNow.ToString("O"),
            NextDueAt: DateTimeOffset.UtcNow.AddHours(1).ToString("O"), LastEvaluatedAt: DateTimeOffset.UtcNow.ToString("O"),
            LeaseOwner: null, LeaseExpiresAt: null);

    private static GatewayScheduledRun CreateWorkflowRun(
        GatewayAutopilotSchedule schedule,
        string id,
        string status = AutopilotRunStatuses.Running,
        string? runtimeRunId = null,
        string? scheduledFor = null,
        string? preRunApprovalId = null) => new(
            Id: id, ScheduleId: schedule.Id, OwnerUserId: schedule.OwnerUserId,
            TargetType: AutopilotTargetTypes.Workflow, TargetId: schedule.TargetId, Status: status,
            TriggerType: "manual", ScheduledFor: scheduledFor ?? DateTimeOffset.UtcNow.ToString("O"),
            SessionId: null, RuntimeRunId: runtimeRunId, PreRunApprovalRequestId: preRunApprovalId,
            InputJson: "{}", OutputJson: null, Error: null, StateCategory: AutopilotRunStatuses.GetStateCategory(status),
            SkipReason: null, BlockingRunId: null, BlockingRunStatusSnapshot: null,
            BlockingRunStateCategory: null, BlockedAt: null,
            CreatedAt: DateTimeOffset.UtcNow.ToString("O"), UpdatedAt: DateTimeOffset.UtcNow.ToString("O"),
            StartedAt: DateTimeOffset.UtcNow.ToString("O"), EndedAt: null);

    private static GatewayWorkflowInstance CreateWorkflowInstance(
        string id,
        GatewayWorkflowDefinition definition,
        string status,
        string? correlationKey = null,
        string? output = null,
        string? error = null)
    {
        string now = DateTimeOffset.UtcNow.ToString("O");
        var envelope = new JsonObject();
        if (output is not null) envelope["output"] = JsonNode.Parse(output);
        GatewayWorkflowStepAttempt[] steps = [new(
            $"attempt-{id}", id, definition.Nodes[0].NodeId, 1, $"run-{id}", $"session-{id}",
            "running", null, now, null, now)];
        return new GatewayWorkflowInstance(
            id, definition.Id, definition.Revision, definition.OwnerUserId, correlationKey, status, false,
            definition.Nodes[0].NodeId, envelope, WorkflowEnvelopeCanonical.Sha256Hex(envelope), 0, definition,
            steps, null, error, now, now, now,
            string.Equals(status, WorkflowInstanceStatuses.Running, StringComparison.Ordinal) ? null : now);
    }

    private static async Task RunSchedulerCycleAsync(AutopilotApiFixture fixture)
    {
        AutopilotSchedulerService scheduler = fixture.Services.GetServices<IHostedService>()
            .OfType<AutopilotSchedulerService>().Single();
        await scheduler.RunCycleOnceAsync(TestContext.Current.CancellationToken);
    }

    private static async Task RunSchedulerMethodAsync(AutopilotApiFixture fixture, string methodName)
    {
        AutopilotSchedulerService scheduler = fixture.Services.GetServices<IHostedService>()
            .OfType<AutopilotSchedulerService>().Single();
        System.Reflection.MethodInfo method = typeof(AutopilotSchedulerService).GetMethod(
            methodName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new XunitException($"Scheduler method '{methodName}' was not found.");
        Task operation = method.Invoke(scheduler, [TestContext.Current.CancellationToken]) as Task
            ?? throw new XunitException($"Scheduler method '{methodName}' did not return a task.");
        await operation;
    }

    [Fact]
    public async Task WorkflowScheduleEndpointsRejectInvalidValuesUnownedTargetsAndMissingMailConnector()
    {
        await using var fixture = await AutopilotApiFixture.CreateAsync();
        string token = await fixture.LoginAsBootstrapAdminAsync();
        GatewayState state = fixture.Services.GetRequiredService<GatewayState>();
        string ownerId = state.Users["admin"].Id;
        IGatewayWorkflowStore workflows = fixture.Services.GetRequiredService<IGatewayWorkflowStore>();
        GatewayWorkflowDefinition typedWorkflow = CreateAutopilotWorkflow(
            "workflow-typed", ownerId,
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray("count"),
                ["properties"] = new JsonObject { ["count"] = new JsonObject { ["type"] = "integer" } },
            });
        workflows.CreateDefinition(typedWorkflow);

        using HttpResponseMessage createInvalid = await fixture.SendAsync(HttpMethod.Post, "/api/autopilot/schedules", new
        {
            targetType = "workflow",
            targetId = typedWorkflow.Id,
            cronExpression = "0 * * * *",
            timeZoneId = "UTC",
            mode = "on",
            schedulePromptContext = "{\"count\":\"wrong\"}",
        }, token);
        Assert.Equal(HttpStatusCode.BadRequest, createInvalid.StatusCode);

        using JsonDocument created = await fixture.SendJsonAsync(HttpMethod.Post, "/api/autopilot/schedules", new
        {
            targetType = "workflow",
            targetId = typedWorkflow.Id,
            cronExpression = "0 * * * *",
            timeZoneId = "UTC",
            mode = "on",
            schedulePromptContext = "{\"count\":1}",
        }, token);
        string scheduleId = created.RootElement.GetProperty("id").GetString()
            ?? throw new XunitException("Workflow schedule creation did not return an id.");
        using HttpResponseMessage updateInvalid = await fixture.SendAsync(HttpMethod.Put, $"/api/autopilot/schedules/{scheduleId}", new
        {
            schedulePromptContext = "{\"count\":\"wrong\"}",
        }, token);
        Assert.Equal(HttpStatusCode.BadRequest, updateInvalid.StatusCode);

        GatewayWorkflowDefinition foreignWorkflow = CreateAutopilotWorkflow("workflow-foreign", "another-owner", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(),
        });
        workflows.CreateDefinition(foreignWorkflow);
        using HttpResponseMessage foreign = await fixture.SendAsync(HttpMethod.Post, "/api/autopilot/schedules", new
        {
            targetType = "workflow",
            targetId = foreignWorkflow.Id,
            cronExpression = "0 * * * *",
            timeZoneId = "UTC",
            mode = "on",
            schedulePromptContext = "{}",
        }, token);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);

        string otherOwnerId = fixture.CreateUserWithOwnedAgent("workflow-owner", "Testing2", "user", "agent-other");
        string otherOwnerToken = await fixture.LoginAsync("workflow-owner", "Testing2");
        GatewayWorkflowDefinition otherOwnedWorkflow = CreateAutopilotWorkflow("workflow-other-owned", otherOwnerId,
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() });
        workflows.CreateDefinition(otherOwnedWorkflow);
        using JsonDocument otherSchedule = await fixture.SendJsonAsync(HttpMethod.Post, "/api/autopilot/schedules", new
        {
            targetType = "workflow",
            targetId = otherOwnedWorkflow.Id,
            cronExpression = "0 * * * *",
            timeZoneId = "UTC",
            mode = "on",
            schedulePromptContext = "{}",
        }, otherOwnerToken);
        string otherScheduleId = otherSchedule.RootElement.GetProperty("id").GetString()
            ?? throw new XunitException("Other owner's workflow schedule did not return an id.");
        using HttpResponseMessage foreignUpdate = await fixture.SendAsync(HttpMethod.Put, $"/api/autopilot/schedules/{otherScheduleId}", new
        {
            schedulePromptContext = "{}",
        }, token);
        Assert.Equal(HttpStatusCode.Forbidden, foreignUpdate.StatusCode);

        GatewayWorkflowDefinition mailWorkflow = CreateAutopilotWorkflow("workflow-mail", ownerId,
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["receivedAfter"] = new JsonObject { ["type"] = "string" } },
            });
        workflows.CreateDefinition(mailWorkflow);
        using HttpResponseMessage missingMail = await fixture.SendAsync(HttpMethod.Post, "/api/autopilot/schedules", new
        {
            targetType = "workflow",
            targetId = mailWorkflow.Id,
            cronExpression = "0 * * * *",
            timeZoneId = "UTC",
            mode = "on",
            schedulePromptContext = "{\"receivedAfter\":\"2026-01-01T00:00:00Z\"}",
        }, token);
        Assert.Equal(HttpStatusCode.BadRequest, missingMail.StatusCode);
        using JsonDocument missingMailBody = JsonDocument.Parse(
            await missingMail.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(WorkflowFirstStepConnectorPolicy.MailConnectorRequiredMessage,
            missingMailBody.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task WorkflowLaunchFailsWhenItsMailConnectorWasRemovedAfterScheduleSave()
    {
        await using var fixture = await AutopilotApiFixture.CreateAsync();
        string token = await fixture.LoginAsBootstrapAdminAsync();
        GatewayState state = fixture.Services.GetRequiredService<GatewayState>();
        string ownerId = state.Users["admin"].Id;
        IGatewayWorkflowStore workflows = fixture.Services.GetRequiredService<IGatewayWorkflowStore>();
        GatewayWorkflowDefinition workflow = CreateAutopilotWorkflow("workflow-mail-removed", ownerId,
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["receivedAfter"] = new JsonObject { ["type"] = "string" } },
            });
        workflows.CreateDefinition(workflow);
        const string connectorId = "test-kautuka-m365-mail";
        EnableMailConnector(state, connectorId);

        using JsonDocument schedule = await fixture.SendJsonAsync(HttpMethod.Post, "/api/autopilot/schedules", new
        {
            targetType = "workflow",
            targetId = workflow.Id,
            cronExpression = "0 * * * *",
            timeZoneId = "UTC",
            mode = "on",
            schedulePromptContext = "{\"receivedAfter\":\"2026-01-01T00:00:00Z\"}",
        }, token);
        string scheduleId = schedule.RootElement.GetProperty("id").GetString()
            ?? throw new XunitException("Workflow schedule creation did not return an id.");
        Assert.True(state.McpServers.Remove(connectorId));

        using JsonDocument launched = await fixture.SendJsonAsync(HttpMethod.Post, $"/api/autopilot/schedules/{scheduleId}/run-now", null, token);

        IGatewayAutopilotStore autopilot = fixture.Services.GetRequiredService<IGatewayAutopilotStore>();
        GatewayScheduledRun run = await WaitForRunAsync(autopilot, scheduleId,
            static item => string.Equals(item.Status, AutopilotRunStatuses.Failed, StringComparison.Ordinal));
        Assert.Equal("failed", run.Status);
        Assert.Equal(WorkflowFirstStepConnectorPolicy.MailConnectorRequiredMessage, run.Error);
        Assert.True(launched.RootElement.GetProperty("saved").GetBoolean());
        Assert.Empty(workflows.ListInstances(ownerId, workflow.Id, 20, 0));
    }

    [Fact]
    public async Task WorkflowStepMailSendWaitsForApproval()
    {
        var mailHandler = new CapturingMailSendHandler();
        var chat = new ScriptedMailApprovalAgentChatService(mailHandler);
        await using var fixture = await AutopilotApiFixture.CreateWithAgentChatServiceAsync(chat);
        chat.Initialize(
            fixture.Services.GetRequiredService<IGatewayApprovalStore>(),
            fixture.Services.GetRequiredService<IMediator>(),
            fixture.Services.GetRequiredService<IGatewayAutopilotStore>(),
            fixture.Services.GetRequiredService<IRuntimeEventStreamHub>());
        string token = await fixture.LoginAsBootstrapAdminAsync();
        GatewayState state = fixture.Services.GetRequiredService<GatewayState>();
        string ownerId = state.Users["admin"].Id;
        IGatewayWorkflowStore workflows = fixture.Services.GetRequiredService<IGatewayWorkflowStore>();
        IGatewayAutopilotStore autopilot = fixture.Services.GetRequiredService<IGatewayAutopilotStore>();
        GatewayWorkflowDefinition workflow = CreateAutopilotWorkflow("workflow-mail-approval", ownerId,
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() });
        workflows.CreateDefinition(workflow);
        const string connectorId = "test-kautuka-m365-mail";
        EnableMailConnector(state, connectorId);
        state.Agents["agent-a"] = state.Agents["agent-a"] with
        {
            ApprovalConfig = new JsonObject
            {
                ["requireMcpIds"] = new JsonArray(connectorId),
                ["requireMcpFunctionFilters"] = new JsonObject { [connectorId] = new JsonArray("mail_send") },
            },
        };

        using JsonDocument schedule = await fixture.SendJsonAsync(HttpMethod.Post, "/api/autopilot/schedules", new
        {
            targetType = "workflow",
            targetId = workflow.Id,
            cronExpression = "0 * * * *",
            timeZoneId = "UTC",
            mode = "on",
            schedulePromptContext = "{}",
            notifyUiInbox = false,
        }, token);
        string scheduleId = schedule.RootElement.GetProperty("id").GetString()!;
        _ = await fixture.SendJsonAsync(HttpMethod.Post, $"/api/autopilot/schedules/{scheduleId}/run-now", null, token);

        GatewayScheduledRun run = await WaitForRunAsync(autopilot, scheduleId,
            static item => !string.IsNullOrWhiteSpace(item.RuntimeRunId));
        GatewayToolApprovalRequest approval = await chat.PendingApproval.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        run = autopilot.GetLatestRunBySchedule(scheduleId)!;
        GatewayWorkflowInstance instance = workflows.GetInstance(run.RuntimeRunId!)
            ?? throw new XunitException("The scheduled workflow instance was not stored.");
        Assert.Equal(WorkflowInstanceStatuses.Running, instance.Status);
        Assert.Equal(WorkflowStepAttemptStatuses.Running, instance.Steps[0].Status);
        Assert.Contains(fixture.Services.GetRequiredService<IGatewayApprovalStore>().ListPending(ownerId, adminAll: false),
            item => item.Id == approval.Id && item.ToolName == "mail_send");
        Assert.Equal("mail_send", approval.ToolName);
        Assert.Contains(instance.Steps, step => step.RunId == approval.RunId || step.SessionId == approval.SessionId);
        Assert.Equal(0, mailHandler.SentCount);
        IAutopilotRunLaunchService launchService = fixture.Services.GetRequiredService<IAutopilotRunLaunchService>();
        Assert.Contains(launchService.ListPendingForRun(run), item => item.Id == approval.Id && item.ToolName == "mail_send");

        IApprovalResolutionService resolution = fixture.Services.GetRequiredService<IApprovalResolutionService>();
        ApprovalResolutionResult result = await resolution.ResolveAsync(new ApprovalResolutionCommand(
            approval.Id, Approved: true, Reason: null, DecidedByUserId: ownerId,
            Source: HitlResolutionSource.Browser, UserId: ownerId, Action: ApprovalActionCatalog.Approve),
            TestContext.Current.CancellationToken);
        Assert.Equal(ApprovalResolutionStatus.Resolved, result.Status);
        await mailHandler.Sent.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await chat.StepCompleted.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(1, mailHandler.SentCount);
    }

    [Fact]
    public async Task WorkflowRunStoresInstanceIdAndWritesStartedInboxNotification()
    {
        await using var fixture = await AutopilotApiFixture.CreateAsync();
        string token = await fixture.LoginAsBootstrapAdminAsync();
        GatewayState state = fixture.Services.GetRequiredService<GatewayState>();
        string ownerId = state.Users["admin"].Id;
        IGatewayWorkflowStore workflows = fixture.Services.GetRequiredService<IGatewayWorkflowStore>();
        GatewayWorkflowDefinition workflow = CreateAutopilotWorkflow("workflow-start-notification", ownerId,
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() });
        workflows.CreateDefinition(workflow);
        using JsonDocument schedule = await fixture.SendJsonAsync(HttpMethod.Post, "/api/autopilot/schedules", new
        {
            targetType = "workflow",
            targetId = workflow.Id,
            cronExpression = "0 * * * *",
            timeZoneId = "UTC",
            mode = "on",
            schedulePromptContext = "{}",
            notifyUiInbox = true,
            notifyChannels = false,
        }, token);
        string scheduleId = schedule.RootElement.GetProperty("id").GetString()
            ?? throw new XunitException("Workflow schedule creation did not return an id.");
        using JsonDocument launchResponse = await fixture.SendJsonAsync(HttpMethod.Post, $"/api/autopilot/schedules/{scheduleId}/run-now", null, token);
        Assert.True(launchResponse.RootElement.GetProperty("saved").GetBoolean());

        IGatewayAutopilotStore autopilot = fixture.Services.GetRequiredService<IGatewayAutopilotStore>();
        GatewayScheduledRun run = await WaitForRunAsync(autopilot, scheduleId, static item => !string.IsNullOrWhiteSpace(item.RuntimeRunId));
        string instanceId = run.RuntimeRunId
            ?? throw new XunitException("Workflow run did not store its instance id.");
        Assert.NotNull(workflows.GetInstance(instanceId));
        GatewayAutopilotNotification notification = await WaitForNotificationAsync(autopilot, ownerId, run.Id);
        using JsonDocument payload = JsonDocument.Parse(notification.PayloadJson
            ?? throw new XunitException("Workflow start notification did not contain a payload."));
        Assert.Equal("Auto-Pilot workflow started.", payload.RootElement.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData(WorkflowInstanceStatuses.Running, null, null, AutopilotRunStatuses.Running)]
    [InlineData(WorkflowInstanceStatuses.Paused, null, null, AutopilotRunStatuses.WaitingForInRunApproval)]
    [InlineData(WorkflowInstanceStatuses.Finished, "{\"ok\":true}", null, AutopilotRunStatuses.Completed)]
    [InlineData(WorkflowInstanceStatuses.Finished, null, null, AutopilotRunStatuses.NoOp)]
    [InlineData(WorkflowInstanceStatuses.Finished, "\"\"", null, AutopilotRunStatuses.NoOp)]
    [InlineData(WorkflowInstanceStatuses.Error, null, "step failed", AutopilotRunStatuses.Failed)]
    public async Task WorkflowInstanceStatusSyncSetsTheScheduledRunOutcome(string instanceStatus, string? output, string? error, string expectedStatus)
    {
        await using var fixture = await AutopilotApiFixture.CreateAsync();
        _ = await fixture.LoginAsBootstrapAdminAsync();
        GatewayState state = fixture.Services.GetRequiredService<GatewayState>();
        string ownerId = state.Users["admin"].Id;
        IGatewayWorkflowStore workflows = fixture.Services.GetRequiredService<IGatewayWorkflowStore>();
        IGatewayAutopilotStore autopilot = fixture.Services.GetRequiredService<IGatewayAutopilotStore>();
        GatewayWorkflowDefinition definition = CreateAutopilotWorkflow($"workflow-status-{Guid.NewGuid():N}", ownerId,
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() });
        workflows.CreateDefinition(definition);
        GatewayAutopilotSchedule schedule = CreateWorkflowSchedule($"schedule-{Guid.NewGuid():N}", ownerId, definition.Id);
        autopilot.CreateSchedule(schedule);
        GatewayWorkflowInstance instance = workflows.CreateInstance(CreateWorkflowInstance(
            $"instance-{Guid.NewGuid():N}", definition, instanceStatus, output: output, error: error));
        GatewayScheduledRun run = autopilot.InsertRun(CreateWorkflowRun(schedule, $"run-{Guid.NewGuid():N}", runtimeRunId: instance.Id));

        await RunSchedulerCycleAsync(fixture);

        GatewayScheduledRun updated = autopilot.GetRunById(run.Id)
            ?? throw new XunitException("The scheduled run disappeared during workflow status synchronization.");
        Assert.Equal(expectedStatus, updated.Status);
        if (expectedStatus == AutopilotRunStatuses.Failed) Assert.Equal(error, updated.Error);
        if (expectedStatus == AutopilotRunStatuses.Completed) Assert.Equal(output, updated.OutputJson);
    }

    [Fact]
    public async Task RestartReconciliationKeepsLiveWorkflowRunAndCompletesItWhenInstanceFinishes()
    {
        await using var fixture = await AutopilotApiFixture.CreateAsync();
        _ = await fixture.LoginAsBootstrapAdminAsync();
        GatewayState state = fixture.Services.GetRequiredService<GatewayState>();
        string ownerId = state.Users["admin"].Id;
        IGatewayWorkflowStore workflows = fixture.Services.GetRequiredService<IGatewayWorkflowStore>();
        IGatewayAutopilotStore autopilot = fixture.Services.GetRequiredService<IGatewayAutopilotStore>();
        GatewayWorkflowDefinition definition = CreateAutopilotWorkflow($"workflow-restart-{Guid.NewGuid():N}", ownerId,
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() });
        workflows.CreateDefinition(definition);
        GatewayAutopilotSchedule schedule = CreateWorkflowSchedule($"schedule-{Guid.NewGuid():N}", ownerId, definition.Id);
        autopilot.CreateSchedule(schedule);
        GatewayWorkflowInstance instance = workflows.CreateInstance(CreateWorkflowInstance(
            $"instance-{Guid.NewGuid():N}", definition, WorkflowInstanceStatuses.Running));
        GatewayScheduledRun run = autopilot.InsertRun(CreateWorkflowRun(schedule, $"run-{Guid.NewGuid():N}", runtimeRunId: instance.Id));

        await RunSchedulerMethodAsync(fixture, "ReconcileNonTerminalRunsAsync");
        Assert.Equal(AutopilotRunStatuses.Running, autopilot.GetRunById(run.Id)?.Status);
        Assert.DoesNotContain(AutopilotRunStatuses.Stale, autopilot.ListNonTerminalRuns(20).Select(static item => item.Status));

        JsonObject finishedEnvelope = new() { ["output"] = "finished" };
        Assert.True(workflows.TryUpdateEnvelope(instance.Id, finishedEnvelope, DateTimeOffset.UtcNow, out _));
        Assert.True(workflows.TrySetInstanceStatus(instance.Id, WorkflowInstanceStatuses.Finished,
            definition.Nodes[0].NodeId, null, null, null, DateTimeOffset.UtcNow, out _));
        await RunSchedulerMethodAsync(fixture, "SyncWorkflowRunsAsync");

        Assert.Equal(AutopilotRunStatuses.Completed, autopilot.GetRunById(run.Id)?.Status);
    }

    [Theory]
    [InlineData(true, AutopilotRunStatuses.Running)]
    [InlineData(false, AutopilotRunStatuses.Rejected)]
    public async Task NeedsApprovalWorkflowCarriesDefinitionAndStartValuesAndResolves(bool approve, string expectedRunStatus)
    {
        await using var fixture = await AutopilotApiFixture.CreateAsync();
        string token = await fixture.LoginAsBootstrapAdminAsync();
        GatewayState state = fixture.Services.GetRequiredService<GatewayState>();
        string ownerId = state.Users["admin"].Id;
        IGatewayWorkflowStore workflows = fixture.Services.GetRequiredService<IGatewayWorkflowStore>();
        IGatewayAutopilotStore autopilot = fixture.Services.GetRequiredService<IGatewayAutopilotStore>();
        IGatewayApprovalStore approvals = fixture.Services.GetRequiredService<IGatewayApprovalStore>();
        GatewayWorkflowDefinition definition = CreateAutopilotWorkflow($"workflow-approval-{approve}", ownerId,
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["topic"] = new JsonObject { ["type"] = "string" } },
            });
        workflows.CreateDefinition(definition);
        GatewayAutopilotSchedule schedule = CreateWorkflowSchedule(
            $"schedule-{Guid.NewGuid():N}", ownerId, definition.Id, "{\"topic\":\"nightly\"}", AutopilotScheduleModes.NeedsApproval);
        autopilot.CreateSchedule(schedule);
        using JsonDocument launchResponse = await fixture.SendJsonAsync(HttpMethod.Post,
            $"/api/autopilot/schedules/{schedule.Id}/run-now", null, token);
        Assert.True(launchResponse.RootElement.GetProperty("saved").GetBoolean());

        GatewayScheduledRun waiting = autopilot.GetLatestRunBySchedule(schedule.Id)
            ?? throw new XunitException("Pre-run approval did not create a scheduled run.");
        Assert.Equal(AutopilotRunStatuses.AwaitingPreRunApproval, waiting.Status);
        GatewayToolApprovalRequest request = approvals.GetById(waiting.PreRunApprovalRequestId ?? string.Empty)
            ?? throw new XunitException("Pre-run approval request was not stored.");
        Assert.Null(request.AgentId);
        using JsonDocument arguments = JsonDocument.Parse(request.ArgumentsJson
            ?? throw new XunitException("Pre-run approval arguments were not stored."));
        Assert.Equal(definition.Name, arguments.RootElement.GetProperty("workflowName").GetString());
        Assert.Equal(definition.Revision, arguments.RootElement.GetProperty("workflowRevision").GetInt32());
        Assert.Equal("nightly", arguments.RootElement.GetProperty("startValues").GetProperty("topic").GetString());

        Assert.True(approvals.Resolve(request.Id, approve, approve ? null : "rejected by test", ownerId, "test"));
        await RunSchedulerCycleAsync(fixture);

        GatewayScheduledRun resolved = autopilot.GetRunById(waiting.Id)
            ?? throw new XunitException("Resolved workflow run was not stored.");
        Assert.Equal(expectedRunStatus, resolved.Status);
        Assert.Equal(approve, workflows.ListInstances(ownerId, definition.Id, 20, 0).Count > 0);
    }

    [Theory]
    [InlineData(true, true, true, false, "2026-10-22T00:00:00Z")]
    [InlineData(true, false, false, false, "2026-10-20T00:00:00Z")]
    [InlineData(false, true, false, false, "2026-10-22T00:00:00Z")]
    [InlineData(false, false, true, false, "2026-01-01T00:00:00Z")]
    [InlineData(true, false, true, true, "supplied")]
    public async Task ReceivedAfterUsesLatestSuccessfulRunOrCreatedAtWithoutOverwritingSuppliedValue(
        bool includeCompleted,
        bool includeNoOp,
        bool includeFailed,
        bool suppliesValue,
        string expectedValue)
    {
        await using var fixture = await AutopilotApiFixture.CreateAsync();
        _ = await fixture.LoginAsBootstrapAdminAsync();
        GatewayState state = fixture.Services.GetRequiredService<GatewayState>();
        string ownerId = state.Users["admin"].Id;
        const string connectorId = "test-kautuka-google-mail";
        EnableMailConnector(state, connectorId);
        IGatewayWorkflowStore workflows = fixture.Services.GetRequiredService<IGatewayWorkflowStore>();
        IGatewayAutopilotStore autopilot = fixture.Services.GetRequiredService<IGatewayAutopilotStore>();
        GatewayWorkflowDefinition definition = CreateAutopilotWorkflow($"workflow-received-{Guid.NewGuid():N}", ownerId,
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["receivedAfter"] = new JsonObject { ["type"] = "string" } },
            });
        workflows.CreateDefinition(definition);
        const string createdAt = "2026-01-01T00:00:00Z";
        string supplied = suppliesValue ? "{\"receivedAfter\":\"supplied\"}" : "{}";
        GatewayAutopilotSchedule schedule = CreateWorkflowSchedule(
            $"schedule-{Guid.NewGuid():N}", ownerId, definition.Id, supplied, createdAt: createdAt);
        autopilot.CreateSchedule(schedule);

        if (includeCompleted)
        {
            GatewayScheduledRun completed = CreateWorkflowRun(schedule, $"completed-{Guid.NewGuid():N}",
                AutopilotRunStatuses.Completed, scheduledFor: "2026-10-20T00:00:00Z") with
            { CreatedAt = "2026-10-20T00:00:00Z" };
            autopilot.InsertRun(completed);
        }
        if (includeNoOp)
        {
            GatewayScheduledRun noOp = CreateWorkflowRun(schedule, $"noop-{Guid.NewGuid():N}",
                AutopilotRunStatuses.NoOp, scheduledFor: "2026-10-22T00:00:00Z") with
            { CreatedAt = "2026-10-22T00:00:00Z" };
            autopilot.InsertRun(noOp);
        }
        if (includeFailed)
        {
            GatewayScheduledRun failed = CreateWorkflowRun(schedule, $"failed-{Guid.NewGuid():N}",
                AutopilotRunStatuses.Failed, scheduledFor: "2026-10-23T00:00:00Z") with
            { CreatedAt = "2026-10-23T00:00:00Z" };
            autopilot.InsertRun(failed);
        }
        GatewayScheduledRun launchRun = autopilot.InsertRun(CreateWorkflowRun(schedule, $"launch-{Guid.NewGuid():N}",
            AutopilotRunStatuses.Scheduled));

        await fixture.Services.GetRequiredService<IAutopilotRunExecutionService>()
            .StartAndExecuteAsync(schedule, launchRun, TestContext.Current.CancellationToken);

        GatewayScheduledRun updated = autopilot.GetRunById(launchRun.Id)
            ?? throw new XunitException("Auto-Pilot workflow launch run was not stored.");
        string instanceId = updated.RuntimeRunId
            ?? throw new XunitException("Auto-Pilot workflow launch did not create an instance.");
        GatewayWorkflowInstance instance = workflows.GetInstance(instanceId)
            ?? throw new XunitException("Auto-Pilot workflow instance was not stored.");
        Assert.Equal(expectedValue, instance.Envelope["receivedAfter"]?.GetValue<string>());
    }

    [Theory]
    [InlineData(AutopilotOverlapPolicies.SkipAndNotify, true)]
    [InlineData(AutopilotOverlapPolicies.SkipSilently, false)]
    public async Task ExistingAutopilotWorkflowInstanceMakesLauncherSkipWithRecordedReason(string overlapPolicy, bool expectsNotification)
    {
        await using var fixture = await AutopilotApiFixture.CreateAsync();
        _ = await fixture.LoginAsBootstrapAdminAsync();
        GatewayState state = fixture.Services.GetRequiredService<GatewayState>();
        string ownerId = state.Users["admin"].Id;
        IGatewayWorkflowStore workflows = fixture.Services.GetRequiredService<IGatewayWorkflowStore>();
        IGatewayAutopilotStore autopilot = fixture.Services.GetRequiredService<IGatewayAutopilotStore>();
        GatewayWorkflowDefinition definition = CreateAutopilotWorkflow($"workflow-overlap-{Guid.NewGuid():N}", ownerId,
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() });
        workflows.CreateDefinition(definition);
        GatewayAutopilotSchedule schedule = CreateWorkflowSchedule(
            $"schedule-{Guid.NewGuid():N}", ownerId, definition.Id, overlapPolicy: overlapPolicy);
        autopilot.CreateSchedule(schedule);
        workflows.CreateInstance(CreateWorkflowInstance(
            $"instance-{Guid.NewGuid():N}", definition, WorkflowInstanceStatuses.Running, $"autopilot:{schedule.Id}"));
        GatewayScheduledRun run = autopilot.InsertRun(CreateWorkflowRun(schedule, $"run-{Guid.NewGuid():N}", AutopilotRunStatuses.Scheduled));

        await fixture.Services.GetRequiredService<IAutopilotRunExecutionService>()
            .StartAndExecuteAsync(schedule, run, TestContext.Current.CancellationToken);

        GatewayScheduledRun skipped = autopilot.GetRunById(run.Id)
            ?? throw new XunitException("Skipped workflow run was not stored.");
        Assert.Equal(AutopilotRunStatuses.Skipped, skipped.Status);
        Assert.Equal("An overlapping workflow run is already in progress.", skipped.SkipReason);
        Assert.Equal(expectsNotification, autopilot.ListNotifications(ownerId, 50, 0, unreadOnly: false)
            .Any(item => item.ScheduledRunId == run.Id && item.ChannelType == "ui_inbox"));
    }

    [Fact]
    public async Task TwoStepWorkflowPassesWorkspaceFilesOnlyToItsFirstAgentRequest()
    {
        await using var fixture = await AutopilotApiFixture.CreateAsync();
        _ = await fixture.LoginAsBootstrapAdminAsync();
        GatewayState state = fixture.Services.GetRequiredService<GatewayState>();
        string ownerId = state.Users["admin"].Id;
        IGatewayWorkflowStore workflows = fixture.Services.GetRequiredService<IGatewayWorkflowStore>();
        IGatewayApprovalStore approvals = fixture.Services.GetRequiredService<IGatewayApprovalStore>();
        IGatewayCatalogStore catalog = fixture.Services.GetRequiredService<IGatewayCatalogStore>();
        IRuntimeEventStreamHub runtimeHub = new RuntimeEventStreamHub();
        var chat = new CapturingWorkflowChatGateway();
        var workflowPublisher = new WorkflowNaradPublisher(workflows, new WorkflowNaradStreamHub(), runtimeHub);
        var execution = new WorkflowExecutionService(
            workflows,
            chat,
            workflowPublisher,
            new RuntimeEventHistoryReader(fixture.Services.GetRequiredService<IAgentDefinitionStore>()),
            runtimeHub,
            approvals,
            fixture.Services.GetRequiredService<IMediator>(),
            NullLogger<WorkflowExecutionService>.Instance);
        string workspaceRoot = Path.Combine(Path.GetTempPath(), $"kautuka-autopilot-files-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspaceRoot);
        try
        {
            string filePath = Path.Combine(workspaceRoot, "report.txt");
            await File.WriteAllTextAsync(filePath, "approved file", TestContext.Current.CancellationToken);
            var node1 = new GatewayWorkflowNode("first", "agent-a", "Read the source", "json", null, [], "none", false, false, 0);
            var node2 = new GatewayWorkflowNode("second", "agent-a", "Summarize the source", "json", null, [], "none", false, false, 0);
            string now = DateTimeOffset.UtcNow.ToString("O");
            var definition = new GatewayWorkflowDefinition("workflow-files", ownerId, "Files workflow", false, 1,
                new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
                new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
                new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
                [node1, node2], now, now);
            workflows.CreateDefinition(definition);
            var authorization = new AgentFilePathAuthorizationService(catalog, new TestWorkspaceRuntimeTool(workspaceRoot));
            var launcher = new WorkflowInstanceLauncher(
                workflows, execution, authorization, catalog,
                fixture.Services.GetRequiredService<IGatewayUserStore>(),
                NullLogger<WorkflowInstanceLauncher>.Instance);

            WorkflowInstanceStartResult start = launcher.Start(definition, ownerId, new JsonObject(), null, ["report.txt"]);
            GatewayWorkflowInstance instance = start.Instance
                ?? throw new XunitException($"The workflow did not start: {start.Error}");
            await WaitForChatRequestsAsync(chat, 2);
            GatewayWorkflowInstance current = workflows.GetInstance(instance.Id)
                ?? throw new XunitException("The workflow instance disappeared while its steps ran.");
            Assert.True(chat.RequestBodies.Count == 2,
                $"Expected two agent requests; workflow status={current.Status}, error={current.Error}, step statuses={string.Join(',', current.Steps.Select(step => step.Status))}");
            Assert.Equal(2, current.Steps.Length);
            Assert.Equal(2, chat.RequestBodies.Count);
            Assert.True(chat.RequestBodies[0].TryGetProperty("workspaceFiles", out JsonElement firstFiles));
            Assert.Equal("report.txt", Assert.Single(firstFiles.EnumerateArray()).GetString());
            Assert.False(chat.RequestBodies[1].TryGetProperty("workspaceFiles", out _));
            execution.Cancel(instance.Id);
        }
        finally
        {
            if (Directory.Exists(workspaceRoot)) Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ThreeStepWorkflowKeepsTheLastStepOutputAsFinalWorkingData()
    {
        await using var fixture = await AutopilotApiFixture.CreateAsync();
        _ = await fixture.LoginAsBootstrapAdminAsync();
        string ownerId = fixture.Services.GetRequiredService<GatewayState>().Users["admin"].Id;
        IGatewayWorkflowStore workflows = fixture.Services.GetRequiredService<IGatewayWorkflowStore>();
        var chat = new CapturingWorkflowChatGateway("{\"result\":\"first\"}", "{\"result\":\"middle\"}", "{\"result\":\"last\"}");
        var runtimeHub = new RuntimeEventStreamHub();
        var execution = new WorkflowExecutionService(
            workflows,
            chat,
            new WorkflowNaradPublisher(workflows, new WorkflowNaradStreamHub(), runtimeHub),
            new RuntimeEventHistoryReader(fixture.Services.GetRequiredService<IAgentDefinitionStore>()),
            runtimeHub,
            fixture.Services.GetRequiredService<IGatewayApprovalStore>(),
            fixture.Services.GetRequiredService<IMediator>(),
            NullLogger<WorkflowExecutionService>.Instance);
        JsonObject schema = new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["result"] = new JsonObject { ["type"] = "string" } },
        };
        string now = DateTimeOffset.UtcNow.ToString("O");
        var definition = new GatewayWorkflowDefinition(
            "workflow-final-working-data", ownerId, "Final working data", false, 1,
            schema.DeepClone().AsObject(), schema.DeepClone().AsObject(), schema.DeepClone().AsObject(),
            [
                new GatewayWorkflowNode("first", "agent-a", "First", "json", null, [], "none", false, false, 0),
                new GatewayWorkflowNode("middle", "agent-a", "Middle", "json", null, [], "none", false, false, 0),
                new GatewayWorkflowNode("last", "agent-a", "Last", "json", null, [], "none", false, false, 0),
            ], now, now);
        workflows.CreateDefinition(definition);
        var launcher = new WorkflowInstanceLauncher(
            workflows,
            execution,
            fixture.Services.GetRequiredService<IAgentFilePathAuthorizationService>(),
            fixture.Services.GetRequiredService<IGatewayCatalogStore>(),
            fixture.Services.GetRequiredService<IGatewayUserStore>(),
            NullLogger<WorkflowInstanceLauncher>.Instance);

        WorkflowInstanceStartResult start = launcher.Start(definition, ownerId, new JsonObject(), null, null);
        GatewayWorkflowInstance instance = start.Instance
            ?? throw new XunitException($"The workflow did not start: {start.Error}");
        await WaitForWorkflowFinishedAsync(workflows, instance.Id);

        GatewayWorkflowInstance completed = workflows.GetInstance(instance.Id)
            ?? throw new XunitException("The workflow instance disappeared while its steps ran.");
        Assert.True(WorkflowInstanceStatuses.Finished == completed.Status,
            $"Expected finished; status={completed.Status}, error={completed.Error}, failureCode={completed.FailureCode}, requests={chat.RequestBodies.Count}.");
        Assert.Equal("last", completed.Envelope["result"]?.GetValue<string>());
        Assert.Equal(3, chat.RequestBodies.Count);
    }

    private static async Task WaitForChatRequestsAsync(CapturingWorkflowChatGateway chat, int count)
    {
        for (int attempt = 0; attempt < 150 && chat.RequestBodies.Count < count; attempt++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

    }

    private static async Task WaitForWorkflowFinishedAsync(IGatewayWorkflowStore workflows, string instanceId)
    {
        for (int attempt = 0; attempt < 150; attempt++)
        {
            if (workflows.GetInstance(instanceId)?.Status is WorkflowInstanceStatuses.Finished or WorkflowInstanceStatuses.Error)
            {
                return;
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkflowLauncherRejectsUnavailableOwnersWithoutCreatingInstances(bool inactive)
    {
        await using var fixture = await AutopilotApiFixture.CreateAsync();
        _ = await fixture.LoginAsBootstrapAdminAsync();
        GatewayState state = fixture.Services.GetRequiredService<GatewayState>();
        string ownerId = state.Users["admin"].Id;
        string launchOwnerId = inactive ? ownerId : "owner-missing";
        GatewayWorkflowDefinition workflow = CreateAutopilotWorkflow($"workflow-owner-{inactive}", launchOwnerId,
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() });
        IGatewayWorkflowStore workflows = fixture.Services.GetRequiredService<IGatewayWorkflowStore>();
        workflows.CreateDefinition(workflow);
        if (inactive)
        {
            state.Users["admin"] = state.Users["admin"] with { IsActive = false };
        }

        WorkflowInstanceLauncher launcher = fixture.Services.GetRequiredService<WorkflowInstanceLauncher>();
        WorkflowInstanceStartResult result = launcher.Start(workflow, launchOwnerId, new JsonObject(), "autopilot:missing-owner");

        Assert.False(result.Succeeded);
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        Assert.Equal("The workflow owner is not available.", result.Error);
        Assert.Empty(workflows.ListInstances(launchOwnerId, workflow.Id, 20, 0));
    }

    [Fact]
    public async Task StartingAStaleAutopilotRunClosesItsUnexpiredApprovalWithoutLaunchingIt()
    {
        await using var fixture = await AutopilotApiFixture.CreateAsync();
        string token = await fixture.LoginAsBootstrapAdminAsync();
        using JsonDocument schedule = await fixture.SendJsonAsync(HttpMethod.Post, "/api/autopilot/schedules", new
        {
            targetType = "agent",
            targetId = "agent-a",
            cronExpression = "0 * * * *",
            timeZoneId = "UTC",
            mode = "needs_approval",
            schedulePromptContext = "Do not execute in this test",
            preRunApprovalExpiryPolicy = "never",
        }, token);
        string scheduleId = schedule.RootElement.GetProperty("id").GetString()!;
        using JsonDocument started = await fixture.SendJsonAsync(HttpMethod.Post,
            $"/api/autopilot/schedules/{scheduleId}/run-now", null, token);
        IGatewayAutopilotStore autopilot = fixture.Services.GetRequiredService<IGatewayAutopilotStore>();
        GatewayScheduledRun run = Assert.IsType<GatewayScheduledRun>(autopilot.GetLatestNonTerminalRunBySchedule(scheduleId));
        Assert.True(autopilot.TransitionRunState(run.Id, run.Status, AutopilotRunStatuses.Stale,
            "Recovered on startup from non-terminal state.", null, run.SessionId, run.RuntimeRunId,
            run.PreRunApprovalRequestId, DateTimeOffset.UtcNow, markStarted: false, markEnded: true));

        using HttpResponseMessage resolution = await fixture.SendAsync(HttpMethod.Post,
            "/api/narad/v1/commands/resolve-interrupt",
            new { interruptId = run.PreRunApprovalRequestId, decision = "approve" }, token);
        Assert.Equal(HttpStatusCode.Conflict, resolution.StatusCode);
        using JsonDocument resolutionBody = JsonDocument.Parse(await resolution.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("expired", resolutionBody.RootElement.GetProperty("decision").GetString());
        IGatewayApprovalStore approvals = fixture.Services.GetRequiredService<IGatewayApprovalStore>();
        Assert.Equal("expired", approvals.GetById(run.PreRunApprovalRequestId!)!.Decision);
        Assert.Equal(AutopilotRunStatuses.Stale, autopilot.GetRunById(run.Id)!.Status);
        Assert.Null(autopilot.GetRunById(run.Id)!.SessionId);
        using JsonDocument pending = await fixture.SendJsonAsync(HttpMethod.Get, "/api/approvals/pending", null, token);
        Assert.DoesNotContain(pending.RootElement.EnumerateArray(), item => item.GetProperty("id").GetString() == run.PreRunApprovalRequestId);
    }

    [Fact]
    public async Task AutopilotScheduleCrudRoundTrips()
    {
        await using var fixture = await AutopilotApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();

        using JsonDocument created = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/autopilot/schedules",
            new
            {
                targetType = "agent",
                targetId = "agent-a",
                cronExpression = "*/5 * * * *",
                timeZoneId = "UTC",
                mode = "on",
                requiresPreRunApproval = false,
                schedulePromptContext = "Run diagnostics",
                workspaceFilePaths = new[] { "docs/runbook.md" },
                notifyUiInbox = true,
                notifyChannels = false,
            },
            accessToken);

        string scheduleId = created.RootElement.GetProperty("id").GetString()
            ?? throw new XunitException("Autopilot schedule creation did not return an id.");
        Assert.Equal("docs/runbook.md", created.RootElement.GetProperty("workspaceFilePaths")[0].GetString());

        using JsonDocument listed = await fixture.SendJsonAsync(HttpMethod.Get, "/api/autopilot/schedules", null, accessToken);
        Assert.Contains(listed.RootElement.EnumerateArray(), item =>
            string.Equals(item.GetProperty("id").GetString(), scheduleId, StringComparison.Ordinal));

        using JsonDocument paused = await fixture.SendJsonAsync(HttpMethod.Post, $"/api/autopilot/schedules/{scheduleId}/pause", null, accessToken);
        Assert.True(paused.RootElement.GetProperty("isPaused").GetBoolean());

        using JsonDocument resumed = await fixture.SendJsonAsync(HttpMethod.Post, $"/api/autopilot/schedules/{scheduleId}/resume", null, accessToken);
        Assert.False(resumed.RootElement.GetProperty("isPaused").GetBoolean());

        using JsonDocument updated = await fixture.SendJsonAsync(
            HttpMethod.Put,
            $"/api/autopilot/schedules/{scheduleId}",
            new
            {
                mode = "needs_approval",
                preRunApprovalExpiryPolicy = "duration",
                preRunApprovalTimeoutSeconds = 900,
                overlapPolicy = "skip_silently",
                schedulePromptContext = "Updated context",
                workspaceFilePaths = new[] { "docs/updated.md" },
            },
            accessToken);
        Assert.Equal("needs_approval", updated.RootElement.GetProperty("mode").GetString());
        Assert.Equal("duration", updated.RootElement.GetProperty("preRunApprovalExpiryPolicy").GetString());
        Assert.Equal(900, updated.RootElement.GetProperty("preRunApprovalTimeoutSeconds").GetInt32());
        Assert.Equal("skip_silently", updated.RootElement.GetProperty("overlapPolicy").GetString());
        Assert.Equal("docs/updated.md", updated.RootElement.GetProperty("workspaceFilePaths")[0].GetString());

        using JsonDocument runNow = await fixture.SendJsonAsync(HttpMethod.Post, $"/api/autopilot/schedules/{scheduleId}/run-now", null, accessToken);
        Assert.True(runNow.RootElement.GetProperty("saved").GetBoolean());

        using JsonDocument runs = await fixture.SendJsonAsync(HttpMethod.Get, "/api/autopilot/runs?limit=10", null, accessToken);
        Assert.Equal(JsonValueKind.Array, runs.RootElement.ValueKind);
        Assert.Contains(runs.RootElement.EnumerateArray(), item =>
            string.Equals(item.GetProperty("status").GetString(), "awaiting_pre_run_approval", StringComparison.Ordinal));

        using JsonDocument pending = await fixture.SendJsonAsync(HttpMethod.Get, "/api/approvals/pending", null, accessToken);
        Assert.Contains(pending.RootElement.EnumerateArray(), item =>
            string.Equals(item.GetProperty("toolType").GetString(), "autopilot_prerun", StringComparison.Ordinal)
            && string.Equals(item.GetProperty("toolName").GetString(), "autopilot_pre_run", StringComparison.Ordinal));

        using JsonDocument inbox = await fixture.SendJsonAsync(HttpMethod.Get, "/api/autopilot/inbox?limit=10", null, accessToken);
        Assert.Equal(JsonValueKind.Array, inbox.RootElement.ValueKind);

        using JsonDocument deleted = await fixture.SendJsonAsync(HttpMethod.Delete, $"/api/autopilot/schedules/{scheduleId}", null, accessToken);
        Assert.True(deleted.RootElement.GetProperty("deleted").GetBoolean());
    }

    [Fact]
    public async Task AutopilotScheduleCreationRejectsInvalidCron()
    {
        await using var fixture = await AutopilotApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();

        using HttpResponseMessage response = await fixture.SendAsync(
            HttpMethod.Post,
            "/api/autopilot/schedules",
            new
            {
                targetType = "agent",
                targetId = "agent-a",
                cronExpression = "invalid-cron",
                timeZoneId = "UTC",
                mode = "on",
            },
            accessToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AutopilotSchedulesRemainOwnerScopedEvenForAdmins()
    {
        await using var fixture = await AutopilotApiFixture.CreateAsync();
        string adminAccessToken = await fixture.LoginAsBootstrapAdminAsync();
        string otherUserId = fixture.CreateUserWithOwnedAgent(
            username: "other-admin",
            password: "Testing2",
            role: "admin",
            agentId: "agent-b");
        string otherAccessToken = await fixture.LoginAsync("other-admin", "Testing2");

        using JsonDocument created = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/autopilot/schedules",
            new
            {
                targetType = "agent",
                targetId = "agent-b",
                cronExpression = "*/5 * * * *",
                timeZoneId = "UTC",
                mode = "on",
            },
            otherAccessToken);

        string scheduleId = created.RootElement.GetProperty("id").GetString()
            ?? throw new XunitException("Autopilot schedule creation did not return an id.");
        Assert.Equal(otherUserId, created.RootElement.GetProperty("ownerUserId").GetString());

        using JsonDocument adminList = await fixture.SendJsonAsync(HttpMethod.Get, "/api/autopilot/schedules", null, adminAccessToken);
        Assert.DoesNotContain(adminList.RootElement.EnumerateArray(), item =>
            string.Equals(item.GetProperty("id").GetString(), scheduleId, StringComparison.Ordinal));

        using HttpResponseMessage detailResponse = await fixture.SendAsync(HttpMethod.Get, $"/api/autopilot/schedules/{scheduleId}", null, adminAccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, detailResponse.StatusCode);
    }

    [Fact]
    public async Task NormalUserWithoutChatCapabilityCanUseAutopilotAndListOwnedAgents()
    {
        await using var fixture = await AutopilotApiFixture.CreateAsync();
        fixture.CreateUserWithOwnedAgent(
            username: "normal-user",
            password: "Testing2",
            role: "user",
            agentId: "agent-normal");
        string accessToken = await fixture.LoginAsync("normal-user", "Testing2");

        using JsonDocument agents = await fixture.SendJsonAsync(HttpMethod.Get, "/api/settings/agents", null, accessToken);
        Assert.Contains(agents.RootElement.GetProperty("agents").EnumerateArray(), item =>
            string.Equals(item.GetProperty("id").GetString(), "agent-normal", StringComparison.Ordinal));

        using JsonDocument created = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/autopilot/schedules",
            new
            {
                targetType = "agent",
                targetId = "agent-normal",
                cronExpression = "*/5 * * * *",
                timeZoneId = "UTC",
                mode = "on",
                schedulePromptContext = "Run diagnostics",
                workspaceFilePaths = new[] { "docs/runbook.md" },
                notifyUiInbox = true,
                notifyChannels = false,
            },
            accessToken);

        Assert.Equal("normal-user", created.RootElement.GetProperty("ownerUserId").GetString() is null ? null : fixture.GetUsernameByUserId(created.RootElement.GetProperty("ownerUserId").GetString()!));
    }

    private sealed class TestWorkspaceRuntimeTool(string rootPath) : IWorkspaceRuntimeTool
    {
        public WorkspaceRuntimeContext DescribeAgentWorkspace(string agentId, string? userId = null)
            => Context(agentId, userId);

        public WorkspaceRuntimeContext RebuildAgentWorkspace(string agentId, string? userId = null)
            => Context(agentId, userId);

        public bool DeleteAgentWorkspace(string agentId) => true;

        public Task<WorkspaceSandboxSession> SpawnAgentSandboxAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkspaceSandboxSession($"agent-{agentId}", true, "agent", agentId, userId));

        public Task<WorkspaceCommandResult> RunAgentShellAsync(string agentId, string command, string? userId = null, int? timeoutSeconds = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkspaceCommandResult("completed", 0, string.Empty, string.Empty));

        public Task<WorkspaceSandboxSession> InspectAgentSandboxAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkspaceSandboxSession($"agent-{agentId}", true, "agent", agentId, userId));

        public Task CloseAgentSandboxAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        private WorkspaceRuntimeContext Context(string agentId, string? userId)
            => new("agent", agentId, userId, rootPath, "/workspace", "/runtime-workspace", "test", 60, [], new Dictionary<string, string>());
    }

    private sealed class CapturingWorkflowChatGateway(params string[] outputs) : IChatGatewayService
    {
        private int requestCount;
        public List<JsonElement> RequestBodies { get; } = [];

        public async IAsyncEnumerable<NaradPersistedEvent> StreamChatAsync(
            JsonElement body,
            AgentRequestContext? requestContext = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            RequestBodies.Add(body.Clone());
            string targetType = body.GetProperty("target_type").GetString() ?? "agent";
            string targetId = body.GetProperty("target_id").GetString() ?? "agent-a";
            string userId = body.GetProperty("user_id").GetString() ?? "user-unknown";
            string runId = body.TryGetProperty("run_id", out JsonElement runIdElement)
                ? runIdElement.GetString() ?? "run-test"
                : "run-test";
            int index = Interlocked.Increment(ref requestCount);
            string sessionId = $"workflow-session-{index}";
            yield return NaradTestEventFactory.FromChatEvent(new GatewayChatSessionReady(sessionId), targetId, targetId, runId);
            string response = outputs.Length == 0 ? "{}" : outputs[Math.Min(index - 1, outputs.Length - 1)];
            yield return NaradTestEventFactory.FromChatEvent(new GatewayChatStreamDone(new GatewayChatResult(
                targetType, targetId, sessionId, response, false, new GatewayMetrics(0, 0, 0))), targetId, targetId, runId);
            await Task.CompletedTask;
            _ = userId;
        }

        public IAsyncEnumerable<NaradPersistedEvent> StreamChatWithApprovalAsync(
            string targetType,
            string targetId,
            string sessionId,
            ToolApprovalResponseInfo approvalResponse,
            string? userId,
            Func<NaradPersistedEvent, CancellationToken, ValueTask>? beforeWaitTerminal = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void PersistRun(string sessionId, string userMessage, GatewayChatResult result, IReadOnlyList<string>? userAssetIds = null, string? userId = null, string? runId = null, string? sourceChannel = null)
            => throw new NotSupportedException();

        public void RecordUserTurnStart(string sessionId, string userMessage, IReadOnlyList<string>? userAssetIds = null, string? userId = null, string? sourceChannel = null)
            => throw new NotSupportedException();

        public void PersistAssistantRunResult(string sessionId, GatewayChatResult result, string? userId = null)
            => throw new NotSupportedException();
    }

    private sealed class CapturingMailSendHandler
    {
        private readonly TaskCompletionSource sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SentCount { get; private set; }
        public Task Sent => sent.Task;
        public void Send() { SentCount++; sent.TrySetResult(); }
    }

    private sealed class ScriptedMailApprovalAgentChatService(CapturingMailSendHandler mailHandler) : IAgentChatService
    {
        private IGatewayApprovalStore? approvalStore;
        private IMediator? mediator;
        private IGatewayAutopilotStore? autopilotStore;
        private IRuntimeEventStreamHub? eventHub;
        private long eventSequence;
        private readonly TaskCompletionSource<GatewayToolApprovalRequest> pendingApproval = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource stepCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<GatewayToolApprovalRequest> PendingApproval => pendingApproval.Task;
        public Task StepCompleted => stepCompleted.Task;

        public void Initialize(IGatewayApprovalStore store, IMediator eventMediator, IGatewayAutopilotStore scheduledRuns, IRuntimeEventStreamHub hub)
        {
            approvalStore = store;
            mediator = eventMediator;
            autopilotStore = scheduledRuns;
            eventHub = hub;
        }

        public async IAsyncEnumerable<NaradPersistedEvent> StreamAsync(
            string agentId,
            string sessionId,
            string? userId,
            string runTrigger,
            string message,
            string? stageTransitionId,
            IReadOnlyList<GatewayConversationAsset> inputAssets,
            IReadOnlyList<AgentWorkspaceFileReference> workspaceFiles,
            IReadOnlyList<InjectedMemoryFact> memoryFacts,
            ToolApprovalResponseInfo? approvalResponse,
            AgentContinuationRequest? continuationRequest,
            AgentRequestContext? requestContext = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = (userId, runTrigger, message, stageTransitionId, inputAssets, workspaceFiles, memoryFacts,
                approvalResponse, continuationRequest);
            string runId = requestContext?.RunId ?? throw new XunitException("The scripted workflow request did not include a run id.");
            string ownerId = userId ?? throw new XunitException("The scripted workflow request did not include an owner.");
            yield return PersistEvent(new GatewayChatSessionReady(sessionId), sessionId, agentId, runId);
            _ = autopilotStore ?? throw new InvalidOperationException("Autopilot store was not initialized.");
            var approval = new GatewayToolApprovalRequest(
                Id: "mail-approval", SessionId: sessionId, RunId: runId, CheckpointId: null,
                AgentId: agentId, UserId: ownerId, ToolCallId: "mail-call", ToolName: "mail_send",
                ToolType: "mcp", McpServerId: "test-kautuka-m365-mail", ArgumentsJson: "{}",
                RequestedAt: DateTimeOffset.UtcNow.ToString("O"), Decision: null, DecidedBy: null,
                DecidedAt: null, Reason: null, ExpiresAt: null);
            (approvalStore ?? throw new InvalidOperationException("Approval store was not initialized.")).CreateRequest(approval);
            await (mediator ?? throw new InvalidOperationException("Mediator was not initialized.")).Publish(
                new HitlApprovalRequested(
                    approval.Id, approval.ToolCallId, approval.ToolName, approval.ArgumentsJson,
                    sessionId, "agent", agentId, ownerId, runId, ToolType: "mcp"),
                ct);
            pendingApproval.TrySetResult(approval);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                GatewayToolApprovalRequest? latest = approvalStore.GetById(approval.Id);
                if (string.Equals(latest?.Decision, "approved", StringComparison.Ordinal)) break;
                await Task.Delay(20, ct);
            }
            mailHandler.Send();
            yield return PersistEvent(new GatewayChatToolResult("mail-call", "{}"), sessionId, agentId, runId);
            stepCompleted.TrySetResult();
            yield return PersistEvent(new GatewayChatStreamDone(new GatewayChatResult(
                "agent", agentId, sessionId, "{}", false, new GatewayMetrics(0, 0, 0), [])), sessionId, agentId, runId);
        }

        private NaradPersistedEvent PersistEvent(GatewayChatStreamEvent chatEvent, string sessionId, string agentId, string runId)
        {
            long sequence = Interlocked.Increment(ref eventSequence);
            NaradPersistedEvent evt = NaradTestEventFactory.FromChatEvent(chatEvent, sessionId, agentId, runId,
                sessionSequence: sequence, rootSequence: sequence);
            (eventHub ?? throw new InvalidOperationException("Runtime event hub was not initialized.")).Publish(evt);
            return evt;
        }
    }

    private sealed class AutopilotApiFixture : IAsyncDisposable
    {
        private readonly string root;
        private readonly string? previousEnvironment;
        private readonly string? previousBootstrapPassword;
        private readonly WebApplication app;

        private AutopilotApiFixture(string root, string? previousEnvironment, string? previousBootstrapPassword, WebApplication app)
        {
            this.root = root;
            this.previousEnvironment = previousEnvironment;
            this.previousBootstrapPassword = previousBootstrapPassword;
            this.app = app;
            Client = app.GetTestClient();
        }

        public HttpClient Client { get; }
        public IServiceProvider Services => app.Services;

        public static Task<AutopilotApiFixture> CreateAsync() => CreateCoreAsync(null);

        public static Task<AutopilotApiFixture> CreateWithAgentChatServiceAsync(IAgentChatService agentChatService)
            => CreateCoreAsync(agentChatService);

        private static async Task<AutopilotApiFixture> CreateCoreAsync(IAgentChatService? agentChatService)
        {
            string root = Path.Combine(Path.GetTempPath(), "kautuka-autopilot-api-tests", Guid.NewGuid().ToString("N"));
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
            if (agentChatService is not null)
            {
                builder.Services.RemoveAll<IAgentChatService>();
                builder.Services.AddSingleton(agentChatService);
                builder.Services.AddSingleton<IAgentChatService>(agentChatService);
            }
            builder.Services.Configure<HostOptions>(o => o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

            WebApplication app = builder.Build();
            var database = app.Services.GetRequiredService<KautukaDatabase>();
            GatewayAppBootstrap.ConfigureApp(app, database);
            await app.StartAsync();

            return new AutopilotApiFixture(root, previousEnvironment, previousBootstrapPassword, app);
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
            PersistOwnedAgent(state, "agent-a", userId, "Autopilot Agent");

            return accessToken;
        }

        public string CreateUserWithOwnedAgent(string username, string password, string role, string agentId)
        {
            GatewayState state = app.Services.GetRequiredService<GatewayState>();
            string userId = $"user-{Guid.NewGuid():N}";
            state.Users[username] = new GatewayUser(
                userId,
                username,
                GatewayPasswordHasher.Hash(password),
                role,
                true,
                null,
                null);
            PersistOwnedAgent(state, agentId, userId, $"Agent for {username}");
            return userId;
        }

        public async Task<string> LoginAsync(string username, string password)
        {
            using JsonDocument json = await SendJsonAsync(
                HttpMethod.Post,
                "/api/auth/login",
                new
                {
                    username,
                    password,
                },
                accessToken: null,
                authenticated: false);

            return json.RootElement.GetProperty("accessToken").GetString()
                ?? throw new XunitException("Login response did not include an access token.");
        }

        public string GetUsernameByUserId(string userId)
        {
            GatewayState state = app.Services.GetRequiredService<GatewayState>();
            GatewayUser? user = state.Users.Values.FirstOrDefault(item => string.Equals(item.Id, userId, StringComparison.Ordinal));
            return user?.Username ?? throw new XunitException($"User '{userId}' not found.");
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
}
