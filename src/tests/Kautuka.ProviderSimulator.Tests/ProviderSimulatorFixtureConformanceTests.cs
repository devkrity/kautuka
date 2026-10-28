using System.Text.Json;

namespace Kautuka.ProviderSimulator.Tests;

public sealed class ProviderSimulatorFixtureConformanceTests
{
    [Theory]
    [InlineData("telegram-get-updates.json", "ok")]
    [InlineData("slack-socket-envelope.json", "envelope_id")]
    public void SanitizedOfficialFixtureHasProvenanceAndRequiredConsumedShape(string file, string requiredProperty)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", file);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.StartsWith("official:", document.RootElement.GetProperty("provenance").GetString(), StringComparison.Ordinal);
        Assert.True(document.RootElement.TryGetProperty("version", out _));
        Assert.True(document.RootElement.TryGetProperty(requiredProperty, out _));
        string serialized = document.RootElement.GetRawText();
        Assert.DoesNotContain("xox", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer ", serialized, StringComparison.OrdinalIgnoreCase);
    }
}
