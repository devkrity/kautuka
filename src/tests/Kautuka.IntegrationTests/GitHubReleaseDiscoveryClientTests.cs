using Kautuka.UpdateService.Discovery;

namespace Kautuka.IntegrationTests;

public sealed class GitHubReleaseDiscoveryClientTests
{
    [Fact]
    public void SelectReleaseReturnsNewestStableReleaseForStableChannel()
    {
        GitHubReleasePayload stable = CreateRelease(
            tagName: "v0.1.0",
            name: "v0.1.0",
            draft: false,
            prerelease: false,
            id: 300);
        GitHubReleasePayload prerelease = CreateRelease(
            tagName: "v0.1.0-preview.2",
            name: "Preview 0.1.0 preview.2",
            draft: false,
            prerelease: true,
            id: 301);

        GitHubReleasePayload? selected = GitHubReleaseDiscoveryClient.SelectRelease([prerelease, stable], "stable");

        Assert.Equal(stable, selected);
    }

    [Fact]
    public void SelectReleaseReturnsNewestPreviewReleaseForPreviewChannel()
    {
        GitHubReleasePayload preview = CreateRelease(
            tagName: "v0.1.0-preview.2",
            name: "Preview 0.1.0 preview.2",
            draft: false,
            prerelease: true,
            id: 401);
        GitHubReleasePayload otherPrerelease = CreateRelease(
            tagName: "v0.1.0-preview.3",
            name: "Preview 0.1.0 preview.3",
            draft: false,
            prerelease: true,
            id: 400);

        GitHubReleasePayload? selected = GitHubReleaseDiscoveryClient.SelectRelease([otherPrerelease, preview], "preview");

        Assert.Equal(otherPrerelease, selected);
    }

    [Fact]
    public void SelectReleaseSkipsDraftsAndReturnsNullForStableWhenOnlyPreviewExists()
    {
        GitHubReleasePayload draftPreview = CreateRelease(
            tagName: "v0.1.0-preview.3",
            name: "Preview draft",
            draft: true,
            prerelease: true,
            id: 600);
        GitHubReleasePayload otherPrerelease = CreateRelease(
            tagName: "v0.1.0-preview.5",
            name: "Preview 0.1.0 preview.5",
            draft: false,
            prerelease: true,
            id: 601);

        GitHubReleasePayload? selected = GitHubReleaseDiscoveryClient.SelectRelease([draftPreview, otherPrerelease], "stable");

        Assert.Null(selected);
    }

    private static GitHubReleasePayload CreateRelease(string tagName, string name, bool draft, bool prerelease, long id)
        => new(
            Id: id,
            TagName: tagName,
            Name: name,
            Draft: draft,
            Prerelease: prerelease,
            HtmlUrl: $"https://example.test/releases/{id}",
            PublishedAt: DateTimeOffset.UtcNow,
            Assets: []);
}
