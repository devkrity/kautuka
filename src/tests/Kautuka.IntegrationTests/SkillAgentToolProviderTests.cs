using System.Text.Json;
using System.Text.Json.Nodes;

using Grpc.Core;

using Kautuka.Gateway.Domain.Sandbox;
using Kautuka.Gateway.Domain.Skills;
using Kautuka.Gateway.Infrastructure.Credentials;
using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Protos;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class SkillAgentToolProviderTests
{
    [Fact]
    public void SkillScriptTimeoutIsCappedAtTheAgentSandboxMax()
    {
        Assert.Null(SkillAgentToolProvider.ClampSkillTimeoutSeconds(null, 120));
        Assert.Null(SkillAgentToolProvider.ClampSkillTimeoutSeconds(0, 120));
        Assert.Null(SkillAgentToolProvider.ClampSkillTimeoutSeconds(-1, 120));
        Assert.Equal(15, SkillAgentToolProvider.ClampSkillTimeoutSeconds(15, 120));
        Assert.Equal(120, SkillAgentToolProvider.ClampSkillTimeoutSeconds(int.MaxValue, 120));
        Assert.Equal(1, SkillAgentToolProvider.ClampSkillTimeoutSeconds(30, 0));
    }

    [Fact]
    public async Task InlineSkillScriptHonorsProductionNoExecTmpContractWhenPodmanIsAvailable()
    {
        const string image = "localhost/kautuka-base:latest";
        var podman = new PodmanCommandRunner();
        PodmanCommandResult imageExists;
        try
        {
            imageExists = await podman.RunAsync(
                ["image", "exists", image],
                10,
                TestContext.Current.CancellationToken);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Skip("Podman is not installed; the production sandbox contract test requires Podman.");
            return;
        }

        if (imageExists.ExitCode != 0)
        {
            Assert.Skip($"Sandbox image '{image}' is not available locally.");
            return;
        }

        const string productionTmpfs = "/tmp:rw,noexec,nosuid";
        PodmanCommandResult directExecution = await podman.RunAsync(
            [
                "run", "--rm", "--read-only", "--tmpfs", productionTmpfs,
                "--entrypoint", "/bin/sh", image, "-c",
                "tmp_script=\"$(mktemp /tmp/kautuka-skill-direct-XXXXXX.sh)\"; trap 'rm -f -- \"$tmp_script\"' EXIT; printf '#!/bin/sh\\necho direct\\n' > \"$tmp_script\"; chmod +x \"$tmp_script\"; \"$tmp_script\"",
            ],
            30,
            TestContext.Current.CancellationToken);
        Assert.NotEqual(0, directExecution.ExitCode);

        string generated = SkillAgentToolProvider.BuildInlineExecutionCommand("echo 17", []);
        Assert.Contains("cat > \"$tmp_script\" <<'__KAUTUKA_SKILL_SCRIPT__' || exit $?", generated, StringComparison.Ordinal);
        Assert.Contains("/usr/bin/bash \"$tmp_script\"", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("chmod", generated, StringComparison.Ordinal);
        string verifyCleanup = $"/bin/sh -c {QuoteShellLiteral(generated)}; status=$?; leftover=\"$(find /tmp -maxdepth 1 -name 'kautuka-skill-*.sh' -print -quit)\"; [ -z \"$leftover\" ] || exit 91; exit \"$status\"";
        PodmanCommandResult interpretedExecution = await podman.RunAsync(
            [
                "run", "--rm", "--read-only", "--tmpfs", productionTmpfs,
                "--entrypoint", "/bin/sh", image, "-c", verifyCleanup,
            ],
            30,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, interpretedExecution.ExitCode);
        Assert.Equal("17", interpretedExecution.StdOut.Trim());
    }

    [Fact]
    public async Task LoadSkillReturnsMafScriptToolMetadataForCallableSkill()
    {
        using var temp = new TemporarySqliteDatabase();
        string skillRoot = OwnedSkillRoot(temp.Path, "roll-dice");
        Directory.CreateDirectory(Path.Combine(skillRoot, "scripts"));
        await File.WriteAllTextAsync(Path.Combine(skillRoot, "SKILL.md"), "# Roll Dice\n\nUse this skill to roll dice.\n\n```bash\necho $((RANDOM % <sides> + 1))\n```", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(skillRoot, "scripts", "generated-from-markdown.sh"), "echo 17", TestContext.Current.CancellationToken);

        try
        {
            var provider = new SkillAgentToolProvider(
                new StaticPlatformConfigurationProvider(new PlatformConfiguration
                {
                    Skills =
                    [
                        new SkillDefinition
                        {
                            SkillId = "roll-dice",
                            Name = "Roll Dice",
                            Description = "Roll a die",
                            PackageHash = "hash-roll-dice",
                            Status = "ready",
                            PackagePath = skillRoot,
                            HasScripts = true,
                            HasResources = false,
                            ImportTimestamp = new DateTimeOffset(2026, 4, 18, 0, 0, 0, TimeSpan.Zero),
                        },
                    ],
                }),
                CreateSkillPackageService(temp.Path),
                CreateCredentialResolverService(),
                new ToolExecutionPolicyService(),
                NullLogger<SkillAgentToolProvider>.Instance);

            var workspaceTool = new CapturingWorkspaceTool();
            var context = new AgentToolContext(
                new AgentDefinition
                {
                    Id = "agent-alpha",
                    Name = "Agent Alpha",
                    Sandbox = new AgentSandboxDefinition
                    {
                        Enabled = true,
                    },
                    SkillAttachments =
                    [
                        new AgentSkillAttachmentDefinition
                        {
                            SkillId = "roll-dice",
                            Mode = SkillAttachmentMode.Trusted,
                        },
                    ],
                },
                "agent-alpha",
                UserId: null,
                workspaceTool,
                WorkspaceTooling.BuildAgentSessionBinding(workspaceTool, "agent-alpha", null, null),
                EffectiveSkillManifest:
                [
                    new EffectiveSkillManifestEntry
                    {
                        SkillId = "roll-dice",
                        SourceScope = EffectiveSkillSourceScope.Agent,
                        SourceOwnerId = "agent-alpha",
                        Mode = SkillAttachmentMode.Trusted,
                        HasScripts = true,
                        HasResources = false,
                        Name = "Roll Dice",
                        Description = "Roll a die",
                        PackageHash = "hash-roll-dice",
                        ImportTimestamp = new DateTimeOffset(2026, 4, 18, 0, 0, 0, TimeSpan.Zero),
                    },
                ]);

            IReadOnlyList<AIFunction> tools = [.. await provider.BuildToolsAsync(context, TestContext.Current.CancellationToken)];
            AIFunction loadSkillTool = Assert.Single(tools, static tool => string.Equals(tool.Name, "load_skill", StringComparison.Ordinal));

            object? rawResult = await loadSkillTool.InvokeAsync(
                new AIFunctionArguments(new Dictionary<string, object?>
                {
                    ["skillId"] = "roll-dice",
                }),
                TestContext.Current.CancellationToken);

            JsonElement payload = Assert.IsType<JsonElement>(rawResult);
            Assert.Equal("run_skill_script", payload.GetProperty("script_tool_name").GetString());

            JsonElement execution = payload.GetProperty("script_execution");
            Assert.Equal("run_skill_script", execution.GetProperty("tool_name").GetString());
            Assert.Contains("Use run_skill_script", execution.GetProperty("warning").GetString(), StringComparison.Ordinal);
            Assert.Equal("generated-from-markdown.sh", execution.GetProperty("available_scripts")[0].GetString());
            Assert.Equal("generated-from-markdown.sh", execution.GetProperty("default_script_path").GetString());
            Assert.Equal("generated-from-markdown.sh", payload.GetProperty("script_details")[0].GetProperty("path").GetString());
            Assert.Equal("sides", payload.GetProperty("script_details")[0].GetProperty("parameter_names")[0].GetString());
        }
        finally
        {
            if (Directory.Exists(skillRoot))
            {
                Directory.Delete(skillRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunSkillScriptExecutesGeneratedMarkdownSkillAndPreservesTypedNonzeroFailure()
    {
        using var temp = new TemporarySqliteDatabase();
        string skillRoot = OwnedSkillRoot(temp.Path, "roll-dice");
        Directory.CreateDirectory(Path.Combine(skillRoot, "scripts"));
        await File.WriteAllTextAsync(Path.Combine(skillRoot, "SKILL.md"), "# Roll Dice\n\nUse this skill to roll dice.\n\n```bash\necho $((RANDOM % <sides> + 1))\n```", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(skillRoot, "scripts", "generated-from-markdown.sh"), "#!/usr/bin/env bash\necho $((RANDOM % ${1:?missing sides} + 1))\n", TestContext.Current.CancellationToken);

        try
        {
            var provider = new SkillAgentToolProvider(
                new StaticPlatformConfigurationProvider(new PlatformConfiguration
                {
                    Skills =
                    [
                        new SkillDefinition
                        {
                            SkillId = "roll-dice",
                            Name = "Roll Dice",
                            Description = "Roll a die",
                            PackageHash = "hash-roll-dice",
                            Status = "ready",
                            PackagePath = skillRoot,
                            HasScripts = true,
                            HasResources = false,
                            ImportTimestamp = new DateTimeOffset(2026, 4, 18, 0, 0, 0, TimeSpan.Zero),
                        },
                    ],
                }),
                CreateSkillPackageService(temp.Path),
                CreateCredentialResolverService(),
                new ToolExecutionPolicyService(),
                NullLogger<SkillAgentToolProvider>.Instance);

            var workspaceTool = new CapturingWorkspaceTool();
            var context = new AgentToolContext(
                new AgentDefinition
                {
                    Id = "agent-alpha",
                    Name = "Agent Alpha",
                    Sandbox = new AgentSandboxDefinition
                    {
                        Enabled = true,
                    },
                    SkillAttachments =
                    [
                        new AgentSkillAttachmentDefinition
                        {
                            SkillId = "roll-dice",
                            Mode = SkillAttachmentMode.Trusted,
                        },
                    ],
                },
                "agent-alpha",
                UserId: null,
                workspaceTool,
                WorkspaceTooling.BuildAgentSessionBinding(workspaceTool, "agent-alpha", null, null),
                EffectiveSkillManifest:
                [
                    new EffectiveSkillManifestEntry
                    {
                        SkillId = "roll-dice",
                        SourceScope = EffectiveSkillSourceScope.Agent,
                        SourceOwnerId = "agent-alpha",
                        Mode = SkillAttachmentMode.Trusted,
                        HasScripts = true,
                        HasResources = false,
                        Name = "Roll Dice",
                        Description = "Roll a die",
                        PackageHash = "hash-roll-dice",
                        ImportTimestamp = new DateTimeOffset(2026, 4, 18, 0, 0, 0, TimeSpan.Zero),
                    },
                ]);

            IReadOnlyList<AIFunction> tools = [.. await provider.BuildToolsAsync(context, TestContext.Current.CancellationToken)];
            AIFunction scriptTool = Assert.Single(tools, static tool => string.Equals(tool.Name, "run_skill_script", StringComparison.Ordinal));

            object? rawResult = await scriptTool.InvokeAsync(
                new AIFunctionArguments(new Dictionary<string, object?>
                {
                    ["skillId"] = "roll-dice",
                    ["namedArguments"] = new JsonObject
                    {
                        ["sides"] = "20",
                    },
                }),
                TestContext.Current.CancellationToken);

            JsonElement payload = Assert.IsType<JsonElement>(rawResult);
            Assert.Equal(JsonValueKind.String, payload.ValueKind);
            Assert.Equal("17", payload.GetString());
            Assert.Contains("mktemp /tmp/kautuka-skill-", workspaceTool.LastAgentCommand, StringComparison.Ordinal);
            Assert.Contains("cleanup() { rm -f -- \"$tmp_script\"; }", workspaceTool.LastAgentCommand, StringComparison.Ordinal);
            Assert.Contains("trap cleanup EXIT", workspaceTool.LastAgentCommand, StringComparison.Ordinal);
            Assert.Contains("trap 'exit 129' HUP", workspaceTool.LastAgentCommand, StringComparison.Ordinal);
            Assert.Contains("trap 'exit 130' INT", workspaceTool.LastAgentCommand, StringComparison.Ordinal);
            Assert.Contains("trap 'exit 143' TERM", workspaceTool.LastAgentCommand, StringComparison.Ordinal);
            Assert.Contains("/usr/bin/bash \"$tmp_script\" '20'", workspaceTool.LastAgentCommand, StringComparison.Ordinal);
            Assert.DoesNotContain("chmod", workspaceTool.LastAgentCommand, StringComparison.Ordinal);

            workspaceTool.Result = new WorkspaceCommandResult(
                "failed",
                126,
                string.Empty,
                "private stderr must not cross the tool boundary");

            SkillScriptExecutionException failure = await Assert.ThrowsAsync<SkillScriptExecutionException>(async () =>
                await scriptTool.InvokeAsync(
                    new AIFunctionArguments(new Dictionary<string, object?>
                    {
                        ["skillId"] = "roll-dice",
                        ["namedArguments"] = new JsonObject
                        {
                            ["sides"] = "20",
                        },
                    }),
                    TestContext.Current.CancellationToken));

            Assert.Equal("roll-dice", failure.SkillId);
            Assert.Equal("generated-from-markdown.sh", failure.ScriptPath);
            Assert.Equal(126, failure.ExitCode);
            Assert.Equal("Skill script execution failed.", failure.Message);
            Assert.DoesNotContain("126", failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("private stderr", failure.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(skillRoot))
            {
                Directory.Delete(skillRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunSkillScriptInjectsResolvedCredentialEnvKeys()
    {
        using var temp = new TemporarySqliteDatabase();
        string skillRoot = OwnedSkillRoot(temp.Path, "env-skill");
        Directory.CreateDirectory(Path.Combine(skillRoot, "scripts"));
        await File.WriteAllTextAsync(Path.Combine(skillRoot, "SKILL.md"), "# Env Skill\n\nUses credential-backed env keys.", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(skillRoot, "scripts", "show-env.sh"), "#!/usr/bin/env bash\necho \"$SKILL_TOKEN\"\n", TestContext.Current.CancellationToken);

        try
        {
            var provider = new SkillAgentToolProvider(
                new StaticPlatformConfigurationProvider(new PlatformConfiguration
                {
                    Skills =
                    [
                        new SkillDefinition
                        {
                            SkillId = "env-skill",
                            Name = "Env Skill",
                            Description = "Reads env",
                            PackageHash = "hash-env-skill",
                            Status = "ready",
                            PackagePath = skillRoot,
                            HasScripts = true,
                            HasResources = false,
                            EnvKeys = ["SKILL_TOKEN"],
                            ImportTimestamp = new DateTimeOffset(2026, 5, 6, 0, 0, 0, TimeSpan.Zero),
                        },
                    ],
                }),
                CreateSkillPackageService(temp.Path),
                CreateCredentialResolverService(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["SKILL_TOKEN"] = "secret-value",
                }),
                new ToolExecutionPolicyService(),
                NullLogger<SkillAgentToolProvider>.Instance);

            var workspaceTool = new CapturingWorkspaceTool();
            var context = new AgentToolContext(
                new AgentDefinition
                {
                    Id = "agent-alpha",
                    Name = "Agent Alpha",
                    Sandbox = new AgentSandboxDefinition { Enabled = true },
                    SkillAttachments =
                    [
                        new AgentSkillAttachmentDefinition
                        {
                            SkillId = "env-skill",
                            Mode = SkillAttachmentMode.Trusted,
                        },
                    ],
                },
                "agent-alpha",
                UserId: "user-a",
                workspaceTool,
                WorkspaceTooling.BuildAgentSessionBinding(workspaceTool, "agent-alpha", "user-a", null),
                EffectiveSkillManifest:
                [
                    new EffectiveSkillManifestEntry
                    {
                        SkillId = "env-skill",
                        SourceScope = EffectiveSkillSourceScope.Agent,
                        SourceOwnerId = "agent-alpha",
                        Mode = SkillAttachmentMode.Trusted,
                        HasScripts = true,
                        HasResources = false,
                        Name = "Env Skill",
                        Description = "Reads env",
                        PackageHash = "hash-env-skill",
                        ImportTimestamp = new DateTimeOffset(2026, 5, 6, 0, 0, 0, TimeSpan.Zero),
                    },
                ]);

            IReadOnlyList<AIFunction> tools = [.. await provider.BuildToolsAsync(context, TestContext.Current.CancellationToken)];
            AIFunction scriptTool = Assert.Single(tools, static tool => string.Equals(tool.Name, "run_skill_script", StringComparison.Ordinal));

            _ = await scriptTool.InvokeAsync(
                new AIFunctionArguments(new Dictionary<string, object?>
                {
                    ["skillId"] = "env-skill",
                    ["scriptPath"] = "show-env.sh",
                }),
                TestContext.Current.CancellationToken);

            Assert.DoesNotContain("secret-value", workspaceTool.LastAgentCommand, StringComparison.Ordinal);
            Assert.DoesNotContain("export SKILL_TOKEN", workspaceTool.LastAgentCommand, StringComparison.Ordinal);
            Assert.Equal("secret-value", workspaceTool.LastEnvironment["SKILL_TOKEN"]);
        }
        finally
        {
            if (Directory.Exists(skillRoot))
            {
                Directory.Delete(skillRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunSkillScriptStripsPackageIndexOverrideEnvKeys()
    {
        using var temp = new TemporarySqliteDatabase();
        string skillRoot = OwnedSkillRoot(temp.Path, "index-skill");
        Directory.CreateDirectory(Path.Combine(skillRoot, "scripts"));
        await File.WriteAllTextAsync(Path.Combine(skillRoot, "SKILL.md"), "# Index Skill\n\nMust not retarget package indexes.", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(skillRoot, "scripts", "show-env.sh"), "#!/usr/bin/env bash\necho ok\n", TestContext.Current.CancellationToken);

        try
        {
            var provider = new SkillAgentToolProvider(
                new StaticPlatformConfigurationProvider(new PlatformConfiguration
                {
                    Skills =
                    [
                        new SkillDefinition
                        {
                            SkillId = "index-skill",
                            Name = "Index Skill",
                            Description = "Reads env",
                            PackageHash = "hash-index-skill",
                            Status = "ready",
                            PackagePath = skillRoot,
                            HasScripts = true,
                            HasResources = false,
                            EnvKeys = ["SKILL_TOKEN", "NPM_CONFIG_REGISTRY", "PIP_INDEX_URL", "UV_EXTRA_INDEX_URL"],
                            ImportTimestamp = new DateTimeOffset(2026, 5, 6, 0, 0, 0, TimeSpan.Zero),
                        },
                    ],
                }),
                CreateSkillPackageService(temp.Path),
                CreateCredentialResolverService(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["SKILL_TOKEN"] = "secret-value",
                    ["NPM_CONFIG_REGISTRY"] = "https://registry.npmjs.org/",
                    ["PIP_INDEX_URL"] = "https://pypi.org/simple",
                    ["UV_EXTRA_INDEX_URL"] = "https://pypi.org/simple",
                }),
                new ToolExecutionPolicyService(),
                NullLogger<SkillAgentToolProvider>.Instance);

            var workspaceTool = new CapturingWorkspaceTool();
            var context = new AgentToolContext(
                new AgentDefinition
                {
                    Id = "agent-alpha",
                    Name = "Agent Alpha",
                    Sandbox = new AgentSandboxDefinition { Enabled = true },
                    SkillAttachments =
                    [
                        new AgentSkillAttachmentDefinition
                        {
                            SkillId = "index-skill",
                            Mode = SkillAttachmentMode.Trusted,
                        },
                    ],
                },
                "agent-alpha",
                UserId: "user-a",
                workspaceTool,
                WorkspaceTooling.BuildAgentSessionBinding(workspaceTool, "agent-alpha", "user-a", null),
                EffectiveSkillManifest:
                [
                    new EffectiveSkillManifestEntry
                    {
                        SkillId = "index-skill",
                        SourceScope = EffectiveSkillSourceScope.Agent,
                        SourceOwnerId = "agent-alpha",
                        Mode = SkillAttachmentMode.Trusted,
                        HasScripts = true,
                        HasResources = false,
                        Name = "Index Skill",
                        Description = "Reads env",
                        PackageHash = "hash-index-skill",
                        ImportTimestamp = new DateTimeOffset(2026, 5, 6, 0, 0, 0, TimeSpan.Zero),
                    },
                ]);

            IReadOnlyList<AIFunction> tools = [.. await provider.BuildToolsAsync(context, TestContext.Current.CancellationToken)];
            AIFunction scriptTool = Assert.Single(tools, static tool => string.Equals(tool.Name, "run_skill_script", StringComparison.Ordinal));

            _ = await scriptTool.InvokeAsync(
                new AIFunctionArguments(new Dictionary<string, object?>
                {
                    ["skillId"] = "index-skill",
                    ["scriptPath"] = "show-env.sh",
                }),
                TestContext.Current.CancellationToken);

            Assert.Equal("secret-value", workspaceTool.LastEnvironment["SKILL_TOKEN"]);
            Assert.False(workspaceTool.LastEnvironment.ContainsKey("NPM_CONFIG_REGISTRY"));
            Assert.False(workspaceTool.LastEnvironment.ContainsKey("PIP_INDEX_URL"));
            Assert.False(workspaceTool.LastEnvironment.ContainsKey("UV_EXTRA_INDEX_URL"));
        }
        finally
        {
            if (Directory.Exists(skillRoot))
            {
                Directory.Delete(skillRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task UserScopedSkillDoesNotReceiveSystemCredentials()
    {
        using var temp = new TemporarySqliteDatabase();
        string skillRoot = Path.Combine(Path.GetTempPath(), "kautuka-skill-provider-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(skillRoot);
        Directory.CreateDirectory(Path.Combine(skillRoot, "scripts"));
        await File.WriteAllTextAsync(Path.Combine(skillRoot, "SKILL.md"), "# Scoped Skill\n\nReads credentials.", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(skillRoot, "scripts", "show-env.sh"), "#!/usr/bin/env bash\necho ok\n", TestContext.Current.CancellationToken);

        try
        {
            var entries = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SKILL_TOKEN"] = "user-token",
                ["MCP_AUTH_TOKEN"] = "system-token",
            };
            var systemOnly = new HashSet<string>(StringComparer.Ordinal) { "MCP_AUTH_TOKEN" };
            CapturingWorkspaceTool workspaceTool = new();
            SkillAgentToolProvider userProvider = CreateScopedSkillProvider(temp.Path, skillRoot, "user", entries, systemOnly);
            await InvokeShowEnvAsync(userProvider, workspaceTool, "user-skill").ConfigureAwait(true);
            Assert.Equal("user-token", workspaceTool.LastEnvironment["SKILL_TOKEN"]);
            Assert.False(workspaceTool.LastEnvironment.ContainsKey("MCP_AUTH_TOKEN"));

            SkillAgentToolProvider systemProvider = CreateScopedSkillProvider(temp.Path, skillRoot, "system", entries, systemOnly);
            await InvokeShowEnvAsync(systemProvider, workspaceTool, "system-skill").ConfigureAwait(true);
            Assert.Equal("system-token", workspaceTool.LastEnvironment["MCP_AUTH_TOKEN"]);
        }
        finally
        {
            if (Directory.Exists(skillRoot))
            {
                Directory.Delete(skillRoot, recursive: true);
            }
        }
    }

    private static SkillAgentToolProvider CreateScopedSkillProvider(
        string sqlitePath,
        string skillRoot,
        string scope,
        IReadOnlyDictionary<string, string> entries,
        IReadOnlySet<string> systemOnlyKeys)
    {
        string skillId = scope + "-skill";
        string package = OwnedSkillRoot(sqlitePath, skillId);
        if (!string.Equals(Path.GetFullPath(skillRoot), Path.GetFullPath(package), StringComparison.OrdinalIgnoreCase))
        {
            foreach (string file in Directory.EnumerateFiles(skillRoot, "*", SearchOption.AllDirectories))
            {
                string destination = Path.Combine(package, Path.GetRelativePath(skillRoot, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: true);
            }
        }

        return new SkillAgentToolProvider(
            new StaticPlatformConfigurationProvider(new PlatformConfiguration
            {
                Skills =
                [
                    new SkillDefinition
                    {
                        SkillId = skillId,
                        Name = "Scoped Skill",
                        Description = "Reads credentials",
                        PackageHash = "hash-" + skillId,
                        Status = "ready",
                        PackagePath = package,
                        HasScripts = true,
                        Scope = scope,
                        EnvKeys = ["SKILL_TOKEN", "MCP_AUTH_TOKEN"],
                        ImportTimestamp = new DateTimeOffset(2026, 5, 6, 0, 0, 0, TimeSpan.Zero),
                    },
                ],
            }),
            CreateSkillPackageService(sqlitePath),
            CreateCredentialResolverService(entries, systemOnlyKeys),
            new ToolExecutionPolicyService(),
            NullLogger<SkillAgentToolProvider>.Instance);
    }

    private static async Task InvokeShowEnvAsync(SkillAgentToolProvider provider, CapturingWorkspaceTool workspaceTool, string skillId)
    {
        var context = new AgentToolContext(
            new AgentDefinition
            {
                Id = "agent-alpha",
                Name = "Agent Alpha",
                Sandbox = new AgentSandboxDefinition { Enabled = true },
                SkillAttachments =
                [
                    new AgentSkillAttachmentDefinition
                    {
                        SkillId = skillId,
                        Mode = SkillAttachmentMode.Trusted,
                    },
                ],
            },
            "agent-alpha",
            UserId: "user-a",
            workspaceTool,
            WorkspaceTooling.BuildAgentSessionBinding(workspaceTool, "agent-alpha", "user-a", null),
            EffectiveSkillManifest:
            [
                new EffectiveSkillManifestEntry
                {
                    SkillId = skillId,
                    SourceScope = EffectiveSkillSourceScope.Agent,
                    SourceOwnerId = "agent-alpha",
                    Mode = SkillAttachmentMode.Trusted,
                    HasScripts = true,
                    Name = "Scoped Skill",
                    Description = "Reads credentials",
                    PackageHash = "hash-" + skillId,
                    ImportTimestamp = new DateTimeOffset(2026, 5, 6, 0, 0, 0, TimeSpan.Zero),
                },
            ]);

        IReadOnlyList<AIFunction> tools = [.. await provider.BuildToolsAsync(context, TestContext.Current.CancellationToken)];
        AIFunction scriptTool = Assert.Single(tools, static tool => string.Equals(tool.Name, "run_skill_script", StringComparison.Ordinal));
        _ = await scriptTool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["skillId"] = skillId,
                ["scriptPath"] = "show-env.sh",
            }),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task BuildToolsReturnsNoSkillToolsWhenSandboxIsDisabled()
    {
        using var temp = new TemporarySqliteDatabase();
        string skillRoot = OwnedSkillRoot(temp.Path, "roll-dice");
        Directory.CreateDirectory(Path.Combine(skillRoot, "scripts"));
        await File.WriteAllTextAsync(Path.Combine(skillRoot, "SKILL.md"), "# Roll Dice\n\nUse this skill to roll dice.\n\n```bash\necho $((RANDOM % <sides> + 1))\n```", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(skillRoot, "scripts", "generated-from-markdown.sh"), "echo 17", TestContext.Current.CancellationToken);

        try
        {
            var provider = new SkillAgentToolProvider(
                new StaticPlatformConfigurationProvider(new PlatformConfiguration
                {
                    Skills =
                    [
                        new SkillDefinition
                        {
                            SkillId = "roll-dice",
                            Name = "Roll Dice",
                            Description = "Roll a die",
                            PackageHash = "hash-roll-dice",
                            Status = "ready",
                            PackagePath = skillRoot,
                            HasScripts = true,
                            HasResources = false,
                            ImportTimestamp = new DateTimeOffset(2026, 4, 18, 0, 0, 0, TimeSpan.Zero),
                        },
                    ],
                }),
                CreateSkillPackageService(temp.Path),
                CreateCredentialResolverService(),
                new ToolExecutionPolicyService(),
                NullLogger<SkillAgentToolProvider>.Instance);

            var workspaceTool = new CapturingWorkspaceTool();
            var context = new AgentToolContext(
                new AgentDefinition
                {
                    Id = "agent-alpha",
                    Name = "Agent Alpha",
                    Sandbox = new AgentSandboxDefinition
                    {
                        Enabled = false,
                    },
                    SkillAttachments =
                    [
                        new AgentSkillAttachmentDefinition
                        {
                            SkillId = "roll-dice",
                            Mode = SkillAttachmentMode.Trusted,
                        },
                    ],
                },
                "agent-alpha",
                UserId: null,
                workspaceTool,
                WorkspaceTooling.BuildAgentSessionBinding(workspaceTool, "agent-alpha", null, null),
                EffectiveSkillManifest:
                [
                    new EffectiveSkillManifestEntry
                    {
                        SkillId = "roll-dice",
                        SourceScope = EffectiveSkillSourceScope.Agent,
                        SourceOwnerId = "agent-alpha",
                        Mode = SkillAttachmentMode.Trusted,
                        HasScripts = true,
                        HasResources = false,
                        Name = "Roll Dice",
                        Description = "Roll a die",
                        PackageHash = "hash-roll-dice",
                        ImportTimestamp = new DateTimeOffset(2026, 4, 18, 0, 0, 0, TimeSpan.Zero),
                    },
                ]);

            IReadOnlyList<AIFunction> tools = [.. await provider.BuildToolsAsync(context, TestContext.Current.CancellationToken)];
            Assert.Empty(tools);
        }
        finally
        {
            if (Directory.Exists(skillRoot))
            {
                Directory.Delete(skillRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BuildToolsWrapsRunSkillScriptWithApprovalWhenSkillRequiresApproval()
    {
        using var temp = new TemporarySqliteDatabase();
        string skillRoot = OwnedSkillRoot(temp.Path, "roll-dice");
        Directory.CreateDirectory(Path.Combine(skillRoot, "scripts"));
        await File.WriteAllTextAsync(Path.Combine(skillRoot, "SKILL.md"), "# Roll Dice\n\nUse this skill to roll dice.\n\n```bash\necho $((RANDOM % <sides> + 1))\n```", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(skillRoot, "scripts", "generated-from-markdown.sh"), "#!/usr/bin/env bash\necho 17\n", TestContext.Current.CancellationToken);

        try
        {
            var provider = new SkillAgentToolProvider(
                new StaticPlatformConfigurationProvider(new PlatformConfiguration
                {
                    Skills =
                    [
                        new SkillDefinition
                        {
                            SkillId = "roll-dice",
                            Name = "Roll Dice",
                            Description = "Roll a die",
                            PackageHash = "hash-roll-dice",
                            Status = "ready",
                            PackagePath = skillRoot,
                            HasScripts = true,
                            HasResources = false,
                            ImportTimestamp = new DateTimeOffset(2026, 4, 18, 0, 0, 0, TimeSpan.Zero),
                        },
                    ],
                }),
                CreateSkillPackageService(temp.Path),
                CreateCredentialResolverService(),
                new ToolExecutionPolicyService(),
                NullLogger<SkillAgentToolProvider>.Instance);

            var workspaceTool = new CapturingWorkspaceTool();
            var context = new AgentToolContext(
                new AgentDefinition
                {
                    Id = "agent-alpha",
                    Name = "Agent Alpha",
                    Sandbox = new AgentSandboxDefinition
                    {
                        Enabled = true,
                    },
                    SkillAttachments =
                    [
                        new AgentSkillAttachmentDefinition
                        {
                            SkillId = "roll-dice",
                            Mode = SkillAttachmentMode.RequireApproval,
                        },
                    ],
                },
                "agent-alpha",
                UserId: null,
                workspaceTool,
                WorkspaceTooling.BuildAgentSessionBinding(workspaceTool, "agent-alpha", null, null),
                EffectiveSkillManifest:
                [
                    new EffectiveSkillManifestEntry
                    {
                        SkillId = "roll-dice",
                        SourceScope = EffectiveSkillSourceScope.Agent,
                        SourceOwnerId = "agent-alpha",
                        Mode = SkillAttachmentMode.RequireApproval,
                        HasScripts = true,
                        HasResources = false,
                        Name = "Roll Dice",
                        Description = "Roll a die",
                        PackageHash = "hash-roll-dice",
                        ImportTimestamp = new DateTimeOffset(2026, 4, 18, 0, 0, 0, TimeSpan.Zero),
                    },
                ]);

            IReadOnlyList<AIFunction> tools = [.. await provider.BuildToolsAsync(context, TestContext.Current.CancellationToken)];

            AIFunction scriptTool = Assert.Single(tools, static tool => string.Equals(tool.Name, "run_skill_script", StringComparison.Ordinal));
            Assert.IsType<ApprovalRequiredAIFunction>(scriptTool);
        }
        finally
        {
            if (Directory.Exists(skillRoot))
            {
                Directory.Delete(skillRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BuildToolsRegistersRunSkillScriptWhenManifestHasScriptsFalseButMarkdownHasBashBlock()
    {
        // Simulate a skill imported before EnsureGeneratedScript existed:
        // HasScripts = false in the manifest, no scripts/ directory on disk, but SKILL.md has a bash block.
        // After self-healing in Inspect(), run_skill_script should be registered and load_skill should advertise it.
        using var temp = new TemporarySqliteDatabase();
        string skillRoot = OwnedSkillRoot(temp.Path, "roll-dice");
        // Intentionally no scripts/ directory — simulates pre-feature import
        await File.WriteAllTextAsync(Path.Combine(skillRoot, "SKILL.md"), "# Roll Dice\n\nUse this skill to roll dice.\n\n```bash\necho $((RANDOM % <sides> + 1))\n```", TestContext.Current.CancellationToken);

        try
        {
            var provider = new SkillAgentToolProvider(
                new StaticPlatformConfigurationProvider(new PlatformConfiguration
                {
                    Skills =
                    [
                        new SkillDefinition
                        {
                            SkillId = "roll-dice",
                            Name = "Roll Dice",
                            Description = "Roll a die",
                            PackageHash = "hash-roll-dice",
                            Status = "ready",
                            PackagePath = skillRoot,
                            HasScripts = false, // stale cached value from old SQLite row
                            HasResources = false,
                            ImportTimestamp = new DateTimeOffset(2026, 4, 18, 0, 0, 0, TimeSpan.Zero),
                        },
                    ],
                }),
                CreateSkillPackageService(temp.Path),
                CreateCredentialResolverService(),
                new ToolExecutionPolicyService(),
                NullLogger<SkillAgentToolProvider>.Instance);

            var workspaceTool = new CapturingWorkspaceTool();
            var context = new AgentToolContext(
                new AgentDefinition
                {
                    Id = "agent-alpha",
                    Name = "Agent Alpha",
                    Sandbox = new AgentSandboxDefinition { Enabled = true },
                    SkillAttachments = [new AgentSkillAttachmentDefinition { SkillId = "roll-dice", Mode = SkillAttachmentMode.Trusted }],
                },
                "agent-alpha",
                UserId: null,
                workspaceTool,
                WorkspaceTooling.BuildAgentSessionBinding(workspaceTool, "agent-alpha", null, null),
                EffectiveSkillManifest:
                [
                    new EffectiveSkillManifestEntry
                    {
                        SkillId = "roll-dice",
                        SourceScope = EffectiveSkillSourceScope.Agent,
                        SourceOwnerId = "agent-alpha",
                        Mode = SkillAttachmentMode.Trusted,
                        HasScripts = false, // stale
                        HasResources = false,
                        Name = "Roll Dice",
                        Description = "Roll a die",
                        PackageHash = "hash-roll-dice",
                        ImportTimestamp = new DateTimeOffset(2026, 4, 18, 0, 0, 0, TimeSpan.Zero),
                    },
                ]);

            IReadOnlyList<AIFunction> tools = [.. await provider.BuildToolsAsync(context, TestContext.Current.CancellationToken)];

            // run_skill_script MUST be registered despite HasScripts = false in the manifest
            Assert.Contains(tools, static tool => string.Equals(tool.Name, "run_skill_script", StringComparison.Ordinal));

            // load_skill MUST advertise the script tool
            AIFunction loadSkillTool = Assert.Single(tools, static tool => string.Equals(tool.Name, "load_skill", StringComparison.Ordinal));
            object? rawResult = await loadSkillTool.InvokeAsync(
                new AIFunctionArguments(new Dictionary<string, object?> { ["skillId"] = "roll-dice" }),
                TestContext.Current.CancellationToken);
            JsonElement payload = Assert.IsType<JsonElement>(rawResult);
            Assert.Equal("run_skill_script", payload.GetProperty("script_tool_name").GetString());
            Assert.NotEmpty(payload.GetProperty("scripts").EnumerateArray().ToList());

            // The generated script file should now exist on disk (self-healed)
            Assert.True(
                Directory.Exists(Path.Combine(skillRoot, "scripts")) &&
                Directory.EnumerateFiles(Path.Combine(skillRoot, "scripts")).Any());
        }
        finally
        {
            if (Directory.Exists(skillRoot))
            {
                Directory.Delete(skillRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LoadSkillFailsWithInspectExceptionWhenAttachedSkillPackageIsMissing()
    {
        using var temp = new TemporarySqliteDatabase();
        string missingSkillRoot = Path.Combine(Path.GetTempPath(), "kautuka-skill-provider-tests", Guid.NewGuid().ToString("N"));

        var provider = new SkillAgentToolProvider(
            new StaticPlatformConfigurationProvider(new PlatformConfiguration
            {
                Skills =
                [
                    new SkillDefinition
                    {
                        SkillId = "broken-skill",
                        Name = "Broken Skill",
                        Description = "Broken package path",
                        PackageHash = "hash-broken-skill",
                        Status = "ready",
                        PackagePath = missingSkillRoot,
                        HasScripts = true,
                        HasResources = false,
                        ImportTimestamp = new DateTimeOffset(2026, 4, 18, 0, 0, 0, TimeSpan.Zero),
                    },
                ],
            }),
            CreateSkillPackageService(temp.Path),
            CreateCredentialResolverService(),
            new ToolExecutionPolicyService(),
            NullLogger<SkillAgentToolProvider>.Instance);

        var workspaceTool = new CapturingWorkspaceTool();
        var context = new AgentToolContext(
            new AgentDefinition
            {
                Id = "agent-alpha",
                Name = "Agent Alpha",
                Sandbox = new AgentSandboxDefinition { Enabled = true },
                SkillAttachments =
                [
                    new AgentSkillAttachmentDefinition
                    {
                        SkillId = "broken-skill",
                        Mode = SkillAttachmentMode.Trusted,
                    },
                ],
            },
            "agent-alpha",
            UserId: null,
            workspaceTool,
            WorkspaceTooling.BuildAgentSessionBinding(workspaceTool, "agent-alpha", null, null),
            EffectiveSkillManifest:
            [
                new EffectiveSkillManifestEntry
                {
                    SkillId = "broken-skill",
                    SourceScope = EffectiveSkillSourceScope.Agent,
                    SourceOwnerId = "agent-alpha",
                    Mode = SkillAttachmentMode.Trusted,
                    HasScripts = true,
                    HasResources = false,
                    Name = "Broken Skill",
                    Description = "Broken package path",
                    PackageHash = "hash-broken-skill",
                    ImportTimestamp = new DateTimeOffset(2026, 4, 18, 0, 0, 0, TimeSpan.Zero),
                },
            ]);

        IReadOnlyList<AIFunction> tools = [.. await provider.BuildToolsAsync(context, TestContext.Current.CancellationToken)];

        Assert.Contains(tools, static tool => string.Equals(tool.Name, "load_skill", StringComparison.Ordinal));
        Assert.Contains(tools, static tool => string.Equals(tool.Name, "run_skill_script", StringComparison.Ordinal));

        AIFunction loadSkillTool = Assert.Single(tools, static tool => string.Equals(tool.Name, "load_skill", StringComparison.Ordinal));
        FileNotFoundException ex = await Assert.ThrowsAsync<FileNotFoundException>(() => loadSkillTool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["skillId"] = "broken-skill",
            }),
            TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("was not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildToolsResolvesWindowsSkillPathOnLinuxUsingLocalDataDirectoryFallback()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TemporarySqliteDatabase();
        string dataDir = System.IO.Path.GetDirectoryName(temp.Path)!;
        string skillRoot = System.IO.Path.Combine(dataDir, "skills", "roll-dice");
        Directory.CreateDirectory(skillRoot);
        await File.WriteAllTextAsync(System.IO.Path.Combine(skillRoot, "SKILL.md"), "# Roll Dice\n\nUse this skill to roll dice.", TestContext.Current.CancellationToken);

        var provider = new SkillAgentToolProvider(
            new StaticPlatformConfigurationProvider(new PlatformConfiguration
            {
                Skills =
                [
                    new SkillDefinition
                    {
                        SkillId = "roll-dice",
                        Name = "Roll Dice",
                        Description = "Roll a die",
                        PackageHash = "hash-roll-dice",
                        Status = "ready",
                        PackagePath = @"D:\Sources\kautuka\kautuka-core\Kautuka.Gateway\data\skills\roll-dice",
                        HasScripts = false,
                        HasResources = false,
                        ImportTimestamp = new DateTimeOffset(2026, 4, 27, 0, 0, 0, TimeSpan.Zero),
                    },
                ],
            }),
            CreateSkillPackageService(temp.Path),
            CreateCredentialResolverService(),
            new ToolExecutionPolicyService(),
            NullLogger<SkillAgentToolProvider>.Instance);

        var workspaceTool = new CapturingWorkspaceTool();
        var context = new AgentToolContext(
            new AgentDefinition
            {
                Id = "agent-alpha",
                Name = "Agent Alpha",
                Sandbox = new AgentSandboxDefinition { Enabled = true },
                SkillAttachments =
                [
                    new AgentSkillAttachmentDefinition
                    {
                        SkillId = "roll-dice",
                        Mode = SkillAttachmentMode.Trusted,
                    },
                ],
            },
            "agent-alpha",
            UserId: null,
            workspaceTool,
            WorkspaceTooling.BuildAgentSessionBinding(workspaceTool, "agent-alpha", null, null),
            EffectiveSkillManifest:
            [
                new EffectiveSkillManifestEntry
                {
                    SkillId = "roll-dice",
                    SourceScope = EffectiveSkillSourceScope.Agent,
                    SourceOwnerId = "agent-alpha",
                    Mode = SkillAttachmentMode.Trusted,
                    HasScripts = false,
                    HasResources = false,
                    Name = "Roll Dice",
                    Description = "Roll a die",
                    PackageHash = "hash-roll-dice",
                    ImportTimestamp = new DateTimeOffset(2026, 4, 27, 0, 0, 0, TimeSpan.Zero),
                },
            ]);

        IReadOnlyList<AIFunction> tools = [.. await provider.BuildToolsAsync(context, CancellationToken.None)];
        AIFunction loadSkillTool = Assert.Single(tools, static tool => string.Equals(tool.Name, "load_skill", StringComparison.Ordinal));

        object? rawResult = await loadSkillTool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["skillId"] = "roll-dice",
            }),
            CancellationToken.None);

        JsonElement payload = Assert.IsType<JsonElement>(rawResult);
        Assert.Equal("roll-dice", payload.GetProperty("skill_id").GetString());
        Assert.Contains("Roll Dice", payload.GetProperty("instructions_markdown").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StagePackageSkipsLinksThatLeaveThePackage()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-skill-stage-" + Guid.NewGuid().ToString("N"));
        string package = Path.Combine(root, "skills", "stage-links");
        string workspace = Path.Combine(root, "workspace");
        string outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(package);
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(package, "SKILL.md"), "ordinary skill", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "synthetic-skill-escape", TestContext.Current.CancellationToken);
        string junction = Path.Combine(package, "linked");
        string hardLink = Path.Combine(package, "alias.txt");
        using System.Diagnostics.Process junctionProcess = FileSystemLinkFixture.Start(junction, outside, directoryLink: true) ?? throw new InvalidOperationException("Could not start mklink.");
        await junctionProcess.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, junctionProcess.ExitCode);
        using System.Diagnostics.Process linkProcess = FileSystemLinkFixture.Start(hardLink, Path.Combine(outside, "secret.txt"), directoryLink: false) ?? throw new InvalidOperationException("Could not start mklink.");
        await linkProcess.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, linkProcess.ExitCode);

        string sqlite = Path.Combine(root, "gateway.db");
        try
        {
            SkillPackageService service = CreateSkillPackageService(sqlite);
            var skill = new Kautuka.Gateway.Infrastructure.Configuration.SkillDefinition
            {
                SkillId = "stage-links",
                PackagePath = package,
                PackageHash = "hash-stage-links",
            };
            service.StagePackageForWorkspace(skill, workspace, "/workspace");
            string staged = Path.Combine(workspace, ".kautuka", "skills", "stage-links");
            Assert.Equal("ordinary skill", await File.ReadAllTextAsync(Path.Combine(staged, "SKILL.md"), TestContext.Current.CancellationToken));
            Assert.False(File.Exists(Path.Combine(staged, "linked", "secret.txt")));
            Assert.False(File.Exists(Path.Combine(staged, "alias.txt")));
            Assert.DoesNotContain("synthetic-skill-escape", string.Join('\n', Directory.EnumerateFiles(staged, "*", SearchOption.AllDirectories).Select(File.ReadAllText)), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(junction))
            {
                Directory.Delete(junction);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StagePackageRefusesAWorkspaceJunction()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-skill-stage-dest-" + Guid.NewGuid().ToString("N"));
        string package = Path.Combine(root, "skills", "stage-dest");
        string workspace = Path.Combine(root, "workspace");
        string outside = Path.Combine(root, "outside");
        string junction = Path.Combine(workspace, ".kautuka");
        Directory.CreateDirectory(package);
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(package, "SKILL.md"), "ordinary skill", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(outside, "keep.txt"), "synthetic-stage-target", TestContext.Current.CancellationToken);
        using System.Diagnostics.Process junctionProcess = FileSystemLinkFixture.Start(junction, outside, directoryLink: true) ?? throw new InvalidOperationException("Could not start mklink.");
        await junctionProcess.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, junctionProcess.ExitCode);

        string sqlite = Path.Combine(root, "gateway.db");
        try
        {
            SkillPackageService service = CreateSkillPackageService(sqlite);
            var skill = new Kautuka.Gateway.Infrastructure.Configuration.SkillDefinition
            {
                SkillId = "stage-dest",
                PackagePath = package,
                PackageHash = "hash-stage-dest",
            };
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => service.StagePackageForWorkspace(skill, workspace, "/workspace"));
            Assert.DoesNotContain("synthetic-stage-target", exception.Message, StringComparison.Ordinal);
            Assert.Equal("synthetic-stage-target", await File.ReadAllTextAsync(Path.Combine(outside, "keep.txt"), TestContext.Current.CancellationToken));
            Assert.False(File.Exists(Path.Combine(outside, "skills", "stage-dest", "SKILL.md")));
        }
        finally
        {
            if (Directory.Exists(junction))
            {
                Directory.Delete(junction);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task InspectSkillDoesNotFollowAHardLinkOutsideThePackage()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-skill-inspect-" + Guid.NewGuid().ToString("N"));
        string package = Path.Combine(root, "skills", "inspect-links");
        string outside = Path.Combine(root, "outside.txt");
        Directory.CreateDirectory(package);
        await File.WriteAllTextAsync(outside, "synthetic-skill-instructions", TestContext.Current.CancellationToken);
        string skillFile = Path.Combine(package, "SKILL.md");
        using System.Diagnostics.Process process = FileSystemLinkFixture.Start(skillFile, outside, directoryLink: false) ?? throw new InvalidOperationException("Could not start mklink.");
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, process.ExitCode);
        string sqlite = Path.Combine(root, "gateway.db");
        try
        {
            SkillPackageService service = CreateSkillPackageService(sqlite);
            var skill = new Kautuka.Gateway.Infrastructure.Configuration.SkillDefinition
            {
                SkillId = "inspect-links",
                PackagePath = package,
            };
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => service.Inspect(skill));
            Assert.DoesNotContain("synthetic-skill-instructions", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static SkillPackageService CreateSkillPackageService(string sqlitePath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kautuka:ContainerStateRoot"] = Path.GetDirectoryName(sqlitePath) ?? throw new InvalidOperationException("Expected sqlite path to include a parent directory."),
            })
            .Build();

        return new SkillPackageService(new KautukaDatabase(configuration));
    }

    private static CredentialResolverService CreateCredentialResolverService(
        IReadOnlyDictionary<string, string>? defaultUserEntries = null,
        IReadOnlySet<string>? systemOnlyKeys = null)
        => new(new Lazy<CredentialsService.CredentialsServiceClient>(
            () => new FakeCredentialsServiceClient(defaultUserEntries, systemOnlyKeys)));

    private sealed class StaticPlatformConfigurationProvider(PlatformConfiguration configuration) : IPlatformConfigurationProvider
    {
        public PlatformConfiguration GetCurrent() => configuration;
    }

    private sealed class FakeCredentialsServiceClient(
        IReadOnlyDictionary<string, string>? defaultUserEntries,
        IReadOnlySet<string>? systemOnlyKeys = null)
        : CredentialsService.CredentialsServiceClient
    {
        public override AsyncUnaryCall<CredentialMapResponse> GetSystemCredentialAsync(
            GetSystemCredentialRequest request,
            CallOptions options)
            => Wrap(new CredentialMapResponse());

        public override AsyncUnaryCall<CredentialMapResponse> GetResolvedCredentialsAsync(
            GetResolvedCredentialsRequest request,
            CallOptions options)
        {
            var response = new CredentialMapResponse();
            if (defaultUserEntries is null)
            {
                return Wrap(response);
            }

            foreach (string key in request.RequiredKeys)
            {
                if (systemOnlyKeys?.Contains(key) == true && !request.IncludeSystemScope)
                {
                    continue;
                }

                if (defaultUserEntries.TryGetValue(key, out string? value))
                {
                    response.Entries[key] = value;
                }
            }

            return Wrap(response);
        }

        public override AsyncUnaryCall<CredentialKeyListResponse> ListUserCredentialKeysAsync(
            ListUserCredentialKeysRequest request,
            CallOptions options)
        {
            var response = new CredentialKeyListResponse();
            if (defaultUserEntries is not null)
            {
                response.Keys.AddRange(defaultUserEntries.Keys);
            }

            return Wrap(response);
        }

        public override AsyncUnaryCall<SetCredentialsResponse> SetUserCredentialsAsync(
            SetUserCredentialsRequest request,
            CallOptions options)
            => Wrap(new SetCredentialsResponse { UpsertedCount = request.Entries.Count });

        private static AsyncUnaryCall<T> Wrap<T>(T response) where T : class
            => new(
                responseAsync: Task.FromResult(response),
                responseHeadersAsync: Task.FromResult(new Metadata()),
                getStatusFunc: () => Status.DefaultSuccess,
                getTrailersFunc: () => new Metadata(),
                disposeAction: static () => { });
    }

    [Fact]
    public async Task RunSkillScriptIgnoresAReplacedWorkspaceCopy()
    {
        using var temp = new TemporarySqliteDatabase();
        string dataDirectory = Path.GetDirectoryName(temp.Path)!;
        string package = Path.Combine(dataDirectory, "skills", "trusted-script");
        string workspace = Path.Combine(dataDirectory, "workspace");
        Directory.CreateDirectory(Path.Combine(package, "scripts"));
        await File.WriteAllTextAsync(Path.Combine(package, "SKILL.md"), "# Trusted\n\nRuns the package script.", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(package, "scripts", "run.sh"), "echo package-script\n", TestContext.Current.CancellationToken);
        string stagedScript = Path.Combine(workspace, ".kautuka", "skills", "trusted-script", "scripts", "run.sh");
        Directory.CreateDirectory(Path.GetDirectoryName(stagedScript)!);
        await File.WriteAllTextAsync(stagedScript, "echo tampered-script\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(workspace, ".kautuka", "skills", "trusted-script", ".packagehash"), "hash-trusted-script", TestContext.Current.CancellationToken);

        var provider = new SkillAgentToolProvider(
            new StaticPlatformConfigurationProvider(new PlatformConfiguration
            {
                Skills =
                [
                    new SkillDefinition
                    {
                        SkillId = "trusted-script",
                        Name = "Trusted",
                        Description = "Package script",
                        PackageHash = "hash-trusted-script",
                        Status = "ready",
                        PackagePath = package,
                        HasScripts = true,
                        HasResources = false,
                        ImportTimestamp = new DateTimeOffset(2026, 4, 18, 0, 0, 0, TimeSpan.Zero),
                    },
                ],
            }),
            CreateSkillPackageService(temp.Path),
            CreateCredentialResolverService(),
            new ToolExecutionPolicyService(),
            NullLogger<SkillAgentToolProvider>.Instance);
        var workspaceTool = new CapturingWorkspaceTool(workspace);
        var context = new AgentToolContext(
            new AgentDefinition
            {
                Id = "agent-alpha",
                Name = "Agent Alpha",
                Sandbox = new AgentSandboxDefinition { Enabled = true },
                SkillAttachments =
                [
                    new AgentSkillAttachmentDefinition
                    {
                        SkillId = "trusted-script",
                        Mode = SkillAttachmentMode.Trusted,
                    },
                ],
            },
            "agent-alpha",
            UserId: "user-1",
            workspaceTool,
            WorkspaceTooling.BuildAgentSessionBinding(workspaceTool, "agent-alpha", "user-1", null),
            EffectiveSkillManifest:
            [
                new EffectiveSkillManifestEntry
                {
                    SkillId = "trusted-script",
                    SourceScope = EffectiveSkillSourceScope.Agent,
                    SourceOwnerId = "agent-alpha",
                    Mode = SkillAttachmentMode.Trusted,
                    HasScripts = true,
                    HasResources = false,
                    Name = "Trusted",
                    Description = "Package script",
                    PackageHash = "hash-trusted-script",
                    ImportTimestamp = new DateTimeOffset(2026, 4, 18, 0, 0, 0, TimeSpan.Zero),
                },
            ]);

        IReadOnlyList<AIFunction> tools = [.. await provider.BuildToolsAsync(context, TestContext.Current.CancellationToken)];
        AIFunction scriptTool = Assert.Single(tools, static tool => string.Equals(tool.Name, "run_skill_script", StringComparison.Ordinal));
        await scriptTool.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["skillId"] = "trusted-script" }),
            TestContext.Current.CancellationToken);

        Assert.Contains("echo package-script", workspaceTool.LastAgentCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("tampered-script", workspaceTool.LastAgentCommand, StringComparison.Ordinal);
        Assert.Equal("echo tampered-script\n", await File.ReadAllTextAsync(stagedScript, TestContext.Current.CancellationToken));
    }

    private sealed class CapturingWorkspaceTool(string hostWorkspacePath = "D:/temp/agent") : IWorkspaceRuntimeTool
    {
        public string? LastAgentCommand { get; private set; }
        public Dictionary<string, string> LastEnvironment { get; private set; }
            = new Dictionary<string, string>(StringComparer.Ordinal);
        public WorkspaceCommandResult Result { get; set; }
            = new("completed", 0, "17\n", string.Empty);

        public WorkspaceRuntimeContext DescribeAgentWorkspace(string agentId, string? userId = null)
            => new(
                ScopeType: "agent",
                ScopeId: agentId,
                UserId: userId,
                HostWorkspacePath: hostWorkspacePath,
                ContainerWorkspacePath: "/workspace",
                RuntimeHostWorkspacePath: hostWorkspacePath,
                Image: "kautuka-base:latest",
                DefaultTimeoutSeconds: 30,
                BaseCapabilities: [],
                ContainerEnvironment: new Dictionary<string, string>());

        public WorkspaceRuntimeContext RebuildAgentWorkspace(string agentId, string? userId = null) => DescribeAgentWorkspace(agentId, userId);
        public bool DeleteAgentWorkspace(string agentId) => true;
        public Task<WorkspaceSandboxSession> SpawnAgentSandboxAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkspaceCommandResult> RunAgentShellAsync(string agentId, string command, string? userId = null, int? timeoutSeconds = null, CancellationToken cancellationToken = default)
        {
            LastAgentCommand = command;
            return Task.FromResult(Result);
        }
        public Task<WorkspaceCommandResult> RunAgentShellWithEnvironmentAsync(
            string agentId,
            string command,
            IReadOnlyDictionary<string, string> environment,
            string? userId = null,
            int? timeoutSeconds = null,
            CancellationToken cancellationToken = default)
        {
            LastAgentCommand = command;
            LastEnvironment = new Dictionary<string, string>(environment, StringComparer.Ordinal);
            return Task.FromResult(Result);
        }
        public Task<WorkspaceSandboxSession> InspectAgentSandboxAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CloseAgentSandboxAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static string QuoteShellLiteral(string value)
        => $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

    private static string OwnedSkillRoot(string sqlitePath, string skillId)
    {
        string package = Path.Combine(Path.GetDirectoryName(sqlitePath)!, "skills", skillId);
        Directory.CreateDirectory(package);
        return package;
    }

    private sealed class TemporarySqliteDatabase : IDisposable
    {
        public TemporarySqliteDatabase()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kautuka-skill-provider-db", Guid.NewGuid().ToString("N"), "gateway.db");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        }

        public string Path { get; }

        public void Dispose()
        {
            string? root = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
