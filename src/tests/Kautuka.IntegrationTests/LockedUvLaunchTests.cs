using System.IO.Compression;
using System.Reflection;

using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class LockedUvLaunchTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PreparedBundleKeepsUvLauncherAndForcesNoSyncOnlyWhenLocked(bool locked)
    {
        string path = Path.Combine(Path.GetTempPath(), "kautuka-uv-launch-" + Guid.NewGuid().ToString("N") + ".mcpb");
        try
        {
            using (ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                archive.CreateEntry("pyproject.toml");
                if (locked) archive.CreateEntry("uv.lock");
            }
            var connector = new WorkspaceMcpContainerConnectorDefinition("owner::web", "base", "uv",
                ["run", "python", "-m", "kautuka_web_tools.server"], true, false, path, "uv",
                [new("UV_NO_SYNC", "0"), new("UV_PROJECT_ENVIRONMENT", "/wrong"), new("OTHER", "${MCPB_ROOT}/data")], 60, 600);
            WorkspaceMcpContainerConnectorDefinition runtime = AgentSandboxInstallContract.ApplyRuntime(connector, "localhost/prepared", true);
            var environment = runtime.Environment.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(locked ? "1" : "0", environment["UV_NO_SYNC"]);
            Assert.Equal(locked ? "/opt/kautuka/mcpb/.venv" : "/wrong", environment["UV_PROJECT_ENVIRONMENT"]);
            Assert.Equal(locked ? "uv" : "/opt/kautuka/mcpb/.venv/bin/python", runtime.Command);
            if (locked) Assert.Equal(connector.Args, runtime.Args);

            var sandboxEnvironment = AgentSandboxInstallContract.PrepareEnvironment(connector, "/agent/bundle");
            var sandbox = sandboxEnvironment.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(locked ? "1" : "0", sandbox["UV_NO_SYNC"]);
            Assert.Equal(locked ? "/agent/bundle/.venv" : "/wrong", sandbox["UV_PROJECT_ENVIRONMENT"]);
            (string command, _) = AgentSandboxInstallContract.RewriteLaunch("uv", "uv", connector.Args,
                "/agent/bundle", true, AgentSandboxInstallContract.HasLockedUvBundle(connector));
            Assert.Equal(locked ? "uv" : "/agent/bundle/.venv/bin/python", command);
            var contract = new WorkspaceContainerConnectorExecutionService.AgentSandboxInstalledContract(
                connector.ConnectorId, "digest", "image", "uv", "/agent/bundle", command, connector.Args,
                sandboxEnvironment, "/agent/bundle", 4, locked);
            MethodInfo apply = typeof(WorkspaceContainerConnectorExecutionService).GetMethod("ApplyInstalledAgentSandboxContract", BindingFlags.NonPublic | BindingFlags.Static)!;
            var merged = (WorkspaceMcpContainerConnectorDefinition)apply.Invoke(null, [connector, contract])!;
            var finalEnvironment = merged.Environment.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(locked ? "1" : "0", finalEnvironment["UV_NO_SYNC"]);
            Assert.Equal(locked ? "/agent/bundle/.venv" : "/wrong", finalEnvironment["UV_PROJECT_ENVIRONMENT"]);
        }
        finally { File.Delete(path); }
    }
}
