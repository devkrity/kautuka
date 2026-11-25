using System.Runtime.InteropServices;
using System.Text.Json;

using Kautuka.Hosting;
using Kautuka.Manager.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class ApplianceRoleImageRetirementTests
{
    [Fact]
    public async Task RemovesOnlyTheExclusiveObsoleteImageByImmutableId()
    {
        var previous = Release("1.0.0-preview.10", 'a');
        var current = Release("1.0.0-preview.11", 'b');
        var staged = Release("1.0.0-preview.12", 'c');
        var obsolete = Release("1.0.0-preview.9", 'd');
        var sharedWithStaged = Release("1.0.0-preview.8", 'c');
        var foreignAlias = Release("1.0.0-preview.7", 'e');
        var differentDigestSameImage = Release("1.0.0-preview.6", 'f');
        var images = new Dictionary<string, string>
        {
            [Reference(previous)] = Image(previous, '1', Tag(previous)),
            [Reference(current)] = Image(current, '2', Tag(current)),
            [Reference(staged)] = Image(staged, '3', Tag(staged)),
            [Reference(obsolete)] = Image(obsolete, '4', Tag(obsolete)),
            [Reference(foreignAlias)] = Image(foreignAlias, '5', Tag(foreignAlias), "localhost/foreign:keep"),
            [Reference(differentDigestSameImage)] = Image(differentDigestSameImage, '1', Tag(differentDigestSameImage)),
        };
        var removals = new List<string[]>();
        var runner = new PodmanCommandRunner((arguments, _) =>
        {
            if (arguments[1] == "inspect")
                return Task.FromResult(new PodmanCommandResult(0, images[arguments[2]], ""));
            removals.Add(arguments.ToArray());
            return Task.FromResult(new PodmanCommandResult(0, "", ""));
        });

        await new ApplianceRoleImageRetirement(runner).RetireAsync(
            [obsolete, sharedWithStaged, foreignAlias, differentDigestSameImage],
            [current, previous, staged], TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "image", "rm", "--no-prune", "sha256:" + new string('4', 64) }, Assert.Single(removals));
    }

    [Fact]
    public async Task ACaseOfMissingRecoveryImageStopsRetirementBeforeAnyRemoval()
    {
        var current = Release("1.0.0", 'a');
        var previous = Release("0.9.0", 'b');
        var obsolete = Release("0.8.0", 'c');
        var runner = new PodmanCommandRunner((arguments, _) =>
        {
            Assert.Contains(arguments[1], new[] { "inspect", "exists" });
            return Task.FromResult(arguments[2] == Reference(current)
                ? new PodmanCommandResult(0, Image(current, '1', Tag(current)), "")
                : new PodmanCommandResult(1, "", ""));
        });

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await new ApplianceRoleImageRetirement(runner).RetireAsync([obsolete], [current, previous],
                TestContext.Current.CancellationToken));
    }

    private static VerifiedApplianceRelease Release(string version, char digest) =>
        new("v" + version, version, new ApplianceDeploymentDescriptor(1, 1,
            new Dictionary<string, string> { ["gateway"] = $"localhost/appliance-gateway@sha256:{new string(digest, 64)}" }));

    private static string Reference(VerifiedApplianceRelease release) => release.Deployment.Images["gateway"];

    private static string Tag(VerifiedApplianceRelease release) => "localhost/appliance-gateway:" + release.Version;

    private static string Image(VerifiedApplianceRelease release, char id, params string[] tags) =>
        JsonSerializer.Serialize(new[]
        {
            new
            {
                Id = new string(id, 64),
                Digest = Reference(release).Split('@')[1],
                RepoDigests = new[] { Reference(release) },
                RepoTags = tags,
                Os = "linux",
                Architecture = RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "amd64",
                Config = new { Labels = new Dictionary<string, string> { ["io.kautuka.service.role"] = "gateway" } }
            }
        });
}
