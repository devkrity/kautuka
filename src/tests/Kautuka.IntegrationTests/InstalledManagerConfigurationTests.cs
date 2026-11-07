using System.Text;

using Kautuka.Hosting.Hosting;

using Microsoft.Extensions.Configuration;

namespace Kautuka.IntegrationTests;

public sealed class InstalledManagerConfigurationTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ContainerDeploymentAcceptsUtf8Preambles(bool settingsBom, bool contractBom)
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-bom-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string settings = Path.Combine(root, "manager.json");
            string contract = Path.Combine(root, "service-scope.json");
            File.WriteAllText(settings, "{\"CoreServices\":{\"Enabled\":true},\"ContainerEgress\":{\"Enabled\":true}}", new UTF8Encoding(settingsBom));
            File.WriteAllText(contract, "{\"applicationServices\":\"containers\"}", new UTF8Encoding(contractBom));
            using var configuration = new ConfigurationManager();
            KautukaInstalledDeploymentConfiguration.Add(configuration, settings, contract);
            Assert.True(configuration.GetValue<bool>("CoreServices:Enabled"));
            Assert.True(configuration.GetValue<bool>("ContainerEgress:Enabled"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InaccessibleDeploymentPathsCannotSelectLegacyDefaults(bool contractIsDirectory)
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-unreadable-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string settings = Path.Combine(root, "manager.json");
            string contract = Path.Combine(root, "service-scope.json");
            Directory.CreateDirectory(contractIsDirectory ? contract : settings);
            File.WriteAllText(contractIsDirectory ? settings : contract, "{}");
            using var configuration = new ConfigurationManager();
            Assert.Throws<UnauthorizedAccessException>(() => KautukaInstalledDeploymentConfiguration.Add(configuration, settings, contract));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void GenuinelyAbsentDeploymentFilesPreserveLegacyConfiguration()
    {
        string missing = Path.Combine(Path.GetTempPath(), "kautuka-absent-config-" + Guid.NewGuid().ToString("N"));
        using var configuration = new ConfigurationManager();
        configuration["ContainerEgress:Enabled"] = "false";
        KautukaInstalledDeploymentConfiguration.Add(configuration, Path.Combine(missing, "manager.json"), Path.Combine(missing, "service-scope.json"));
        Assert.Equal("false", configuration["ContainerEgress:Enabled"]);
    }

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
