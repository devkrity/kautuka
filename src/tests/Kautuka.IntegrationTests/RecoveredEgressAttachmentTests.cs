using System.Globalization;
using System.Text.Json;
using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class RecoveredEgressAttachmentTests
{
    [Fact]
    public async Task RecoveryInspectsTwiceAndConnectsOnlyMissingScopes()
    {
        var expected = Enumerable.Range(0, 22).ToDictionary(i => "scope-" + i.ToString("D2", CultureInfo.InvariantCulture), i => "10.0." + i.ToString(CultureInfo.InvariantCulture) + ".2");
        var attached = expected.Where(pair => pair.Key != "scope-05" && pair.Key != "scope-19")
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        var connections = new List<string>();
        int inspections = 0;
        Task<JsonDocument> Inspect(CancellationToken _)
        {
            inspections++;
            var networks = attached.ToDictionary(pair => pair.Key, pair => new { IPAddress = pair.Value });
            return Task.FromResult(JsonDocument.Parse(JsonSerializer.Serialize(new[] { new { NetworkSettings = new { Networks = networks } } })));
        }
        Task Connect(string name, string address, CancellationToken _)
        {
            connections.Add(name); attached.Add(name, address); return Task.CompletedTask;
        }
        await ContainerEgressRuntime.ReconcileRecoveredAttachmentsAsync(expected, Inspect, Connect, TestContext.Current.CancellationToken);
        Assert.Equal(2, inspections);
        Assert.Equal(new[] { "scope-05", "scope-19" }, connections);
    }

    [Fact]
    public async Task RecoveryRejectsConnectionThatDoesNotAppearInFinalInspection()
    {
        int inspections = 0;
        Task<JsonDocument> Inspect(CancellationToken _)
        {
            inspections++;
            return Task.FromResult(JsonDocument.Parse("[{\"NetworkSettings\":{\"Networks\":{}}}]"));
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => ContainerEgressRuntime.ReconcileRecoveredAttachmentsAsync(
            new Dictionary<string, string> { ["scope"] = "10.0.0.2" }, Inspect,
            (_, _, _) => Task.CompletedTask, TestContext.Current.CancellationToken));
        Assert.Equal(2, inspections);
    }

    [Fact]
    public void FindsOnlyMissingScopesAndRequiresEveryFinalAttachment()
    {
        var expected = Enumerable.Range(0, 22).ToDictionary(i => "scope-" + i.ToString("D2", CultureInfo.InvariantCulture), i => "10.0." + i.ToString(CultureInfo.InvariantCulture) + ".2");
        var attached = expected.Where(pair => pair.Key != "scope-05" && pair.Key != "scope-19")
            .ToDictionary(pair => pair.Key, pair => new { IPAddress = pair.Value });
        using JsonDocument initial = JsonDocument.Parse(JsonSerializer.Serialize(attached));
        Assert.Equal(new[] { "scope-05", "scope-19" }, ContainerEgressRuntime.MissingRecoveredAttachments(initial.RootElement, expected, false));
        Assert.Throws<InvalidOperationException>(() => ContainerEgressRuntime.MissingRecoveredAttachments(initial.RootElement, expected, true));
        attached["scope-05"] = new { IPAddress = expected["scope-05"] };
        attached["scope-19"] = new { IPAddress = expected["scope-19"] };
        using JsonDocument complete = JsonDocument.Parse(JsonSerializer.Serialize(attached));
        Assert.Empty(ContainerEgressRuntime.MissingRecoveredAttachments(complete.RootElement, expected, true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WrongAddressFailsBothInitialAndFinalChecks(bool requireComplete)
    {
        using JsonDocument attachments = JsonDocument.Parse("{\"scope\":{\"IPAddress\":\"10.0.0.9\"}}");
        Assert.Throws<InvalidOperationException>(() => ContainerEgressRuntime.MissingRecoveredAttachments(
            attachments.RootElement, new Dictionary<string, string> { ["scope"] = "10.0.0.2" }, requireComplete));
    }
}
