using System.Text;
using System.Net;
using System.Security.Cryptography;
using Kautuka.UpdateService.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Kautuka.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class ApplianceDeploymentManifestTests
{
    private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static string Service(string name) => $"\"{name}\":{{\"image\":\"registry.example/{name}@sha256:{Digest}\"}}";
    private static string Valid() => "{\"applianceDeployment\":{\"schemaVersion\":1,\"hookAbi\":1,\"services\":{" +
        string.Join(',', new[] { "credentialshost", "channelhost", "gateway", "webhost", "updateservice" }.Select(Service)) + "}}}";
    private static ApplianceDeploymentDescriptor? Parse(string text) => ApplianceDeploymentManifest.Parse(Encoding.UTF8.GetBytes(text));

    [Theory]
    [InlineData(null)]
    [InlineData("v1.2.3-preview.9")]
    public async Task PreparationWritesVerifiedApplianceMetadataOnlyInApplianceMode(string? installedAppliance)
    {
        string temp = Path.Combine(Path.GetTempPath(), "appliance-manifest-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string keyPath = Path.Combine(temp, "public.pem");
        await File.WriteAllTextAsync(keyPath, key.ExportSubjectPublicKeyInfoPem(), TestContext.Current.CancellationToken);
        byte[] manifest = Encoding.UTF8.GetBytes(Valid());
        string signature = Convert.ToBase64String(key.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        var factory = new ManifestFactory(manifest, signature);
        var coordinator = new UpdateCoordinator(null!, null!, null!, factory, null!, null!, Options.Create(new UpdateOptions
        {
            ApplianceReleaseId = installedAppliance,
            AllowUnsignedLocalFeeds = true,
            ManifestPublicKeyPath = keyPath,
        }), NullLogger<UpdateCoordinator>.Instance);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.DownloadPlatformReleaseDescriptorAsync("https://example.test/manifest", null, temp, TestContext.Current.CancellationToken));
            string manifestPath = Path.Combine(temp, "kautuka-platform-release.json");
            string signaturePath = Path.Combine(temp, "kautuka-platform-release.sig");
            Assert.False(File.Exists(manifestPath));
            Assert.False(File.Exists(signaturePath));
            Task DownloadSignedManifest() => coordinator.DownloadPlatformReleaseDescriptorAsync("https://example.test/manifest", "https://example.test/signature", temp, TestContext.Current.CancellationToken);
            if (installedAppliance is null)
            {
                InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(DownloadSignedManifest);
                Assert.Contains("activation is not supported", error.Message, StringComparison.Ordinal);
                Assert.False(File.Exists(manifestPath));
                Assert.False(File.Exists(signaturePath));
                return;
            }

            await DownloadSignedManifest();
            Assert.Equal(manifest, await File.ReadAllBytesAsync(manifestPath, TestContext.Current.CancellationToken));
            Assert.Equal(signature, await File.ReadAllTextAsync(signaturePath, TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(temp, true); }
    }

    private sealed class ManifestFactory(byte[] manifest, string signature) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new ManifestHandler(manifest, signature));
    }

    private sealed class ManifestHandler(byte[] manifest, string signature) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri!.AbsolutePath == "/signature" ? new StringContent(signature) : new ByteArrayContent(manifest),
            });
    }
    [Fact]
    public void ValidSignedSectionShapeAndLegacyAbsence()
    {
        Assert.Null(Parse("{}"));
        ApplianceDeploymentDescriptor result = Assert.IsType<ApplianceDeploymentDescriptor>(Parse(Valid()));
        Assert.Equal(1, result.HookAbi);
        Assert.Equal(5, result.Images.Count);
    }

    [Fact]
    public void InvalidSectionsFailClosed()
    {
        string valid = Valid();
        string[] invalid =
        [
            valid.Replace("\"hookAbi\":1", "\"hookAbi\":0", StringComparison.Ordinal),
            valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal),
            valid.Replace(Service("gateway"), Service("gateway") + "," + Service("gateway"), StringComparison.Ordinal),
            valid.Replace(Service("gateway"), Service("other"), StringComparison.Ordinal),
            valid.Replace("\"hookAbi\":1", "\"hookAbi\":1,\"unknown\":true", StringComparison.Ordinal),
            valid.Replace("registry.example/gateway@", "gateway:latest@", StringComparison.Ordinal),
            valid.Replace("registry.example/gateway@", "registry.example:0/gateway@", StringComparison.Ordinal),
            valid.Replace("\"applianceDeployment\":", "\"ApplianceDeployment\":", StringComparison.Ordinal),
            valid.Replace("\"hookAbi\":1", "\"hookAbi\":1,\"hookAbi\":1", StringComparison.Ordinal),
        ];
        foreach (string text in invalid) Assert.Throws<InvalidDataException>(() => Parse(text));
    }
}
