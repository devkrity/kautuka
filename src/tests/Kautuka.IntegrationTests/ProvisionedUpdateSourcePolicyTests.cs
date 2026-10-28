using Kautuka.UpdateService.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class ProvisionedUpdateSourcePolicyTests
{
    [Fact]
    public void ResolveReturnsCatalogManifestUrlWhenProvisioned()
    {
        string sourceDocumentPath = CreateSourceDocument(
            """
            {
              "schemaVersion": 1,
              "manifestUrl": "https://updates.kautuka.dev/manifest",
              "catalogManifestUrl": "https://store.kautuka.dev/catalog/manifest"
            }
            """);

        try
        {
            ProvisionedUpdateSourceDocument? document = ProvisionedUpdateSourcePolicy.Resolve(sourceDocumentPath);

            Assert.NotNull(document);
            Assert.Equal("https://updates.kautuka.dev/manifest", document.ManifestUrl);
            Assert.Equal("https://store.kautuka.dev/catalog/manifest", document.CatalogManifestUrl);
        }
        finally
        {
            DeleteSourceDocument(sourceDocumentPath);
        }
    }

    [Fact]
    public void ResolveAllowsCatalogOnlyProvisionedDocument()
    {
        string sourceDocumentPath = CreateSourceDocument(
            """
            {
              "schemaVersion": 1,
              "catalogManifestUrl": "https://store.kautuka.dev/catalog/manifest"
            }
            """);

        try
        {
            ProvisionedUpdateSourceDocument? document = ProvisionedUpdateSourcePolicy.Resolve(sourceDocumentPath);

            Assert.NotNull(document);
            Assert.Null(document.ManifestUrl);
            Assert.Null(document.PreviewManifestUrl);
            Assert.Null(document.StableManifestUrl);
            Assert.Equal("https://store.kautuka.dev/catalog/manifest", document.CatalogManifestUrl);
        }
        finally
        {
            DeleteSourceDocument(sourceDocumentPath);
        }
    }

    [Fact]
    public void ResolveReturnsNullWhenAllManifestUrlsAreEmpty()
    {
        string sourceDocumentPath = CreateSourceDocument(
            """
            {
              "schemaVersion": 1,
              "manifestUrl": "",
              "previewManifestUrl": "",
              "stableManifestUrl": ""
            }
            """);

        try
        {
            ProvisionedUpdateSourceDocument? document = ProvisionedUpdateSourcePolicy.Resolve(sourceDocumentPath);

            Assert.Null(document);
        }
        finally
        {
            DeleteSourceDocument(sourceDocumentPath);
        }
    }

    private static string CreateSourceDocument(string json)
    {
        string directoryPath = Path.Combine(Path.GetTempPath(), "kautuka-update-source-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);

        string sourceDocumentPath = Path.Combine(directoryPath, "update-source.json");
        File.WriteAllText(sourceDocumentPath, json);

        return sourceDocumentPath;
    }

    private static void DeleteSourceDocument(string sourceDocumentPath)
    {
        string? directoryPath = Path.GetDirectoryName(sourceDocumentPath);

        if (!string.IsNullOrWhiteSpace(directoryPath) && Directory.Exists(directoryPath))
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }
}
