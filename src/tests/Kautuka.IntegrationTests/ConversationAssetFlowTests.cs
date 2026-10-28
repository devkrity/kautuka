using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Assets;
using Kautuka.Gateway.Domain.Auth;
using Kautuka.Gateway.Domain.Autopilot;
using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Sandbox;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Infrastructure.Channels;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Xunit.Sdk;

namespace Kautuka.IntegrationTests;

public sealed class ConversationAssetFlowTests
{
    [Fact]
    public void ProcessToolResultExtractsGeneratedArtifactsAndScrubsInlineContent()
    {
        var rawResult = new JsonObject
        {
            ["summary"] = "Wrote report",
            ["artifacts"] = new JsonArray
            {
                new JsonObject
                {
                    ["fileName"] = "report.md",
                    ["contentType"] = "text/markdown",
                    ["text"] = "# Report\n\nGenerated from the tool.",
                },
            },
        };

        ChatToolResultProcessor.ProcessedToolResult processed = ChatToolResultProcessor.ProcessToolResult(rawResult, "write_report");

        ChatToolResultProcessor.PendingConversationAsset asset = Assert.Single(processed.Assets);
        Assert.Equal("report.md", asset.FileName);
        Assert.Equal("text/markdown", asset.ContentType);
        Assert.Equal("download", asset.Disposition);
        Assert.Equal("# Report\n\nGenerated from the tool.", Encoding.UTF8.GetString(asset.ContentBytes));
        Assert.NotNull(processed.ResultJson);
        Assert.Contains("generatedAsset", processed.ResultJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Generated from the tool.", processed.ResultJson, StringComparison.Ordinal);
    }

    [Fact]
    public void ProcessToolResultDoesNotReadAFileOutsideTheWorkspace()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-tool-root-" + Guid.NewGuid().ToString("N"));
        string outside = Path.Combine(Path.GetTempPath(), "kautuka-tool-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "inside.txt"), "inside-marker");
        File.WriteAllText(outside, "synthetic-tool-escape");
        try
        {
            var escaped = new JsonObject
            {
                ["artifacts"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["fileName"] = "secret.txt",
                        ["path"] = outside,
                    },
                },
            };
            ChatToolResultProcessor.ProcessedToolResult blocked = ChatToolResultProcessor.ProcessToolResult(escaped, "write_report", root);
            Assert.Empty(blocked.Assets);
            Assert.DoesNotContain("synthetic-tool-escape", blocked.ResultJson, StringComparison.Ordinal);

            var sibling = new JsonObject
            {
                ["artifacts"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["fileName"] = "secret.txt",
                        ["path"] = Path.Combine(root + "-other", "secret.txt"),
                    },
                },
            };
            ChatToolResultProcessor.ProcessedToolResult prefix = ChatToolResultProcessor.ProcessToolResult(sibling, "write_report", root);
            Assert.Empty(prefix.Assets);

            var allowed = new JsonObject
            {
                ["artifacts"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["path"] = "inside.txt",
                    },
                },
            };
            ChatToolResultProcessor.ProcessedToolResult kept = ChatToolResultProcessor.ProcessToolResult(allowed, "write_report", root);
            ChatToolResultProcessor.PendingConversationAsset asset = Assert.Single(kept.Assets);
            Assert.Equal("inside-marker", Encoding.UTF8.GetString(asset.ContentBytes));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task WorkspaceReadRejectsHardLinkAndDeviceNameInsideTheRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-read-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string inside = Path.Combine(root, "inside.txt");
        await File.WriteAllTextAsync(inside, "inside-marker", TestContext.Current.CancellationToken);
        string outside = Path.Combine(Path.GetTempPath(), "kautuka-read-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(outside, new string('x', 37), TestContext.Current.CancellationToken);
        string link = Path.Combine(root, "alias.txt");
        using System.Diagnostics.Process process = FileSystemLinkFixture.Start(link, outside, directoryLink: false) ?? throw new XunitException("Could not start mklink.");
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        string linkOutput = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        string linkError = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, linkOutput + linkError);
        try
        {
            Assert.True(Kautuka.Gateway.Domain.AgentFiles.WorkspaceFile.TryReadUnderRoot(root, "inside.txt", 1024, out byte[]? kept, out _));
            Assert.Equal("inside-marker", Encoding.UTF8.GetString(kept!));
            Assert.Throws<IOException>(() => Kautuka.Gateway.Domain.AgentFiles.WorkspaceFile.OpenWrite(link, FileMode.Create));
            Assert.Equal(37, new FileInfo(outside).Length);
            Assert.False(Kautuka.Gateway.Domain.AgentFiles.WorkspaceFile.TryReadUnderRoot(root, "alias.txt", 1024, out byte[]? leaked, out _));
            Assert.Null(leaked);
            Assert.Equal(!OperatingSystem.IsWindows(), Kautuka.Gateway.Domain.AgentFiles.WorkspaceFile.TryResolveUnderRoot(root, "NUL.txt", out _));
        }
        finally
        {
            if (File.Exists(link))
            {
                File.Delete(link);
            }

            Directory.Delete(root, recursive: true);
            File.Delete(outside);
        }
    }

    [Fact]
    public void LinuxStatxMatchesTheKernelLinkCountLayout()
    {
        Assert.Equal(16, Marshal.OffsetOf<Kautuka.Gateway.Domain.AgentFiles.WorkspaceFile.LinuxStatx>(nameof(Kautuka.Gateway.Domain.AgentFiles.WorkspaceFile.LinuxStatx.NumLinks)));
        Assert.Equal(256, Marshal.SizeOf<Kautuka.Gateway.Domain.AgentFiles.WorkspaceFile.LinuxStatx>());
    }

    [Fact]
    public async Task AssetEndpointsUploadAndDownloadConversationAsset()
    {
        await using var fixture = await AssetApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        string sessionId = fixture.CreateSession().Id;
        string fileName = $"note-{Guid.NewGuid():N}.txt";

        using MultipartFormDataContent upload = new();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("hello from upload"));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        upload.Add(fileContent, "file", fileName);
        upload.Add(new StringContent("userUpload"), "origin");
        upload.Add(new StringContent("download"), "disposition");

        using JsonDocument created = await fixture.SendMultipartAsync($"/api/sessions/{sessionId}/assets", upload, accessToken);
        string assetId = created.RootElement.GetProperty("id").GetString()
            ?? throw new XunitException("Upload response did not contain an asset id.");
        Assert.Equal(fileName, created.RootElement.GetProperty("fileName").GetString());
        Assert.Equal("download", created.RootElement.GetProperty("disposition").GetString());

        using HttpRequestMessage request = new(HttpMethod.Get, $"/api/sessions/{sessionId}/assets/{assetId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using HttpResponseMessage response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
        string payload = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode, payload);
        Assert.Equal("hello from upload", payload);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.True(fixture.WorkspaceEntryExists("asset-test-agent", $"attachments/{fileName}"));

        using HttpRequestMessage listRequest = new(HttpMethod.Get, $"/api/sessions/{sessionId}/assets?ids={assetId}");
        listRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using HttpResponseMessage listResponse = await fixture.Client.SendAsync(listRequest, TestContext.Current.CancellationToken);
        using JsonDocument listed = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(listResponse.IsSuccessStatusCode, listed.RootElement.GetRawText());
        Assert.Equal(assetId, listed.RootElement.GetProperty("assets")[0].GetProperty("id").GetString());
        Assert.Equal(fileName, listed.RootElement.GetProperty("assets")[0].GetProperty("fileName").GetString());
    }

    [Fact]
    public async Task DeletingSessionRemovesReferencesButKeepsWorkspaceFile()
    {
        await using var fixture = await AssetApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        GatewaySession session = fixture.CreateSession();
        string fileName = $"note-{Guid.NewGuid():N}.txt";

        using MultipartFormDataContent upload = new();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("keep workspace file"));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        upload.Add(fileContent, "file", fileName);

        _ = await fixture.SendMultipartAsync($"/api/sessions/{session.Id}/assets", upload, accessToken);
        Assert.True(fixture.WorkspaceEntryExists("asset-test-agent", $"attachments/{fileName}"));

        IGatewaySessionStore sessionStore = fixture.GetRequiredService<IGatewaySessionStore>();
        Assert.True(sessionStore.DeleteSession(session.Id));
        Assert.True(fixture.WorkspaceEntryExists("asset-test-agent", $"attachments/{fileName}"));

        using HttpRequestMessage request = new(HttpMethod.Get, "/api/sessions/" + session.Id + "/assets/asset-missing");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using HttpResponseMessage response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AssetUploadRejectsUnsupportedMimeType()
    {
        await using var fixture = await AssetApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        string sessionId = fixture.CreateSession().Id;

        using MultipartFormDataContent upload = new();
        var fileContent = new ByteArrayContent([1, 2, 3]);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/x-msdownload");
        upload.Add(fileContent, "file", "danger.exe");

        using HttpResponseMessage response = await fixture.SendMultipartExpectingStatusAsync($"/api/sessions/{sessionId}/assets", upload, accessToken, System.Net.HttpStatusCode.BadRequest);
        string payload = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("Executable or installable files are blocked", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AssetUploadRejectsAgentOutputOrigin()
    {
        await using var fixture = await AssetApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        string sessionId = fixture.CreateSession().Id;
        string fileName = $"danger-{Guid.NewGuid():N}.exe";

        using MultipartFormDataContent upload = new();
        var fileContent = new ByteArrayContent([1, 2, 3]);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        upload.Add(fileContent, "file", fileName);
        upload.Add(new StringContent("agentOutput"), "origin");

        using HttpResponseMessage response = await fixture.SendMultipartExpectingStatusAsync($"/api/sessions/{sessionId}/assets", upload, accessToken, System.Net.HttpStatusCode.BadRequest);
        string payload = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("cannot use origin 'agentOutput'", payload, StringComparison.Ordinal);
        Assert.False(fixture.WorkspaceEntryExists("asset-test-agent", $"attachments/{fileName}"));
    }

    [Fact]
    public async Task AssetUploadAllowsOfficeDocumentsAndInfersMimeTypeFromExtension()
    {
        await using var fixture = await AssetApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        string sessionId = fixture.CreateSession().Id;
        string fileName = $"brief-{Guid.NewGuid():N}.docx";

        using MultipartFormDataContent upload = new();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("docx-bytes"));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        upload.Add(fileContent, "file", fileName);
        upload.Add(new StringContent("userUpload"), "origin");

        using JsonDocument created = await fixture.SendMultipartAsync($"/api/sessions/{sessionId}/assets", upload, accessToken);

        Assert.Equal(fileName, created.RootElement.GetProperty("fileName").GetString());
        Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document", created.RootElement.GetProperty("contentType").GetString());
    }

    [Fact]
    public async Task AssetDownloadRejectsCrossSessionAccess()
    {
        await using var fixture = await AssetApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        string sourceSessionId = fixture.CreateSession().Id;
        string otherSessionId = fixture.CreateSession().Id;

        using MultipartFormDataContent upload = new();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("session scoped"));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        upload.Add(fileContent, "file", "scoped.txt");

        using JsonDocument created = await fixture.SendMultipartAsync($"/api/sessions/{sourceSessionId}/assets", upload, accessToken);
        string assetId = created.RootElement.GetProperty("id").GetString()
            ?? throw new XunitException("Upload response did not contain an asset id.");

        using HttpRequestMessage request = new(HttpMethod.Get, $"/api/sessions/{otherSessionId}/assets/{assetId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using HttpResponseMessage response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AssetDownloadReturnsNotFoundWhenWorkspaceFileIsMissingWithoutLeakingHostPaths()
    {
        await using var fixture = await AssetApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        string sessionId = fixture.CreateSession().Id;
        string fileName = $"missing-{Guid.NewGuid():N}.txt";

        using MultipartFormDataContent upload = new();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("temporary attachment"));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        upload.Add(fileContent, "file", fileName);

        using JsonDocument created = await fixture.SendMultipartAsync($"/api/sessions/{sessionId}/assets", upload, accessToken);
        string assetId = created.RootElement.GetProperty("id").GetString()
            ?? throw new XunitException("Upload response did not contain an asset id.");
        string fullAttachmentPath = fixture.GetWorkspaceFullPath("asset-test-agent", $"attachments/{fileName}");

        fixture.DeleteWorkspaceEntry("asset-test-agent", $"attachments/{fileName}");

        using HttpRequestMessage request = new(HttpMethod.Get, $"/api/sessions/{sessionId}/assets/{assetId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using HttpResponseMessage response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
        string payload = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains($"Asset '{assetId}' not found.", payload, StringComparison.Ordinal);
        Assert.DoesNotContain(fullAttachmentPath, payload, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.WorkspaceHostPath("asset-test-agent"), payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AssetDownloadRejectsHardLinkThatLeavesTheWorkspace()
    {
        await using var fixture = await AssetApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        string sessionId = fixture.CreateSession().Id;
        string fileName = $"linked-{Guid.NewGuid():N}.txt";

        using MultipartFormDataContent upload = new();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("ordinary attachment"));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        upload.Add(fileContent, "file", fileName);

        using JsonDocument created = await fixture.SendMultipartAsync($"/api/sessions/{sessionId}/assets", upload, accessToken);
        string assetId = created.RootElement.GetProperty("id").GetString()
            ?? throw new XunitException("Upload response did not contain an asset id.");
        string attachmentPath = fixture.GetWorkspaceFullPath("asset-test-agent", $"attachments/{fileName}");
        string outsideFile = Path.Combine(Path.GetTempPath(), "kautuka-asset-hardlink-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(outsideFile, "synthetic-asset-escape", TestContext.Current.CancellationToken);
        fixture.DeleteWorkspaceEntry("asset-test-agent", $"attachments/{fileName}");

        using System.Diagnostics.Process process = FileSystemLinkFixture.Start(attachmentPath, outsideFile, directoryLink: false) ?? throw new XunitException("Could not start mklink.");
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        string linkOutput = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        string linkError = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, linkOutput + linkError);

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, $"/api/sessions/{sessionId}/assets/{assetId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using HttpResponseMessage response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
            string payload = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
            Assert.DoesNotContain("synthetic-asset-escape", payload, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(attachmentPath))
            {
                File.Delete(attachmentPath);
            }

            File.Delete(outsideFile);
        }
    }

    [Fact]
    public async Task AssetDownloadRejectsAnAttachmentsDirectoryJunction()
    {
        await using var fixture = await AssetApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        string sessionId = fixture.CreateSession().Id;
        string fileName = $"joined-{Guid.NewGuid():N}.txt";

        using MultipartFormDataContent upload = new();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("ordinary attachment"));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        upload.Add(fileContent, "file", fileName);

        using JsonDocument created = await fixture.SendMultipartAsync($"/api/sessions/{sessionId}/assets", upload, accessToken);
        string assetId = created.RootElement.GetProperty("id").GetString()
            ?? throw new XunitException("Upload response did not contain an asset id.");
        string attachmentPath = fixture.GetWorkspaceFullPath("asset-test-agent", $"attachments/{fileName}");
        string attachmentsDirectory = Path.GetDirectoryName(attachmentPath) ?? throw new XunitException("Attachment path has no directory.");
        string parkedDirectory = attachmentsDirectory + "-parked";
        Directory.Move(attachmentsDirectory, parkedDirectory);
        string outsideDirectory = Path.Combine(Path.GetTempPath(), "kautuka-asset-junction-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDirectory);
        await File.WriteAllTextAsync(Path.Combine(outsideDirectory, fileName), "synthetic-junction-escape", TestContext.Current.CancellationToken);

        using System.Diagnostics.Process process = FileSystemLinkFixture.Start(attachmentsDirectory, outsideDirectory, directoryLink: true) ?? throw new XunitException("Could not start mklink.");
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        string linkOutput = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        string linkError = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, linkOutput + linkError);

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, $"/api/sessions/{sessionId}/assets/{assetId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using HttpResponseMessage response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
            string payload = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
            Assert.DoesNotContain("synthetic-junction-escape", payload, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(attachmentsDirectory))
            {
                Directory.Delete(attachmentsDirectory);
            }

            if (Directory.Exists(parkedDirectory))
            {
                Directory.Move(parkedDirectory, attachmentsDirectory);
            }

            Directory.Delete(outsideDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task AssetDownloadRequiresSessionAccessWhenTheOwnerIsMissing()
    {
        await using var fixture = await AssetApiFixture.CreateAsync();
        string adminToken = await fixture.LoginAsBootstrapAdminAsync();
        string sessionId = fixture.CreateSession().Id;
        const string secret = "synthetic-unowned-attachment";

        using MultipartFormDataContent upload = new();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(secret));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        upload.Add(fileContent, "file", $"unowned-{Guid.NewGuid():N}.txt");
        using JsonDocument created = await fixture.SendMultipartAsync($"/api/sessions/{sessionId}/assets", upload, adminToken);
        string assetId = created.RootElement.GetProperty("id").GetString()
            ?? throw new XunitException("Upload response did not contain an asset id.");

        KautukaDatabase database = fixture.GetRequiredService<KautukaDatabase>();
        using (var connection = new SqliteConnection(database.ConnectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE conversation_assets SET owner_user_id = NULL WHERE id = $id;";
            command.Parameters.AddWithValue("$id", assetId);
            Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        }

        using JsonDocument invited = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/users/invite",
            new { username = "asset-reader", email = "asset-reader@example.com", password = "Testing1", role = "user" },
            adminToken);
        using JsonDocument memberLogin = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/auth/login",
            new { username = "asset-reader", password = "Testing1" },
            accessToken: null,
            authenticated: false);
        string memberToken = memberLogin.RootElement.GetProperty("accessToken").GetString()
            ?? throw new XunitException("Member login did not include an access token.");

        using HttpRequestMessage denied = new(HttpMethod.Get, $"/api/sessions/{sessionId}/assets/{assetId}");
        denied.Headers.Authorization = new AuthenticationHeaderValue("Bearer", memberToken);
        using HttpResponseMessage deniedResponse = await fixture.Client.SendAsync(denied, TestContext.Current.CancellationToken);
        string deniedBody = await deniedResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, deniedResponse.StatusCode);
        Assert.DoesNotContain(secret, deniedBody, StringComparison.Ordinal);

        using HttpRequestMessage allowed = new(HttpMethod.Get, $"/api/sessions/{sessionId}/assets/{assetId}");
        allowed.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using HttpResponseMessage allowedResponse = await fixture.Client.SendAsync(allowed, TestContext.Current.CancellationToken);
        string allowedBody = await allowedResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.OK, allowedResponse.StatusCode);
        Assert.Contains(secret, allowedBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AssetDownloadAndParsingReuseTheSingleWorkspaceAttachmentFile()
    {
        await using var fixture = await AssetApiFixture.CreateAsync(new EchoDocumentParsingClient());
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        GatewaySession session = fixture.CreateSession();
        string fileName = $"shared-{Guid.NewGuid():N}.docx";

        using MultipartFormDataContent upload = new();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("original workspace-backed document"));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        upload.Add(fileContent, "file", fileName);
        upload.Add(new StringContent("userUpload"), "origin");

        using JsonDocument created = await fixture.SendMultipartAsync($"/api/sessions/{session.Id}/assets", upload, accessToken);
        string assetId = created.RootElement.GetProperty("id").GetString()
            ?? throw new XunitException("Upload response did not contain an asset id.");

        IConversationAssetStore assetStore = fixture.GetRequiredService<IConversationAssetStore>();
        WorkspaceAttachmentReferenceSummary reference = Assert.Single(assetStore.ListRetainedWorkspaceAttachmentReferences("asset-test-agent"));
        Assert.Equal($"attachments/{fileName}", reference.RelativePath);
        Assert.Equal(1, fixture.CountWorkspaceFilesByName("asset-test-agent", "attachments", fileName));

        fixture.WriteWorkspaceFile("asset-test-agent", reference.RelativePath, "updated shared workspace document");

        using (HttpRequestMessage request = new(HttpMethod.Get, $"/api/sessions/{session.Id}/assets/{assetId}"))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using HttpResponseMessage response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
            string payload = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            Assert.True(response.IsSuccessStatusCode, payload);
            Assert.Equal("updated shared workspace document", payload);
        }

        Assert.True(assetStore.TryOpenAsset(session.Id, assetId, fixture.CurrentUserId, out AssetDownloadResult? assetDownload));
        await using Stream content = assetDownload!.Content;

        IConversationAssetIngestionService ingestionService = fixture.GetRequiredService<IConversationAssetIngestionService>();
        ParsedAsset parsed = await ingestionService.IngestParseRequiredAsync(
            session.Id,
            assetId,
            fileName,
            content,
            TestContext.Current.CancellationToken);

        Assert.True(parsed.IsSuccessful);
        Assert.Contains("updated shared workspace document", parsed.NormalizedMarkdown, StringComparison.Ordinal);
        Assert.Equal(1, fixture.CountWorkspaceFilesByName("asset-test-agent", "attachments", fileName));
    }

    [Fact]
    public async Task ParsedAssetStatusEndpointReturnsNotFoundWhenSessionMissing()
    {
        await using var fixture = await AssetApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();

        using HttpRequestMessage request = new(HttpMethod.Get, "/api/sessions/missing-session/assets/asset-1/parsed");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using HttpResponseMessage response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AssetStoreReportsRetainedAndUnreferencedWorkspaceAttachments()
    {
        await using var fixture = await AssetApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        GatewaySession session = fixture.CreateSession();
        string retainedFileName = $"retained-{Guid.NewGuid():N}.txt";
        string unreferencedFileName = $"orphaned-{Guid.NewGuid():N}.txt";

        using (MultipartFormDataContent upload = new())
        {
            var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("retained workspace file"));
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            upload.Add(fileContent, "file", retainedFileName);

            _ = await fixture.SendMultipartAsync($"/api/sessions/{session.Id}/assets", upload, accessToken);
        }

        fixture.WriteWorkspaceFile("asset-test-agent", $"attachments/{unreferencedFileName}", "orphaned workspace file");

        IConversationAssetStore assetStore = fixture.GetRequiredService<IConversationAssetStore>();
        WorkspaceAttachmentReferenceSummary retained = Assert.Single(assetStore.ListRetainedWorkspaceAttachmentReferences("asset-test-agent"));
        Assert.Equal($"attachments/{retainedFileName}", retained.RelativePath);
        Assert.Equal(retainedFileName, retained.FileName);
        Assert.Equal(1, retained.ReferenceCount);

        IReadOnlyList<WorkspaceAttachmentFileSummary> unreferencedFiles = assetStore.ListUnreferencedWorkspaceAttachmentFiles("asset-test-agent");
        WorkspaceAttachmentFileSummary unreferenced = Assert.Single(
            unreferencedFiles,
            item => string.Equals(item.RelativePath, $"attachments/{unreferencedFileName}", StringComparison.Ordinal));
        Assert.Equal(unreferencedFileName, unreferenced.FileName);
        Assert.True(unreferenced.SizeBytes > 0);
        Assert.DoesNotContain(
            unreferencedFiles,
            item => string.Equals(item.RelativePath, $"attachments/{retainedFileName}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AssetRecoveryDeletesOnlyDurablyStagedInterruptedUpload()
    {
        await using var fixture = await AssetApiFixture.CreateAsync();
        _ = await fixture.LoginAsBootstrapAdminAsync();
        GatewaySession session = fixture.CreateSession();
        const string assetId = "asset-interrupted-upload";
        const string fileName = "interrupted-upload.txt";
        string storagePath = $"attachments/{fileName}";
        fixture.WriteWorkspaceFile("asset-test-agent", storagePath, "incomplete upload");
        fixture.WriteWorkspaceFile("asset-test-agent", "attachments/legitimate-output.txt", "agent output");

        var database = fixture.GetRequiredService<KautukaDatabase>();
        using (var connection = new SqliteConnection(database.ConnectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            using var insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO conversation_asset_upload_staging(asset_id, session_id, storage_path, created_at) "
                + "VALUES ($assetId, $sessionId, $storagePath, $createdAt);";
            insert.Parameters.AddWithValue("$assetId", assetId);
            insert.Parameters.AddWithValue("$sessionId", session.Id);
            insert.Parameters.AddWithValue("$storagePath", storagePath);
            insert.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        ConversationAssetStore store = fixture.GetRequiredService<ConversationAssetStore>();
        await store.ReconcileStagedUploadsAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(fixture.WorkspaceEntryExists("asset-test-agent", storagePath));
        Assert.True(fixture.WorkspaceEntryExists("asset-test-agent", "attachments/legitimate-output.txt"));
        using var verify = new SqliteConnection(database.ConnectionString);
        await verify.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using var count = verify.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM conversation_asset_upload_staging WHERE asset_id = $assetId;";
        count.Parameters.AddWithValue("$assetId", assetId);
        Assert.Equal(0L, Assert.IsType<long>(await count.ExecuteScalarAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)));
    }

    [Fact]
    public async Task ParsedAssetStatusEndpointRejectsUnauthorizedRequests()
    {
        await using var fixture = await AssetApiFixture.CreateAsync();

        using HttpRequestMessage request = new(HttpMethod.Get, "/api/sessions/session-1/assets/asset-1/parsed");
        using HttpResponseMessage response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ParsedAssetStatusEndpointReturnsNotFoundWhenParsedAssetMissing()
    {
        await using var fixture = await AssetApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        string sessionId = fixture.CreateSession().Id;

        using HttpRequestMessage request = new(HttpMethod.Get, $"/api/sessions/{sessionId}/assets/missing-asset/parsed");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using HttpResponseMessage response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ParsedAssetStatusEndpointReturnsParsedAssetPayload()
    {
        await using var fixture = await AssetApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        string sessionId = fixture.CreateSession().Id;
        const string assetId = "asset-parsed-1";

        IParsedAssetStore parsedAssetStore = fixture.GetRequiredService<IParsedAssetStore>();
        await parsedAssetStore.StoreAsync(
            new ParsedAssetCreationCommand(
                AssetId: assetId,
                SessionId: sessionId,
                Status: ParsedAssetStatus.Success,
                ParserName: "internal-mcp-parser",
                ParserVersion: "1.0.0",
                NormalizedMarkdown: "# Parsed",
                MetadataJson: "{}",
                Warning: null,
                Error: null),
            TestContext.Current.CancellationToken);

        using HttpRequestMessage request = new(HttpMethod.Get, $"/api/sessions/{sessionId}/assets/{assetId}/parsed");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using HttpResponseMessage response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
        string content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode, content);
        using JsonDocument json = JsonDocument.Parse(content);
        Assert.Equal(assetId, json.RootElement.GetProperty("assetId").GetString());
        Assert.Equal("success", json.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ParsedAssetStatusesEndpointReturnsSessionAssets()
    {
        await using var fixture = await AssetApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        string sessionId = fixture.CreateSession().Id;

        IParsedAssetStore parsedAssetStore = fixture.GetRequiredService<IParsedAssetStore>();
        await parsedAssetStore.StoreAsync(
            new ParsedAssetCreationCommand(
                AssetId: "asset-pending-1",
                SessionId: sessionId,
                Status: ParsedAssetStatus.Pending,
                ParserName: "internal-mcp-parser",
                ParserVersion: "1.0.0",
                NormalizedMarkdown: null,
                MetadataJson: null,
                Warning: null,
                Error: null),
            TestContext.Current.CancellationToken);

        using HttpRequestMessage request = new(HttpMethod.Get, $"/api/sessions/{sessionId}/assets/parsed");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using HttpResponseMessage response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
        string content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode, content);
        using JsonDocument json = JsonDocument.Parse(content);
        JsonElement assets = json.RootElement.GetProperty("assets");
        Assert.Equal(JsonValueKind.Array, assets.ValueKind);
        Assert.NotEmpty(assets.EnumerateArray());
    }

    private sealed class AssetApiFixture : IAsyncDisposable
    {
        private readonly string root;
        private readonly string? previousEnvironment;
        private readonly string? previousBootstrapPassword;
        private readonly WebApplication app;
        private string? currentUserId;

        private AssetApiFixture(
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

        public string CurrentUserId => currentUserId ?? throw new XunitException("No authenticated user is currently set on the fixture.");

        public static async Task<AssetApiFixture> CreateAsync(IDocumentParsingClient? parsingClient = null)
        {
            string root = Path.Combine(Path.GetTempPath(), "kautuka-asset-api-tests", Guid.NewGuid().ToString("N"));
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
                ["Kautuka:Workspace:BaseRoot"] = Path.Combine(root, "workspaces"),
            });

            builder.WebHost.UseTestServer();
            GatewayAppBootstrap.ConfigureServices(builder);
            RemoveHostedService<WorkspaceDesiredStateStartupService>(builder.Services);
            RemoveHostedService<ChannelHostOutboxWorker>(builder.Services);
            RemoveHostedService<ChannelHostIngressWorker>(builder.Services);
            RemoveHostedService<AutopilotSchedulerService>(builder.Services);
            ReplaceSingletonIfProvided(builder.Services, parsingClient);
            builder.Services.Configure<HostOptions>(options => options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

            WebApplication app = builder.Build();
            var database = app.Services.GetRequiredService<KautukaDatabase>();
            GatewayAppBootstrap.ConfigureApp(app, database);
            await app.StartAsync();

            return new AssetApiFixture(
                root,
                previousEnvironment,
                previousBootstrapPassword,
                app);
        }

        private static void RemoveHostedService<THostedService>(IServiceCollection services) where THostedService : class, IHostedService
        {
            ServiceDescriptor[] descriptors = services
                .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                    && descriptor.ImplementationType == typeof(THostedService))
                .ToArray();

            foreach (ServiceDescriptor descriptor in descriptors)
            {
                services.Remove(descriptor);
            }
        }

        private static void ReplaceSingletonIfProvided(IServiceCollection services, IDocumentParsingClient? parsingClient)
        {
            if (parsingClient is null)
            {
                return;
            }

            ServiceDescriptor[] descriptors = services
                .Where(static descriptor => descriptor.ServiceType == typeof(IDocumentParsingClient))
                .ToArray();

            foreach (ServiceDescriptor descriptor in descriptors)
            {
                services.Remove(descriptor);
            }

            services.AddSingleton(parsingClient);
        }

        public GatewaySession CreateSession()
        {
            string ownerUserId = currentUserId ?? "user-asset";
            IGatewayCatalogStore catalog = app.Services.GetRequiredService<IGatewayCatalogStore>();
            if (!catalog.TryGetAgent("asset-test-agent", out _))
            {
                catalog.SaveAgent(
                    "asset-test-agent",
                    new GatewayAgentUpsertRequest { Name = "Asset Test Agent" },
                    ownerUserId);
            }

            return app.Services.GetRequiredService<IGatewaySessionStore>().CreateSession(
                "agent",
                "asset-test-agent",
                ownerUserId);
        }

        public async Task<string> LoginAsBootstrapAdminAsync()
        {
            await SendJsonAsync(
                HttpMethod.Post,
                "/api/auth/setup",
                new { username = "admin", password = "Testing1", setupToken = app.Services.GetRequiredService<SetupPossession>().Peek() },
                accessToken: null,
                authenticated: false);

            using JsonDocument json = await SendJsonAsync(
                HttpMethod.Post,
                "/api/auth/login",
                new { username = "admin", password = "Testing1" },
                accessToken: null,
                authenticated: false);

            currentUserId = json.RootElement.GetProperty("user").GetProperty("id").GetString();

            return json.RootElement.GetProperty("accessToken").GetString()
                ?? throw new XunitException("Login response did not include an access token.");
        }

        public async Task<JsonDocument> SendJsonAsync(HttpMethod method, string path, object? body, string? accessToken = null, bool authenticated = true)
        {
            using HttpRequestMessage request = new(method, path);
            if (body is not null)
            {
                request.Content = System.Net.Http.Json.JsonContent.Create(body);
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

        public async Task<JsonDocument> SendMultipartAsync(string path, MultipartFormDataContent content, string accessToken)
        {
            using HttpRequestMessage request = new(HttpMethod.Post, path)
            {
                Content = content,
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using HttpResponseMessage response = await Client.SendAsync(request);
            string responseContent = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new XunitException($"Request to '{path}' failed with {(int)response.StatusCode}: {responseContent}");
            }

            return JsonDocument.Parse(responseContent);
        }

        public async Task<HttpResponseMessage> SendMultipartExpectingStatusAsync(string path, MultipartFormDataContent content, string accessToken, System.Net.HttpStatusCode expectedStatusCode)
        {
            using HttpRequestMessage request = new(HttpMethod.Post, path)
            {
                Content = content,
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            HttpResponseMessage response = await Client.SendAsync(request);
            Assert.Equal(expectedStatusCode, response.StatusCode);
            return response;
        }

        public T GetRequiredService<T>() where T : notnull
            => app.Services.GetRequiredService<T>();

        public bool WorkspaceEntryExists(string agentId, string relativePath)
        {
            string fullPath = GetWorkspaceFullPath(agentId, relativePath);
            return Directory.Exists(fullPath) || File.Exists(fullPath);
        }

        public string WorkspaceHostPath(string agentId)
        {
            IWorkspaceRuntimeTool workspaceTool = app.Services.GetRequiredService<IWorkspaceRuntimeTool>();
            WorkspaceRuntimeContext workspace = workspaceTool.DescribeAgentWorkspace(agentId, currentUserId);
            return workspace.HostWorkspacePath;
        }

        public string GetWorkspaceFullPath(string agentId, string relativePath)
            => Path.Combine(WorkspaceHostPath(agentId), relativePath.Replace('/', Path.DirectorySeparatorChar));

        public void WriteWorkspaceFile(string agentId, string relativePath, string content)
        {
            string fullPath = GetWorkspaceFullPath(agentId, relativePath);
            string parentPath = Path.GetDirectoryName(fullPath) ?? throw new XunitException("Expected workspace file path to have a parent directory.");
            Directory.CreateDirectory(parentPath);
            File.WriteAllText(fullPath, content);
        }

        public void DeleteWorkspaceEntry(string agentId, string relativePath)
        {
            string fullPath = GetWorkspaceFullPath(agentId, relativePath);
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
                return;
            }

            if (Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
            }
        }

        public int CountWorkspaceFilesByName(string agentId, string relativeDirectoryPath, string fileName)
        {
            string directoryPath = GetWorkspaceFullPath(agentId, relativeDirectoryPath);
            if (!Directory.Exists(directoryPath))
            {
                return 0;
            }

            return Directory.EnumerateFiles(directoryPath, fileName, SearchOption.AllDirectories).Count();
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

    private sealed class EchoDocumentParsingClient : IDocumentParsingClient
    {
        public async Task<DocumentParsingResult> ParseAsync(string fileName, Stream content, CancellationToken cancellationToken)
        {
            using var reader = new StreamReader(content, Encoding.UTF8, leaveOpen: true);
            string text = await reader.ReadToEndAsync(cancellationToken);
            content.Position = 0;

            return new DocumentParsingResult
            {
                Success = true,
                NormalizedMarkdown = $"Parsed: {fileName}\n{text}",
                MetadataJson = "{}",
                Warning = null,
                Error = null,
                ParserVersion = "test-parser-1.0.0",
            };
        }

        public Task<bool> HealthCheckAsync(CancellationToken cancellationToken)
            => Task.FromResult(true);
    }
}
