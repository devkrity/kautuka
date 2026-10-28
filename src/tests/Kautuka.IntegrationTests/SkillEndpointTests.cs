using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Kautuka.Gateway.Domain.Auth;
using Kautuka.Gateway.Domain.Skills;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Xunit.Sdk;

namespace Kautuka.IntegrationTests;

public sealed class SkillEndpointTests
{
    [Fact]
    public async Task ValidateEndpointReadsSkillArchiveMetadata()
    {
        await using var fixture = await SkillApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();

        using JsonDocument response = await fixture.ValidateAsync(CreateWrappedArchive(), accessToken, skillId: "validated-skill");

        JsonElement skill = response.RootElement.GetProperty("single").GetProperty("skill");
        Assert.Equal("validated-skill", skill.GetProperty("id").GetString());
        Assert.Equal("validated-skill", skill.GetProperty("name").GetString());
        Assert.True(skill.GetProperty("hasResources").GetBoolean());
        Assert.True(skill.GetProperty("hasScripts").GetBoolean());
        Assert.Equal("single", response.RootElement.GetProperty("type").GetString());
        Assert.Equal("references/guide.md", response.RootElement.GetProperty("single").GetProperty("resourcePaths")[0].GetString());
        Assert.Equal("scripts/run.ps1", response.RootElement.GetProperty("single").GetProperty("scriptPaths")[0].GetString());
    }

    [Fact]
    public async Task ImportEndpointPersistsSkillAndSupportsInspectionAndDelete()
    {
        await using var fixture = await SkillApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();

        using JsonDocument importResponse = await fixture.ImportAsync(CreateWrappedArchive(), accessToken, skillId: "ops-skill");
        Assert.Equal("single", importResponse.RootElement.GetProperty("type").GetString());
        Assert.Equal("validated-skill", importResponse.RootElement.GetProperty("single").GetProperty("skill").GetProperty("id").GetString());

        using JsonDocument listResponse = await fixture.SendJsonAsync(HttpMethod.Get, "/api/skills", body: null, accessToken);
        JsonElement listedSkill = listResponse.RootElement
            .GetProperty("skills")
            .EnumerateArray()
            .First(static skill => string.Equals(skill.GetProperty("id").GetString(), "validated-skill", StringComparison.Ordinal));
        Assert.Equal("validated-skill", listedSkill.GetProperty("id").GetString());

        using JsonDocument inspectionResponse = await fixture.SendJsonAsync(HttpMethod.Get, "/api/skills/validated-skill", body: null, accessToken);
        Assert.Contains("# Validated Skill", inspectionResponse.RootElement.GetProperty("instructionsMarkdown").GetString(), StringComparison.Ordinal);

        using JsonDocument deleteResponse = await fixture.SendJsonAsync(HttpMethod.Delete, "/api/skills/validated-skill", body: null, accessToken);
        Assert.True(deleteResponse.RootElement.GetProperty("deleted").GetBoolean());

        using JsonDocument emptyListResponse = await fixture.SendJsonAsync(HttpMethod.Get, "/api/skills", body: null, accessToken);
        Assert.DoesNotContain(
            emptyListResponse.RootElement.GetProperty("skills").EnumerateArray(),
            static skill => string.Equals(skill.GetProperty("id").GetString(), "validated-skill", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidateEndpointReturnsBadRequestForInvalidPackage()
    {
        await using var fixture = await SkillApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();

        using JsonDocument response = await fixture.ValidateExpectingErrorAsync(CreateArchiveWithoutInstructions(), accessToken, skillId: "broken-skill", expectedStatusCode: System.Net.HttpStatusCode.BadRequest);

        Assert.Contains("SKILL.md", response.RootElement.GetProperty("detail").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateEndpointSynthesizesShellScriptForMarkdownOnlySkill()
    {
        await using var fixture = await SkillApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();

        using JsonDocument response = await fixture.ValidateAsync(CreateArchiveWithMarkdownShellBlock(), accessToken, skillId: "roll-dice");

        JsonElement skill = response.RootElement.GetProperty("single").GetProperty("skill");
        Assert.True(skill.GetProperty("hasScripts").GetBoolean());
        Assert.Equal("scripts/generated-from-markdown.sh", response.RootElement.GetProperty("single").GetProperty("scriptPaths")[0].GetString());
    }

    [Fact]
    public async Task ImportEndpointPersistsGeneratedShellScriptForMarkdownOnlySkill()
    {
        await using var fixture = await SkillApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();

        using JsonDocument importResponse = await fixture.ImportAsync(CreateArchiveWithMarkdownShellBlock(), accessToken, skillId: "roll-dice");
        Assert.True(importResponse.RootElement.GetProperty("single").GetProperty("skill").GetProperty("hasScripts").GetBoolean());

        using JsonDocument inspectionResponse = await fixture.SendJsonAsync(HttpMethod.Get, "/api/skills/roll-dice", body: null, accessToken);
        Assert.Equal("generated-from-markdown.sh", inspectionResponse.RootElement.GetProperty("scriptPaths")[0].GetString());
    }

    [Fact]
    public async Task PublicApisImportApprovalRequiredSkillAndConfigureAaatAgents()
    {
        await using var fixture = await SkillApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();

        using JsonDocument imported = await fixture.ImportAsync(CreateArchiveWithMarkdownShellBlock(), accessToken, skillId: "roll-dice");
        Assert.Equal("roll-dice", imported.RootElement.GetProperty("single").GetProperty("skill").GetProperty("id").GetString());
        await fixture.SendJsonAsync(HttpMethod.Post, "/api/settings/providers/save", new
        {
            id = "acceptance-provider",
            type = "ollama",
            settings = new { base_url = "http://127.0.0.1:11434" },
        }, accessToken);
        await fixture.SendJsonAsync(HttpMethod.Post, "/api/settings/models/save", new
        {
            id = "acceptance-model",
            providerId = "acceptance-provider",
            modelName = "acceptance-model",
            settings = new { },
        }, accessToken);

        await fixture.SendJsonAsync(HttpMethod.Put, "/api/settings/agents/acceptance-roller", new
        {
            name = "Roller",
            modelId = "acceptance-model",
            systemPrompt = "Use roll-dice for every requested roll.",
            mcpBindings = Array.Empty<object>(),
            skillAttachments = new[] { new { skillId = "roll-dice", mode = "requireApproval" } },
            session = new { historyEnabled = true, maxHistoryRuns = 5, summariesEnabled = false },
            sandbox = new { enabled = true, allowedLanguages = Array.Empty<string>(), maxMemory = "2g", maxTimeout = 120 },
            toolExposure = new { exposedAsTool = true, description = "Roll dice", invocationTimeoutSeconds = 60 },
        }, accessToken);
        await fixture.SendJsonAsync(HttpMethod.Put, "/api/settings/agents/acceptance-assistant", new
        {
            name = "Assistant",
            modelId = "acceptance-model",
            systemPrompt = "Delegate dice rolls to Roller.",
            mcpBindings = Array.Empty<object>(),
            skillAttachments = Array.Empty<object>(),
            session = new { historyEnabled = true, maxHistoryRuns = 5, summariesEnabled = false },
            sandbox = new { enabled = false, allowedLanguages = Array.Empty<string>(), maxMemory = "2g", maxTimeout = 120 },
            approvalConfig = new
            {
                agentToolIds = new[] { "acceptance-roller" },
                approvalRequiredAgentToolIds = new[] { "acceptance-roller" },
            },
        }, accessToken);

        using JsonDocument agents = await fixture.SendJsonAsync(HttpMethod.Get, "/api/settings/agents", body: null, accessToken);
        JsonElement roller = agents.RootElement.GetProperty("agents").EnumerateArray().Single(agent => agent.GetProperty("id").GetString() == "acceptance-roller");
        JsonElement assistant = agents.RootElement.GetProperty("agents").EnumerateArray().Single(agent => agent.GetProperty("id").GetString() == "acceptance-assistant");
        Assert.Equal("roll-dice", roller.GetProperty("skillAttachments")[0].GetProperty("skillId").GetString());
        Assert.True(roller.GetProperty("sandbox").GetProperty("enabled").GetBoolean());
        Assert.True(roller.GetProperty("toolExposure").GetProperty("exposedAsTool").GetBoolean());
        Assert.Equal("acceptance-roller", assistant.GetProperty("approvalConfig").GetProperty("agentToolIds")[0].GetString());
        Assert.Equal("acceptance-roller", assistant.GetProperty("approvalConfig").GetProperty("approvalRequiredAgentToolIds")[0].GetString());
    }

    [Fact]
    public async Task ImportEndpointDoesNotReplaceASkillTheCallerCannotManage()
    {
        await using var fixture = await SkillApiFixture.CreateAsync();
        string adminToken = await fixture.LoginAsBootstrapAdminAsync();
        _ = await fixture.ImportAsync(CreateWrappedArchive(), adminToken, skillId: "validated-skill");

        using JsonDocument invited = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/users/invite",
            new
            {
                username = "skill-importer",
                email = "skill-importer@example.com",
                password = "Testing1",
                role = "user",
            },
            adminToken);
        string userId = invited.RootElement.GetProperty("user").GetProperty("id").GetString()
            ?? throw new XunitException("Invite response did not include a user id.");
        _ = await fixture.SendJsonAsync(
            HttpMethod.Put,
            $"/api/settings/users/{userId}/import-permissions",
            new
            {
                canImportConnectors = false,
                canImportSkills = true,
            },
            adminToken);
        string memberToken = await fixture.LoginAsAsync("skill-importer", "Testing1");

        using JsonDocument rejected = await fixture.SendMultipartAsync(
            "/api/skills/import",
            CreateReplacementArchive(),
            memberToken,
            skillId: "validated-skill",
            expectedStatusCode: System.Net.HttpStatusCode.Forbidden);
        Assert.Equal("Forbidden.", rejected.RootElement.GetProperty("detail").GetString());

        using JsonDocument inspection = await fixture.SendJsonAsync(HttpMethod.Get, "/api/skills/validated-skill", body: null, adminToken);
        Assert.Contains("Use this skill to inspect runtime state.", inspection.RootElement.GetProperty("instructionsMarkdown").GetString(), StringComparison.Ordinal);

        using JsonDocument ownImport = await fixture.ImportAsync(CreateOwnedArchive(), memberToken, skillId: "owned-skill");
        Assert.Equal("owned-skill", ownImport.RootElement.GetProperty("single").GetProperty("skill").GetProperty("id").GetString());
        (string ownedScope, string? ownedOwner) = fixture.ReadStoredSkillOwnership("owned-skill");
        Assert.Equal("user", ownedScope);
        Assert.Equal(userId, ownedOwner);

        using JsonDocument replaced = await fixture.ImportAsync(CreateReplacementArchive(), adminToken, skillId: "validated-skill");
        Assert.Contains("Replaced skill body.", replaced.RootElement.GetProperty("single").GetProperty("instructionsMarkdown").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SkillMetadataCannotRetargetOrDeleteAnotherDirectory()
    {
        await using var fixture = await SkillApiFixture.CreateAsync();
        string adminToken = await fixture.LoginAsBootstrapAdminAsync();
        using JsonDocument imported = await fixture.ImportAsync(CreateWrappedArchive(), adminToken, skillId: "validated-skill");
        string importedPackage = imported.RootElement.GetProperty("single").GetProperty("skill").GetProperty("packagePath").GetString()
            ?? throw new XunitException("Imported skill did not include a package path.");
        string importedInstructions = Path.Combine(importedPackage, "SKILL.md");
        Assert.True(File.Exists(importedInstructions));

        using JsonDocument invited = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/users/invite",
            new
            {
                username = "skill-path",
                email = "skill-path@example.com",
                password = "Testing1",
                role = "user",
            },
            adminToken);
        string memberToken = await fixture.LoginAsAsync("skill-path", "Testing1");

        string outside = Path.Combine(Path.GetTempPath(), "kautuka-skill-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        string marker = Path.Combine(outside, "marker.txt");
        await File.WriteAllTextAsync(marker, "keep", TestContext.Current.CancellationToken);
        try
        {
            using JsonDocument outsideSave = await fixture.SendJsonAsync(
                HttpMethod.Post,
                "/api/skills/path-probe",
                new { name = "Path Probe", packagePath = outside },
                memberToken);
            Assert.True(string.IsNullOrEmpty(outsideSave.RootElement.GetProperty("packagePath").GetString()));

            using JsonDocument borrowedSave = await fixture.SendJsonAsync(
                HttpMethod.Post,
                "/api/skills/path-probe",
                new { name = "Path Probe", packagePath = importedPackage },
                memberToken);
            Assert.True(string.IsNullOrEmpty(borrowedSave.RootElement.GetProperty("packagePath").GetString()));

            using JsonDocument deleted = await fixture.SendJsonAsync(HttpMethod.Delete, "/api/skills/path-probe", body: null, memberToken);
            Assert.True(deleted.RootElement.GetProperty("deleted").GetBoolean());
            Assert.True(File.Exists(marker));
            Assert.True(File.Exists(importedInstructions));
        }
        finally
        {
            if (Directory.Exists(outside))
            {
                Directory.Delete(outside, recursive: true);
            }
        }
    }

    [Fact]
    public async Task NonAdminCannotCreateSystemLockedSkillByCase()
    {
        await using var fixture = await SkillApiFixture.CreateAsync();
        string adminToken = await fixture.LoginAsBootstrapAdminAsync();
        using JsonDocument invited = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/users/invite",
            new
            {
                username = "skill-case",
                email = "skill-case@example.com",
                password = "Testing1",
                role = "user",
            },
            adminToken);
        string memberToken = await fixture.LoginAsAsync("skill-case", "Testing1");

        using HttpRequestMessage request = new(HttpMethod.Post, "/api/skills/Kautuka-Docs")
        {
            Content = JsonContent.Create(new { name = "Docs copy", envKeys = new[] { "provider.docs.api_key" } }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", memberToken);
        using HttpResponseMessage response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);

        using HttpRequestMessage lookup = new(HttpMethod.Get, "/api/skills/Kautuka-Docs");
        lookup.Headers.Authorization = new AuthenticationHeaderValue("Bearer", memberToken);
        using HttpResponseMessage missing = await fixture.Client.SendAsync(lookup, TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task CollapsedSkillIdCannotStageOrDeleteAnotherPackage()
    {
        await using var fixture = await SkillApiFixture.CreateAsync();
        string adminToken = await fixture.LoginAsBootstrapAdminAsync();
        using JsonDocument imported = await fixture.ImportAsync(CreateWrappedArchive(), adminToken, skillId: "validated-skill");
        string importedPackage = imported.RootElement.GetProperty("single").GetProperty("skill").GetProperty("packagePath").GetString()
            ?? throw new XunitException("Imported skill did not include a package path.");
        string importedInstructions = Path.Combine(importedPackage, "SKILL.md");
        Assert.True(File.Exists(importedInstructions));

        Assert.Equal(string.Empty, fixture.ConfineSkillPackagePath("validated-skill.", importedPackage));
        fixture.DeleteSkillPackage("validated-skill.", importedPackage);
        Assert.Equal(string.Empty, fixture.ConfineSkillPackagePath("Validated-Skill", importedPackage));
        fixture.DeleteSkillPackage("Validated-Skill", importedPackage);
        Assert.True(File.Exists(importedInstructions));

        using HttpRequestMessage caseSave = new(HttpMethod.Post, "/api/skills/Validated-Skill")
        {
            Content = JsonContent.Create(new { name = "Validated Skill", packagePath = importedPackage }),
        };
        caseSave.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using HttpResponseMessage caseSaveResponse = await fixture.Client.SendAsync(caseSave, TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, caseSaveResponse.StatusCode);
        Assert.True(File.Exists(importedInstructions));

        string workspace = Path.Combine(Path.GetTempPath(), "kautuka-skill-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(workspace, ".kautuka"));
        string marker = Path.Combine(workspace, ".kautuka", "keep.txt");
        await File.WriteAllTextAsync(marker, "keep", TestContext.Current.CancellationToken);
        try
        {
            Assert.Throws<InvalidOperationException>(() => fixture.StageSkillPackage(new SkillDefinition { SkillId = ".." }, workspace));
            if (OperatingSystem.IsWindows())
            {
                Assert.Throws<InvalidOperationException>(() => fixture.StageSkillPackage(
                    new SkillDefinition { SkillId = "Validated-Skill", PackagePath = importedPackage },
                    workspace));
            }

            Assert.True(File.Exists(importedInstructions));
            Assert.True(File.Exists(marker));
        }
        finally
        {
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ImportEndpointReturnsBundleResultForMultiSkillArchive()
    {
        await using var fixture = await SkillApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();

        using JsonDocument importResponse = await fixture.ImportAsync(CreateBundleArchive(), accessToken, skillId: "ignored-skill-id");
        Assert.Equal("bundle", importResponse.RootElement.GetProperty("type").GetString());
        Assert.Equal(2, importResponse.RootElement.GetProperty("bundle").GetProperty("skills").GetArrayLength());
    }

    [Fact]
    public async Task ImportEndpointRejectsBundleWithSharedRootFiles()
    {
        await using var fixture = await SkillApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();

        using JsonDocument response = await fixture.SendMultipartAsync(
            "/api/skills/import",
            CreateBundleArchiveWithSharedRootScripts(),
            accessToken,
            skillId: "ignored-skill-id",
            expectedStatusCode: System.Net.HttpStatusCode.BadRequest);

        Assert.Contains("child skill directories", response.RootElement.GetProperty("detail").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] CreateWrappedArchive()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            CreateEntry(archive, "validated-skill/SKILL.md", "---\nname: validated-skill\ndescription: Skill package for tests\nmetadata:\n  source: tests\n---\n\n# Validated Skill\n\nUse this skill to inspect runtime state.");
            CreateEntry(archive, "validated-skill/references/guide.md", "Resource body");
            CreateEntry(archive, "validated-skill/scripts/run.ps1", "Write-Output 'ok'");
        }

        return stream.ToArray();
    }

    private static byte[] CreateReplacementArchive()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            CreateEntry(archive, "validated-skill/SKILL.md", "---\nname: validated-skill\ndescription: Replaced skill\n---\n\n# Replaced\n\nReplaced skill body.");
        }

        return stream.ToArray();
    }

    private static byte[] CreateOwnedArchive()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            CreateEntry(archive, "owned-skill/SKILL.md", "---\nname: owned-skill\ndescription: Owned by the importer\n---\n\n# Owned\n\nThis skill belongs to the importer.");
        }

        return stream.ToArray();
    }

    private static byte[] CreateArchiveWithoutInstructions()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            CreateEntry(archive, "broken/references/guide.md", "Resource body");
        }

        return stream.ToArray();
    }

    private static byte[] CreateArchiveWithMarkdownShellBlock()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            CreateEntry(archive, "roll-dice/SKILL.md", "---\nname: roll-dice\ndescription: Use this skill to roll dice\n---\n\n# Roll Dice\n\n```bash\necho $((RANDOM % <sides> + 1))\n```");
        }

        return stream.ToArray();
    }

    private static byte[] CreateBundleArchive()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            CreateEntry(archive, "skills/alpha-skill/SKILL.md", "---\nname: alpha-skill\ndescription: Alpha skill\n---\n\n# Alpha\n");
            CreateEntry(archive, "skills/alpha-skill/references/guide.md", "alpha");
            CreateEntry(archive, "skills/beta-skill/SKILL.md", "---\nname: beta-skill\ndescription: Beta skill\n---\n\n# Beta\n");
            CreateEntry(archive, "skills/beta-skill/scripts/run.sh", "#!/usr/bin/env sh\necho beta\n");
        }

        return stream.ToArray();
    }

    private static byte[] CreateBundleArchiveWithSharedRootScripts()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            CreateEntry(archive, "skills/shared/run.sh", "#!/usr/bin/env sh\necho shared\n");
            CreateEntry(archive, "skills/alpha-skill/SKILL.md", "---\nname: alpha-skill\ndescription: Alpha skill\n---\n\n# Alpha\n");
            CreateEntry(archive, "skills/beta-skill/SKILL.md", "---\nname: beta-skill\ndescription: Beta skill\n---\n\n# Beta\n");
        }

        return stream.ToArray();
    }

    private static void CreateEntry(ZipArchive archive, string path, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(path);
        using StreamWriter writer = new(entry.Open(), Encoding.UTF8, leaveOpen: false);
        writer.Write(content);
    }

    private sealed class SkillApiFixture : IAsyncDisposable
    {
        private readonly string root;
        private readonly string? previousEnvironment;
        private readonly string? previousBootstrapPassword;
        private readonly WebApplication app;

        private SkillApiFixture(string root, string? previousEnvironment, string? previousBootstrapPassword, WebApplication app)
        {
            this.root = root;
            this.previousEnvironment = previousEnvironment;
            this.previousBootstrapPassword = previousBootstrapPassword;
            this.app = app;
            Client = app.GetTestClient();
        }

        public HttpClient Client { get; }

        public string ConfineSkillPackagePath(string skillId, string requestedPath)
            => app.Services.GetRequiredService<ISkillPackageService>().ConfinePackagePath(skillId, requestedPath, existingPath: null);

        public void DeleteSkillPackage(string skillId, string packagePath)
            => app.Services.GetRequiredService<ISkillPackageService>().DeletePackage(skillId, packagePath);

        public void StageSkillPackage(SkillDefinition skill, string workspaceHostPath)
            => app.Services.GetRequiredService<ISkillPackageService>().StagePackageForWorkspace(skill, workspaceHostPath, "/workspace");

        public (string Scope, string? OwnerUserId) ReadStoredSkillOwnership(string skillId)
        {
            SkillDefinition skill = app.Services.GetRequiredService<IPlatformConfigurationProvider>()
                .GetCurrent()
                .Skills
                .Single(item => string.Equals(item.SkillId, skillId, StringComparison.Ordinal));
            return (skill.Scope, skill.OwnerUserId);
        }

        public static async Task<SkillApiFixture> CreateAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "kautuka-skill-api-tests", Guid.NewGuid().ToString("N"));
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
            builder.Services.Configure<HostOptions>(o => o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

            WebApplication app = builder.Build();
            var database = app.Services.GetRequiredService<KautukaDatabase>();
            GatewayAppBootstrap.ConfigureApp(app, database);
            await app.StartAsync();

            return new SkillApiFixture(root, previousEnvironment, previousBootstrapPassword, app);
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

            return json.RootElement.GetProperty("accessToken").GetString()
                ?? throw new XunitException("Login response did not include an access token.");
        }

        public async Task<string> LoginAsAsync(string username, string password)
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

        public async Task<JsonDocument> ValidateAsync(byte[] archiveBytes, string accessToken, string skillId)
            => await SendMultipartAsync("/api/skills/validate", archiveBytes, accessToken, skillId);

        public async Task<JsonDocument> ImportAsync(byte[] archiveBytes, string accessToken, string skillId)
            => await SendMultipartAsync("/api/skills/import", archiveBytes, accessToken, skillId);

        public async Task<JsonDocument> ValidateExpectingErrorAsync(byte[] archiveBytes, string accessToken, string skillId, System.Net.HttpStatusCode expectedStatusCode)
            => await SendMultipartAsync("/api/skills/validate", archiveBytes, accessToken, skillId, expectedStatusCode);

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

        public async Task<JsonDocument> SendMultipartAsync(string path, byte[] archiveBytes, string accessToken, string skillId, System.Net.HttpStatusCode expectedStatusCode = System.Net.HttpStatusCode.OK)
        {
            using MultipartFormDataContent content = new();
            var fileContent = new ByteArrayContent(archiveBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            content.Add(fileContent, "file", "skill.zip");
            content.Add(new StringContent(skillId), "skillId");

            using HttpRequestMessage request = new(HttpMethod.Post, path)
            {
                Content = content,
            };

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using HttpResponseMessage response = await Client.SendAsync(request);
            string responseContent = await response.Content.ReadAsStringAsync();
            if (response.StatusCode != expectedStatusCode)
            {
                throw new XunitException($"Request to '{path}' returned {(int)response.StatusCode} but expected {(int)expectedStatusCode}: {responseContent}");
            }

            return JsonDocument.Parse(responseContent);
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
    }
}
