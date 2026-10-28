using Kautuka.Gateway.Domain.AgentFiles;
namespace Kautuka.Gateway.Endpoints.Runtime;

internal static partial class GatewayRuntimeEndpointMappings
{
    internal static void MapGatewayRuntimeAgentFileEndpoints(WebApplication app)
    {
        app.MapGet("/api/agents/{agentId}/files", (
            string agentId,
            string? path,
            HttpContext context,
            IAgentFileExchangeService fileExchangeService,
            IGatewayAuthGuard auth) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            AgentFileListOperationResult result = fileExchangeService.ListDirectory(agentId, user, path);
            return result.Status switch
            {
                AgentFileOperationStatus.Success when result.Response is not null
                    => Results.Json(result.Response, KautukaJsonContext.Default.GatewayAgentFileListResponse),
                AgentFileOperationStatus.SandboxDisabled when result.DisabledResponse is not null
                    => Results.Json(result.DisabledResponse, KautukaJsonContext.Default.GatewayAgentFilesDisabledResponse, statusCode: StatusCodes.Status409Conflict),
                AgentFileOperationStatus.Forbidden
                    => Results.Json(new GatewayErrorResponse(result.ErrorMessage ?? "Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden),
                AgentFileOperationStatus.NotFound
                    => Results.Json(new GatewayErrorResponse(result.ErrorMessage ?? "Folder not found."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound),
                _ => Results.Json(new GatewayErrorResponse(result.ErrorMessage ?? "Requested path is not accessible."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest),
            };
        });

        app.MapGet("/api/agents/{agentId}/files/search", (
            string agentId,
            string query,
            int? limit,
            HttpContext context,
            IAgentFileExchangeService fileExchangeService,
            IGatewayAuthGuard auth) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            AgentFileSearchOperationResult result = fileExchangeService.SearchWorkspace(agentId, user, query, limit ?? 8);
            return result.Status switch
            {
                AgentFileOperationStatus.Success when result.Response is not null
                    => Results.Json(result.Response, KautukaJsonContext.Default.GatewayAgentFileSearchResponse),
                AgentFileOperationStatus.SandboxDisabled when result.DisabledResponse is not null
                    => Results.Json(result.DisabledResponse, KautukaJsonContext.Default.GatewayAgentFilesDisabledResponse, statusCode: StatusCodes.Status409Conflict),
                AgentFileOperationStatus.Forbidden
                    => Results.Json(new GatewayErrorResponse(result.ErrorMessage ?? "Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden),
                AgentFileOperationStatus.NotFound
                    => Results.Json(new GatewayErrorResponse(result.ErrorMessage ?? "Folder not found."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound),
                _ => Results.Json(new GatewayErrorResponse(result.ErrorMessage ?? "Requested path is not accessible."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest),
            };
        });

        app.MapPost("/api/agents/{agentId}/files", async (
            string agentId,
            HttpContext context,
            IAgentFileExchangeService fileExchangeService,
            IGatewayAuthGuard auth,
            CancellationToken cancellationToken) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            IFormCollection form = await context.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
            IFormFile? file = form.Files.GetFile("file") ?? (form.Files.Count > 0 ? form.Files[0] : null);
            if (file is null)
            {
                return Results.BadRequest(new GatewayErrorResponse("File upload requires a file."));
            }

            string? path = form.TryGetValue("path", out Microsoft.Extensions.Primitives.StringValues pathValues) ? pathValues.ToString() : null;
            bool overwrite = form.TryGetValue("overwrite", out Microsoft.Extensions.Primitives.StringValues overwriteValues)
                && bool.TryParse(overwriteValues.ToString(), out bool parsedOverwrite)
                && parsedOverwrite;

            using Stream stream = file.OpenReadStream();
            AgentFileUploadOperationResult result = await fileExchangeService.UploadFileAsync(
                agentId,
                user,
                path,
                new AgentFileUploadRequest(file.FileName, file.ContentType ?? "application/octet-stream", file.Length, overwrite, stream),
                cancellationToken).ConfigureAwait(false);

            return result.Status switch
            {
                AgentFileOperationStatus.Success when result.Response is not null
                    => Results.Json(result.Response, KautukaJsonContext.Default.GatewayAgentFileUploadResponse),
                AgentFileOperationStatus.SandboxDisabled when result.DisabledResponse is not null
                    => Results.Json(result.DisabledResponse, KautukaJsonContext.Default.GatewayAgentFilesDisabledResponse, statusCode: StatusCodes.Status409Conflict),
                AgentFileOperationStatus.Forbidden
                    => Results.Json(new GatewayErrorResponse(result.ErrorMessage ?? "Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden),
                AgentFileOperationStatus.NotFound
                    => Results.Json(new GatewayErrorResponse(result.ErrorMessage ?? "Folder not found."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound),
                AgentFileOperationStatus.Conflict
                    => Results.Json(new GatewayErrorResponse(result.ErrorMessage ?? "A file with that name already exists in the selected folder."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status409Conflict),
                _ => Results.Json(new GatewayErrorResponse(result.ErrorMessage ?? "Requested path is not accessible."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest),
            };
        });

        app.MapGet("/api/agents/{agentId}/files/content", (
            string agentId,
            string path,
            HttpContext context,
            IAgentFileExchangeService fileExchangeService,
            IGatewayAuthGuard auth) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            AgentFileDownloadOperationResult result = fileExchangeService.DownloadFile(agentId, user, path);
            return result.Status switch
            {
                AgentFileOperationStatus.Success when result.Response is not null
                    => Results.File(result.Response.Content, result.Response.ContentType, result.Response.FileName, enableRangeProcessing: true),
                AgentFileOperationStatus.SandboxDisabled when result.DisabledResponse is not null
                    => Results.Json(result.DisabledResponse, KautukaJsonContext.Default.GatewayAgentFilesDisabledResponse, statusCode: StatusCodes.Status409Conflict),
                AgentFileOperationStatus.Forbidden
                    => Results.Json(new GatewayErrorResponse(result.ErrorMessage ?? "Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden),
                AgentFileOperationStatus.NotFound
                    => Results.Json(new GatewayErrorResponse(result.ErrorMessage ?? "File not found."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound),
                _ => Results.Json(new GatewayErrorResponse(result.ErrorMessage ?? "Requested path is not accessible."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest),
            };
        });

        app.MapDelete("/api/agents/{agentId}/files", (
            string agentId,
            string? path,
            HttpContext context,
            IAgentFileExchangeService fileExchangeService,
            IGatewayAuthGuard auth) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            AgentFileDeleteOperationResult result = fileExchangeService.DeleteEntry(agentId, user, path);
            return result.Status switch
            {
                AgentFileOperationStatus.Success
                    => Results.NoContent(),
                AgentFileOperationStatus.SandboxDisabled when result.DisabledResponse is not null
                    => Results.Json(result.DisabledResponse, KautukaJsonContext.Default.GatewayAgentFilesDisabledResponse, statusCode: StatusCodes.Status409Conflict),
                AgentFileOperationStatus.Forbidden
                    => Results.Json(new GatewayErrorResponse(result.ErrorMessage ?? "Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden),
                AgentFileOperationStatus.NotFound
                    => Results.Json(new GatewayErrorResponse(result.ErrorMessage ?? "File or folder not found."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound),
                AgentFileOperationStatus.Conflict
                    => Results.Json(new GatewayErrorResponse(result.ErrorMessage ?? "Folder cannot be deleted while it contains files."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status409Conflict),
                _ => Results.Json(new GatewayErrorResponse(result.ErrorMessage ?? "Requested path is not accessible."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest),
            };
        });

        app.MapPost("/api/agents/{agentId}/files/delete", (
            string agentId,
            GatewayAgentFileDeleteRequest request,
            HttpContext context,
            IAgentFileExchangeService fileExchangeService,
            IGatewayAuthGuard auth) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            AgentFileDeleteOperationResult result = fileExchangeService.DeleteEntry(agentId, user, request.Path);
            return result.Status switch
            {
                AgentFileOperationStatus.Success
                    => Results.NoContent(),
                AgentFileOperationStatus.SandboxDisabled when result.DisabledResponse is not null
                    => Results.Json(result.DisabledResponse, KautukaJsonContext.Default.GatewayAgentFilesDisabledResponse, statusCode: StatusCodes.Status409Conflict),
                AgentFileOperationStatus.Forbidden
                    => Results.Json(new GatewayErrorResponse(result.ErrorMessage ?? "Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden),
                AgentFileOperationStatus.NotFound
                    => Results.Json(new GatewayErrorResponse(result.ErrorMessage ?? "File or folder not found."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound),
                AgentFileOperationStatus.Conflict
                    => Results.Json(new GatewayErrorResponse(result.ErrorMessage ?? "Folder cannot be deleted while it contains files."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status409Conflict),
                _ => Results.Json(new GatewayErrorResponse(result.ErrorMessage ?? "Requested path is not accessible."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest),
            };
        });

        app.MapPost("/api/agents/{agentId}/files/folders", (
            string agentId,
            GatewayAgentFileCreateFolderRequest request,
            HttpContext context,
            IAgentFileExchangeService fileExchangeService,
            IGatewayAuthGuard auth) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            AgentFileCreateFolderOperationResult result = fileExchangeService.CreateFolder(agentId, user, request.Path, request.Name);
            return result.Status switch
            {
                AgentFileOperationStatus.Success when result.Response is not null
                    => Results.Json(result.Response, KautukaJsonContext.Default.GatewayAgentFileCreateFolderResponse),
                AgentFileOperationStatus.SandboxDisabled when result.DisabledResponse is not null
                    => Results.Json(result.DisabledResponse, KautukaJsonContext.Default.GatewayAgentFilesDisabledResponse, statusCode: StatusCodes.Status409Conflict),
                AgentFileOperationStatus.Forbidden
                    => Results.Json(new GatewayErrorResponse(result.ErrorMessage ?? "Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden),
                AgentFileOperationStatus.NotFound
                    => Results.Json(new GatewayErrorResponse(result.ErrorMessage ?? "Folder not found."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound),
                AgentFileOperationStatus.Conflict
                    => Results.Json(new GatewayErrorResponse(result.ErrorMessage ?? "A file system entry with that name already exists in the selected folder."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status409Conflict),
                _ => Results.Json(new GatewayErrorResponse(result.ErrorMessage ?? "Requested path is not accessible."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest),
            };
        });
    }
}
