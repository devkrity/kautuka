using System.Text.Json;

using Kautuka.ChannelContracts.Assets;
using Kautuka.Gateway.Domain.AgentFiles;
using Kautuka.Gateway.Domain.Assets;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Domain.Execution;

internal sealed partial class AgentChatService
{
    private static string? FormatHitlExpiresAt(TimeSpan? hitlExpiry)
    {
        if (hitlExpiry is null || hitlExpiry.Value <= TimeSpan.Zero)
        {
            return null;
        }

        return DateTimeOffset.UtcNow.Add(hitlExpiry.Value).ToString("O");
    }

    private long? TryResolveAgentDefinitionRevision(string? agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return null;
        }

        if (gatewayState.Agents.TryGetValue(agentId, out GatewayAgent? catalogAgent)
            && catalogAgent.Revision > 0)
        {
            return catalogAgent.Revision;
        }

        string storageLocatorKey = gatewayState.AgentStorageLocators.TryGetValue(agentId, out string? locator)
            && !string.IsNullOrWhiteSpace(locator)
                ? locator
                : agentId;
        try
        {
            if (gatewayState.AgentDefinitionStore.TryLoadLatestDefinition(storageLocatorKey, out long revision) is not null
                && revision > 0)
            {
                return revision;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or SqliteException or JsonException)
        {
            AgentChatServiceLog.AgentDefinitionRevisionLookupFailed(logger, exception, agentId, storageLocatorKey);
        }

        return null;
    }

    private static string? TryReadDeniedToolMessage(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(resultJson);
            if (document.RootElement.TryGetProperty("result", out JsonElement result)
                && result.ValueKind == JsonValueKind.String)
            {
                return result.GetString();
            }
        }
        catch (JsonException)
        {
            // Fall through to default deny message.
        }

        return null;
    }

    private static GatewayChatResult Error(string targetType, string targetId, string sessionId, string error) =>
        new(targetType, targetId, sessionId, $"[{error}]", false, new GatewayMetrics(0, 0, 0), null, null, [],
            Run: new GatewayRunState(GatewayRunPhases.Failed));

    private async Task<IList<ChatMessage>> BuildTurnMessagesAsync(
        string sessionId,
        string? userId,
        string message,
        IReadOnlyList<GatewayConversationAsset> inputAssets,
        IReadOnlyList<AgentWorkspaceFileReference> workspaceFiles,
        ToolApprovalResponseInfo? approvalResponse,
        bool hasExistingSessionHistory,
        bool resultCompletesExistingToolCall,
        CancellationToken ct)
    {
        if (approvalResponse is null)
        {
            return [await BuildUserTurnMessageAsync(sessionId, userId, message, inputAssets, workspaceFiles, ct).ConfigureAwait(false)];
        }

        string resumeMessage = hasExistingSessionHistory ? string.Empty : message;
        return BuildApprovalTurnMessages(sessionId, userId, resumeMessage, approvalResponse, logger, resultCompletesExistingToolCall);
    }

    internal static IList<ChatMessage> BuildApprovalTurnMessages(
        string sessionId,
        string? userId,
        string message,
        ToolApprovalResponseInfo approvalResponse,
        ILogger logger,
        bool resultCompletesExistingToolCall = false)
    {

        AgentToolInvocationContextState? invocationContext = AgentToolInvocationContext.Current;
        AgentChatServiceLog.ReplayingApprovalResponse(
            logger,
            sessionId,
            userId,
            approvalResponse.ToolName,
            approvalResponse.ToolCallId,
            approvalResponse.RunId,
            approvalResponse.Approved,
            invocationContext?.CallerSessionId);

        List<ChatMessage> approvalTurnMessages = [];
        if (!string.IsNullOrWhiteSpace(message))
        {
            approvalTurnMessages.Add(new ChatMessage(ChatRole.User, message));
        }

        IDictionary<string, object?>? args = null;
        if (!string.IsNullOrEmpty(approvalResponse.ArgumentsJson))
        {
            try
            {
                args = JsonSerializer.Deserialize(approvalResponse.ArgumentsJson, KautukaJsonContext.Default.DictionaryStringObject);
            }
            catch (JsonException ex)
            {
                AgentChatServiceLog.ToolApprovalArgumentsParseFailed(logger, ex, approvalResponse.ToolName, approvalResponse.ToolCallId);
            }
        }

        IReadOnlyList<ToolApprovalResponseInfo> pack = approvalResponse.EnumeratePack();
        List<AIContent> functionCalls = [];
        List<AIContent> functionResults = [];
        List<AIContent> approvalContents = [];
        foreach (ToolApprovalResponseInfo packResponse in pack)
        {
            IDictionary<string, object?>? packArgs = args;
            if (!string.Equals(packResponse.RequestId, approvalResponse.RequestId, StringComparison.Ordinal))
            {
                packArgs = null;
                if (!string.IsNullOrEmpty(packResponse.ArgumentsJson))
                {
                    try
                    {
                        packArgs = JsonSerializer.Deserialize(packResponse.ArgumentsJson, KautukaJsonContext.Default.DictionaryStringObject);
                    }
                    catch (JsonException ex)
                    {
                        AgentChatServiceLog.ToolApprovalArgumentsParseFailed(logger, ex, packResponse.ToolName, packResponse.ToolCallId);
                    }
                }
            }

            var packCall = new FunctionCallContent(packResponse.ToolCallId, packResponse.ToolName, packArgs);
            if (string.IsNullOrWhiteSpace(packResponse.ResultJson))
            {
                approvalContents.Add(new ToolApprovalResponseContent(packResponse.RequestId, packResponse.Approved, packCall)
                {
                    Reason = packResponse.Reason,
                });
                continue;
            }

            object? resultPayload = packResponse.ResultJson;
            try
            {
                resultPayload = JsonSerializer.Deserialize(packResponse.ResultJson, KautukaJsonContext.Default.Object);
            }
            catch (JsonException)
            {
            }

            if (!resultCompletesExistingToolCall)
            {
                functionCalls.Add(packCall);
            }

            functionResults.Add(new FunctionResultContent(packResponse.ToolCallId, resultPayload));
        }

        if (functionCalls.Count > 0)
        {
            approvalTurnMessages.Add(new ChatMessage(ChatRole.Assistant, functionCalls));
        }

        if (functionResults.Count > 0)
        {
            approvalTurnMessages.Add(new ChatMessage(ChatRole.Tool, functionResults));
        }

        if (approvalContents.Count > 0)
        {
            approvalTurnMessages.Add(new ChatMessage(ChatRole.Tool, approvalContents));
        }

        return approvalTurnMessages;
    }

    private async Task<ChatMessage> BuildUserTurnMessageAsync(
        string sessionId,
        string? userId,
        string message,
        IReadOnlyList<GatewayConversationAsset> inputAssets,
        IReadOnlyList<AgentWorkspaceFileReference> workspaceFiles,
        CancellationToken ct)
    {
        if (inputAssets.Count == 0 && workspaceFiles.Count == 0)
        {
            return new ChatMessage(ChatRole.User, message);
        }

        List<AIContent> contents = [new TextContent(BuildUserTurnText(message, inputAssets, workspaceFiles))];

        foreach (GatewayConversationAsset asset in inputAssets)
        {
            if (!assetStore.TryOpenAsset(sessionId, asset.Id, userId, out AssetDownloadResult? result) || result is null)
            {
                AgentChatServiceLog.InputAssetOpenFailed(logger, asset.Id, sessionId);
                continue;
            }

            using (result.Content)
            {
                // Classify the attachment
                var category = assetIngestionService.ClassifyAttachment(result.FileName, result.ContentType);

                switch (category)
                {
                    case AttachmentCategory.DirectText:
                        {
                            // Text files go through as text content
                            try
                            {
                                using var reader = new StreamReader(result.Content);
                                string textContent = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                                if (!string.IsNullOrWhiteSpace(textContent))
                                {
                                    contents.Add(new TextContent($"[{result.FileName}]\n{textContent}"));
                                }
                            }
                            catch (IOException ex)
                            {
                                AgentChatServiceLog.AttachmentProcessingFailed(logger, asset.Id, "text", ex);
                            }
                            catch (ObjectDisposedException ex)
                            {
                                AgentChatServiceLog.AttachmentProcessingFailed(logger, asset.Id, "text", ex);
                            }
                            break;
                        }

                    case AttachmentCategory.DirectImage:
                        {
                            // Images go through as direct binary
                            using var imgBuffer = new MemoryStream();
                            await result.Content.CopyToAsync(imgBuffer, ct).ConfigureAwait(false);
                            if (imgBuffer.Length > 0)
                            {
                                contents.Add(new DataContent(imgBuffer.ToArray(), result.ContentType)
                                {
                                    Name = result.FileName,
                                });
                            }
                            break;
                        }

                    case AttachmentCategory.ParseRequiredDocument:
                        {
                            // Copy stream into MemoryStream once so it can be reused
                            using var memoryStream = new MemoryStream();
                            await result.Content.CopyToAsync(memoryStream, ct).ConfigureAwait(false);
                            memoryStream.Position = 0;

                            bool parsedContentUsed = false;

                            if (gatewayState.SystemSettings.DocumentIngestionEnabled)
                            {
                                try
                                {
                                    var parsedAsset = await assetIngestionService.IngestParseRequiredAsync(sessionId, asset.Id, result.FileName, memoryStream, ct).ConfigureAwait(false);
                                    if (parsedAsset.IsSuccessful && !string.IsNullOrEmpty(parsedAsset.NormalizedMarkdown))
                                    {
                                        contents.Add(new TextContent($"[{result.FileName}]\n{parsedAsset.NormalizedMarkdown}"));
                                        AgentChatServiceLog.DocumentParseSuccessUsed(logger, asset.Id, result.FileName);
                                        parsedContentUsed = true;
                                    }
                                    else
                                    {
                                        AgentChatServiceLog.DocumentParseFailed(logger, asset.Id, result.FileName, parsedAsset.SummaryMessage);
                                        // Fall through to fallback logic
                                    }
                                }
                                catch (IOException ex)
                                {
                                    AgentChatServiceLog.DocumentParseFailed(logger, asset.Id, result.FileName, ex.Message);
                                    // Fall through to fallback logic
                                }
                                catch (InvalidOperationException ex)
                                {
                                    AgentChatServiceLog.DocumentParseFailed(logger, asset.Id, result.FileName, ex.Message);
                                    // Fall through to fallback logic
                                }
                            }
                            else
                            {
                                AgentChatServiceLog.DocumentRequiresParsingButParsingDisabled(logger, asset.Id, result.FileName);
                            }

                            // Check for PDF fallback
                            if (!parsedContentUsed
                                && result.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) &&
                                gatewayState.SystemSettings.DocumentIngestionPdfImageFallbackEnabled &&
                                gatewayState.SystemSettings.DocumentIngestionVisionRouteAllowed)
                            {
                                // Reset stream position and send as DataContent for vision fallback
                                memoryStream.Position = 0;
                                contents.Add(new DataContent(memoryStream.ToArray(), result.ContentType)
                                {
                                    Name = result.FileName,
                                });
                                AgentChatServiceLog.PdfFallbackUsed(logger, asset.Id, result.FileName);
                            }
                            else
                            {
                                // Skip if no fallback or parse already handled the document.
                            }
                            break;
                        }

                    case AttachmentCategory.UnsupportedBinary:
                        {
                            // Unsupported binaries: do not send
                            AgentChatServiceLog.AttachmentCategoryNotSupported(logger, asset.Id, result.FileName, "unsupported binary");
                            break;
                        }
                }
            }
        }

        foreach (AgentWorkspaceFileReference workspaceFile in workspaceFiles)
        {
            await AppendWorkspaceFileByPathAsync(contents, workspaceFile, sessionId, ct).ConfigureAwait(false);
        }

        return new ChatMessage(ChatRole.User, contents);
    }

    private async Task AppendWorkspaceFileByPathAsync(
        List<AIContent> contents,
        AgentWorkspaceFileReference workspaceFile,
        string sessionId,
        CancellationToken ct)
    {
        using FileStream content = WorkspaceFile.OpenRead(workspaceFile.ResolvedPath);
        await AppendWorkspaceFileContentAsync(contents, workspaceFile, content, sessionId, ct).ConfigureAwait(false);
    }

    private async Task AppendWorkspaceFileContentAsync(
        List<AIContent> contents,
        AgentWorkspaceFileReference workspaceFile,
        Stream content,
        string sessionId,
        CancellationToken ct)
    {
        AttachmentCategory category = assetIngestionService.ClassifyAttachment(workspaceFile.FileName, workspaceFile.ContentType);

        switch (category)
        {
            case AttachmentCategory.DirectText:
                using (var reader = new StreamReader(content, leaveOpen: true))
                {
                    string textContent = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                    content.Position = 0;
                    if (!string.IsNullOrWhiteSpace(textContent))
                    {
                        contents.Add(new TextContent($"[{workspaceFile.FileName}]\n{textContent}"));
                    }
                }
                break;

            case AttachmentCategory.DirectImage:
                using (var imgBuffer = new MemoryStream())
                {
                    await content.CopyToAsync(imgBuffer, ct).ConfigureAwait(false);
                    if (imgBuffer.Length > 0)
                    {
                        contents.Add(new DataContent(imgBuffer.ToArray(), workspaceFile.ContentType)
                        {
                            Name = workspaceFile.FileName,
                        });
                    }
                }
                break;

            case AttachmentCategory.ParseRequiredDocument:
                using (var memoryStream = new MemoryStream())
                {
                    await content.CopyToAsync(memoryStream, ct).ConfigureAwait(false);
                    memoryStream.Position = 0;
                    ParsedAsset parsedAsset = await assetIngestionService.IngestParseRequiredAsync(
                        sessionId,
                        $"workspace-file-{workspaceFile.RelativePath.Replace('/', '_')}",
                        workspaceFile.FileName,
                        memoryStream,
                        ct).ConfigureAwait(false);
                    if (parsedAsset.IsSuccessful && !string.IsNullOrEmpty(parsedAsset.NormalizedMarkdown))
                    {
                        contents.Add(new TextContent($"[{workspaceFile.FileName}]\n{parsedAsset.NormalizedMarkdown}"));
                    }
                }
                break;

            case AttachmentCategory.UnsupportedBinary:
                break;
        }
    }

    private static string BuildUserTurnText(
        string message,
        IReadOnlyList<GatewayConversationAsset> inputAssets,
        IReadOnlyList<AgentWorkspaceFileReference> workspaceFiles)
    {
        if (inputAssets.Count == 0 && workspaceFiles.Count == 0)
        {
            return message;
        }

        string text = string.IsNullOrWhiteSpace(message)
            ? "Use the attached conversation assets as context for this request."
            : message.Trim();

        string manifest = string.Join(
            Environment.NewLine,
            inputAssets.Select(static asset => $"- {asset.FileName} ({asset.ContentType}, {asset.SizeBytes} bytes, origin: {asset.Origin})"));

        string workspaceManifest = string.Join(
            Environment.NewLine,
            workspaceFiles.Select(static file => $"- {file.RelativePath} ({file.ContentType}, {file.SizeBytes} bytes, origin: workspaceFile)"));

        string assetsSection = inputAssets.Count == 0
            ? string.Empty
            : $"Attached conversation assets:{Environment.NewLine}{manifest}";
        string workspaceSection = workspaceFiles.Count == 0
            ? string.Empty
            : $"Referenced workspace files:{Environment.NewLine}{workspaceManifest}";
        string separator = assetsSection.Length > 0 && workspaceSection.Length > 0
            ? Environment.NewLine + Environment.NewLine
            : string.Empty;

        return $"{text}{Environment.NewLine}{Environment.NewLine}{assetsSection}{separator}{workspaceSection}";
    }
}

internal static partial class AgentChatServiceLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Agent {AgentId} chat: {TokenIn} in / {TokenOut} out / {ElapsedMs}ms")]
    public static partial void AgentChatCompleted(ILogger logger, string agentId, long tokenIn, long tokenOut, long elapsedMs);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Workspace validation failed for agent {AgentId}")]
    public static partial void AgentWorkspaceValidationFailed(ILogger logger, Exception exception, string agentId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Chat failed for agent {AgentId}")]
    public static partial void AgentChatFailed(ILogger logger, Exception exception, string agentId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Stream failed for agent {AgentId}")]
    public static partial void AgentStreamFailed(ILogger logger, Exception exception, string agentId);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Agent {AgentId} stream: {TokenIn} in / {TokenOut} out / {ElapsedMs}ms")]
    public static partial void AgentStreamCompleted(ILogger logger, string agentId, long tokenIn, long tokenOut, long elapsedMs);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Failed to parse tool approval arguments JSON for tool {ToolName} (call {ToolCallId})")]
    public static partial void ToolApprovalArgumentsParseFailed(ILogger logger, Exception exception, string toolName, string toolCallId);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "Failed to persist generated assistant asset {FileName} for session {SessionId}")]
    public static partial void AgentOutputAssetPersistFailed(ILogger logger, Exception exception, string sessionId, string fileName);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "Failed to reopen input asset {AssetId} for session {SessionId}; continuing without binary attachment")]
    public static partial void InputAssetOpenFailed(ILogger logger, string assetId, string sessionId);

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning, Message = "Failed to process attachment {AssetId} as {Category}")]
    public static partial void AttachmentProcessingFailed(ILogger logger, string assetId, string category, Exception exception);

    [LoggerMessage(EventId = 10, Level = LogLevel.Information, Message = "Document {AssetId} ({FileName}) requires parsing but parsing is disabled; attachment will not be sent to model")]
    public static partial void DocumentRequiresParsingButParsingDisabled(ILogger logger, string assetId, string fileName);

    [LoggerMessage(EventId = 11, Level = LogLevel.Information, Message = "Attachment {AssetId} ({FileName}) is {Category} and will not be sent to model")]
    public static partial void AttachmentCategoryNotSupported(ILogger logger, string assetId, string fileName, string category);

    [LoggerMessage(EventId = 12, Level = LogLevel.Information, Message = "Document {AssetId} ({FileName}) parsed successfully and markdown content used")]
    public static partial void DocumentParseSuccessUsed(ILogger logger, string assetId, string fileName);

    [LoggerMessage(EventId = 13, Level = LogLevel.Warning, Message = "Document {AssetId} ({FileName}) parsing failed: {Reason}; falling back to other handling")]
    public static partial void DocumentParseFailed(ILogger logger, string assetId, string fileName, string? reason);

    [LoggerMessage(EventId = 14, Level = LogLevel.Information, Message = "PDF {AssetId} ({FileName}) using image fallback for vision processing")]
    public static partial void PdfFallbackUsed(ILogger logger, string assetId, string fileName);

    [LoggerMessage(EventId = 15, Level = LogLevel.Information, Message = "Publishing HitlApprovalRequested from AgentChatService for request {RequestId} tool={ToolName} runId={RunId} userId={UserId}")]
    public static partial void PublishingHitlApprovalRequested(ILogger logger, string requestId, string toolName, string runId, string? userId);

    [LoggerMessage(EventId = 27, Level = LogLevel.Information, Message = "Auto-approving tool {ToolName} for agent {AgentId} because approval policy does not require HITL (request {RequestId}, run {RunId})")]
    public static partial void AutoApprovingToolByPolicy(ILogger logger, string requestId, string toolName, string agentId, string runId);

    [LoggerMessage(EventId = 28, Level = LogLevel.Warning, Message = "Ignoring framework function result for tool call {ToolCallId}; no open canonical tool call was observed")]
    public static partial void OrphanedFunctionResultIgnored(ILogger logger, string toolCallId);

    [LoggerMessage(EventId = 29, Level = LogLevel.Error, Message = "Framework tool execution failed for agent {AgentId}, session {SessionId}, run {RunId}, tool {ToolName}, call {ToolCallId}; code={FailureCode}, exceptionType={ExceptionType}")]
    public static partial void FrameworkToolExecutionFailed(ILogger logger, string agentId, string sessionId, string runId, string toolCallId, string toolName, string failureCode, string exceptionType);

    [LoggerMessage(EventId = 16, Level = LogLevel.Debug, Message = "Published HitlApprovalRequested from AgentChatService for request {RequestId}")]
    public static partial void PublishedHitlApprovalRequested(ILogger logger, string requestId);

    [LoggerMessage(EventId = 30, Level = LogLevel.Warning, Message = "Channel HITL notify failed for request {RequestId}; pause continues")]
    public static partial void HitlChannelNotifyFailed(ILogger logger, Exception exception, string requestId);

    [LoggerMessage(EventId = 17, Level = LogLevel.Information, Message = "Detected agent-tool approval boundary for session {SessionId}, request {RequestId}; continuationRun={ContinuationRunId}, nextCheckpointIndex={NextCheckpointIndex}")]
    public static partial void AgentToolApprovalBoundaryDetected(ILogger logger, string sessionId, string requestId, string? continuationRunId, int nextCheckpointIndex);

    [LoggerMessage(EventId = 18, Level = LogLevel.Information, Message = "Persisted agent-tool approval checkpoint for session {SessionId}, request {RequestId}; checkpoint={CheckpointId}, checkpointIndex={CheckpointIndex}")]
    public static partial void AgentToolApprovalCheckpointPersisted(ILogger logger, string sessionId, string requestId, string checkpointId, int checkpointIndex);

    [LoggerMessage(EventId = 19, Level = LogLevel.Debug, Message = "Agent-tool approval lookup session missing for session {SessionId}")]
    public static partial void AgentToolApprovalLookupSessionMissing(ILogger logger, string sessionId);

    [LoggerMessage(EventId = 20, Level = LogLevel.Information, Message = "Matched agent-tool approval lookup for session {SessionId}, request {RequestId}; childSession={ChildSessionId}")]
    public static partial void AgentToolApprovalLookupMatched(ILogger logger, string sessionId, string requestId, string? childSessionId);

    [LoggerMessage(EventId = 24, Level = LogLevel.Information, Message = "Finalizing stream for session {SessionId}, agent {AgentId}; result={Result}, suppressPausedAssistantOutput={SuppressPausedAssistantOutput}, responseLength={ResponseLength}, thinkingLength={ThinkingLength}, toolCallCount={ToolCallCount}, approvalRequestCount={ApprovalRequestCount}")]
    private static partial void FinalizingStreamCore(ILogger logger, string sessionId, string agentId, string result, bool suppressPausedAssistantOutput, int responseLength, int thinkingLength, int toolCallCount, int approvalRequestCount);

    [LoggerMessage(EventId = 25, Level = LogLevel.Information, Message = "Replaying approval response into agent run for session {SessionId}; userId={UserId}, toolName={ToolName}, toolCallId={ToolCallId}, runId={RunId}, approved={Approved}, agentToolCallerSessionId={AgentToolCallerSessionId}")]
    private static partial void ReplayingApprovalResponseCore(ILogger logger, string sessionId, string? userId, string toolName, string toolCallId, string? runId, bool approved, string? agentToolCallerSessionId);

    public static void ReplayingApprovalResponse(ILogger logger, string sessionId, string? userId, string toolName, string toolCallId, string? runId, bool approved, string? agentToolCallerSessionId)
    {
        ReplayingApprovalResponseCore(logger, sessionId, userId, toolName, toolCallId, runId, approved, agentToolCallerSessionId);
    }

    public static void AaatFinalizingStream(ILogger logger, string sessionId, string agentId, string result, bool suppressPausedAssistantOutput, int responseLength, int thinkingLength, int toolCallCount, int approvalRequestCount)
    {
        FinalizingStreamCore(logger, sessionId, agentId, result, suppressPausedAssistantOutput, responseLength, thinkingLength, toolCallCount, approvalRequestCount);
    }

    [LoggerMessage(EventId = 31, Level = LogLevel.Warning, Message = "Failed to resolve agent definition revision for HITL stamp; agent={AgentId}, locator={StorageLocatorKey}")]
    public static partial void AgentDefinitionRevisionLookupFailed(ILogger logger, Exception exception, string agentId, string storageLocatorKey);

}
