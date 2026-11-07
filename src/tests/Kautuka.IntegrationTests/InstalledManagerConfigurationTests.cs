using Kautuka.Hosting.Hosting;

using Microsoft.Extensions.Configuration;

namespace Kautuka.IntegrationTests;

public sealed class InstalledManagerConfigurationTests
{
    [Theory]
    [InlineData("host", null, true)]
    [InlineData("containers", null, false)]
    [InlineData("containers", "{\"CoreServices\":{\"Enabled\":true}}", false)]
    [InlineData("containers", "{\"ContainerEgress\":{\"Enabled\":true}}", false)]
    [InlineData("containers", "{\"CoreServices\":{\"Enabled\":true},\"ContainerEgress\":{\"Enabled\":true}}", true)]
    [InlineData("unknown", null, false)]
    public void ContainerContractRequiresPersistentEnabledSettings(string mode, string? settings, bool succeeds)
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-installed-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string settingsPath = Path.Combine(root, "manager.json");
            string contractPath = Path.Combine(root, "service-scope.json");
            File.WriteAllText(contractPath, "{\"applicationServices\":\"" + mode + "\"}");
            if (settings is not null) File.WriteAllText(settingsPath, settings);
            using var configuration = new ConfigurationManager();
            // Earlier providers cannot stand in for a missing installed file.
            configuration["CoreServices:Enabled"] = "true";
            configuration["ContainerEgress:Enabled"] = "true";
            if (succeeds) KautukaInstalledDeploymentConfiguration.Add(configuration, settingsPath, contractPath);
            else Assert.Throws<InvalidOperationException>(() => KautukaInstalledDeploymentConfiguration.Add(configuration, settingsPath, contractPath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
