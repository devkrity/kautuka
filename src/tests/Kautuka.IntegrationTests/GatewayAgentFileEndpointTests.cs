using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Kautuka.Gateway.Domain.Assets;
using Kautuka.Gateway.Domain.Auth;
using Kautuka.Gateway.Domain.Autopilot;
using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Domain.Memory;
using Kautuka.Gateway.Domain.Sandbox;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Infrastructure.Channels;
using Kautuka.Telemetry.Telemetry;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Xunit.Sdk;

namespace Kautuka.IntegrationTests;

public sealed class GatewayAgentFileEndpointTests
{
    [Fact]
    public async Task ListEndpointReturnsAgentWorkspaceEntriesForAuthorizedUser()
    {
        await using var fixture = await AgentFilesApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        const string agentId = "agent-files-enabled";

        await fixture.CreateAgentAsync(agentId, sandboxEnabled: true);
        fixture.WriteWorkspaceFile(agentId, "reports/summary.txt", "hello from agent workspace");

        using HttpRequestMessage request = new(HttpMethod.Get, $"/api/agents/{agentId}/files?path=reports");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using HttpResponseMessage response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
        string content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode, content);
        using JsonDocument json = JsonDocument.Parse(content);
        JsonElement entries = json.RootElement.GetProperty("entries");
        JsonElement entry = Assert.Single(entries.EnumerateArray());
        Assert.Equal("summary.txt", entry.GetProperty("name").GetString());
        Assert.Equal("reports/summary.txt", entry.GetProperty("path").GetString());
        Assert.Equal("file", entry.GetProperty("entryType").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("totalFileCount").GetInt32());
        Assert.Equal(26, json.RootElement.GetProperty("totalSizeBytes").GetInt64());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("fileCount").ValueKind);
    }

    [Fact]
    public async Task DownloadRejectsJunctionThatLeavesTheWorkspace()
    {
        await using var fixture = await AgentFilesApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        const string agentId = "agent-files-junction";
        await fixture.CreateAgentAsync(agentId, sandboxEnabled: true);

        string outsideDirectory = Path.Combine(Path.GetTempPath(), "kautuka-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDirectory);
        await File.WriteAllTextAsync(Path.Combine(outsideDirectory, "secret.txt"), "synthetic-outside-marker", TestContext.Current.CancellationToken);
        string link = Path.Combine(fixture.WorkspacePath(agentId, null), "linked");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        using Process process = FileSystemLinkFixture.Start(link, outsideDirectory, directoryLink: true) ?? throw new XunitException("Could not start mklink.");
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        string linkOutput = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        string linkError = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, linkOutput + linkError);

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, $"/api/agents/{agentId}/files/content?path=linked/secret.txt");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using HttpResponseMessage response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
            string content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.DoesNotContain("synthetic-outside-marker", content, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }

            Directory.Delete(outsideDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadRejectsHardLinkThatLeavesTheWorkspace()
    {
        await using var fixture = await AgentFilesApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        const string agentId = "agent-files-hardlink";
        await fixture.CreateAgentAsync(agentId, sandboxEnabled: true);

        string outsideFile = Path.Combine(Path.GetTempPath(), "kautuka-hardlink-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(outsideFile, "synthetic-hardlink-marker", TestContext.Current.CancellationToken);
        string link = Path.Combine(fixture.WorkspacePath(agentId, null), "alias.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        using Process process = FileSystemLinkFixture.Start(link, outsideFile, directoryLink: false) ?? throw new XunitException("Could not start mklink.");
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        string linkOutput = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        string linkError = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, linkOutput + linkError);

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, $"/api/agents/{agentId}/files/content?path=alias.txt");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using HttpResponseMessage response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
            string content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.DoesNotContain("synthetic-hardlink-marker", content, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(link))
            {
                File.Delete(link);
            }

            File.Delete(outsideFile);
        }
    }

    [Fact]
    public async Task DownloadRejectsWindowsDeviceNames()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Device names are reserved only on Windows.");

        await using var fixture = await AgentFilesApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        const string agentId = "agent-files-device";
        await fixture.CreateAgentAsync(agentId, sandboxEnabled: true);

        using HttpRequestMessage request = new(HttpMethod.Get, $"/api/agents/{agentId}/files/content?path=NUL.txt");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using HttpResponseMessage response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
        string content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("not accessible", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SystemAgentWorkspacesAreSeparateForEachUser()
    {
        await using var fixture = await AgentFilesApiFixture.CreateAsync();
        await fixture.LoginAsBootstrapAdminAsync();
        const string agentId = "agent-files-system";
        await fixture.CreateAgentAsync(agentId, sandboxEnabled: true);
        fixture.SetAgentScope(agentId, "system");

        string first = fixture.WorkspacePath(agentId, "user-one");
        string second = fixture.WorkspacePath(agentId, "user-two");
        Assert.NotEqual(first, second);
        Assert.EndsWith(Path.Combine("agents", fixture.WorkspaceScopeName(agentId), "user-one"), first, StringComparison.Ordinal);
        Assert.EndsWith(Path.Combine("agents", fixture.WorkspaceScopeName(agentId), "user-two"), second, StringComparison.Ordinal);

        fixture.WriteWorkspaceFile(agentId, "notes.txt", "private-to-admin");
        string memberToken = await fixture.InviteAndLoginUserAsync("member-files", "Testing1", "user");
        using HttpRequestMessage list = new(HttpMethod.Get, $"/api/agents/{agentId}/files");
        list.Headers.Authorization = new AuthenticationHeaderValue("Bearer", memberToken);
        using HttpResponseMessage listed = await fixture.Client.SendAsync(list, TestContext.Current.CancellationToken);
        string listedBody = await listed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.DoesNotContain("private-to-admin", listedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("notes.txt", listedBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListEndpointReturnsRecursiveFileCountsForFolders()
    {
        await using var fixture = await AgentFilesApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        const string agentId = "agent-files-folder-counts";

        await fixture.CreateAgentAsync(agentId, sandboxEnabled: true);
        fixture.WriteWorkspaceFile(agentId, "reports/summary.txt", "a");
        fixture.WriteWorkspaceFile(agentId, "reports/archive/deeper.txt", "b");

        using HttpRequestMessage request = new(HttpMethod.Get, $"/api/agents/{agentId}/files");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using HttpResponseMessage response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
        string content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode, content);
        using JsonDocument json = JsonDocument.Parse(content);
        JsonElement reports = Assert.Single(json.RootElement.GetProperty("entries").EnumerateArray());
        Assert.Equal("reports", reports.GetProperty("name").GetString());
        Assert.Equal(2, reports.GetProperty("fileCount").GetInt32());
        Assert.Equal(2, json.RootElement.GetProperty("totalFileCount").GetInt32());
    }

    [Fact]
    public async Task ListEndpointReturnsDisabledStateWhenSandboxDisabled()
    {
        await using var fixture = await AgentFilesApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        const string agentId = "agent-files-disabled";

        await fixture.CreateAgentAsync(agentId, sandboxEnabled: false);
        fixture.WriteWorkspaceFile(agentId, "reports/summary.txt", "should not be listed");

        using HttpRequestMessage request = new(HttpMethod.Get, $"/api/agents/{agentId}/files?path=reports");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using HttpResponseMessage response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
        string content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using JsonDocument json = JsonDocument.Parse(content);
        Assert.Equal("sandbox_disabled", json.RootElement.GetProperty("reasonCode").GetString());
    }

    [Fact]
    public async Task DownloadEndpointServesHostViewFilesWhenSandboxDisabled()
    {
        await using var fixture = await AgentFilesApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        const string agentId = "agent-view-files-disabled-sandbox";

        await fixture.CreateAgentAsync(agentId, sandboxEnabled: false);
        fixture.WriteWorkspaceFile(agentId, "view-files/abc/photo.jpg", "photo-bytes");
        fixture.WriteWorkspaceFile(agentId, "reports/summary.txt", "not a view file");

        using HttpRequestMessage viewRequest = new(HttpMethod.Get, $"/api/agents/{agentId}/files/content?path=view-files/abc/photo.jpg");
        viewRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using HttpResponseMessage viewResponse = await fixture.Client.SendAsync(viewRequest, TestContext.Current.CancellationToken);
        string viewPayload = await viewResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(viewResponse.IsSuccessStatusCode, viewPayload);
        Assert.Equal("photo-bytes", viewPayload);

        using HttpRequestMessage otherRequest = new(HttpMethod.Get, $"/api/agents/{agentId}/files/content?path=reports/summary.txt");
        otherRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using HttpResponseMessage otherResponse = await fixture.Client.SendAsync(otherRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, otherResponse.StatusCode);
    }

    [Fact]
    public async Task SearchEndpointReturnsRecursiveMatchesAcrossWorkspace()
    {
        await using var fixture = await AgentFilesApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        const string agentId = "agent-files-search";

        await fixture.CreateAgentAsync(agentId, sandboxEnabled: true);
        fixture.WriteWorkspaceFile(agentId, "reports/screenshots/132343.png", "png-data");
        fixture.WriteWorkspaceFile(agentId, "reports/summary.txt", "hello");

        using HttpRequestMessage request = new(HttpMethod.Get, $"/api/agents/{agentId}/files/search?query=1323");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using HttpResponseMessage response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
        string content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode, content);
        using JsonDocument json = JsonDocument.Parse(content);
        JsonElement entry = Assert.Single(json.RootElement.GetProperty("entries").EnumerateArray());
        Assert.Equal("132343.png", entry.GetProperty("name").GetString());
        Assert.Equal("reports/screenshots/132343.png", entry.GetProperty("path").GetString());
    }

    [Fact]
    public async Task UploadAndDownloadEndpointsRoundTripWorkspaceFile()
    {
        await using var fixture = await AgentFilesApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        const string agentId = "agent-files-roundtrip";
        string fileName = $"roundtrip-{Guid.NewGuid():N}.txt";

        await fixture.CreateAgentAsync(agentId, sandboxEnabled: true);
        fixture.EnsureWorkspaceDirectory(agentId, "reports");

        using MultipartFormDataContent upload = new();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("roundtrip data"));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        upload.Add(fileContent, "file", fileName);
        upload.Add(new StringContent("reports"), "path");

        using JsonDocument created = await fixture.SendMultipartAsync($"/api/agents/{agentId}/files", upload, accessToken);
        Assert.Equal($"reports/{fileName}", created.RootElement.GetProperty("path").GetString());

        using HttpRequestMessage request = new(HttpMethod.Get, $"/api/agents/{agentId}/files/content?path=reports/{fileName}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using HttpResponseMessage response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
        string payload = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode, payload);
        Assert.Equal("roundtrip data", payload);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task UploadEndpointRejectsBlockedExecutableFiles()
    {
        await using var fixture = await AgentFilesApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        const string agentId = "agent-files-upload-blocked";

        await fixture.CreateAgentAsync(agentId, sandboxEnabled: true);
        fixture.EnsureWorkspaceDirectory(agentId, "incoming");

        using MultipartFormDataContent upload = new();
        var fileContent = new ByteArrayContent([1, 2, 3]);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/x-msdownload");
        upload.Add(fileContent, "file", "danger.exe");
        upload.Add(new StringContent("incoming"), "path");

        using HttpResponseMessage response = await fixture.SendMultipartExpectingStatusAsync(
            $"/api/agents/{agentId}/files",
            upload,
            accessToken,
            HttpStatusCode.BadRequest);
        string payload = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("Executable or installable files are blocked", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadEndpointRejectsReservedInternalFileName()
    {
        await using var fixture = await AgentFilesApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        const string agentId = "agent-files-upload-reserved-name";

        await fixture.CreateAgentAsync(agentId, sandboxEnabled: true);
        fixture.EnsureWorkspaceDirectory(agentId, "incoming");

        using MultipartFormDataContent upload = new();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("blocked"));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        upload.Add(fileContent, "file", ".kautuka");
        upload.Add(new StringContent("incoming"), "path");

        using HttpResponseMessage response = await fixture.SendMultipartExpectingStatusAsync(
            $"/api/agents/{agentId}/files",
            upload,
            accessToken,
            HttpStatusCode.BadRequest);
        string payload = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("reserved and cannot be used", payload, StringComparison.Ordinal);
        Assert.False(fixture.WorkspaceEntryExists(agentId, "incoming/.kautuka"));
    }

    [Fact]
    public void UploadNameRejectsWindowsTrailingDotOrSpace()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal("notes.txt", WorkspaceAttachmentUploadPolicy.NormalizeLeafFileName("notes.txt"));
        Assert.Throws<InvalidOperationException>(() => WorkspaceAttachmentUploadPolicy.NormalizeLeafFileName("notes.txt."));
        Assert.Throws<InvalidOperationException>(() => WorkspaceAttachmentUploadPolicy.NormalizeLeafFileName("notes.txt "));
    }

    [Fact]
    public async Task DownloadRejectsWindowsTrailingDotThatCollapsesOntoAnotherName()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var fixture = await AgentFilesApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        const string agentId = "agent-files-trailing-dot";

        await fixture.CreateAgentAsync(agentId, sandboxEnabled: true);
        fixture.WriteWorkspaceFile(agentId, ".kautuka/secret.txt", "synthetic-kautuka-secret");
        fixture.WriteWorkspaceFile(agentId, "reports/notes.txt", "synthetic-visible-note");

        using HttpResponseMessage internalAlias = await fixture.Client.SendAsync(
            AuthorizedGet($"/api/agents/{agentId}/files/content?path=.kautuka./secret.txt", accessToken),
            TestContext.Current.CancellationToken);
        string internalBody = await internalAlias.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, internalAlias.StatusCode);
        Assert.DoesNotContain("synthetic-kautuka-secret", internalBody, StringComparison.Ordinal);

        using HttpResponseMessage fileAlias = await fixture.Client.SendAsync(
            AuthorizedGet($"/api/agents/{agentId}/files/content?path=reports/notes.txt.", accessToken),
            TestContext.Current.CancellationToken);
        string aliasBody = await fileAlias.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, fileAlias.StatusCode);
        Assert.DoesNotContain("synthetic-visible-note", aliasBody, StringComparison.Ordinal);

        using HttpResponseMessage ordinary = await fixture.Client.SendAsync(
            AuthorizedGet($"/api/agents/{agentId}/files/content?path=reports/notes.txt", accessToken),
            TestContext.Current.CancellationToken);
        string ordinaryBody = await ordinary.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, ordinary.StatusCode);
        Assert.Contains("synthetic-visible-note", ordinaryBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadRejectsWindowsShortNameForTheInternalDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var fixture = await AgentFilesApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        const string agentId = "agent-files-short-name";

        await fixture.CreateAgentAsync(agentId, sandboxEnabled: true);
        fixture.WriteWorkspaceFile(agentId, ".kautuka/secret.txt", "synthetic-kautuka-short-secret");
        string? shortRelative = fixture.ShortNameRelativePath(agentId, ".kautuka/secret.txt");
        if (shortRelative is null || shortRelative.Contains(".kautuka", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        using HttpResponseMessage response = await fixture.Client.SendAsync(
            AuthorizedGet($"/api/agents/{agentId}/files/content?path={Uri.EscapeDataString(shortRelative)}", accessToken),
            TestContext.Current.CancellationToken);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("synthetic-kautuka-short-secret", body, StringComparison.Ordinal);
    }

    private static HttpRequestMessage AuthorizedGet(string path, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    [Fact]
    public async Task CreateFolderEndpointCreatesDirectoryInsideAuthorizedWorkspace()
    {
        await using var fixture = await AgentFilesApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        const string agentId = "agent-files-create-folder";
        string folderName = $"generated-{Guid.NewGuid():N}";

        await fixture.CreateAgentAsync(agentId, sandboxEnabled: true);
        fixture.EnsureWorkspaceDirectory(agentId, "reports");

        using JsonDocument created = await fixture.SendAuthorizedJsonAsync(
            HttpMethod.Post,
            $"/api/agents/{agentId}/files/folders",
            new { path = "reports", name = folderName },
            accessToken);

        Assert.Equal($"reports/{folderName}", created.RootElement.GetProperty("path").GetString());
        Assert.True(fixture.WorkspaceEntryExists(agentId, $"reports/{folderName}"));
    }

    [Fact]
    public async Task CreateFolderEndpointRejectsReservedInternalFolderName()
    {
        await using var fixture = await AgentFilesApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        const string agentId = "agent-files-folder-reserved-name";

        await fixture.CreateAgentAsync(agentId, sandboxEnabled: true);
        fixture.EnsureWorkspaceDirectory(agentId, "reports");

        using HttpResponseMessage response = await fixture.SendAuthorizedJsonExpectingStatusAsync(
            HttpMethod.Post,
            $"/api/agents/{agentId}/files/folders",
            new { path = "reports", name = ".kautuka" },
            accessToken,
            HttpStatusCode.BadRequest);
        string payload = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("reserved and cannot be used", payload, StringComparison.Ordinal);
        Assert.False(fixture.WorkspaceEntryExists(agentId, "reports/.kautuka"));
    }

    [Fact]
    public async Task CreateFolderEndpointCreatesNestedDirectoriesInsideAuthorizedWorkspace()
    {
        await using var fixture = await AgentFilesApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        const string agentId = "agent-files-folder-nested-create";
        string nestedFolderPath = $"generated-{Guid.NewGuid():N}/child-{Guid.NewGuid():N}";

        await fixture.CreateAgentAsync(agentId, sandboxEnabled: true);
        fixture.EnsureWorkspaceDirectory(agentId, "reports");

        using JsonDocument created = await fixture.SendAuthorizedJsonAsync(
            HttpMethod.Post,
            $"/api/agents/{agentId}/files/folders",
            new { path = "reports", name = nestedFolderPath },
            accessToken);

        string[] nestedSegments = nestedFolderPath.Split('/');
        Assert.Equal($"reports/{nestedFolderPath}", created.RootElement.GetProperty("path").GetString());
        Assert.Equal(nestedSegments[^1], created.RootElement.GetProperty("name").GetString());
        Assert.True(fixture.WorkspaceEntryExists(agentId, $"reports/{nestedSegments[0]}"));
        Assert.True(fixture.WorkspaceEntryExists(agentId, $"reports/{nestedFolderPath}"));
    }

    [Fact]
    public async Task CreateFolderEndpointSanitizesNestedCreationFailuresWithoutLeakingHostPaths()
    {
        await using var fixture = await AgentFilesApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        const string agentId = "agent-files-folder-nested-blocked";
        string blockingFolder = $"generated-{Guid.NewGuid():N}";

        await fixture.CreateAgentAsync(agentId, sandboxEnabled: true);
        fixture.EnsureWorkspaceDirectory(agentId, "reports");
        fixture.WriteWorkspaceFile(agentId, $"reports/{blockingFolder}", "blocking file");

        using HttpResponseMessage response = await fixture.SendAuthorizedJsonExpectingStatusAsync(
            HttpMethod.Post,
            $"/api/agents/{agentId}/files/folders",
            new { path = "reports", name = $"{blockingFolder}/test" },
            accessToken,
            HttpStatusCode.BadRequest);
        string payload = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("Folder could not be created in the selected location", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("/.kautuka/", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/workspaces/agents/", payload, StringComparison.OrdinalIgnoreCase);
        Assert.False(fixture.WorkspaceEntryExists(agentId, $"reports/{blockingFolder}/test"));
    }

    [Fact]
    public async Task UploadEndpointOverwritesExistingFileWhenRequested()
    {
        await using var fixture = await AgentFilesApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        const string agentId = "agent-files-overwrite";
        string fileName = $"overwrite-{Guid.NewGuid():N}.txt";

        await fixture.CreateAgentAsync(agentId, sandboxEnabled: true);
        fixture.WriteWorkspaceFile(agentId, $"reports/{fileName}", "old content");

        using MultipartFormDataContent upload = new();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("replacement content"));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        upload.Add(fileContent, "file", fileName);
        upload.Add(new StringContent("reports"), "path");
        upload.Add(new StringContent("true"), "overwrite");

        using JsonDocument created = await fixture.SendMultipartAsync($"/api/agents/{agentId}/files", upload, accessToken);
        Assert.Equal($"reports/{fileName}", created.RootElement.GetProperty("path").GetString());
        Assert.Equal("replacement content", fixture.ReadWorkspaceFile(agentId, $"reports/{fileName}"));
    }

    [Fact]
    public async Task DeleteEndpointRemovesFileInsideAuthorizedWorkspace()
    {
        await using var fixture = await AgentFilesApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        const string agentId = "agent-files-delete-file";

        await fixture.CreateAgentAsync(agentId, sandboxEnabled: true);
        fixture.WriteWorkspaceFile(agentId, "reports/remove-me.txt", "delete me");

        using HttpResponseMessage response = await fixture.SendAuthorizedDeleteExpectingStatusAsync(
            $"/api/agents/{agentId}/files?path=reports/remove-me.txt",
            accessToken,
            HttpStatusCode.NoContent);

        Assert.False(fixture.WorkspaceEntryExists(agentId, "reports/remove-me.txt"));
    }

    [Fact]
    public async Task DeleteEndpointRejectsReferencedAttachmentFile()
    {
        await using var fixture = await AgentFilesApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        const string agentId = "agent-files-delete-referenced-attachment";
        string fileName = $"attachment-{Guid.NewGuid():N}.txt";

        await fixture.CreateAgentAsync(agentId, sandboxEnabled: true);
        GatewaySession session = fixture.CreateSession(agentId);

        using MultipartFormDataContent upload = new();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("keep attachment"));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        upload.Add(fileContent, "file", fileName);

        using JsonDocument created = await fixture.SendMultipartAsync($"/api/sessions/{session.Id}/assets", upload, accessToken);
        string storedFileName = created.RootElement.GetProperty("fileName").GetString()
            ?? throw new XunitException("Upload response did not include a file name.");
        Assert.True(fixture.WorkspaceEntryExists(agentId, $"attachments/{storedFileName}"));

        using HttpResponseMessage response = await fixture.SendAuthorizedJsonExpectingStatusAsync(
            HttpMethod.Post,
            $"/api/agents/{agentId}/files/delete",
            new { path = $"attachments/{storedFileName}" },
            accessToken,
            HttpStatusCode.Conflict);
        string payload = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("referenced by a retained chat session", payload, StringComparison.Ordinal);
        Assert.True(fixture.WorkspaceEntryExists(agentId, $"attachments/{storedFileName}"));
    }

    [Fact]
    public async Task DeleteEndpointAllowsSameAttachmentNameInAnotherUsersSystemWorkspace()
    {
        await using var fixture = await AgentFilesApiFixture.CreateAsync();
        string adminToken = await fixture.LoginAsBootstrapAdminAsync();
        const string agentId = "agent-files-delete-other-user-attachment";
        string fileName = $"attachment-{Guid.NewGuid():N}.txt";

        await fixture.CreateAgentAsync(agentId, sandboxEnabled: true);
        fixture.SetAgentScope(agentId, "system");
        string adminUserId = fixture.RequireCurrentUserId();
        GatewaySession session = fixture.CreateSession(agentId);

        using MultipartFormDataContent upload = new();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("admin attachment"));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        upload.Add(fileContent, "file", fileName);

        using JsonDocument created = await fixture.SendMultipartAsync($"/api/sessions/{session.Id}/assets", upload, adminToken);
        string storedFileName = created.RootElement.GetProperty("fileName").GetString()
            ?? throw new XunitException("Upload response did not include a file name.");
        string adminFile = Path.Combine(fixture.WorkspacePath(agentId, adminUserId), "attachments", storedFileName);
        Assert.True(File.Exists(adminFile));

        string memberToken = await fixture.InviteAndLoginUserAsync("member-attachment-delete", "Testing1", "user");
        fixture.WriteWorkspaceFile(agentId, $"attachments/{storedFileName}", "member copy");

        using HttpResponseMessage response = await fixture.SendAuthorizedJsonExpectingStatusAsync(
            HttpMethod.Post,
            $"/api/agents/{agentId}/files/delete",
            new { path = $"attachments/{storedFileName}" },
            memberToken,
            HttpStatusCode.NoContent);

        Assert.False(fixture.WorkspaceEntryExists(agentId, $"attachments/{storedFileName}"));
        Assert.True(File.Exists(adminFile));
    }

    [Fact]
    public async Task DeleteEndpointRemovesFolderRecursivelyInsideAuthorizedWorkspace()
    {
        await using var fixture = await AgentFilesApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        const string agentId = "agent-files-delete-folder";

        await fixture.CreateAgentAsync(agentId, sandboxEnabled: true);
        fixture.EnsureWorkspaceDirectory(agentId, "reports/archive/empty");

        using HttpResponseMessage response = await fixture.SendAuthorizedDeleteExpectingStatusAsync(
            $"/api/agents/{agentId}/files?path=reports/archive",
            accessToken,
            HttpStatusCode.NoContent);

        Assert.False(fixture.WorkspaceEntryExists(agentId, "reports/archive"));
    }

    [Fact]
    public async Task DeleteEndpointRejectsFolderThatContainsFiles()
    {
        await using var fixture = await AgentFilesApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        const string agentId = "agent-files-delete-non-empty-folder";

        await fixture.CreateAgentAsync(agentId, sandboxEnabled: true);
        fixture.WriteWorkspaceFile(agentId, "reports/archive/nested.txt", "keep tree");

        using HttpResponseMessage response = await fixture.SendAuthorizedJsonExpectingStatusAsync(
            HttpMethod.Post,
            $"/api/agents/{agentId}/files/delete",
            new { path = "reports/archive" },
            accessToken,
            HttpStatusCode.Conflict);
        string payload = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("Folder cannot be deleted while it contains files", payload, StringComparison.Ordinal);
        Assert.True(fixture.WorkspaceEntryExists(agentId, "reports/archive/nested.txt"));
    }

    [Fact]
    public async Task DeleteEndpointRejectsDeletingWorkspaceRoot()
    {
        await using var fixture = await AgentFilesApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        const string agentId = "agent-files-delete-root";

        await fixture.CreateAgentAsync(agentId, sandboxEnabled: true);
        fixture.WriteWorkspaceFile(agentId, "reports/keep.txt", "keep");

        using HttpResponseMessage response = await fixture.SendAuthorizedDeleteExpectingStatusAsync(
            $"/api/agents/{agentId}/files",
            accessToken,
            HttpStatusCode.BadRequest);
        string payload = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("Workspace root cannot be deleted", payload, StringComparison.Ordinal);
        Assert.True(fixture.WorkspaceEntryExists(agentId, "reports/keep.txt"));
    }

    [Theory]
    [InlineData("../reports")]
    [InlineData("%2E%2E%2Freports")]
    [InlineData(".kautuka")]
    [InlineData(".kautuka/skills")]
    [InlineData("%2Ekautuka%2Fskills")]
    public async Task ListEndpointRejectsTraversalAndInternalPaths(string invalidPath)
    {
        await using var fixture = await AgentFilesApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        const string agentId = "agent-files-list-invalid";

        await fixture.CreateAgentAsync(agentId, sandboxEnabled: true);

        using HttpRequestMessage request = new(HttpMethod.Get, $"/api/agents/{agentId}/files?path={invalidPath}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using HttpResponseMessage response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
        string payload = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("/tmp/", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../reports/file.txt")]
    [InlineData(".kautuka/skills/secret.txt")]
    [InlineData("%2Ekautuka%2Fskills%2Fsecret.txt")]
    public async Task DownloadEndpointRejectsTraversalAndInternalPaths(string invalidPath)
    {
        await using var fixture = await AgentFilesApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        const string agentId = "agent-files-download-invalid";

        await fixture.CreateAgentAsync(agentId, sandboxEnabled: true);

        using HttpRequestMessage request = new(HttpMethod.Get, $"/api/agents/{agentId}/files/content?path={invalidPath}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using HttpResponseMessage response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
        string payload = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("/tmp/", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../reports")]
    [InlineData(".kautuka")]
    [InlineData("%2Ekautuka%2Fskills")]
    public async Task UploadEndpointRejectsTraversalAndInternalPaths(string invalidPath)
    {
        await using var fixture = await AgentFilesApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        const string agentId = "agent-files-upload-invalid";

        await fixture.CreateAgentAsync(agentId, sandboxEnabled: true);
        fixture.EnsureWorkspaceDirectory(agentId, "reports");

        using MultipartFormDataContent upload = new();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("blocked"));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        upload.Add(fileContent, "file", "blocked.txt");
        upload.Add(new StringContent(invalidPath), "path");

        using HttpResponseMessage response = await fixture.SendMultipartExpectingStatusAsync(
            $"/api/agents/{agentId}/files",
            upload,
            accessToken,
            HttpStatusCode.BadRequest);
        string payload = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("/tmp/", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../reports")]
    [InlineData(".kautuka")]
    [InlineData("%2Ekautuka%2Fskills")]
    public async Task CreateFolderEndpointRejectsTraversalAndInternalPaths(string invalidPath)
    {
        await using var fixture = await AgentFilesApiFixture.CreateAsync();
        string accessToken = await fixture.LoginAsBootstrapAdminAsync();
        const string agentId = "agent-files-folder-invalid";

        await fixture.CreateAgentAsync(agentId, sandboxEnabled: true);
        fixture.EnsureWorkspaceDirectory(agentId, "reports");

        using HttpResponseMessage response = await fixture.SendAuthorizedJsonExpectingStatusAsync(
            HttpMethod.Post,
            $"/api/agents/{agentId}/files/folders",
            new { path = invalidPath, name = "blocked-folder" },
            accessToken,
            HttpStatusCode.BadRequest);
        string payload = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("/tmp/", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListEndpointReturnsForbiddenForAnotherUsersAgent()
    {
        await using var fixture = await AgentFilesApiFixture.CreateAsync();
        string adminToken = await fixture.LoginAsBootstrapAdminAsync();
        const string agentId = "agent-files-owner-a";

        await fixture.CreateAgentAsync(agentId, sandboxEnabled: true);
        fixture.WriteWorkspaceFile(agentId, "reports/owner-a.txt", "owner-a");

        string otherUserToken = await fixture.InviteAndLoginUserAsync("member-a", "Testing1", "user");

        using HttpRequestMessage request = new(HttpMethod.Get, $"/api/agents/{agentId}/files?path=reports");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", otherUserToken);

        using HttpResponseMessage response = await fixture.Client.SendAsync(request, TestContext.Current.CancellationToken);
        string payload = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("owner-a.txt", payload, StringComparison.Ordinal);
    }

    private sealed class AgentFilesApiFixture : IAsyncDisposable
    {
        private readonly string root;
        private readonly string? previousEnvironment;
        private readonly string? previousBootstrapPassword;
        private readonly WebApplication app;
        private string? currentUserId;

        private AgentFilesApiFixture(
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

        public static async Task<AgentFilesApiFixture> CreateAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "kautuka-agent-files-tests", Guid.NewGuid().ToString("N"));
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
                // These endpoint tests never assert telemetry. The defaults point OTLP at
                // openobserve on :20215; under a parallel suite every fixture exports into a
                // refused port, and disposing those exporters mid-flight is what lets a
                // cancelled CTS callback escape ServiceProviderEngineScope.DisposeAsync as
                // TaskCanceledException from this fixture's DisposeAsync.
                [KautukaTelemetryDefaults.TracesEndpointKey] = string.Empty,
                [KautukaTelemetryDefaults.MetricsEndpointKey] = string.Empty,
                [KautukaTelemetryDefaults.LogsEndpointKey] = string.Empty,
            });

            builder.WebHost.UseTestServer();
            GatewayAppBootstrap.ConfigureServices(builder);
            RemoveHostedService<WorkspaceDesiredStateStartupService>(builder.Services);
            RemoveHostedService<ChannelHostOutboxWorker>(builder.Services);
            RemoveHostedService<ChannelHostIngressWorker>(builder.Services);
            RemoveHostedService<AutopilotSchedulerService>(builder.Services);
            // Short endpoint tests finish well under ChannelApprovalRecoveryService's 5s
            // startup delay and the other PeriodicTimer loops. Leaving them registered means
            // every dispose cancels them mid-await; under parallel load that cancel races the
            // DI scope teardown and surfaces as a fixture DisposeAsync error.
            RemoveHostedService<ChannelApprovalRecoveryService>(builder.Services);
            RemoveHostedService<HitlExpirySweeperService>(builder.Services);
            RemoveHostedService<AgentMemoryEmbeddingReconciliationService>(builder.Services);
            builder.Services.Configure<HostOptions>(options => options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

            WebApplication app = builder.Build();
            var database = app.Services.GetRequiredService<KautukaDatabase>();
            GatewayAppBootstrap.ConfigureApp(app, database);
            await app.StartAsync();

            return new AgentFilesApiFixture(root, previousEnvironment, previousBootstrapPassword, app);
        }

        public async Task<string> LoginAsBootstrapAdminAsync()
        {
            await SendJsonAsync(HttpMethod.Post, "/api/auth/setup", new { username = "admin", password = "Testing1", setupToken = app.Services.GetRequiredService<SetupPossession>().Peek() });

            using JsonDocument json = await SendJsonAsync(HttpMethod.Post, "/api/auth/login", new { username = "admin", password = "Testing1" });
            currentUserId = json.RootElement.GetProperty("user").GetProperty("id").GetString();
            return json.RootElement.GetProperty("accessToken").GetString()
                ?? throw new XunitException("Login response did not include an access token.");
        }

        public async Task CreateAgentAsync(string agentId, bool sandboxEnabled)
        {
            if (string.IsNullOrWhiteSpace(currentUserId))
            {
                throw new InvalidOperationException("Login is required before creating an agent.");
            }

            IGatewayCatalogStore catalog = app.Services.GetRequiredService<IGatewayCatalogStore>();
            _ = await catalog.SaveAgentAsync(
                agentId,
                new GatewayAgentUpsertRequest
                {
                    Name = agentId,
                    ModelId = "model-alpha",
                    SystemPrompt = "You are a test agent.",
                    Sandbox = new GatewayAgentSandboxPayload
                    {
                        Enabled = sandboxEnabled,
                        AllowedLanguages = ["bash"],
                        MaxMemory = "2g",
                        MaxTimeout = 120,
                    },
                    ApprovalConfig = new GatewayApprovalConfigPayload(),
                    ToolExposure = new GatewayAgentToolExposurePayload(),
                },
                currentUserId,
                TestContext.Current.CancellationToken);
        }

        public string RequireCurrentUserId()
            => currentUserId ?? throw new XunitException("No signed-in user.");

        public GatewaySession CreateSession(string agentId)
            => app.Services.GetRequiredService<IGatewaySessionStore>().CreateSession(
                "agent",
                agentId,
                currentUserId ?? "user-agent-files");

        public string WorkspacePath(string agentId, string? userId)
        {
            IWorkspaceRuntimeTool workspaceTool = app.Services.GetRequiredService<IWorkspaceRuntimeTool>();
            return workspaceTool.DescribeAgentWorkspace(agentId, userId).HostWorkspacePath;
        }

        public string WorkspaceScopeName(string agentId)
            => Path.GetFileName(WorkspacePath(agentId, null));

        public void SetAgentScope(string agentId, string scope)
        {
            KautukaDatabase database = app.Services.GetRequiredService<KautukaDatabase>();
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(database.ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE agents SET scope = $scope WHERE id = $id;";
            command.Parameters.AddWithValue("$scope", scope);
            command.Parameters.AddWithValue("$id", agentId);
            if (command.ExecuteNonQuery() != 1)
            {
                throw new XunitException($"Agent '{agentId}' was not updated.");
            }

            GatewayState state = app.Services.GetRequiredService<GatewayState>();
            if (state.Agents.TryGetValue(agentId, out GatewayAgent? agent) && agent is not null)
            {
                state.Agents[agentId] = agent with { Scope = scope };
            }
        }

        public void WriteWorkspaceFile(string agentId, string relativePath, string content)
        {
            IWorkspaceRuntimeTool workspaceTool = app.Services.GetRequiredService<IWorkspaceRuntimeTool>();
            WorkspaceRuntimeContext workspace = workspaceTool.DescribeAgentWorkspace(agentId, currentUserId);
            string fullPath = Path.Combine(workspace.HostWorkspacePath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? workspace.HostWorkspacePath);
            File.WriteAllText(fullPath, content);
        }

        public string? ShortNameRelativePath(string agentId, string relativePath)
        {
            IWorkspaceRuntimeTool workspaceTool = app.Services.GetRequiredService<IWorkspaceRuntimeTool>();
            WorkspaceRuntimeContext workspace = workspaceTool.DescribeAgentWorkspace(agentId, currentUserId);
            string fullPath = Path.Combine(workspace.HostWorkspacePath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            string directory = Path.GetDirectoryName(fullPath) ?? workspace.HostWorkspacePath;
            string parent = Path.GetDirectoryName(directory) ?? workspace.HostWorkspacePath;
            string? shortDirectory = ReadShortDirectoryName(parent, Path.GetFileName(directory));
            if (string.IsNullOrEmpty(shortDirectory))
            {
                return null;
            }

            return shortDirectory + "/" + Path.GetFileName(fullPath);
        }

        private static string? ReadShortDirectoryName(string parent, string longName)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c dir /x \"" + parent + "\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            foreach (string rawLine in output.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                if (!line.EndsWith(longName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                int longIndex = Array.FindIndex(parts, part => part.Equals(longName, StringComparison.OrdinalIgnoreCase));
                if (longIndex > 0 && parts[longIndex - 1].Contains('~', StringComparison.Ordinal))
                {
                    return parts[longIndex - 1];
                }
            }

            return null;
        }

        public void EnsureWorkspaceDirectory(string agentId, string relativePath)
        {
            IWorkspaceRuntimeTool workspaceTool = app.Services.GetRequiredService<IWorkspaceRuntimeTool>();
            WorkspaceRuntimeContext workspace = workspaceTool.DescribeAgentWorkspace(agentId, currentUserId);
            string fullPath = Path.Combine(workspace.HostWorkspacePath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(fullPath);
        }

        public bool WorkspaceEntryExists(string agentId, string relativePath)
        {
            IWorkspaceRuntimeTool workspaceTool = app.Services.GetRequiredService<IWorkspaceRuntimeTool>();
            WorkspaceRuntimeContext workspace = workspaceTool.DescribeAgentWorkspace(agentId, currentUserId);
            string fullPath = Path.Combine(workspace.HostWorkspacePath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            return Directory.Exists(fullPath) || File.Exists(fullPath);
        }

        public string ReadWorkspaceFile(string agentId, string relativePath)
        {
            IWorkspaceRuntimeTool workspaceTool = app.Services.GetRequiredService<IWorkspaceRuntimeTool>();
            WorkspaceRuntimeContext workspace = workspaceTool.DescribeAgentWorkspace(agentId, currentUserId);
            string fullPath = Path.Combine(workspace.HostWorkspacePath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            return File.ReadAllText(fullPath);
        }

        public async Task<string> InviteAndLoginUserAsync(string username, string password, string role)
        {
            IGatewayUserStore users = app.Services.GetRequiredService<IGatewayUserStore>();
            JsonElement payload = JsonSerializer.SerializeToElement(new
            {
                username,
                password,
                role,
            });

            GatewayUserListItem? invited = users.InviteUser(payload, out string? error);
            if (invited is null)
            {
                throw new XunitException(error ?? "Failed to invite user.");
            }

            using JsonDocument json = await SendJsonAsync(HttpMethod.Post, "/api/auth/login", new { username, password });
            currentUserId = json.RootElement.GetProperty("user").GetProperty("id").GetString();
            return json.RootElement.GetProperty("accessToken").GetString()
                ?? throw new XunitException("Login response did not include an access token.");
        }

        private async Task<JsonDocument> SendJsonAsync(HttpMethod method, string path, object? body)
        {
            using HttpRequestMessage request = new(method, path);
            if (body is not null)
            {
                request.Content = System.Net.Http.Json.JsonContent.Create(body);
            }

            using HttpResponseMessage response = await Client.SendAsync(request, TestContext.Current.CancellationToken);
            string content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
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

            using HttpResponseMessage response = await Client.SendAsync(request, TestContext.Current.CancellationToken);
            string responseContent = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new XunitException($"Request to '{path}' failed with {(int)response.StatusCode}: {responseContent}");
            }

            return JsonDocument.Parse(responseContent);
        }

        public async Task<JsonDocument> SendAuthorizedJsonAsync(HttpMethod method, string path, object body, string accessToken)
        {
            using HttpRequestMessage request = new(method, path)
            {
                Content = System.Net.Http.Json.JsonContent.Create(body),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using HttpResponseMessage response = await Client.SendAsync(request, TestContext.Current.CancellationToken);
            string responseContent = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new XunitException($"Request to '{path}' failed with {(int)response.StatusCode}: {responseContent}");
            }

            return JsonDocument.Parse(responseContent);
        }

        public async Task<HttpResponseMessage> SendAuthorizedJsonExpectingStatusAsync(HttpMethod method, string path, object body, string accessToken, HttpStatusCode expectedStatusCode)
        {
            using HttpRequestMessage request = new(method, path)
            {
                Content = System.Net.Http.Json.JsonContent.Create(body),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            HttpResponseMessage response = await Client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(expectedStatusCode, response.StatusCode);
            return response;
        }

        public async Task<HttpResponseMessage> SendMultipartExpectingStatusAsync(string path, MultipartFormDataContent content, string accessToken, HttpStatusCode expectedStatusCode)
        {
            using HttpRequestMessage request = new(HttpMethod.Post, path)
            {
                Content = content,
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            HttpResponseMessage response = await Client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(expectedStatusCode, response.StatusCode);
            return response;
        }

        public async Task<HttpResponseMessage> SendAuthorizedDeleteExpectingStatusAsync(string path, string accessToken, HttpStatusCode expectedStatusCode)
        {
            using HttpRequestMessage request = new(HttpMethod.Delete, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            HttpResponseMessage response = await Client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(expectedStatusCode, response.StatusCode);
            return response;
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

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();

            // Stop before dispose so BackgroundService.StopAsync can cancel ExecuteAsync with
            // SuppressThrowing. DisposeAsync alone tears down the DI scope while those loops
            // are still awaiting, which is the path that threw TaskCanceledException from
            // this fixture under the parallel suite.
            try
            {
                await app.StopAsync().ConfigureAwait(false);
            }
            finally
            {
                await app.DisposeAsync().ConfigureAwait(false);
            }

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
