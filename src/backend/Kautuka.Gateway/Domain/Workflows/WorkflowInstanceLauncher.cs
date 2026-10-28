using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.AgentFiles;
using Kautuka.Gateway.Domain.Stores;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Domain.Workflows;

internal sealed record WorkflowInstanceStartResult(
    GatewayWorkflowInstance? Instance,
    int? StatusCode,
    string? Error)
{
    public bool Succeeded => Instance is not null;
}

internal sealed class WorkflowInstanceLauncher(
    IGatewayWorkflowStore workflowStore,
    IWorkflowExecutionService executionService,
    IAgentFilePathAuthorizationService fileAuthorization,
    IGatewayCatalogStore catalog,
    IGatewayUserStore users,
    ILogger<WorkflowInstanceLauncher> logger)
{
    public WorkflowInstanceStartResult Start(
        GatewayWorkflowDefinition definition,
        string ownerUserId,
        JsonObject input,
        string? correlationKey,
        IReadOnlyList<string>? files = null)
    {
        try
        {
            if (definition.Archived || workflowStore.GetDefinition(definition.Id)?.Archived == true)
            {
                return new WorkflowInstanceStartResult(
                    null,
                    StatusCodes.Status409Conflict,
                    "This workflow is archived. Restore it to run it.");
            }

            if (!users.TryGetUserById(ownerUserId, out GatewayUser? owner) || owner is null || !owner.IsActive)
            {
                return new WorkflowInstanceStartResult(null, StatusCodes.Status400BadRequest, "The workflow owner is not available.");
            }
            if (!WorkflowFirstStepConnectorPolicy.ValidateMailConnector(definition, catalog, out string? connectorError))
            {
                return new WorkflowInstanceStartResult(null, StatusCodes.Status400BadRequest, connectorError);
            }
            WorkflowSchemaSubset.ValidateInstance(definition.InputSchema, input);
            WorkflowSchemaSubset.ValidateInstance(definition.WorkingSchema, input);
            WorkflowEnvelopeCanonical.EnsureSafe(input);
            WorkflowArtifact[] artifacts = ValidateAndCreateArtifacts(definition, owner, files ?? []);
            string now = DateTimeOffset.UtcNow.ToString("O");
            string instanceId = $"wfi-{Guid.NewGuid():N}";
            GatewayWorkflowStepAttempt[] attempts = [.. definition.Nodes.Select((node, index) => new GatewayWorkflowStepAttempt(
                Id: $"wattempt-{Guid.NewGuid():N}",
                InstanceId: instanceId,
                NodeId: node.NodeId,
                Attempt: 1,
                RunId: $"wrun-{Guid.NewGuid():N}",
                SessionId: null,
                Status: WorkflowStepAttemptStatuses.Pending,
                FailureCode: null,
                StartedAt: null,
                EndedAt: null,
                CreatedAt: now))];
            JsonObject envelope = JsonNode.Parse(input.ToJsonString())!.AsObject();
            var instance = new GatewayWorkflowInstance(
                Id: instanceId,
                WorkflowId: definition.Id,
                DefinitionRevision: definition.Revision,
                OwnerUserId: ownerUserId,
                CorrelationKey: correlationKey,
                Status: WorkflowInstanceStatuses.Running,
                StopRequested: false,
                CurrentNodeId: definition.Nodes[0].NodeId,
                Envelope: envelope,
                EnvelopeHash: WorkflowEnvelopeCanonical.Sha256Hex(envelope),
                EnvelopeRevision: 0,
                Snapshot: definition,
                Steps: attempts,
                FailureCode: null,
                Error: null,
                CreatedAt: now,
                UpdatedAt: now,
                StartedAt: now,
                EndedAt: null);

            GatewayWorkflowInstance created = workflowStore.CreateInstance(instance, artifacts.Select(artifact => artifact with { InstanceId = instanceId }).ToArray());
            executionService.Enqueue(created.Id);
            return new WorkflowInstanceStartResult(created, null, null);
        }
        catch (WorkflowSchemaException ex)
        {
            return new WorkflowInstanceStartResult(null, StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new WorkflowInstanceStartResult(null, StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (FileNotFoundException ex)
        {
            return new WorkflowInstanceStartResult(null, StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            return new WorkflowInstanceStartResult(null, StatusCodes.Status409Conflict, "An overlapping instance is already running for this correlation key.");
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or NotSupportedException or JsonException)
        {
            WorkflowInstanceLauncherLog.StartFailed(logger, ex, definition.Id);
            return new WorkflowInstanceStartResult(null, StatusCodes.Status500InternalServerError, "Couldn't start this run.");
        }
    }

    private WorkflowArtifact[] ValidateAndCreateArtifacts(
        GatewayWorkflowDefinition definition,
        GatewayUser owner,
        IReadOnlyList<string> files)
    {
        if (files.Count == 0) return [];
        GatewayWorkflowNode firstNode = definition.Nodes[0];
        List<WorkflowArtifact> artifacts = [];
        foreach (string requestedPath in files)
        {
            AgentFilePathAuthorizationResult authorization = fileAuthorization.Authorize(firstNode.AgentId, owner, requestedPath);
            if (authorization.Status != AgentFileOperationStatus.Success || !File.Exists(authorization.ResolvedPath))
            {
                throw new FileNotFoundException("A selected file is not available in the first step agent's workspace.", requestedPath);
            }

            string storagePath = authorization.RelativePath.Replace('\\', '/');
            using FileStream stream = File.OpenRead(authorization.ResolvedPath);
            string sha256 = Convert.ToHexString(SHA256.HashData(stream));
            artifacts.Add(new WorkflowArtifact($"wartifact-{Guid.NewGuid():N}", string.Empty, sha256, Path.GetFileName(storagePath), storagePath, DateTimeOffset.UtcNow.ToString("O")));
        }
        return [.. artifacts];
    }
}

internal static partial class WorkflowInstanceLauncherLog
{
    [LoggerMessage(EventId = 7101, Level = LogLevel.Error, Message = "Failed to start workflow instance for {WorkflowId}")]
    public static partial void StartFailed(ILogger logger, Exception exception, string workflowId);
}
