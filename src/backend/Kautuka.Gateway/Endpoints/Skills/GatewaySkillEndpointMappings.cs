using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Skills;
using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.Gateway.Endpoints.Skills;

internal static class GatewaySkillEndpointMappings
{
    public static WebApplication MapGatewaySkillEndpoints(this WebApplication app)
    {
        app.MapGet("/api/skills", (IGatewayCatalogStore catalog, IGatewayAuthGuard auth, HttpContext context) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
                return Results.Unauthorized();

            return Results.Json(
                new GatewaySkillsResponse([.. catalog.ListSkills().Where(skill => GatewayOwnershipAccess.CanAccessSkill(user, skill)).Select(ToSkillPayload)]),
                KautukaJsonContext.Default.GatewaySkillsResponse);
        });

        app.MapGet("/api/skills/{skillId}", (string skillId, IGatewayCatalogStore catalog, IGatewayAuthGuard auth, HttpContext context) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
                return Results.Unauthorized();

            if (!catalog.TryGetSkill(skillId, out GatewaySkill? skill) || !GatewayOwnershipAccess.CanAccessSkill(user, skill))
            {
                return Results.Json(new GatewayErrorResponse("Skill not found"), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
            }

            ISkillPackageService packageService = context.RequestServices.GetRequiredService<ISkillPackageService>();
            SkillPackageInspection inspection;
            try
            {
                inspection = packageService.Inspect(ToDefinition(skill!));
            }
            catch (FileNotFoundException)
            {
                return Results.Json(new GatewayErrorResponse("Skill package files are missing from disk. Re-import the skill."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Json(
                new GatewaySkillInspectionResponse(
                    ToSkillPayload(skill!),
                    inspection.InstructionsMarkdown,
                    [.. inspection.ResourcePaths],
                    [.. inspection.ScriptPaths]),
                KautukaJsonContext.Default.GatewaySkillInspectionResponse);
        });

        app.MapPost("/api/skills/validate", async (HttpRequest request, ISkillPackageService packageService, IGatewayAuthGuard auth, HttpContext context, CancellationToken ct) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
                return Results.Unauthorized();

            if (!CanImportSkills(user))
            {
                return Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }

            IFormFile? archive = await GetArchiveAsync(request, ct).ConfigureAwait(false);
            if (archive is null)
            {
                return Results.Json(new GatewayErrorResponse("Skill archive file is required."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
            }

            IFormCollection form = await request.ReadFormAsync(ct).ConfigureAwait(false);
#pragma warning disable CA2007 // await using on Stream — no SynchronizationContext in ASP.NET Core
            await using Stream stream = archive.OpenReadStream();
#pragma warning restore CA2007
            try
            {
                SkillPackageImportOutcome outcome = await packageService.ValidateImportOutcomeAsync(stream, BuildImportRequest(form, archive.FileName), ct).ConfigureAwait(false);
                return Results.Json(ToImportResultPayload(outcome), KautukaJsonContext.Default.GatewaySkillImportResult);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new GatewayErrorResponse(ex.Message), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        app.MapPost("/api/skills/import", async (HttpRequest request, IGatewayCatalogStore catalog, ISkillPackageService packageService, IGatewayAuthGuard auth, HttpContext context, CancellationToken ct) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
                return Results.Unauthorized();

            if (!CanImportSkills(user))
            {
                return Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }

            IFormFile? archive = await GetArchiveAsync(request, ct).ConfigureAwait(false);
            if (archive is null)
            {
                return Results.Json(new GatewayErrorResponse("Skill archive file is required."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
            }

            IFormCollection form = await request.ReadFormAsync(ct).ConfigureAwait(false);
            SkillPackageImportRequest importRequest = BuildImportRequest(form, archive.FileName);
            using var archiveBuffer = new MemoryStream();
            try
            {
#pragma warning disable CA2007 // await using on Stream — no SynchronizationContext in ASP.NET Core
                await using (Stream stream = archive.OpenReadStream())
                {
                    await CopyBoundedArchiveAsync(stream, archiveBuffer, ct).ConfigureAwait(false);
                }
#pragma warning restore CA2007
                archiveBuffer.Position = 0;
                SkillPackageImportOutcome preview = await packageService.ValidateImportOutcomeAsync(archiveBuffer, importRequest, ct).ConfigureAwait(false);
                foreach (SkillPackageInspection inspection in preview.Skills)
                {
                    if (BuiltInSkillCatalog.IsSystemLockedSkill(inspection.Definition.SkillId)
                        && !GatewayOwnershipAccess.IsAdmin(user))
                    {
                        return Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
                    }

                    if (catalog.TryGetSkill(inspection.Definition.SkillId, out GatewaySkill? existing)
                        && existing is not null
                        && !GatewayOwnershipAccess.CanManageSkill(user, existing))
                    {
                        return Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
                    }
                }
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new GatewayErrorResponse(ex.Message), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
            }

            SkillPackageImportOutcome outcome;
            try
            {
                archiveBuffer.Position = 0;
                outcome = await packageService.ImportOutcomeAsync(archiveBuffer, importRequest, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new GatewayErrorResponse(ex.Message), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status400BadRequest);
            }

            string scope = GatewayOwnershipAccess.ResolveCatalogScope(user, GetOptionalFormValue(form, "scope"));
            string? ownerUserId = GatewayOwnershipAccess.ResolveCatalogOwnerUserId(scope, user);

            var persisted = new List<SkillPackageInspection>();
            foreach (SkillPackageInspection inspection in outcome.Skills)
            {
                JsonObject payload = new()
                {
                    ["name"] = inspection.Definition.Name,
                    ["description"] = inspection.Definition.Description,
                    ["packageHash"] = inspection.Definition.PackageHash,
                    ["status"] = inspection.Definition.Status,
                    ["packagePath"] = inspection.Definition.PackagePath,
                    ["hasResources"] = inspection.Definition.HasResources,
                    ["hasScripts"] = inspection.Definition.HasScripts,
                    ["enabled"] = inspection.Definition.Enabled,
                    ["importTimestamp"] = inspection.Definition.ImportTimestamp.ToString("O"),
                    ["sourceMetadata"] = JsonObject.Create(JsonSerializer.SerializeToElement(
                        inspection.Definition.SourceMetadata,
                        KautukaJsonContext.Default.DictionaryStringString)),
                };

                GatewaySkill saved = catalog.SaveSkill(
                    inspection.Definition.SkillId,
                    JsonSerializer.SerializeToElement(payload, KautukaJsonContext.Default.JsonObject),
                    scope,
                    ownerUserId);

                SkillDefinition persistedDefinition = ToDefinition(saved);
                persisted.Add(inspection with { Definition = persistedDefinition });
            }

            SkillPackageImportOutcome persistedOutcome = persisted.Count == 1
                ? new SkillPackageImportOutcome(false, persisted[0], persisted)
                : new SkillPackageImportOutcome(true, null, persisted);

            return Results.Json(ToImportResultPayload(persistedOutcome), KautukaJsonContext.Default.GatewaySkillImportResult);
        });

        app.MapPost("/api/skills/{skillId}", (string skillId, JsonElement body, IGatewayCatalogStore catalog, IGatewayAuthGuard auth, HttpContext context) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
                return Results.Unauthorized();

            if (BuiltInSkillCatalog.IsSystemLockedSkill(skillId) && !GatewayOwnershipAccess.IsAdmin(user))
            {
                return Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }

            if (catalog.ListSkills().Any(skill =>
                string.Equals(skill.Id, skillId, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(skill.Id, skillId, StringComparison.Ordinal)))
            {
                return Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }

            bool hasExisting = catalog.TryGetSkill(skillId, out GatewaySkill? existing) && existing is not null;
            if (hasExisting && !GatewayOwnershipAccess.CanManageSkill(user, existing))
            {
                return Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }

            string scope = GatewayOwnershipAccess.ResolveCatalogScope(user, body.GetStringProperty("scope", string.Empty));
            string? ownerUserId = GatewayOwnershipAccess.ResolveCatalogOwnerUserId(scope, user);

            GatewaySkill saved = catalog.SaveSkill(skillId, body, scope, ownerUserId);
            return Results.Json(ToSkillPayload(saved), KautukaJsonContext.Default.GatewaySkillListItem);
        });

        app.MapDelete("/api/skills/{skillId}", (string skillId, IGatewayCatalogStore catalog, IGatewayAuthGuard auth, HttpContext context) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
                return Results.Unauthorized();

            if (!catalog.TryGetSkill(skillId, out GatewaySkill? skill) || skill is null)
            {
                return Results.Json(new GatewayErrorResponse("Skill not found"), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
            }

            if (!GatewayOwnershipAccess.CanManageSkill(user, skill))
            {
                return Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }

            if (BuiltInSkillCatalog.IsSystemLockedSkill(skillId))
            {
                return Results.Json(new GatewayErrorResponse("Skill is system-locked and cannot be deleted."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
            }

            bool removed = catalog.DeleteSkill(skillId);
            return removed
                ? Results.Json(new GatewayDeletedResponse(true), KautukaJsonContext.Default.GatewayDeletedResponse)
                : Results.Json(new GatewayErrorResponse("Skill not found"), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status404NotFound);
        });

        return app;
    }

    private static GatewaySkillListItem ToSkillPayload(GatewaySkill skill) => new(
        skill.Id,
        skill.Name,
        skill.Description,
        skill.PackageHash,
        skill.Status,
        skill.PackagePath,
        skill.HasResources,
        skill.HasScripts,
        skill.Enabled,
        [.. skill.EnvKeys ?? []],
        skill.ImportTimestamp,
        new Dictionary<string, string>(skill.SourceMetadata, StringComparer.OrdinalIgnoreCase),
        skill.Scope,
        skill.OwnerUserId);

    private static GatewaySkillImportResult ToImportResultPayload(SkillPackageImportOutcome outcome)
    {
        GatewaySkillInspectionResponse[] payloads = [.. outcome.Skills.Select(inspection => ToSkillInspectionPayload(inspection.Definition, inspection))];
        if (!outcome.IsBundle)
        {
            return new GatewaySkillImportResult("single", payloads[0], null);
        }

        return new GatewaySkillImportResult("bundle", null, new GatewayBundleImportResponse(payloads));
    }

    private static GatewaySkillInspectionResponse ToSkillInspectionPayload(
        SkillDefinition skill,
        SkillPackageInspection inspection,
        string scope = GatewayOwnershipScopes.System,
        string? ownerUserId = null)
        => new(
            new GatewaySkillListItem(
                skill.SkillId,
                skill.Name,
                skill.Description,
                skill.PackageHash,
                skill.Status,
                skill.PackagePath,
                skill.HasResources,
                skill.HasScripts,
                skill.Enabled,
                [.. skill.EnvKeys ?? []],
                skill.ImportTimestamp.ToString("O"),
                new Dictionary<string, string>(skill.SourceMetadata, StringComparer.OrdinalIgnoreCase),
                scope,
                ownerUserId),
            inspection.InstructionsMarkdown,
            [.. inspection.ResourcePaths],
            [.. inspection.ScriptPaths]);

    private static SkillDefinition ToDefinition(GatewaySkill skill)
        => new()
        {
            SkillId = skill.Id,
            Name = skill.Name,
            Description = skill.Description,
            PackageHash = skill.PackageHash,
            Status = skill.Status,
            PackagePath = skill.PackagePath,
            HasResources = skill.HasResources,
            HasScripts = skill.HasScripts,
            Enabled = skill.Enabled,
            EnvKeys = [.. skill.EnvKeys ?? []],
            ImportTimestamp = DateTimeOffset.TryParse(skill.ImportTimestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset timestamp) ? timestamp : DateTimeOffset.UtcNow,
            SourceMetadata = new Dictionary<string, string>(skill.SourceMetadata, StringComparer.OrdinalIgnoreCase),
        };

    private static SkillPackageImportRequest BuildImportRequest(IFormCollection form, string fileName)
        => new(
            fileName,
            ParseSourceMetadata(GetOptionalFormValue(form, "sourceMetadata")));

    private static async Task<IFormFile?> GetArchiveAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        IFormCollection form = await request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        return form.Files.GetFile("file") ?? (form.Files.Count > 0 ? form.Files[0] : null);
    }

    private static string? GetOptionalFormValue(IFormCollection form, string key)
    {
        if (!form.TryGetValue(key, out Microsoft.Extensions.Primitives.StringValues values))
        {
            return null;
        }

        string? value = values.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static Dictionary<string, string>? ParseSourceMetadata(string? sourceMetadataJson)
    {
        if (string.IsNullOrWhiteSpace(sourceMetadataJson))
        {
            return null;
        }

        return JsonSerializer.Deserialize(sourceMetadataJson, KautukaJsonContext.Default.DictionaryStringString)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static async Task CopyBoundedArchiveAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            total = checked(total + read);
            if (total > SkillPackageService.MaxCompressedArchiveBytes)
            {
                throw new InvalidOperationException($"Archive exceeds the compressed size limit of {SkillPackageService.MaxCompressedArchiveBytes} bytes.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool CanImportSkills(GatewayUser user)
        => string.Equals(user.Role, "admin", StringComparison.OrdinalIgnoreCase)
            || user.CanImportSkills;
}
