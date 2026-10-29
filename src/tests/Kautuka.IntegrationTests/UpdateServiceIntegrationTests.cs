using System.Reflection;
using System.Text.Json;

using Kautuka.Manager.Protos;
using Kautuka.UpdateService.Abstractions;
using Kautuka.UpdateService.Hosting;
using Kautuka.UpdateService.Models;
using Kautuka.UpdateService.State;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kautuka.IntegrationTests;

public sealed class UpdateServiceIntegrationTests
{
    [Fact]
    public void UpdateOptionsValidationSucceedsWhenNoReleaseSourceIsConfigured()
    {
        var validator = new UpdateOptionsValidation();

        ValidateOptionsResult result = validator.Validate(null, new UpdateOptions
        {
            Enabled = true,
            StateRootPath = CreateTempDirectory(),
            ReleaseManifestUrl = null,
            RepositoryOwner = null,
            RepositoryName = null,
            AllowUnsignedLocalFeeds = false
        });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task UpdateRuntimePolicyStorePersistsPolicyInServiceRoot()
    {
        string tempRoot = CreateTempDirectory();

        try
        {
            var store = new UpdateRuntimePolicyStore(Options.Create(new UpdateOptions
            {
                Enabled = true,
                StateRootPath = tempRoot,
                ReleaseChannel = "stable"
            }));

            await store.SetReleaseChannelAsync("preview", TestContext.Current.CancellationToken).ConfigureAwait(true);
            UpdateRuntimePolicy policy = await new UpdateRuntimePolicyStore(Options.Create(new UpdateOptions
            {
                Enabled = true,
                StateRootPath = tempRoot,
                ReleaseChannel = "stable"
            })).GetAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

            Assert.Equal("preview", policy.ReleaseChannel);
            Assert.True(File.Exists(Path.Combine(tempRoot, "update-runtime-policy.json")));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task JsonUpdateStateStorePersistsStateInServiceRoot()
    {
        string tempRoot = CreateTempDirectory();

        try
        {
            UpdateStateRecord expected = UpdateStateRecord.CreateDefault() with
            {
                State = UpdateWorkflowState.Available,
                LastError = "test-state"
            };

            var store = new JsonUpdateStateStore(Options.Create(new UpdateOptions
            {
                Enabled = true,
                StateRootPath = tempRoot
            }));

            await store.SaveAsync(expected, TestContext.Current.CancellationToken).ConfigureAwait(true);
            UpdateStateRecord actual = await new JsonUpdateStateStore(Options.Create(new UpdateOptions
            {
                Enabled = true,
                StateRootPath = tempRoot
            })).GetAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

            Assert.Equal(UpdateWorkflowState.Available, actual.State);
            Assert.Equal("test-state", actual.LastError);
            Assert.True(File.Exists(Path.Combine(tempRoot, "update-state.json")));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateStateStoresRejectCorruptPersistedJsonInsteadOfResettingState()
    {
        string tempRoot = CreateTempDirectory();

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(tempRoot, "update-state.json"),
                "{not-json",
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            var stateStore = new JsonUpdateStateStore(Options.Create(new UpdateOptions
            {
                Enabled = true,
                StateRootPath = tempRoot,
            }));

            InvalidDataException stateError = await Assert.ThrowsAsync<InvalidDataException>(
                async () => await stateStore.GetAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
            Assert.Contains("update-state.json", stateError.Message, StringComparison.Ordinal);

            await File.WriteAllTextAsync(
                Path.Combine(tempRoot, "update-operations.json"),
                "[] trailing",
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            var historyStore = new UpdateOperationHistoryStore(Options.Create(new UpdateOptions
            {
                Enabled = true,
                StateRootPath = tempRoot,
            }));

            InvalidDataException historyError = await Assert.ThrowsAsync<InvalidDataException>(
                async () => await historyStore.GetRecentAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
            Assert.Contains("update-operations.json", historyError.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ResolveManagerInstallRootMapsVersionDirectoryBackToCurrentSymlink()
    {
        string tempRoot = CreateTempDirectory();

        try
        {
            string applianceRoot = Path.Combine(tempRoot, "opt", "kautuka");
            string currentPath = CreateVersionedInstall(
                applianceRoot,
                "manager",
                "release-current",
                "kautuka-manager",
                "manager-current");

            string resolvedVersionPath = Path.GetFullPath(Path.Combine(
                applianceRoot,
                "manager",
                "versions",
                "release-current"));

            Assert.Equal(currentPath, UpdateServiceIntegration.ResolveManagerInstallRoot(currentPath));
            Assert.Equal(currentPath, UpdateServiceIntegration.ResolveManagerInstallRoot(resolvedVersionPath));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void CurrentPointerTargetsVersionFollowsCurrentSymlink()
    {
        string tempRoot = CreateTempDirectory();

        try
        {
            string applianceRoot = Path.Combine(tempRoot, "opt", "kautuka");
            string currentPath = CreateVersionedInstall(
                applianceRoot,
                "manager",
                "0.0.3-preview.1",
                "kautuka-manager",
                "manager-current");

            Assert.True(UpdateServiceIntegration.CurrentPointerTargetsVersion(currentPath, "0.0.3-preview.1"));
            Assert.False(UpdateServiceIntegration.CurrentPointerTargetsVersion(currentPath, "0.0.2-preview.1"));
            Assert.False(UpdateServiceIntegration.CurrentPointerTargetsVersion(Path.Combine(applianceRoot, "missing", "current"), "0.0.3-preview.1"));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ResolveManagerVersionUsesInstalledManagerVersionFile()
    {
        string tempRoot = CreateTempDirectory();

        try
        {
            string applianceRoot = Path.Combine(tempRoot, "opt", "kautuka");
            string managerCurrent = CreateVersionedInstall(
                applianceRoot,
                "manager",
                "release-current",
                "kautuka-manager",
                "manager-current");
            WriteFile(Path.Combine(managerCurrent, "release-version.txt"), "0.1.1-preview.20260524102858\n");

            string resolvedVersion = UpdateServiceIntegration.ResolveManagerVersion(managerCurrent);

            Assert.Equal("0.1.1-preview.20260524102858", resolvedVersion);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void HostServiceVersionActivatorRejectsReleaseIdThatEscapesTheVersionRoot()
    {
        string tempRoot = CreateTempDirectory();

        try
        {
            string applianceRoot = Path.Combine(tempRoot, "opt", "kautuka");
            string managerCurrent = CreateVersionedInstall(applianceRoot, "manager", "release-current", "kautuka-manager", "manager-current");
            string keepPath = Path.Combine(applianceRoot, "manager", "keep.txt");
            File.WriteAllText(keepPath, "keep");
            string extractedRoot = Path.Combine(tempRoot, "extract");
            WriteFile(Path.Combine(extractedRoot, "bin", "kautuka-manager"), "manager-next");

            Assert.Throws<InvalidOperationException>(() =>
                HostServiceVersionActivator.ActivateManagerAndHostServices(managerCurrent, "..", extractedRoot, NullLogger.Instance));

            Assert.Equal("keep", File.ReadAllText(keepPath));
            Assert.Equal("manager-current", File.ReadAllText(Path.Combine(applianceRoot, "manager", "current", "kautuka-manager")));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void HostServiceVersionActivatorActivatesSiblingVersionRoots()
    {
        string tempRoot = CreateTempDirectory();

        try
        {
            string applianceRoot = Path.Combine(tempRoot, "opt", "kautuka");
            string releaseId = "release-next";
            string extractedRoot = Path.Combine(tempRoot, "extract");

            string managerCurrent = CreateVersionedInstall(applianceRoot, "manager", "release-current", "kautuka-manager", "manager-current");
            CreateVersionedInstall(applianceRoot, "gateway", "release-current", "kautuka-gateway", "gateway-current");
            CreateVersionedInstall(applianceRoot, "credentialshost", "release-current", "kautuka-credentialshost", "credential-current");
            CreateVersionedInstall(applianceRoot, "channelhost", "release-current", "kautuka-channelhost", "channel-current");
            CreateVersionedInstall(applianceRoot, "updateservice", "release-current", "kautuka-updateservice", "updateservice-current");
            CreateVersionedInstall(applianceRoot, "webhost", "release-current", "kautuka-webhost", "web-current");
            CreateVersionedInstall(applianceRoot, "tui", "release-current", "kautuka-tui", "tui-current");

            WriteFile(Path.Combine(extractedRoot, "bin", "kautuka-manager"), "manager-next");
            WriteFile(Path.Combine(extractedRoot, "services", "gateway", "kautuka-gateway"), "gateway-next");
            WriteFile(Path.Combine(extractedRoot, "services", "credentialshost", "kautuka-credentialshost"), "credential-next");
            WriteFile(Path.Combine(extractedRoot, "services", "channelhost", "kautuka-channelhost"), "channel-next");
            WriteFile(Path.Combine(extractedRoot, "services", "updateservice", "kautuka-updateservice"), "updateservice-next");
            WriteFile(Path.Combine(extractedRoot, "services", "webhost", "kautuka-webhost"), "web-next");
            WriteFile(Path.Combine(extractedRoot, "tools", "tui", "kautuka-tui"), "tui-next");

            HostServiceVersionActivator.ActivateManagerAndHostServices(managerCurrent, releaseId, extractedRoot, NullLogger.Instance);

            Assert.Equal("manager-next", File.ReadAllText(Path.Combine(applianceRoot, "manager", "current", "kautuka-manager")));
            Assert.Equal("gateway-next", File.ReadAllText(Path.Combine(applianceRoot, "gateway", "current", "kautuka-gateway")));
            Assert.Equal("credential-next", File.ReadAllText(Path.Combine(applianceRoot, "credentialshost", "current", "kautuka-credentialshost")));
            Assert.Equal("channel-next", File.ReadAllText(Path.Combine(applianceRoot, "channelhost", "current", "kautuka-channelhost")));
            Assert.Equal("updateservice-next", File.ReadAllText(Path.Combine(applianceRoot, "updateservice", "current", "kautuka-updateservice")));
            Assert.Equal("web-next", File.ReadAllText(Path.Combine(applianceRoot, "webhost", "current", "kautuka-webhost")));
            Assert.Equal("tui-next", File.ReadAllText(Path.Combine(applianceRoot, "tui", "current", "kautuka-tui")));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void HostServiceVersionActivatorActivatesTuiFromManagerStagedLayout()
    {
        string tempRoot = CreateTempDirectory();

        try
        {
            string applianceRoot = Path.Combine(tempRoot, "opt", "kautuka");
            string releaseId = "release-next";
            string extractedRoot = Path.Combine(tempRoot, "extract");

            string managerCurrent = CreateVersionedInstall(applianceRoot, "manager", "release-current", "kautuka-manager", "manager-current");
            CreateVersionedInstall(applianceRoot, "gateway", "release-current", "kautuka-gateway", "gateway-current");
            CreateVersionedInstall(applianceRoot, "credentialshost", "release-current", "kautuka-credentialshost", "credential-current");
            CreateVersionedInstall(applianceRoot, "channelhost", "release-current", "kautuka-channelhost", "channel-current");
            CreateVersionedInstall(applianceRoot, "updateservice", "release-current", "kautuka-updateservice", "updateservice-current");
            CreateVersionedInstall(applianceRoot, "webhost", "release-current", "kautuka-webhost", "web-current");
            CreateVersionedInstall(applianceRoot, "tui", "release-current", "kautuka-tui", "tui-current");

            WriteFile(Path.Combine(extractedRoot, "bin", "kautuka-manager"), "manager-next");
            WriteFile(Path.Combine(extractedRoot, "services", "gateway", "kautuka-gateway"), "gateway-next");
            WriteFile(Path.Combine(extractedRoot, "services", "credentialshost", "kautuka-credentialshost"), "credential-next");
            WriteFile(Path.Combine(extractedRoot, "services", "channelhost", "kautuka-channelhost"), "channel-next");
            WriteFile(Path.Combine(extractedRoot, "services", "updateservice", "kautuka-updateservice"), "updateservice-next");
            WriteFile(Path.Combine(extractedRoot, "services", "webhost", "kautuka-webhost"), "web-next");
            WriteFile(Path.Combine(extractedRoot, "manager", "tools", "tui", "kautuka-tui"), "tui-next");

            HostServiceVersionActivator.ActivateManagerAndHostServices(managerCurrent, releaseId, extractedRoot, NullLogger.Instance);

            Assert.Equal("tui-next", File.ReadAllText(Path.Combine(applianceRoot, "tui", "current", "kautuka-tui")));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void HostServiceVersionActivatorSkipsReactivatingUpdateServiceWhenAlreadyOnTargetRelease()
    {
        string tempRoot = CreateTempDirectory();

        try
        {
            string applianceRoot = Path.Combine(tempRoot, "opt", "kautuka");
            string releaseId = "release-next";
            string extractedRoot = Path.Combine(tempRoot, "extract");

            string managerCurrent = CreateVersionedInstall(applianceRoot, "manager", "release-current", "kautuka-manager", "manager-current");
            CreateVersionedInstall(applianceRoot, "gateway", "release-current", "kautuka-gateway", "gateway-current");
            CreateVersionedInstall(applianceRoot, "credentialshost", "release-current", "kautuka-credentialshost", "credential-current");
            CreateVersionedInstall(applianceRoot, "channelhost", "release-current", "kautuka-channelhost", "channel-current");
            string updateServiceCurrent = CreateVersionedInstall(applianceRoot, "updateservice", releaseId, "kautuka-updateservice", "updateservice-current");
            CreateVersionedInstall(applianceRoot, "webhost", "release-current", "kautuka-webhost", "web-current");
            CreateVersionedInstall(applianceRoot, "tui", "release-current", "kautuka-tui", "tui-current");

            WriteFile(Path.Combine(extractedRoot, "bin", "kautuka-manager"), "manager-next");
            WriteFile(Path.Combine(extractedRoot, "services", "gateway", "kautuka-gateway"), "gateway-next");
            WriteFile(Path.Combine(extractedRoot, "services", "credentialshost", "kautuka-credentialshost"), "credential-next");
            WriteFile(Path.Combine(extractedRoot, "services", "channelhost", "kautuka-channelhost"), "channel-next");
            WriteFile(Path.Combine(extractedRoot, "services", "updateservice", "kautuka-updateservice"), "updateservice-refreshed");
            WriteFile(Path.Combine(extractedRoot, "services", "webhost", "kautuka-webhost"), "web-next");
            WriteFile(Path.Combine(extractedRoot, "tools", "tui", "kautuka-tui"), "tui-next");

            HostServiceVersionActivator.ActivateManagerAndHostServices(managerCurrent, releaseId, extractedRoot, NullLogger.Instance);

            Assert.Equal("updateservice-current", File.ReadAllText(Path.Combine(updateServiceCurrent, "kautuka-updateservice")));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ClearGeneratedAgentWorkspaceStateRemovesOnlyGeneratedRuntimeContent()
    {
        string tempRoot = CreateTempDirectory();

        try
        {
            string visibleRoot = Path.Combine(tempRoot, "srv", "kautuka");
            string agentWorkspace = Path.Combine(visibleRoot, "workspaces", "agents", "admin-general");
            string generatedStateFile = Path.Combine(agentWorkspace, ".kautuka", "skills", "roll-dice", "scripts", "run.sh");
            string userFile = Path.Combine(agentWorkspace, "uploads", "note.txt");
            string otherWorkspace = Path.Combine(visibleRoot, "workspaces", "agents", "kautuka-kautuka");

            WriteFile(generatedStateFile, "echo stale");
            WriteFile(userFile, "keep me");
            Directory.CreateDirectory(otherWorkspace);

            UpdateServiceIntegration.ClearGeneratedAgentWorkspaceState(visibleRoot, NullLogger.Instance);

            Assert.False(Directory.Exists(Path.Combine(agentWorkspace, ".kautuka")));
            Assert.Equal("keep me", File.ReadAllText(userFile));
            Assert.True(Directory.Exists(otherWorkspace));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task TryContinuePendingActivationAsyncDoesNotResumeCsharpDeploy()
    {
        string tempRoot = CreateTempDirectory();

        try
        {
            string visibleRoot = Path.Combine(tempRoot, "srv", "kautuka");
            string runtimeRoot = Path.Combine(tempRoot, "runtime", "updateService");
            string applianceRoot = Path.Combine(tempRoot, "opt", "kautuka");
            string extractedRoot = Path.Combine(tempRoot, "extract");
            string releaseId = "release-next";

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Kautuka:Mode"] = "dev",
                    ["Kautuka:HostPaths:VisibleRoot"] = visibleRoot,
                    ["Kautuka:HostPaths:InternalStateRoot"] = Path.Combine(tempRoot, "runtime"),
                    ["Kautuka:HostPaths:LogsRoot"] = Path.Combine(tempRoot, "logs"),
                    ["Kautuka:ContainerStateRoot"] = Path.Combine(tempRoot, "runtime"),
                })
                .Build();

            CreateVersionedInstall(applianceRoot, "manager", "release-current", "kautuka-manager", "manager-current");
            CreateVersionedInstall(applianceRoot, "gateway", "release-current", "kautuka-gateway", "gateway-current");
            CreateVersionedInstall(applianceRoot, "credentialshost", "release-current", "kautuka-credentialshost", "credential-current");
            CreateVersionedInstall(applianceRoot, "channelhost", "release-current", "kautuka-channelhost", "channel-current");
            string updateServiceCurrent = CreateVersionedInstall(applianceRoot, "updateservice", releaseId, "kautuka-updateservice", "updateservice-current");
            CreateVersionedInstall(applianceRoot, "webhost", "release-current", "kautuka-webhost", "web-current");
            CreateVersionedInstall(applianceRoot, "tui", "release-current", "kautuka-tui", "tui-current");

            WriteFile(Path.Combine(extractedRoot, "bin", "kautuka-manager"), "manager-next");
            WriteFile(Path.Combine(extractedRoot, "services", "gateway", "kautuka-gateway"), "gateway-next");
            WriteFile(Path.Combine(extractedRoot, "services", "credentialshost", "kautuka-credentialshost"), "credential-next");
            WriteFile(Path.Combine(extractedRoot, "services", "channelhost", "kautuka-channelhost"), "channel-next");
            WriteFile(Path.Combine(extractedRoot, "services", "updateservice", "kautuka-updateservice"), "updateservice-next");
            WriteFile(Path.Combine(extractedRoot, "services", "webhost", "kautuka-webhost"), "web-next");
            WriteFile(Path.Combine(extractedRoot, "tools", "tui", "kautuka-tui"), "tui-next");

            string requestPath = Path.Combine(runtimeRoot, "updates", "pending-activation.env");
            WriteFile(
                requestPath,
                string.Join(
                    '\n',
                    "ACTIVATION_OPERATION=apply",
                    $"ACTIVATION_RELEASE_ID={releaseId}",
                    $"ACTIVATION_PLATFORM_EXTRACTED_PATH={extractedRoot}",
                    "ACTIVATION_REQUESTED_AT_UTC=2026-05-24T00:00:00.0000000+00:00",
                    string.Empty));

            var runtimePaths = new UpdateServiceRuntimePaths(configuration);
            var integration = new UpdateServiceIntegration(
                runtimePaths,
                configuration,
                Options.Create(new UpdateOptions()),
                new RecordingManagerStatusClient(new ManagerStatusResponse()),
                new TestHostApplicationLifetime(),
                NullLogger<UpdateServiceIntegration>.Instance);

            bool resumed = await integration.TryContinuePendingActivationAsync(updateServiceCurrent, TestContext.Current.CancellationToken).ConfigureAwait(true);

            Assert.False(resumed);
            Assert.Equal(
                "manager-current",
                await File.ReadAllTextAsync(Path.Combine(applianceRoot, "manager", "current", "kautuka-manager"), TestContext.Current.CancellationToken).ConfigureAwait(true));
            Assert.Equal(
                "gateway-current",
                await File.ReadAllTextAsync(Path.Combine(applianceRoot, "gateway", "current", "kautuka-gateway"), TestContext.Current.CancellationToken).ConfigureAwait(true));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void RestoreRollbackBackupSnapshotRestoresSharedStateAndPreservesUpdaterCache()
    {
        string tempRoot = CreateTempDirectory();

        try
        {
            string backupRoot = Path.Combine(tempRoot, "backups", "20260514010101-release-current");
            string dataBackupRoot = Path.Combine(backupRoot, "data");
            string sharedDataRoot = Path.Combine(tempRoot, "shared");

            WriteFile(Path.Combine(dataBackupRoot, "kautuka.db"), "db-from-backup");
            WriteFile(Path.Combine(dataBackupRoot, "kautuka.key"), "key-from-backup");
            WriteFile(Path.Combine(dataBackupRoot, "gateway-auth.key"), "auth-key-from-backup");
            WriteFile(Path.Combine(dataBackupRoot, "manager", "manager.json"), "manager-from-backup");
            WriteFile(Path.Combine(dataBackupRoot, "gateway", "gateway.json"), "gateway-from-backup");
            WriteFile(Path.Combine(dataBackupRoot, "agents", "agent-a", "agent.db"), "agent-from-backup");
            WriteFile(Path.Combine(dataBackupRoot, "credentialsHost", "kautuka.credentials.db"), "credentials-from-backup");
            WriteFile(Path.Combine(dataBackupRoot, "channelHost", "channelhost.db"), "channel-from-backup");
            WriteFile(Path.Combine(dataBackupRoot, "updateService", "settings.json"), "settings-from-backup");

            WriteFile(Path.Combine(sharedDataRoot, "kautuka.db"), "db-live");
            WriteFile(Path.Combine(sharedDataRoot, "kautuka.key"), "key-live");
            WriteFile(Path.Combine(sharedDataRoot, "gateway-auth.key"), "auth-key-live");
            WriteFile(Path.Combine(sharedDataRoot, "manager", "manager.json"), "manager-live");
            WriteFile(Path.Combine(sharedDataRoot, "gateway", "gateway.json"), "gateway-live");
            WriteFile(Path.Combine(sharedDataRoot, "agents", "agent-a", "agent.db"), "agent-live");
            WriteFile(Path.Combine(sharedDataRoot, "credentialsHost", "kautuka.credentials.db"), "credentials-live");
            WriteFile(Path.Combine(sharedDataRoot, "channelHost", "channelhost.db"), "channel-live");
            WriteFile(Path.Combine(sharedDataRoot, "updateService", "settings.json"), "settings-live");
            WriteFile(Path.Combine(sharedDataRoot, "updateService", "updates", "state.json"), "updates-live");
            WriteFile(Path.Combine(sharedDataRoot, "updateService", "old.json"), "stale-live-file");

            UpdateServiceIntegration.RestoreRollbackBackupSnapshot(backupRoot, sharedDataRoot, NullLogger.Instance);

            Assert.Equal("db-from-backup", File.ReadAllText(Path.Combine(sharedDataRoot, "kautuka.db")));
            Assert.Equal("key-from-backup", File.ReadAllText(Path.Combine(sharedDataRoot, "kautuka.key")));
            Assert.Equal("auth-key-from-backup", File.ReadAllText(Path.Combine(sharedDataRoot, "gateway-auth.key")));
            Assert.Equal("manager-from-backup", File.ReadAllText(Path.Combine(sharedDataRoot, "manager", "manager.json")));
            Assert.Equal("gateway-from-backup", File.ReadAllText(Path.Combine(sharedDataRoot, "gateway", "gateway.json")));
            Assert.Equal("agent-from-backup", File.ReadAllText(Path.Combine(sharedDataRoot, "agents", "agent-a", "agent.db")));
            Assert.Equal("credentials-from-backup", File.ReadAllText(Path.Combine(sharedDataRoot, "credentialsHost", "kautuka.credentials.db")));
            Assert.Equal("channel-from-backup", File.ReadAllText(Path.Combine(sharedDataRoot, "channelHost", "channelhost.db")));
            Assert.Equal("settings-from-backup", File.ReadAllText(Path.Combine(sharedDataRoot, "updateService", "settings.json")));
            Assert.Equal("updates-live", File.ReadAllText(Path.Combine(sharedDataRoot, "updateService", "updates", "state.json")));
            Assert.False(File.Exists(Path.Combine(sharedDataRoot, "updateService", "old.json")));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SmokeReleaseCoordinatorAppliesBuiltArtifactsFromAvailableReleaseAsync()
    {
        string? smokeReleaseDir = Environment.GetEnvironmentVariable("KAUTUKA_SMOKE_RELEASE_DIR");
        string? smokeReleaseBaseUrl = Environment.GetEnvironmentVariable("KAUTUKA_SMOKE_RELEASE_BASE_URL");
        string? publicKeyPathFromEnv = Environment.GetEnvironmentVariable("KAUTUKA_SMOKE_MANIFEST_PUBLIC_KEY_PATH");
        string? manifestPrivateKey = Environment.GetEnvironmentVariable("KAUTUKA_SMOKE_MANIFEST_PRIVATE_KEY");

        if (string.IsNullOrWhiteSpace(smokeReleaseDir)
            || string.IsNullOrWhiteSpace(smokeReleaseBaseUrl)
            || (string.IsNullOrWhiteSpace(publicKeyPathFromEnv) && string.IsNullOrWhiteSpace(manifestPrivateKey)))
        {
            return;
        }

        string manifestPath = Path.Combine(smokeReleaseDir, "kautuka-platform-release.json");
        string signaturePath = Path.Combine(smokeReleaseDir, "kautuka-platform-release.sig");
        Assert.True(File.Exists(manifestPath));
        Assert.True(File.Exists(signaturePath));

        using JsonDocument manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, TestContext.Current.CancellationToken).ConfigureAwait(true));
        string version = manifest.RootElement.GetProperty("version").GetString()!;
        string releaseId = manifest.RootElement.TryGetProperty("releaseId", out JsonElement releaseIdElement)
            ? releaseIdElement.GetString()!
            : version;
        string channel = manifest.RootElement.GetProperty("channel").GetString()!;
        string releaseTag = manifest.RootElement.GetProperty("releaseTag").GetString()!;
        string artifactUrl = manifest.RootElement.GetProperty("artifacts").EnumerateArray()
            .First(artifact => string.Equals(artifact.GetProperty("platform").GetString(), "linux-x64", StringComparison.OrdinalIgnoreCase))
            .GetProperty("url").GetString()!;

        string tempRoot = CreateTempDirectory();

        try
        {
            var stateStore = new InMemoryUpdateStateStore(
                UpdateStateRecord.CreateDefault() with
                {
                    State = UpdateWorkflowState.Available,
                    AvailableRelease = new UpdateReleaseReference(
                        ReleaseId: releaseId,
                        Version: version,
                        Channel: channel,
                        ReleaseFingerprint: "smoke-fingerprint",
                        PlatformArtifactUrl: artifactUrl,
                        PlatformReleaseAssetUrl: $"{smokeReleaseBaseUrl.TrimEnd('/')}/manifest",
                        PlatformReleaseSignatureAssetUrl: $"{smokeReleaseBaseUrl.TrimEnd('/')}/kautuka-platform-release.sig",
                        SourceUrl: smokeReleaseBaseUrl,
                        PublishedAtUtc: null,
                        Tag: releaseTag)
                });

            var hostIntegration = new RecordingUpdateServiceIntegration(tempRoot);
            string publicKeyPath = Path.Combine(tempRoot, "manifest-public.pem");
            if (!string.IsNullOrWhiteSpace(publicKeyPathFromEnv))
            {
                File.Copy(publicKeyPathFromEnv, publicKeyPath, overwrite: true);
            }
            else
            {
                await File.WriteAllTextAsync(publicKeyPath, ConvertManifestPrivateKeyToPublicKeyPem(manifestPrivateKey!), TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            var options = Options.Create(new UpdateOptions
            {
                Enabled = true,
                StateRootPath = tempRoot,
                ManifestPublicKeyPath = publicKeyPath
            });

            var services = new ServiceCollection();
            services.AddHttpClient("kautuka.updateservice.github");
            await using ServiceProvider provider = services.BuildServiceProvider();
            IHttpClientFactory httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();

            var coordinator = new UpdateCoordinator(
                stateStore,
                new InMemoryUpdateRuntimePolicyStore("preview"),
                new NullReleaseDiscoveryClient(NullLogger<NullReleaseDiscoveryClient>.Instance),
                httpClientFactory,
                hostIntegration,
                new UpdateOperationHistoryStore(options),
                options,
                NullLogger<UpdateCoordinator>.Instance);

            UpdateStatusSnapshot applying = await coordinator.ApplyPreparedReleaseAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.Equal(UpdateWorkflowState.Applying, applying.State);
            Assert.Equal(releaseId, hostIntegration.ActivatedReleaseId);
            Assert.True(File.Exists(hostIntegration.ActivatedPlatformPackagePath));
            Assert.True(Directory.Exists(hostIntegration.ActivatedPlatformExtractedPath));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GetHostContextAsyncUsesManagerStatusFacts()
    {
        string tempRoot = CreateTempDirectory();

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Kautuka:Mode"] = "dev",
                    ["Kautuka:HostPaths:VisibleRoot"] = Path.Combine(tempRoot, "visible"),
                    ["Kautuka:HostPaths:InternalStateRoot"] = Path.Combine(tempRoot, "runtime"),
                    ["Kautuka:HostPaths:LogsRoot"] = Path.Combine(tempRoot, "logs"),
                    ["Kautuka:ContainerStateRoot"] = Path.Combine(tempRoot, "runtime"),
                })
                .Build();

            string runtimeRoot = Path.Combine(tempRoot, "runtime", "updateService");
            Directory.CreateDirectory(runtimeRoot);

            var runtimePaths = new UpdateServiceRuntimePaths(configuration);
            var managerStatusClient = new RecordingManagerStatusClient(new ManagerStatusResponse
            {
                Ok = true,
                CoreReady = true,
                ActiveExecCount = 3,
                Message = "status",
            });

            var integration = new UpdateServiceIntegration(
                runtimePaths,
                configuration,
                Options.Create(new UpdateOptions()),
                managerStatusClient,
                new TestHostApplicationLifetime(),
                NullLogger<UpdateServiceIntegration>.Instance);

            UpdateServiceContext hostContext = await integration.GetHostContextAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

            Assert.Equal(runtimeRoot, hostContext.RuntimeRoot);
            Assert.Equal(UpdateServiceIntegration.ResolveManagerInstallRoot(AppContext.BaseDirectory), hostContext.InstallRoot);
            Assert.False(string.IsNullOrWhiteSpace(hostContext.ManagerVersion));
            Assert.True(hostContext.IsReady);
            Assert.True(hostContext.HostStatusObserved);
            Assert.Equal(3, hostContext.ActiveExecCount);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GetHostContextAsyncMarksManagerStatusUnobservedOnRpcFailure()
    {
        string tempRoot = CreateTempDirectory();

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Kautuka:Mode"] = "dev",
                    ["Kautuka:HostPaths:VisibleRoot"] = Path.Combine(tempRoot, "visible"),
                    ["Kautuka:HostPaths:InternalStateRoot"] = Path.Combine(tempRoot, "runtime"),
                    ["Kautuka:HostPaths:LogsRoot"] = Path.Combine(tempRoot, "logs"),
                    ["Kautuka:ContainerStateRoot"] = Path.Combine(tempRoot, "runtime"),
                })
                .Build();

            var integration = new UpdateServiceIntegration(
                new UpdateServiceRuntimePaths(configuration),
                configuration,
                Options.Create(new UpdateOptions()),
                new ThrowingManagerStatusClient(),
                new TestHostApplicationLifetime(),
                NullLogger<UpdateServiceIntegration>.Instance);

            UpdateServiceContext hostContext = await integration.GetHostContextAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

            Assert.False(hostContext.HostStatusObserved);
            Assert.Equal(0, hostContext.ActiveExecCount);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyPreparedReleaseAsyncBlocksWhenManagerStatusIsUnobserved()
    {
        string tempRoot = CreateTempDirectory();

        try
        {
            var stateStore = new InMemoryUpdateStateStore(
                UpdateStateRecord.CreateDefault() with
                {
                    State = UpdateWorkflowState.Available,
                    AvailableRelease = new UpdateReleaseReference(
                        "rel-2",
                        "0.0.2",
                        "preview",
                        "fp",
                        null,
                        null,
                        null,
                        null,
                        null,
                        "v0.0.2")
                });

            var hostIntegration = new RecordingUpdateServiceIntegration(tempRoot, hostStatusObserved: false);
            var options = Options.Create(new UpdateOptions
            {
                Enabled = true,
                StateRootPath = tempRoot,
                RepositoryOwner = "kautuka",
                RepositoryName = "kautuka"
            });

            var coordinator = new UpdateCoordinator(
                stateStore,
                new InMemoryUpdateRuntimePolicyStore("preview"),
                new NullReleaseDiscoveryClient(NullLogger<NullReleaseDiscoveryClient>.Instance),
                new ServiceCollection().AddHttpClient().BuildServiceProvider().GetRequiredService<IHttpClientFactory>(),
                hostIntegration,
                new UpdateOperationHistoryStore(options),
                options,
                NullLogger<UpdateCoordinator>.Instance);

            UpdateStatusSnapshot status = await coordinator.ApplyPreparedReleaseAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

            Assert.Contains("Manager host status is unavailable", status.LastError, StringComparison.Ordinal);
            Assert.Null(hostIntegration.ActivatedReleaseId);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ActivatePreparedUpdateAsyncInvokesHelperAndDoesNotCopyGatewayDatabase()
    {
        string tempRoot = CreateTempDirectory();

        try
        {
            string helperPath = OperatingSystem.IsWindows()
                ? Path.Combine(tempRoot, "apply-release.cmd")
                : Path.Combine(tempRoot, "apply-release");
            if (OperatingSystem.IsWindows())
            {
                await File.WriteAllTextAsync(helperPath, "@echo off\r\necho helper %*\r\nexit /b 0\r\n", TestContext.Current.CancellationToken).ConfigureAwait(true);
            }
            else
            {
                await File.WriteAllTextAsync(helperPath, "#!/bin/sh\necho helper \"$@\"\nexit 0\n", TestContext.Current.CancellationToken).ConfigureAwait(true);
                File.SetUnixFileMode(helperPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            string artifactPath = Path.Combine(tempRoot, "kautuka-linux-amd64.tar.gz");
            await File.WriteAllBytesAsync(artifactPath, [0x1f, 0x8b], TestContext.Current.CancellationToken).ConfigureAwait(true);
            string dbPath = Path.Combine(tempRoot, "runtime", "kautuka.db");
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            await File.WriteAllTextAsync(dbPath, "gateway-owned", TestContext.Current.CancellationToken).ConfigureAwait(true);

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Kautuka:Mode"] = "dev",
                    ["Kautuka:HostPaths:VisibleRoot"] = Path.Combine(tempRoot, "visible"),
                    ["Kautuka:HostPaths:InternalStateRoot"] = Path.Combine(tempRoot, "runtime"),
                    ["Kautuka:HostPaths:LogsRoot"] = Path.Combine(tempRoot, "logs"),
                    ["Kautuka:ContainerStateRoot"] = Path.Combine(tempRoot, "runtime"),
                })
                .Build();

            var integration = new UpdateServiceIntegration(
                new UpdateServiceRuntimePaths(configuration),
                configuration,
                Options.Create(new UpdateOptions
                {
                    ApplyReleaseHelperPath = helperPath,
                    ApplyReleaseHelperUseSudo = false,
                    HostServiceActivationTimeout = TimeSpan.FromSeconds(15)
                }),
                new RecordingManagerStatusClient(new ManagerStatusResponse()),
                new TestHostApplicationLifetime(),
                NullLogger<UpdateServiceIntegration>.Instance);

            await integration.ActivatePreparedUpdateAsync(
                new UpdatePreparedArtifacts(
                    "0.0.2-preview.1",
                    artifactPath,
                    Path.Combine(tempRoot, "extracted")),
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            Assert.Equal("gateway-owned", await File.ReadAllTextAsync(dbPath, TestContext.Current.CancellationToken).ConfigureAwait(true));
            Assert.False(Directory.Exists(Path.Combine(tempRoot, "runtime", "updateService", "updates", "backups")));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void BuildSystemActivationPlanUsesExplicitRuntimeServiceRestartsForApply()
    {
        IReadOnlyList<string> plan = InvokeBuildSystemActivationPlan("apply");

        Assert.Equal(
            [
                "kautuka-credentialshost.service",
                "kautuka-manager.service",
                "kautuka-gateway.service",
                "kautuka-channelhost.service",
                "kautuka-webhost.service",
            ],
            plan);
    }

    [Fact]
    public void BuildSystemActivationPlanDoesNotRestartUpdateServiceDuringApply()
    {
        IReadOnlyList<string> plan = InvokeBuildSystemActivationPlan("apply");

        Assert.DoesNotContain("kautuka-updateservice.service", plan);
    }

    [Fact]
    public void BuildSystemActivationPlanRestartsUpdateServiceLastDuringRollback()
    {
        IReadOnlyList<string> plan = InvokeBuildSystemActivationPlan("rollback");

        Assert.Equal("kautuka-updateservice.service", plan[^1]);
    }

    [Fact]
    public async Task EnsureActivationLauncherAsyncUsesAbsoluteSudoPath()
    {
        string tempRoot = CreateTempDirectory();

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Kautuka:Mode"] = "dev",
                    ["Kautuka:HostPaths:VisibleRoot"] = Path.Combine(tempRoot, "visible"),
                    ["Kautuka:HostPaths:InternalStateRoot"] = Path.Combine(tempRoot, "runtime"),
                    ["Kautuka:HostPaths:LogsRoot"] = Path.Combine(tempRoot, "logs"),
                    ["Kautuka:ContainerStateRoot"] = Path.Combine(tempRoot, "runtime"),
                })
                .Build();

            var integration = new UpdateServiceIntegration(
                new UpdateServiceRuntimePaths(configuration),
                configuration,
                Options.Create(new UpdateOptions()),
                new RecordingManagerStatusClient(new ManagerStatusResponse()),
                new TestHostApplicationLifetime(),
                NullLogger<UpdateServiceIntegration>.Instance);

            string launcherPath = await InvokeEnsureActivationLauncherAsync(integration).ConfigureAwait(true);
            string launcherContents = await File.ReadAllTextAsync(launcherPath, TestContext.Current.CancellationToken).ConfigureAwait(true);

            Assert.Contains("exec /usr/bin/sudo -n \"$helper_path\" kautuka-updateservice.service", launcherContents, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SyncProvisionedConfigurationCopiesUpdateSourceAndManifestKeyFromStagedPayload()
    {
        string tempRoot = CreateTempDirectory();

        try
        {
            string extractedRoot = Path.Combine(tempRoot, "extracted");
            string provisionedRoot = Path.Combine(extractedRoot, "provisioning", "etc", "kautuka");
            string destinationRoot = Path.Combine(tempRoot, "etc", "kautuka");
            Directory.CreateDirectory(provisionedRoot);
            Directory.CreateDirectory(destinationRoot);

            string stagedUpdateSource = Path.Combine(provisionedRoot, "update-source.json");
            string stagedManifestKey = Path.Combine(provisionedRoot, "manifest-public-key.pem");
            string destinationUpdateSource = Path.Combine(destinationRoot, "update-source.json");
            string destinationManifestKey = Path.Combine(destinationRoot, "manifest-public-key.pem");

            WriteFile(stagedUpdateSource, "{\n  \"catalogManifestUrl\": \"https://catalog.example/manifest\"\n}\n");
            WriteFile(stagedManifestKey, "PUBLIC KEY\n");
            WriteFile(destinationUpdateSource, "old-source\n");
            WriteFile(destinationManifestKey, "old-key\n");

            InvokeSyncProvisionedConfiguration(extractedRoot, destinationUpdateSource, destinationManifestKey);

            Assert.Equal(
                await File.ReadAllTextAsync(stagedUpdateSource, TestContext.Current.CancellationToken).ConfigureAwait(true),
                await File.ReadAllTextAsync(destinationUpdateSource, TestContext.Current.CancellationToken).ConfigureAwait(true));
            Assert.Equal(
                await File.ReadAllTextAsync(stagedManifestKey, TestContext.Current.CancellationToken).ConfigureAwait(true),
                await File.ReadAllTextAsync(destinationManifestKey, TestContext.Current.CancellationToken).ConfigureAwait(true));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void EnsureProvisionedConfigurationWritableFailsWhenManagedDestinationCannotBeReplaced()
    {
        string tempRoot = CreateTempDirectory();
        string destinationUpdateSource = string.Empty;

        try
        {
            string extractedRoot = Path.Combine(tempRoot, "extracted");
            string provisionedRoot = Path.Combine(extractedRoot, "provisioning", "etc", "kautuka");
            string destinationRoot = Path.Combine(tempRoot, "etc", "kautuka");
            Directory.CreateDirectory(provisionedRoot);
            Directory.CreateDirectory(destinationRoot);

            string stagedUpdateSource = Path.Combine(provisionedRoot, "update-source.json");
            string stagedManifestKey = Path.Combine(provisionedRoot, "manifest-public-key.pem");
            destinationUpdateSource = Path.Combine(destinationRoot, "update-source.json");
            string destinationManifestKey = Path.Combine(destinationRoot, "manifest-public-key.pem");

            WriteFile(stagedUpdateSource, "{\n  \"catalogManifestUrl\": \"https://catalog.example/manifest\"\n}\n");
            WriteFile(stagedManifestKey, "PUBLIC KEY\n");
            WriteFile(destinationUpdateSource, "old-source\n");
            WriteFile(destinationManifestKey, "old-key\n");

            SetReadOnly(destinationUpdateSource, isReadOnly: true);

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => InvokeEnsureProvisionedConfigurationWritable(extractedRoot, destinationUpdateSource, destinationManifestKey));

            Assert.Contains(destinationUpdateSource, exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(destinationUpdateSource) && File.Exists(destinationUpdateSource))
            {
                SetReadOnly(destinationUpdateSource, isReadOnly: false);
            }

            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void IsExpectedRestartHandoffExitCodeTreatsSigTermAsExpected()
    {
        Assert.True(UpdateServiceIntegration.IsExpectedRestartHandoffExitCode(143));
        Assert.False(UpdateServiceIntegration.IsExpectedRestartHandoffExitCode(1));
        Assert.False(UpdateServiceIntegration.IsExpectedRestartHandoffExitCode(0));
    }

    [Theory]
    [InlineData("1.2.0", "1.2.0-preview.1", 1)]
    [InlineData("0.1.20-preview.9", "0.1.20", -1)]
    [InlineData("1.2.0-preview.2", "1.2.0-preview.1", 1)]
    [InlineData("1.2.0-preview.1", "1.2.0", -1)]
    [InlineData("1.2.0", "1.2.0", 0)]
    public void UpdateReleaseVersionComparerOrdersStableAndPrereleaseVersions(string left, string right, int expectedSign)
    {
        bool compared = UpdateReleaseVersionComparer.TryCompare(left, right, out int comparison);

        Assert.True(compared);
        Assert.Equal(expectedSign, Math.Sign(comparison));
    }

    [Fact]
    public async Task ApplyUpdatePolicyAsyncBlocksSwitchToStableUntilNewerStableExists()
    {
        var stateStore = new InMemoryUpdateStateStore(
            UpdateStateRecord.CreateDefault() with
            {
                CurrentRelease = CreateRelease("1.2.0-preview.2", "preview"),
                EffectiveReleaseChannel = "preview",
            });

        var runtimePolicyStore = new InMemoryUpdateRuntimePolicyStore("preview");
        var discoveryClient = new StubReleaseDiscoveryClient(_ => ReleaseDiscoveryResult.Available(CreateRelease("1.1.9", "stable")));
        var coordinator = CreateCoordinator(
            stateStore,
            runtimePolicyStore,
            discoveryClient,
            managerVersion: "1.2.0-preview.2");

        UpdateStatusSnapshot status = await coordinator.ApplyUpdatePolicyAsync("stable", TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal("stable", status.EffectiveReleaseChannel);
        Assert.Equal(UpdateEligibilityState.Blocked, status.EligibilityState);
        Assert.Equal(UpdateEligibilityReason.NewerStableRequired, status.EligibilityReason);
        Assert.Null(status.AvailableRelease);
    }

    [Fact]
    public async Task ApplyUpdatePolicyAsyncBlocksSwitchToPreviewWhenNoNewerPreviewExists()
    {
        var stateStore = new InMemoryUpdateStateStore(
            UpdateStateRecord.CreateDefault() with
            {
                CurrentRelease = CreateRelease("1.2.0", "stable"),
                EffectiveReleaseChannel = "stable",
            });

        var runtimePolicyStore = new InMemoryUpdateRuntimePolicyStore("stable");
        var discoveryClient = new StubReleaseDiscoveryClient(_ => ReleaseDiscoveryResult.Available(CreateRelease("1.2.0-preview.1", "preview")));
        var coordinator = CreateCoordinator(
            stateStore,
            runtimePolicyStore,
            discoveryClient,
            managerVersion: "1.2.0");

        UpdateStatusSnapshot status = await coordinator.ApplyUpdatePolicyAsync("preview", TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal("preview", status.EffectiveReleaseChannel);
        Assert.Equal(UpdateEligibilityState.Blocked, status.EligibilityState);
        Assert.Equal(UpdateEligibilityReason.NoNewerPreview, status.EligibilityReason);
        Assert.Null(status.AvailableRelease);
    }

    [Fact]
    public async Task ApplyUpdatePolicyAsyncDoesNotOfferAReplayedOlderPreviewPointer()
    {
        var stateStore = new InMemoryUpdateStateStore(
            UpdateStateRecord.CreateDefault() with
            {
                CurrentRelease = CreateRelease("1.2.0-preview.4", "preview"),
                EffectiveReleaseChannel = "preview",
            });

        var runtimePolicyStore = new InMemoryUpdateRuntimePolicyStore("preview");
        var discoveryClient = new StubReleaseDiscoveryClient(_ => ReleaseDiscoveryResult.Available(CreateRelease("1.2.0-preview.3", "preview")));
        var coordinator = CreateCoordinator(
            stateStore,
            runtimePolicyStore,
            discoveryClient,
            managerVersion: "1.2.0-preview.4");

        UpdateStatusSnapshot status = await coordinator.ApplyUpdatePolicyAsync("preview", TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(UpdateEligibilityState.None, status.EligibilityState);
        Assert.Equal(UpdateEligibilityReason.AlreadyCurrent, status.EligibilityReason);
        Assert.Null(status.AvailableRelease);
    }

    [Fact]
    public async Task ApplyUpdatePolicyAsyncMarksNewerReleaseAvailableWhenTrackHasHigherVersion()
    {
        var stateStore = new InMemoryUpdateStateStore(
            UpdateStateRecord.CreateDefault() with
            {
                CurrentRelease = CreateRelease("1.2.0-preview.2", "preview"),
                EffectiveReleaseChannel = "preview",
            });

        var runtimePolicyStore = new InMemoryUpdateRuntimePolicyStore("preview");
        var candidateRelease = CreateRelease("1.2.0", "stable");
        var discoveryClient = new StubReleaseDiscoveryClient(_ => ReleaseDiscoveryResult.Available(candidateRelease));
        var coordinator = CreateCoordinator(
            stateStore,
            runtimePolicyStore,
            discoveryClient,
            managerVersion: "1.2.0-preview.2");

        UpdateStatusSnapshot status = await coordinator.ApplyUpdatePolicyAsync("stable", TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(UpdateEligibilityState.Available, status.EligibilityState);
        Assert.Equal(UpdateEligibilityReason.None, status.EligibilityReason);
        Assert.Equal(candidateRelease.Version, status.AvailableRelease?.Version);
    }

    /// <summary>
    /// Apply rejects any asset URL that is not https or loopback http, but discovery used to
    /// ignore the URLs entirely. A feed published over plain http on a LAN address was
    /// advertised as Available and then failed at apply, so the appliance sat offering an
    /// update it could never install.
    /// </summary>
    [Fact]
    public async Task RefreshDiscoveryAsyncBlocksReleaseWhoseAssetUrlsApplyWouldReject()
    {
        var stateStore = new InMemoryUpdateStateStore(
            UpdateStateRecord.CreateDefault() with
            {
                CurrentRelease = CreateRelease("1.2.0", "stable"),
                EffectiveReleaseChannel = "stable",
            });

        UpdateReleaseReference candidateRelease = CreateRelease("1.3.0", "stable") with
        {
            PlatformReleaseAssetUrl = "http://192.168.1.20:20300/kautuka-platform-release.json",
            PlatformReleaseSignatureAssetUrl = "http://192.168.1.20:20300/kautuka-platform-release.sig",
        };

        var runtimePolicyStore = new InMemoryUpdateRuntimePolicyStore("stable");
        var discoveryClient = new StubReleaseDiscoveryClient(_ => ReleaseDiscoveryResult.Available(candidateRelease));
        var coordinator = CreateCoordinator(
            stateStore,
            runtimePolicyStore,
            discoveryClient,
            managerVersion: "1.2.0");

        await coordinator.RefreshDiscoveryAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        UpdateStatusSnapshot status = await coordinator.GetStatusAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(UpdateEligibilityState.Blocked, status.EligibilityState);
        Assert.Equal(UpdateEligibilityReason.UntrustedSourceUrl, status.EligibilityReason);

        // Nothing to offer, but the reason has to reach the operator.
        Assert.Null(status.AvailableRelease);
        Assert.Contains("must be https or loopback http", status.LastError, StringComparison.Ordinal);
        Assert.Contains("192.168.1.20", status.LastError, StringComparison.Ordinal);
    }

    /// <summary>
    /// The counterpart to the block above: a loopback feed is how the appliance is tested and
    /// how <c>local-release.sh</c> is meant to be pointed, so it must stay offerable.
    /// </summary>
    [Fact]
    public async Task RefreshDiscoveryAsyncOffersReleaseServedOverLoopbackHttp()
    {
        var stateStore = new InMemoryUpdateStateStore(
            UpdateStateRecord.CreateDefault() with
            {
                CurrentRelease = CreateRelease("1.2.0", "stable"),
                EffectiveReleaseChannel = "stable",
            });

        UpdateReleaseReference candidateRelease = CreateRelease("1.3.0", "stable") with
        {
            PlatformReleaseAssetUrl = "http://127.0.0.1:20300/kautuka-platform-release.json",
            PlatformReleaseSignatureAssetUrl = "http://127.0.0.1:20300/kautuka-platform-release.sig",
        };

        var runtimePolicyStore = new InMemoryUpdateRuntimePolicyStore("stable");
        var discoveryClient = new StubReleaseDiscoveryClient(_ => ReleaseDiscoveryResult.Available(candidateRelease));
        var coordinator = CreateCoordinator(
            stateStore,
            runtimePolicyStore,
            discoveryClient,
            managerVersion: "1.2.0");

        await coordinator.RefreshDiscoveryAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        UpdateStatusSnapshot status = await coordinator.GetStatusAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(UpdateEligibilityState.Available, status.EligibilityState);
        Assert.Equal(UpdateEligibilityReason.None, status.EligibilityReason);
        Assert.Equal("1.3.0", status.AvailableRelease?.Version);
        Assert.Null(status.LastError);
    }

    [Fact]
    public async Task ApplyUpdatePolicyAsyncAllowsManifestConfiguredDiscoveryWithoutGitHubRepositoryFields()
    {
        var stateStore = new InMemoryUpdateStateStore(UpdateStateRecord.CreateDefault());
        var runtimePolicyStore = new InMemoryUpdateRuntimePolicyStore("preview");
        UpdateReleaseReference candidateRelease = CreateRelease("0.1.1-preview.20260513052829", "preview");
        var discoveryClient = new StubReleaseDiscoveryClient(_ => ReleaseDiscoveryResult.Available(candidateRelease));

        var services = new ServiceCollection();
        services.AddHttpClient("kautuka.updateservice.github");
        using ServiceProvider provider = services.BuildServiceProvider();
        IHttpClientFactory httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();

        var updateOptions = Options.Create(new UpdateOptions
        {
            Enabled = true,
            StateRootPath = CreateTempDirectory(),
            ReleaseManifestUrl = "http://192.168.1.12:20300/manifest",
            RepositoryOwner = null,
            RepositoryName = null,
        });
        var coordinator = new UpdateCoordinator(
            stateStore,
            runtimePolicyStore,
            discoveryClient,
            httpClientFactory,
            new RecordingUpdateServiceIntegration(CreateTempDirectory(), "0.1.1-preview.20260513051143"),
            new UpdateOperationHistoryStore(updateOptions),
            updateOptions,
            NullLogger<UpdateCoordinator>.Instance);

        UpdateStatusSnapshot status = await coordinator.ApplyUpdatePolicyAsync("preview", TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(UpdateEligibilityState.Available, status.EligibilityState);
        Assert.Equal(UpdateEligibilityReason.None, status.EligibilityReason);
        Assert.Equal(candidateRelease.Version, status.AvailableRelease?.Version);
        Assert.Null(status.LastError);
    }

    [Fact]
    public async Task GetStatusAsyncClearsStaleGitHubDiscoveryErrorWhenManifestSourceIsConfigured()
    {
        var stateStore = new InMemoryUpdateStateStore(
            UpdateStateRecord.CreateDefault() with
            {
                CurrentRelease = CreateRelease("1.0.0+0f3c0f36394d7993c505747f29677e305c9519a7", "preview"),
                LastError = "GitHub release discovery failed with HTTP 404.",
                LastDiscoveryAttemptUtc = DateTimeOffset.UtcNow.AddHours(-12),
                SourceRepositoryOwner = "devkrity",
                SourceRepositoryName = "kautuka",
            });

        var services = new ServiceCollection();
        services.AddHttpClient("kautuka.updateservice.github");
        using ServiceProvider provider = services.BuildServiceProvider();
        IHttpClientFactory httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();

        var updateOptions = Options.Create(new UpdateOptions
        {
            Enabled = true,
            StateRootPath = CreateTempDirectory(),
            RepositoryOwner = "devkrity",
            RepositoryName = "kautuka",
            ReleaseManifestUrl = "http://127.0.0.1:20300/manifest",
        });
        var coordinator = new UpdateCoordinator(
            stateStore,
            new InMemoryUpdateRuntimePolicyStore("preview"),
            new StubReleaseDiscoveryClient(_ => ReleaseDiscoveryResult.NoRelease()),
            httpClientFactory,
            new RecordingUpdateServiceIntegration(CreateTempDirectory(), "1.0.0+0f3c0f36394d7993c505747f29677e305c9519a7"),
            new UpdateOperationHistoryStore(updateOptions),
            updateOptions,
            NullLogger<UpdateCoordinator>.Instance);

        UpdateStatusSnapshot status = await coordinator.GetStatusAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Null(status.LastError);
        Assert.Null(status.AvailableRelease);
    }

    [Fact]
    public async Task GetStatusAsyncClearsRollbackStateWhenObservedHostVersionChangedOutOfBand()
    {
        var stateStore = new InMemoryUpdateStateStore(
            UpdateStateRecord.CreateDefault() with
            {
                State = UpdateWorkflowState.Current,
                CurrentRelease = CreateRelease("0.1.1-preview.20260513043308", "preview"),
                LastKnownGoodRelease = CreateRelease("0.1.1-preview.20260513035438", "preview"),
            });

        var coordinator = CreateCoordinator(
            stateStore,
            new InMemoryUpdateRuntimePolicyStore("preview"),
            new StubReleaseDiscoveryClient(_ => ReleaseDiscoveryResult.NoRelease()),
            managerVersion: "0.1.1-preview.20260513051143");

        UpdateStatusSnapshot status = await coordinator.GetStatusAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(UpdateWorkflowState.Current, status.State);
        Assert.Equal("0.1.1-preview.20260513051143", status.CurrentRelease?.Version);
        Assert.Equal("0.1.1-preview.20260513035438", status.LastKnownGoodRelease?.Version);
        Assert.Null(status.AvailableRelease);
        Assert.Null(status.LastError);
    }

    [Fact]
    public async Task GetStatusAsyncPromotesPreviousCurrentReleaseToRollbackTargetWhenHostVersionChangedOutOfBand()
    {
        var stateStore = new InMemoryUpdateStateStore(
            UpdateStateRecord.CreateDefault() with
            {
                State = UpdateWorkflowState.Current,
                CurrentRelease = CreateRelease("0.1.1-preview.20260513043308", "preview"),
                LastKnownGoodRelease = null,
            });

        var coordinator = CreateCoordinator(
            stateStore,
            new InMemoryUpdateRuntimePolicyStore("preview"),
            new StubReleaseDiscoveryClient(_ => ReleaseDiscoveryResult.NoRelease()),
            managerVersion: "0.1.1-preview.20260513051143");

        UpdateStatusSnapshot status = await coordinator.GetStatusAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(UpdateWorkflowState.Current, status.State);
        Assert.Equal("0.1.1-preview.20260513051143", status.CurrentRelease?.Version);
        Assert.Equal("0.1.1-preview.20260513051143", status.CurrentRelease?.ReleaseId);
        Assert.Equal("0.1.1-preview.20260513043308", status.LastKnownGoodRelease?.Version);
        Assert.Null(status.AvailableRelease);
        Assert.Null(status.LastError);
    }

    [Fact]
    public async Task GetStatusAsyncUsesManagerReleaseIdWhenItDiffersFromManagerVersion()
    {
        var stateStore = new InMemoryUpdateStateStore(UpdateStateRecord.CreateDefault());

        var services = new ServiceCollection();
        services.AddHttpClient("kautuka.updateservice.github");
        using ServiceProvider provider = services.BuildServiceProvider();
        IHttpClientFactory httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();

        var updateOptions = Options.Create(new UpdateOptions
        {
            Enabled = true,
            StateRootPath = CreateTempDirectory(),
            RepositoryOwner = "kautuka",
            RepositoryName = "kautuka",
        });
        var coordinator = new UpdateCoordinator(
            stateStore,
            new InMemoryUpdateRuntimePolicyStore("preview"),
            new StubReleaseDiscoveryClient(_ => ReleaseDiscoveryResult.NoRelease()),
            httpClientFactory,
            new RecordingUpdateServiceIntegration(
                CreateTempDirectory(),
                managerVersion: "0.1.1-preview.20260524143637",
                managerReleaseId: "local-feature-autopilot-enhancements-20260524143637"),
            new UpdateOperationHistoryStore(updateOptions),
            updateOptions,
            NullLogger<UpdateCoordinator>.Instance);

        UpdateStatusSnapshot status = await coordinator.GetStatusAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal("local-feature-autopilot-enhancements-20260524143637", status.CurrentRelease?.ReleaseId);
        Assert.Equal("0.1.1-preview.20260524143637", status.CurrentRelease?.Version);
    }

    [Fact]
    public async Task GetStatusAsyncFinalizesApplyingReleaseWhenTargetVersionIsRunning()
    {
        UpdateReleaseReference current = CreateRelease("0.1.0", "preview");
        UpdateReleaseReference target = CreateRelease("0.2.0", "preview");
        var stateStore = new InMemoryUpdateStateStore(
            UpdateStateRecord.CreateDefault() with
            {
                State = UpdateWorkflowState.Applying,
                StateChangedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                CurrentRelease = current,
                StagedRelease = target,
            });

        var coordinator = CreateCoordinator(
            stateStore,
            new InMemoryUpdateRuntimePolicyStore("preview"),
            new StubReleaseDiscoveryClient(_ => ReleaseDiscoveryResult.NoRelease()),
            managerVersion: target.Version);

        UpdateStatusSnapshot status = await coordinator.GetStatusAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(UpdateWorkflowState.Current, status.State);
        Assert.Equal(target.Version, status.CurrentRelease?.Version);

        UpdateStateRecord persisted = await stateStore.GetAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Null(persisted.StagedRelease);
        Assert.Null(persisted.PreparedArtifacts);
        Assert.Null(persisted.LastError);
    }

    [Fact]
    public async Task GetStatusAsyncRecordsSuccessfulApplyHistoryWithPreviousCurrentVersion()
    {
        UpdateReleaseReference current = CreateRelease("0.1.0", "preview");
        UpdateReleaseReference target = CreateRelease("0.2.0", "preview");
        var stateStore = new InMemoryUpdateStateStore(
            UpdateStateRecord.CreateDefault() with
            {
                State = UpdateWorkflowState.Applying,
                StateChangedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                CurrentRelease = current,
                StagedRelease = target,
            });

        var services = new ServiceCollection();
        services.AddHttpClient("kautuka.updateservice.github");
        using ServiceProvider provider = services.BuildServiceProvider();
        IHttpClientFactory httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();

        string tempRoot = CreateTempDirectory();
        var updateOptions = Options.Create(new UpdateOptions
        {
            Enabled = true,
            HostServiceActivationTimeout = TimeSpan.FromMinutes(2),
            StateRootPath = tempRoot,
            RepositoryOwner = "kautuka",
            RepositoryName = "kautuka",
        });
        var operationStore = new UpdateOperationHistoryStore(updateOptions);
        var coordinator = new UpdateCoordinator(
            stateStore,
            new InMemoryUpdateRuntimePolicyStore("preview"),
            new StubReleaseDiscoveryClient(_ => ReleaseDiscoveryResult.NoRelease()),
            httpClientFactory,
            new RecordingUpdateServiceIntegration(CreateTempDirectory(), target.Version),
            operationStore,
            updateOptions,
            NullLogger<UpdateCoordinator>.Instance);

        UpdateStatusSnapshot status = await coordinator.GetStatusAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(UpdateWorkflowState.Current, status.State);

        IReadOnlyList<UpdateOperationRecord> history = await operationStore.GetRecentAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        UpdateOperationRecord success = Assert.Single(history);
        Assert.Equal("apply", success.OperationType);
        Assert.True(success.Succeeded);
        Assert.Equal(current.Version, success.FromVersion);
        Assert.Equal(target.Version, success.ToVersion);
    }

    [Fact]
    public async Task GetStatusAsyncClearsInactiveStagedReleaseThatMatchesCurrentVersion()
    {
        UpdateReleaseReference current = CreateRelease("0.2.0", "preview");
        UpdateReleaseReference available = CreateRelease("0.3.0", "preview");
        var stateStore = new InMemoryUpdateStateStore(
            UpdateStateRecord.CreateDefault() with
            {
                State = UpdateWorkflowState.Available,
                CurrentRelease = current,
                AvailableRelease = available,
                StagedRelease = current,
                PreparedArtifacts = new UpdatePreparedArtifacts(
                    TargetReleaseId: current.ReleaseId,
                    PlatformPackagePath: "/tmp/kautuka-package.tar.gz",
                    PlatformExtractedPath: "/tmp/kautuka-extract")
            });

        var coordinator = CreateCoordinator(
            stateStore,
            new InMemoryUpdateRuntimePolicyStore("preview"),
            new StubReleaseDiscoveryClient(_ => ReleaseDiscoveryResult.NoRelease()),
            managerVersion: current.Version);

        UpdateStatusSnapshot status = await coordinator.GetStatusAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(available.Version, status.AvailableRelease?.Version);

        UpdateStateRecord persisted = await stateStore.GetAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Null(persisted.StagedRelease);
        Assert.Null(persisted.PreparedArtifacts);
    }

    [Fact]
    public async Task ApplyUpdatePolicyAsyncClearsStaleStagedReleaseWhenDiscoveryFindsNewerAvailableRelease()
    {
        UpdateReleaseReference current = CreateRelease("0.2.0", "preview");
        UpdateReleaseReference staleStaged = CreateRelease("0.2.1", "preview");
        UpdateReleaseReference discovered = CreateRelease("0.3.0", "preview");
        var stateStore = new InMemoryUpdateStateStore(
            UpdateStateRecord.CreateDefault() with
            {
                State = UpdateWorkflowState.Available,
                CurrentRelease = current,
                AvailableRelease = staleStaged,
                StagedRelease = staleStaged,
                PreparedArtifacts = new UpdatePreparedArtifacts(
                    TargetReleaseId: staleStaged.ReleaseId,
                    PlatformPackagePath: "/tmp/kautuka-package.tar.gz",
                    PlatformExtractedPath: "/tmp/kautuka-extract")
            });

        var coordinator = CreateCoordinator(
            stateStore,
            new InMemoryUpdateRuntimePolicyStore("preview"),
            new StubReleaseDiscoveryClient(_ => ReleaseDiscoveryResult.Available(discovered)),
            managerVersion: current.Version);

        UpdateStatusSnapshot status = await coordinator.ApplyUpdatePolicyAsync("preview", TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(discovered.Version, status.AvailableRelease?.Version);

        UpdateStateRecord persisted = await stateStore.GetAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(discovered.Version, persisted.AvailableRelease?.Version);
        Assert.Null(persisted.StagedRelease);
        Assert.Null(persisted.PreparedArtifacts);
    }

    [Fact]
    public async Task GetStatusAsyncRecoversLegacyStuckApplyingStateBackToAvailable()
    {
        UpdateReleaseReference current = CreateRelease("0.1.0", "preview");
        UpdateReleaseReference target = CreateRelease("0.2.0", "preview");
        var stateStore = new InMemoryUpdateStateStore(
            UpdateStateRecord.CreateDefault() with
            {
                State = UpdateWorkflowState.Applying,
                CurrentRelease = current,
                AvailableRelease = target,
                StagedRelease = target,
                PreparedArtifacts = new UpdatePreparedArtifacts(
                    TargetReleaseId: target.ReleaseId,
                    PlatformPackagePath: "/tmp/kautuka-package.tar.gz",
                    PlatformExtractedPath: "/tmp/kautuka-extract")
            });

        var coordinator = CreateCoordinator(
            stateStore,
            new InMemoryUpdateRuntimePolicyStore("preview"),
            new StubReleaseDiscoveryClient(_ => ReleaseDiscoveryResult.NoRelease()),
            managerVersion: current.Version,
            activationTimeout: TimeSpan.FromMinutes(1));

        UpdateStatusSnapshot status = await coordinator.GetStatusAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(UpdateWorkflowState.Available, status.State);
        Assert.Equal(target.Version, status.AvailableRelease?.Version);
        Assert.Contains("can be retried", status.LastError, StringComparison.OrdinalIgnoreCase);

        UpdateStateRecord persisted = await stateStore.GetAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Null(persisted.StagedRelease);
        Assert.Null(persisted.PreparedArtifacts);
        Assert.Equal(UpdateWorkflowState.Available, persisted.State);
    }

    private static UpdateCoordinator CreateCoordinator(
        IUpdateStateStore stateStore,
        IUpdateRuntimePolicyStore runtimePolicyStore,
        IReleaseDiscoveryClient discoveryClient,
        string managerVersion,
        string? managerReleaseId = null,
        TimeSpan? activationTimeout = null)
    {
        var services = new ServiceCollection();
        services.AddHttpClient("kautuka.updateservice.github");
        ServiceProvider provider = services.BuildServiceProvider();
        IHttpClientFactory httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();

        var updateOptions = Options.Create(new UpdateOptions
        {
            Enabled = true,
            HostServiceActivationTimeout = activationTimeout ?? TimeSpan.FromMinutes(2),
            StateRootPath = CreateTempDirectory(),
            RepositoryOwner = "kautuka",
            RepositoryName = "kautuka",
        });
        return new UpdateCoordinator(
            stateStore,
            runtimePolicyStore,
            discoveryClient,
            httpClientFactory,
            new RecordingUpdateServiceIntegration(CreateTempDirectory(), managerVersion, managerReleaseId),
            new UpdateOperationHistoryStore(updateOptions),
            updateOptions,
            NullLogger<UpdateCoordinator>.Instance);
    }

    private static UpdateReleaseReference CreateRelease(string version, string channel)
        => new(
            ReleaseId: $"release-{version}",
            Version: version,
            Channel: channel,
            ReleaseFingerprint: $"fingerprint-{version}",
            PlatformArtifactUrl: null,
            PlatformReleaseAssetUrl: null,
            PlatformReleaseSignatureAssetUrl: null,
            SourceUrl: "https://example.test/manifest",
            PublishedAtUtc: DateTimeOffset.UtcNow,
            Tag: version);

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"kautuka-update-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "Kautuka.UpdateService.Tests";

        public string ContentRootPath { get; set; } = Path.GetTempPath();

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }

    private sealed class RecordingManagerStatusClient(ManagerStatusResponse response) : IManagerStatusClient
    {
        public Task<ManagerStatusResponse> GetHostStatusAsync(CancellationToken cancellationToken)
            => Task.FromResult(response);
    }

    private static string CreateVersionedInstall(string applianceRoot, string serviceKey, string releaseId, string executableName, string markerContent)
    {
        string serviceRoot = Path.Combine(applianceRoot, serviceKey);
        string versionRoot = Path.Combine(serviceRoot, "versions", releaseId);
        string currentRoot = Path.Combine(serviceRoot, "current");

        WriteFile(Path.Combine(versionRoot, executableName), markerContent);
        Directory.CreateDirectory(serviceRoot);
        try
        {
            Directory.CreateSymbolicLink(currentRoot, versionRoot);
        }
        catch (IOException ex) when (OperatingSystem.IsWindows()
            && ex.Message.Contains("required privilege", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Skip("Directory symlink tests require Windows Developer Mode or Create symbolic links privilege.");
            throw;
        }
        return currentRoot;
    }

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string ConvertManifestPrivateKeyToPublicKeyPem(string privateKeyPem)
    {
        using var ecdsa = System.Security.Cryptography.ECDsa.Create();
        ecdsa.ImportFromPem(privateKeyPem);
        byte[] publicKey = ecdsa.ExportSubjectPublicKeyInfo();
        string base64 = Convert.ToBase64String(publicKey, Base64FormattingOptions.InsertLineBreaks);
        return $"-----BEGIN PUBLIC KEY-----\n{base64}\n-----END PUBLIC KEY-----\n";
    }

    private static List<string> InvokeBuildSystemActivationPlan(string operation)
    {
        MethodInfo method = typeof(UpdateServiceIntegration)
            .GetMethod("BuildSystemActivationPlan", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("BuildSystemActivationPlan method was not found.");

        object? result = method.Invoke(null, [operation, false]);
        var commands = Assert.IsAssignableFrom<System.Collections.IEnumerable>(result);
        List<string> plan = [];

        foreach (object command in commands)
        {
            plan.Add(Assert.IsType<string>(command));
        }

        return plan;
    }

    private static async Task<string> InvokeEnsureActivationLauncherAsync(UpdateServiceIntegration integration)
    {
        MethodInfo method = typeof(UpdateServiceIntegration)
            .GetMethod("EnsureActivationLauncherAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("EnsureActivationLauncherAsync method was not found.");

        object? result = method.Invoke(integration, [TestContext.Current.CancellationToken]);
        ValueTask<string> task = Assert.IsType<ValueTask<string>>(result);
        return await task.ConfigureAwait(true);
    }

    private static void InvokeSyncProvisionedConfiguration(string extractedRoot, string destinationUpdateSource, string destinationManifestKey)
    {
        MethodInfo method = typeof(UpdateServiceIntegration)
            .GetMethod("SyncProvisionedConfiguration", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SyncProvisionedConfiguration method was not found.");

        method.Invoke(null, [extractedRoot, destinationUpdateSource, destinationManifestKey, NullLogger.Instance]);
    }

    private static void InvokeEnsureProvisionedConfigurationWritable(string extractedRoot, string destinationUpdateSource, string destinationManifestKey)
    {
        MethodInfo method = typeof(UpdateServiceIntegration)
            .GetMethod("EnsureProvisionedConfigurationWritable", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("EnsureProvisionedConfigurationWritable method was not found.");

        try
        {
            method.Invoke(null, [extractedRoot, destinationUpdateSource, destinationManifestKey, NullLogger.Instance]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static void SetReadOnly(string path, bool isReadOnly)
    {
        if (OperatingSystem.IsWindows())
        {
            FileAttributes attributes = File.GetAttributes(path);
            File.SetAttributes(
                path,
                isReadOnly
                    ? attributes | FileAttributes.ReadOnly
                    : attributes & ~FileAttributes.ReadOnly);
            return;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(
                path,
                isReadOnly
                    ? UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    private sealed class InMemoryUpdateStateStore(UpdateStateRecord state) : IUpdateStateStore
    {
        private UpdateStateRecord current = state;

        public ValueTask<UpdateStateRecord> GetAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(current);

        public ValueTask SaveAsync(UpdateStateRecord state, CancellationToken cancellationToken = default)
        {
            current = state;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InMemoryUpdateRuntimePolicyStore(string initialReleaseChannel) : IUpdateRuntimePolicyStore
    {
        private UpdateRuntimePolicy current = UpdateRuntimePolicy.CreateDefault(initialReleaseChannel);

        public ValueTask<UpdateRuntimePolicy> GetAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(current);

        public ValueTask<UpdateRuntimePolicy> SetReleaseChannelAsync(string releaseChannel, CancellationToken cancellationToken = default)
        {
            current = new UpdateRuntimePolicy(current.SchemaVersion, UpdateRuntimePolicy.NormalizeReleaseChannel(releaseChannel));
            return ValueTask.FromResult(current);
        }
    }

    private sealed class StubReleaseDiscoveryClient(Func<ReleaseDiscoveryRequest, ReleaseDiscoveryResult> resolver) : IReleaseDiscoveryClient
    {
        public Task<ReleaseDiscoveryResult> DiscoverAsync(ReleaseDiscoveryRequest request, CancellationToken cancellationToken)
            => Task.FromResult(resolver(request));
    }

    private sealed class ThrowingManagerStatusClient : IManagerStatusClient
    {
        public Task<ManagerStatusResponse> GetHostStatusAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("manager unavailable");
    }

    private sealed class RecordingUpdateServiceIntegration(
        string runtimeRoot,
        string managerVersion = "0.0.0-test",
        string? managerReleaseId = null,
        bool hostStatusObserved = true) : IUpdateServiceIntegration
    {
        public string? ActivatedReleaseId { get; private set; }
        public string ActivatedPlatformPackagePath { get; private set; } = string.Empty;
        public string ActivatedPlatformExtractedPath { get; private set; } = string.Empty;

        public ValueTask<UpdateServiceContext> GetHostContextAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(new UpdateServiceContext(runtimeRoot, runtimeRoot, managerReleaseId ?? managerVersion, managerVersion, true, 0, hostStatusObserved));

        public ValueTask ActivatePreparedUpdateAsync(UpdatePreparedArtifacts preparedArtifacts, CancellationToken cancellationToken)
        {
            ActivatedReleaseId = preparedArtifacts.TargetReleaseId;
            ActivatedPlatformPackagePath = preparedArtifacts.PlatformPackagePath;
            ActivatedPlatformExtractedPath = preparedArtifacts.PlatformExtractedPath;
            return ValueTask.CompletedTask;
        }

        public ValueTask ActivateRollbackAsync(UpdateReleaseReference release, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask<bool> TryContinuePendingActivationAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(false);

        public ValueTask RequestSupervisorReconcileAsync(CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }
}
