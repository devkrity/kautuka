using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.Gateway.Endpoints.Settings;

internal static partial class GatewaySettingsEndpointMappings
{
    private static void MapPersonaSettingsEndpoints(WebApplication app)
    {
        app.MapGet("/api/settings/persona/definitions", (IGatewaySettingsStore settings, IGatewayAuthGuard auth, HttpContext context) =>
        {
            if (!auth.TryAuthorize(context, out _))
            {
                return Results.Unauthorized();
            }

            return Results.Json(settings.GetPersonaDefinitionsPayload(), KautukaJsonContext.Default.GatewayPersonaDefinitionsResponse);
        });

        app.MapPut("/api/settings/persona/definitions", (JsonElement body, IGatewaySettingsStore settings, IGatewayAuthGuard auth, HttpContext context) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out _);
            if (unauthorized is not null)
            {
                return unauthorized;
            }

            try
            {
                settings.SavePersonaDefinitions(body);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new GatewayErrorResponse(ex.Message));
            }

            return Results.Json(settings.GetPersonaDefinitionsPayload(), KautukaJsonContext.Default.GatewayPersonaDefinitionsResponse);
        });

        app.MapGet("/api/settings/persona/profiles", (IGatewaySettingsStore settings, IGatewayAuthGuard auth, HttpContext context) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out _);
            if (unauthorized is not null)
            {
                return unauthorized;
            }

            return Results.Json(settings.GetPersonaProfilesPayload(), KautukaJsonContext.Default.GatewayPersonaProfilesResponse);
        });

        app.MapPut("/api/settings/persona/profiles/{agentId}", async (string agentId, JsonElement body, IGatewayCatalogStore catalog, IGatewaySettingsStore settings, IGatewayAuthGuard auth, HttpContext context, CancellationToken ct) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out _);
            if (unauthorized is not null)
            {
                return unauthorized;
            }

            if (!catalog.TryGetAgent(agentId, out GatewayAgent? agent) || agent is null)
            {
                return Results.Json(new GatewayErrorResponse("Agent not found"), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
            }

            if (body.ValueKind != JsonValueKind.Object)
            {
                return Results.BadRequest(new GatewayErrorResponse("Persona profile payload must be a JSON object."));
            }

            try
            {
                GatewayAgentUpsertRequest request = new()
                {
                    Name = agent.Name,
                    ModelId = agent.ModelId,
                    SystemPrompt = agent.SystemPrompt,
                    SuperExecMode = agent.SuperExecMode,
                    McpBindings = [.. (agent.McpBindings ?? []).Select(static binding => new GatewayAgentMcpBindingPayload
                    {
                        ConnectorId = binding.ConnectorId,
                        Context = binding.Context,
                    })],
                    SkillAttachments = [.. agent.SkillAttachments.Select(static attachment => new GatewayAgentSkillAttachmentPayload
                    {
                        SkillId = attachment.SkillId,
                        Mode = attachment.Mode switch
                        {
                            SkillAttachmentMode.Disabled => "disabled",
                            SkillAttachmentMode.Trusted => "trusted",
                            SkillAttachmentMode.RequireApproval => "requireApproval",
                            _ => "disabled",
                        },
                    })],
                    Session = GatewayAgentSettingsMapper.NormalizeStoredSession(agent.Session),
                    Sandbox = GatewayAgentSettingsMapper.NormalizeStoredSandbox(agent.Sandbox),
                    ApprovalConfig = GatewayAgentSettingsMapper.NormalizeStoredApprovalConfig(agent.ApprovalConfig),
                    Persona = JsonObject.Create(body) ?? new JsonObject(),
                    ToolExposure = GatewayAgentSettingsMapper.NormalizeStoredToolExposure(agent.ToolExposure),
                };

                _ = await catalog.SaveAgentAsync(agentId, request, agent.OwnerUserId, ct).ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new GatewayErrorResponse(ex.Message));
            }
            return Results.Json(settings.GetPersonaProfilesPayload(), KautukaJsonContext.Default.GatewayPersonaProfilesResponse);
        });
    }
}
