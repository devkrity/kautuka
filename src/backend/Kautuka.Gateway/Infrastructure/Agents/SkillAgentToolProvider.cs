using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Sandbox;
using Kautuka.Gateway.Domain.Skills;
using Kautuka.Gateway.Infrastructure.Credentials;

using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Infrastructure.Agents;

internal sealed class SkillAgentToolProvider(
    IPlatformConfigurationProvider configurationProvider,
    ISkillPackageService skillPackageService,
    CredentialResolverService credentialResolver,
    IToolExecutionPolicyService toolExecutionPolicyService,
    ILogger<SkillAgentToolProvider> logger) : IAgentToolSurfaceContributor
{
    private static readonly HashSet<string> packageIndexOverrideKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "NPM_CONFIG_REGISTRY",
        "PIP_INDEX_URL",
        "PIP_TRUSTED_HOST",
        "PIP_EXTRA_INDEX_URL",
        "UV_INDEX_URL",
        "UV_DEFAULT_INDEX",
        "UV_EXTRA_INDEX_URL",
        "UV_INDEX",
    };

    public string SurfaceSource => AgentToolSurfaceSources.Skill;

    public async ValueTask<IEnumerable<AIFunction>> BuildToolsAsync(AgentToolContext context, CancellationToken ct)
    {
        AgentToolSurfaceContribution contribution = await ContributeAsync(context, ct).ConfigureAwait(false);
        return await HydrateAsync(context, contribution.Entries, ct).ConfigureAwait(false);
    }

    public ValueTask<AgentToolSurfaceContribution> ContributeAsync(AgentToolContext context, CancellationToken ct)
    {
        _ = ct;
        ArgumentNullException.ThrowIfNull(context);

        IReadOnlyList<EffectiveSkillManifestEntry> manifest = context.EffectiveSkillManifest ?? [];
        if (manifest.Count == 0)
        {
            return ValueTask.FromResult(new AgentToolSurfaceContribution([], Complete: true));
        }

        Dictionary<string, EffectiveSkillManifestEntry> manifestById = IndexManifest(manifest);
        Dictionary<string, SkillDefinition> skillsById = IndexSkills(configurationProvider.GetCurrent().Skills, manifestById);
        if (skillsById.Count == 0)
        {
            return ValueTask.FromResult(new AgentToolSurfaceContribution([], Complete: true));
        }

        // Pre-inspect all skills so we use the real on-disk state (including self-healed scripts)
        // rather than the cached HasScripts flag from SQLite, which may be stale for packages
        // imported before script generation was introduced.
        var inspections = new Dictionary<string, SkillPackageInspection>(StringComparer.OrdinalIgnoreCase);
        foreach ((string skillId, SkillDefinition skill) in skillsById)
        {
#pragma warning disable CA1031, CA1848 // Broad catch intentional; LoggerMessage deferred for non-hot path
            try
            {
                inspections[skillId] = skillPackageService.Inspect(skill);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[Skills] Failed to inspect skill '{SkillId}' for agent '{AgentId}'. Falling back to manifest metadata.", skillId, context.AgentId);
            }
#pragma warning restore CA1031, CA1848
        }

        ToolPolicyDecision exposureDecision = toolExecutionPolicyService.EvaluateExposure(
            ToolPolicyCategory.Skill,
            context.AgentDefinition);
        if (!exposureDecision.Allowed)
        {
#pragma warning disable CA1848, CA1873 // LoggerMessage deferred; policy-denial path is non-hot
            logger.LogInformation(
                "[Skills] Skill tools are not exposed for agent '{AgentId}' due to policy reason '{ReasonCode}'.",
                context.AgentId,
                exposureDecision.ReasonCode);
#pragma warning restore CA1848, CA1873
            return ValueTask.FromResult(new AgentToolSurfaceContribution([], Complete: true));
        }

        bool hasAnyResources = manifestById.Values.Any(static m => m.HasResources)
            || inspections.Values.Any(static i => i.ResourcePaths.Count > 0);
        bool hasAnyScripts = manifestById.Values.Any(static m => m.HasScripts)
            || inspections.Values.Any(static i => i.ScriptPaths.Count > 0);

        var entries = new List<AgentToolSurfaceEntry>
        {
            AgentToolSurfaceEntry.Describe(
                SurfaceSource,
                "load_skill",
                "load_skill",
                "Load the instructions and available files for a resolved skill by skillId.",
                "{}"),
        };

        if (hasAnyResources)
        {
            entries.Add(AgentToolSurfaceEntry.Describe(
                SurfaceSource,
                "read_skill_resource",
                "read_skill_resource",
                "Read a text resource from a resolved skill by skillId and resource path.",
                "{}"));
        }

        if (hasAnyScripts)
        {
            entries.Add(AgentToolSurfaceEntry.Describe(
                SurfaceSource,
                "run_skill_script",
                "run_skill_script",
                "Execute a script from a loaded skill. Provide skillId and optionally scriptPath. Generated markdown-backed skills can use namedArguments for placeholders like sides.",
                "{}"));
        }

        return ValueTask.FromResult(new AgentToolSurfaceContribution(entries, Complete: true));
    }

    public ValueTask<IReadOnlyList<AIFunction>> HydrateAsync(
        AgentToolContext context,
        IReadOnlyList<AgentToolSurfaceEntry> entries,
        CancellationToken ct)
    {
        _ = ct;
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entries);

        IReadOnlyList<EffectiveSkillManifestEntry> manifest = context.EffectiveSkillManifest ?? [];
        if (manifest.Count == 0)
        {
            return ValueTask.FromResult<IReadOnlyList<AIFunction>>([]);
        }

        Dictionary<string, EffectiveSkillManifestEntry> manifestById = IndexManifest(manifest);
        Dictionary<string, SkillDefinition> skillsById = IndexSkills(configurationProvider.GetCurrent().Skills, manifestById);
        if (skillsById.Count == 0)
        {
            return ValueTask.FromResult<IReadOnlyList<AIFunction>>([]);
        }

        ToolPolicyDecision exposureDecision = toolExecutionPolicyService.EvaluateExposure(
            ToolPolicyCategory.Skill,
            context.AgentDefinition);
        if (!exposureDecision.Allowed)
        {
            return ValueTask.FromResult<IReadOnlyList<AIFunction>>([]);
        }

        HashSet<string> names = entries
            .Where(static entry => string.Equals(entry.Source, AgentToolSurfaceSources.Skill, StringComparison.Ordinal))
            .Select(static entry => entry.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (names.Count == 0)
        {
            return ValueTask.FromResult<IReadOnlyList<AIFunction>>([]);
        }

        bool requireApprovalForAnySkill = toolExecutionPolicyService.EvaluateApproval(new ToolApprovalRequest(
            Identity: new ToolIdentity(
                Category: ToolPolicyCategory.Skill,
                ToolName: "read_skill_resource"),
            SkillMode: manifestById.Values.Any(static m => m.Mode == SkillAttachmentMode.RequireApproval)
                ? SkillAttachmentMode.RequireApproval
                : SkillAttachmentMode.Trusted)).RequiresApproval;

        var tools = new List<AIFunction>();
        if (names.Contains("load_skill"))
        {
            tools.Add(BuildLoadSkillTool(manifestById, skillsById));
        }

        if (names.Contains("read_skill_resource"))
        {
            AIFunction readResourceTool = BuildReadResourceTool(manifestById, skillsById);
            tools.Add(requireApprovalForAnySkill ? new ApprovalRequiredAIFunction(readResourceTool) : readResourceTool);
        }

        if (names.Contains("run_skill_script"))
        {
            AIFunction runSkillScriptTool = BuildRunSkillScriptTool(context, manifestById, skillsById);
            tools.Add(requireApprovalForAnySkill ? new ApprovalRequiredAIFunction(runSkillScriptTool) : runSkillScriptTool);
        }

        return new ValueTask<IReadOnlyList<AIFunction>>(tools);
    }

    private AIFunction BuildLoadSkillTool(
        Dictionary<string, EffectiveSkillManifestEntry> manifestById,
        Dictionary<string, SkillDefinition> skillsById)
    {
        return AIFunctionFactory.Create(
            ([System.ComponentModel.Description("The canonical skillId to load.")] string skillId) =>
            {
                EffectiveSkillManifestEntry manifest = GetManifestEntry(manifestById, skillId);
                if (!skillsById.TryGetValue(manifest.SkillId, out SkillDefinition? skill))
                {
                    throw new InvalidOperationException($"Skill '{skillId}' is attached but is not available in configuration.");
                }

                SkillPackageInspection inspection = skillPackageService.Inspect(skill);

                bool scriptExecutionAvailable = inspection.ScriptPaths.Count > 0;
                string? scriptToolName = scriptExecutionAvailable ? "run_skill_script" : null;

                var payload = new JsonObject
                {
                    ["skill_id"] = manifest.SkillId,
                    ["name"] = manifest.Name,
                    ["description"] = manifest.Description,
#pragma warning disable CA1308 // Lowercase is the canonical storage format for skill scope and mode values
                    ["source_scope"] = manifest.SourceScope.ToString().ToLowerInvariant(),
                    ["mode"] = manifest.Mode.ToString().ToLowerInvariant(),
#pragma warning restore CA1308
                    ["package_hash"] = manifest.PackageHash,
                    ["import_timestamp"] = manifest.ImportTimestamp.ToString("O"),
                    ["instructions_markdown"] = inspection.InstructionsMarkdown,
                    ["resources"] = new JsonArray([.. inspection.ResourcePaths.Select(static path => JsonValue.Create(path)!)]),
                    ["scripts"] = new JsonArray([.. inspection.ScriptPaths.Select(static path => JsonValue.Create(path)!)]),
                    ["script_details"] = new JsonArray([.. inspection.Scripts.Select(static script => new JsonObject
                    {
                        ["path"] = script.RelativePath,
                        ["parameter_names"] = new JsonArray([.. script.ParameterNames.Select(static parameterName => JsonValue.Create(parameterName)!)]),
                    })]),
                };

                if (!string.IsNullOrEmpty(scriptToolName))
                {
                    payload["script_tool_name"] = scriptToolName;
                    payload["script_execution"] = new JsonObject
                    {
                        ["tool_name"] = scriptToolName,
                        ["argument_contract"] = "Call run_skill_script with skillId, optional scriptPath, optional namedArguments, optional arguments, and optional timeoutSeconds.",
                        ["warning"] = "Use run_skill_script for skill execution. Provide namedArguments for generated markdown-backed scripts when parameter names are listed in script_details.",
                        ["default_script_path"] = inspection.ScriptPaths.Count == 1 ? inspection.ScriptPaths[0] : null,
                        ["available_scripts"] = new JsonArray([.. inspection.ScriptPaths.Select(static path => JsonValue.Create(path)!)]),
                    };
                }
                else if (inspection.ScriptPaths.Count > 0)
                {
                    payload["script_execution_unavailable_reason"] = "This skill does not currently expose a callable script tool.";
                }

                return payload;
            },
            "load_skill",
            "Load the instructions and available files for a resolved skill by skillId.");
    }

    private AIFunction BuildReadResourceTool(
        Dictionary<string, EffectiveSkillManifestEntry> manifestById,
        Dictionary<string, SkillDefinition> skillsById)
    {
        return AIFunctionFactory.Create(
            ([System.ComponentModel.Description("The canonical skillId to read from.")] string skillId,
             [System.ComponentModel.Description("The relative path of the resource under the skill's resources directory.")] string resourcePath) =>
            {
                EffectiveSkillManifestEntry manifest = GetManifestEntry(manifestById, skillId);
                if (!manifest.HasResources)
                {
                    throw new InvalidOperationException($"Skill '{skillId}' does not expose resources.");
                }

                SkillDefinition skill = skillsById[manifest.SkillId];
                return new JsonObject
                {
                    ["skill_id"] = manifest.SkillId,
                    ["resource_path"] = resourcePath,
                    ["content"] = skillPackageService.ReadResourceText(skill, resourcePath),
                };
            },
            "read_skill_resource",
            "Read a text resource from a resolved skill by skillId and resource path.");
    }

    private AIFunction BuildRunSkillScriptTool(
        AgentToolContext context,
        Dictionary<string, EffectiveSkillManifestEntry> manifestById,
        Dictionary<string, SkillDefinition> skillsById)
    {
        return AIFunctionFactory.Create(
            async (
                [System.ComponentModel.Description("The canonical skillId to execute.")] string skillId,
                CancellationToken cancellationToken,
                [System.ComponentModel.Description("Optional relative path of the script under the skill's scripts directory. When omitted and only one script is available, that script is used automatically.")] string? scriptPath = null,
                [System.ComponentModel.Description("Optional named arguments for generated markdown scripts, for example {\"sides\":\"20\"}.")] JsonObject? namedArguments = null,
                [System.ComponentModel.Description("Optional positional arguments appended after any named arguments, for example [\"20\",\"advantage\"].")] JsonArray? arguments = null,
                [System.ComponentModel.Description("Optional timeout in seconds.")] int? timeoutSeconds = null) =>
            {
                EffectiveSkillManifestEntry manifest = GetManifestEntry(manifestById, skillId);
                if (!manifest.HasScripts)
                {
                    throw new InvalidOperationException($"Skill '{skillId}' does not expose scripts.");
                }

                SkillDefinition skill = skillsById[manifest.SkillId];
                SkillPackageInspection inspection = skillPackageService.Inspect(skill);
                string resolvedScriptPath = ResolveScriptPath(inspection, scriptPath);
                string[] resolvedArguments = ResolveScriptArguments(inspection, resolvedScriptPath, NormalizeNamedArguments(namedArguments), NormalizePositionalArguments(arguments));
                return await ExecuteSkillScriptAsync(context, manifest, skill, resolvedScriptPath, resolvedArguments, timeoutSeconds, cancellationToken).ConfigureAwait(false);
            },
                "run_skill_script",
            "Execute a script from a loaded skill. Provide skillId and optionally scriptPath. Generated markdown-backed skills can use namedArguments for placeholders like sides.");
    }

    private async Task<string> ExecuteSkillScriptAsync(
        AgentToolContext context,
        EffectiveSkillManifestEntry manifest,
        SkillDefinition skill,
        string scriptPath,
        IReadOnlyList<string> arguments,
        int? timeoutSeconds,
        CancellationToken cancellationToken)
    {
        ToolPolicyDecision executionDecision = toolExecutionPolicyService.ValidateExecution(
            ToolPolicyCategory.Skill,
            context.AgentDefinition);
        if (!executionDecision.Allowed)
        {
            throw new InvalidOperationException(
                $"Skill execution denied by policy ({executionDecision.ReasonCode}): {executionDecision.Message}");
        }

        int? boundedTimeoutSeconds = ClampSkillTimeoutSeconds(timeoutSeconds, context.AgentDefinition.Sandbox.MaxTimeout);
        AgentToolInvocationContextState? invocationContext = AgentToolInvocationContext.Current;
        SkillAgentToolProviderLog.ExecutingSkillScript(
            logger,
            context.AgentId,
            context.SessionId,
            context.UserId,
            manifest.SkillId,
            scriptPath,
            boundedTimeoutSeconds,
            invocationContext?.CallerSessionId,
            context.WorkspaceBinding.ScopeType,
            context.WorkspaceBinding.ScopeId);

        WorkspaceRuntimeContext workspace = context.WorkspaceTool.DescribeAgentWorkspace(context.AgentId, context.UserId);
        skillPackageService.StagePackageForWorkspace(skill, workspace.HostWorkspacePath, workspace.ContainerWorkspacePath);
        string scriptContent = skillPackageService.ReadScriptText(skill, scriptPath);
        Dictionary<string, string> env = await ResolveSkillEnvAsync(skill, context.UserId ?? string.Empty, cancellationToken).ConfigureAwait(false);
        string command = BuildInlineExecutionCommand(scriptContent, arguments);

        WorkspaceCommandResult result = await context.WorkspaceTool.RunAgentShellWithEnvironmentAsync(
            context.AgentId,
            command,
            env,
            context.UserId,
            boundedTimeoutSeconds,
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new SkillScriptExecutionException(manifest.SkillId, scriptPath, result.ExitCode);
        }

        string stdout = result.StdOut.Trim();
        return string.IsNullOrWhiteSpace(stdout) ? "(no output)" : stdout;
    }

    private static Dictionary<string, EffectiveSkillManifestEntry> IndexManifest(IReadOnlyList<EffectiveSkillManifestEntry> manifest)
        => SkillIdentity.Index(manifest, static entry => entry.SkillId);

    private static Dictionary<string, SkillDefinition> IndexSkills(
        IEnumerable<SkillDefinition> skills,
        Dictionary<string, EffectiveSkillManifestEntry> manifestById)
    {
        Dictionary<string, SkillDefinition> indexed = SkillIdentity.Index(skills, static skill => skill.SkillId);
        return indexed.Where(pair => manifestById.ContainsKey(pair.Key)).ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
    }

    private static EffectiveSkillManifestEntry GetManifestEntry(Dictionary<string, EffectiveSkillManifestEntry> manifestById, string skillId)
        => manifestById.TryGetValue(skillId, out EffectiveSkillManifestEntry? entry)
            ? entry
            : throw new InvalidOperationException($"Skill '{skillId}' is not available in the current execution context.");

    private static string ResolveScriptPath(SkillPackageInspection inspection, string? scriptPath)
    {
        if (string.IsNullOrWhiteSpace(scriptPath))
        {
            return inspection.ScriptPaths.Count switch
            {
                1 => inspection.ScriptPaths[0],
                0 => throw new InvalidOperationException("The selected skill does not expose any scripts."),
                _ => throw new InvalidOperationException("This skill exposes multiple scripts. Provide scriptPath explicitly."),
            };
        }

        string normalized = scriptPath.Replace('\\', '/').TrimStart('/');
        if (normalized.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["scripts/".Length..];
        }

        string? matched = inspection.ScriptPaths.FirstOrDefault(path => string.Equals(path, normalized, StringComparison.OrdinalIgnoreCase));
        return matched ?? throw new InvalidOperationException($"Script '{scriptPath}' was not found in the selected skill.");
    }

    private static Dictionary<string, string>? NormalizeNamedArguments(JsonObject? namedArguments)
    {
        if (namedArguments is null || namedArguments.Count == 0)
        {
            return null;
        }

        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, JsonNode? value) in namedArguments)
        {
            if (string.IsNullOrWhiteSpace(key) || value is null)
            {
                continue;
            }

            string? text = value switch
            {
                JsonValue jsonValue when TryReadJsonValue(jsonValue, out string? scalar) => scalar,
                _ => null,
            };

            if (!string.IsNullOrWhiteSpace(text))
            {
                normalized[key] = text;
            }
        }

        return normalized.Count == 0 ? null : normalized;
    }

    private static bool TryReadJsonValue(JsonValue jsonValue, out string? value)
    {
        if (jsonValue.TryGetValue<string>(out string? stringValue))
        {
            value = stringValue;
            return true;
        }

        if (jsonValue.TryGetValue<bool>(out bool boolValue))
        {
            value = boolValue ? "true" : "false";
            return true;
        }

        if (jsonValue.TryGetValue<int>(out int intValue))
        {
            value = intValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }

        if (jsonValue.TryGetValue<long>(out long longValue))
        {
            value = longValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }

        if (jsonValue.TryGetValue<double>(out double doubleValue))
        {
            value = doubleValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }

        if (jsonValue.TryGetValue<decimal>(out decimal decimalValue))
        {
            value = decimalValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }

        value = null;
        return false;
    }

    private static List<string>? NormalizePositionalArguments(JsonArray? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return null;
        }

        var normalized = new List<string>(arguments.Count);
        foreach (JsonNode? argument in arguments)
        {
            if (argument is not JsonValue jsonValue || !TryReadJsonValue(jsonValue, out string? value) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            normalized.Add(value);
        }

        return normalized.Count == 0 ? null : normalized;
    }

    private static string[] ResolveScriptArguments(
        SkillPackageInspection inspection,
        string scriptPath,
        IReadOnlyDictionary<string, string>? namedArguments,
        List<string>? positionalArguments)
    {
        var resolvedArguments = new List<string>();
        var normalizedNamedArguments = namedArguments is null
            ? null
            : new Dictionary<string, string>(namedArguments, StringComparer.OrdinalIgnoreCase);

        if (normalizedNamedArguments is not null && normalizedNamedArguments.Count > 0)
        {
            SkillScriptInspection? script = inspection.Scripts.FirstOrDefault(candidate => string.Equals(candidate.RelativePath, scriptPath, StringComparison.OrdinalIgnoreCase));
            IReadOnlyList<string> parameterNames = script?.ParameterNames ?? [];
            if (parameterNames.Count == 0)
            {
                throw new InvalidOperationException($"Script '{scriptPath}' does not define named parameters.");
            }

            foreach (string parameterName in parameterNames)
            {
                if (!normalizedNamedArguments.TryGetValue(parameterName, out string? value) || string.IsNullOrWhiteSpace(value))
                {
                    throw new InvalidOperationException($"Missing required named argument '{parameterName}' for script '{scriptPath}'.");
                }

                resolvedArguments.Add(value);
            }

            string[] unexpectedArguments = [.. normalizedNamedArguments.Keys.Where(key => !parameterNames.Contains(key, StringComparer.OrdinalIgnoreCase))];
            if (unexpectedArguments.Length > 0)
            {
                throw new InvalidOperationException($"Unknown named argument(s) for script '{scriptPath}': {string.Join(", ", unexpectedArguments)}.");
            }
        }

        if (positionalArguments is not null && positionalArguments.Count > 0)
        {
            resolvedArguments.AddRange(positionalArguments);
        }

        return [.. resolvedArguments];
    }

    internal static int? ClampSkillTimeoutSeconds(int? requested, int maxTimeout)
    {
        if (requested is not > 0)
        {
            return null;
        }

        int cap = Math.Max(1, maxTimeout);
        return Math.Min(requested.Value, cap);
    }

    internal static string BuildInlineExecutionCommand(string scriptContent, IReadOnlyList<string> arguments)
    {
        string normalizedScript = scriptContent.Replace("\r\n", "\n", StringComparison.Ordinal);
        string delimiter = "__KAUTUKA_SKILL_SCRIPT__";
        if (normalizedScript.Contains(delimiter, StringComparison.Ordinal))
        {
            delimiter = $"{delimiter}_{Guid.NewGuid():N}";
        }

        string escapedArguments = BuildEscapedArguments(arguments);
        string runPart = string.IsNullOrWhiteSpace(escapedArguments)
            ? "/usr/bin/bash \"$tmp_script\""
            : $"/usr/bin/bash \"$tmp_script\" {escapedArguments}";

        return string.Join(
            '\n',
            "tmp_script=\"$(mktemp /tmp/kautuka-skill-XXXXXX.sh)\" || exit $?",
            "cleanup() { rm -f -- \"$tmp_script\"; }",
            "trap cleanup EXIT",
            "trap 'exit 129' HUP",
            "trap 'exit 130' INT",
            "trap 'exit 143' TERM",
            $"cat > \"$tmp_script\" <<'{delimiter}' || exit $?",
            normalizedScript,
            delimiter,
            runPart);
    }

    private async ValueTask<Dictionary<string, string>> ResolveSkillEnvAsync(SkillDefinition skill, string userId, CancellationToken cancellationToken)
    {
        if (skill.EnvKeys.Count == 0)
        {
            return [];
        }

        bool includeSystemScope = !string.Equals(skill.Scope, GatewayOwnershipScopes.User, StringComparison.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, string> resolved = await credentialResolver.GetResolvedAsync(userId, skill.EnvKeys, includeSystemScope, includeHeldSecrets: false, cancellationToken).ConfigureAwait(false);
        return resolved
            .Where(static pair => !packageIndexOverrideKeys.Contains(pair.Key))
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
    }

    private static string BuildEscapedArguments(IReadOnlyList<string> arguments)
        => string.Join(" ", arguments.Select(static argument => $"'{EscapeSingleQuotedShellLiteral(argument)}'"));

    private static string EscapeSingleQuotedShellLiteral(string value)
        => value.Replace("'", "'\"'\"'", StringComparison.Ordinal);
}

[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1032:Implement standard exception constructors",
    Justification = "This internal trust-boundary exception requires typed fields and a fixed sanitized message; arbitrary-message constructors would permit stderr disclosure.")]
internal sealed class SkillScriptExecutionException(
    string skillId,
    string scriptPath,
    int exitCode) : InvalidOperationException("Skill script execution failed.")
{
    public string SkillId { get; } = skillId;

    public string ScriptPath { get; } = scriptPath;

    public int ExitCode { get; } = exitCode;
}

internal static partial class SkillAgentToolProviderLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Executing skill script for agent {AgentId}, session {SessionId}, user {UserId}; skillId={SkillId}, scriptPath={ScriptPath}, timeoutSeconds={TimeoutSeconds}, agentToolCallerSessionId={AgentToolCallerSessionId}, workspaceScopeType={ScopeType}, workspaceScopeId={ScopeId}")]
    private static partial void ExecutingSkillScriptCore(ILogger logger, string agentId, string sessionId, string? userId, string skillId, string scriptPath, int? timeoutSeconds, string? agentToolCallerSessionId, string scopeType, string scopeId);

    public static void ExecutingSkillScript(ILogger logger, string agentId, string sessionId, string? userId, string skillId, string scriptPath, int? timeoutSeconds, string? agentToolCallerSessionId, string scopeType, string scopeId)
    {
        ExecutingSkillScriptCore(logger, agentId, sessionId, userId, skillId, scriptPath, timeoutSeconds, agentToolCallerSessionId, scopeType, scopeId);
    }
}
