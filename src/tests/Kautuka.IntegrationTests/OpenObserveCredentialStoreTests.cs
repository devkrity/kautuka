using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class OpenObserveCredentialStoreTests
{
    [Fact]
    public void CredentialsPersistWithManagerOnlyPermissionsAndExporterUsesSeparateToken()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-openobserve-credentials-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new OpenObserveCredentialStore(root);
            string password = store.GetOrCreateRootPassword();
            Assert.Equal(48, password.Length);
            Assert.Equal(password, new OpenObserveCredentialStore(root).GetOrCreateRootPassword());
            Assert.Equal("manager", Path.GetFileName(Path.GetDirectoryName(store.RootPasswordPath)));
            Assert.Equal("exporter", Path.GetFileName(store.ExporterDirectoryPath));
            Assert.StartsWith("..", Path.GetRelativePath(store.ExporterDirectoryPath, store.RootPasswordPath), StringComparison.Ordinal);

            string exporterHeader = OpenObserveCredentialStore.CreateBasicAuthorizationHeader(
                OpenObserveCredentialStore.IngestionTokenName,
                "separate-ingestion-token");
            store.SaveIngestionAuthorizationHeader(exporterHeader);
            Assert.Equal(exporterHeader, new OpenObserveCredentialStore(root).ReadIngestionAuthorizationHeader());
            string decodedExporterCredential = System.Text.Encoding.UTF8.GetString(
                Convert.FromBase64String(exporterHeader["Basic ".Length..]));
            Assert.Equal(OpenObserveCredentialStore.IngestionTokenName + ":separate-ingestion-token", decodedExporterCredential);
            Assert.DoesNotContain(password, decodedExporterCredential, StringComparison.Ordinal);

            if (OperatingSystem.IsLinux())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(Path.Combine(root, "telemetry")));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(store.RootPasswordPath));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(store.IngestionAuthorizationPath));
            }
            store.ClearIngestionAuthorizationHeader();
            Assert.Null(store.ReadIngestionAuthorizationHeader());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RootPasswordIsCryptographicallyRandomAcrossFreshStores()
    {
        string firstRoot = Path.Combine(Path.GetTempPath(), "kautuka-openobserve-credentials-" + Guid.NewGuid().ToString("N"));
        string secondRoot = Path.Combine(Path.GetTempPath(), "kautuka-openobserve-credentials-" + Guid.NewGuid().ToString("N"));
        try
        {
            string first = new OpenObserveCredentialStore(firstRoot).GetOrCreateRootPassword();
            string second = new OpenObserveCredentialStore(secondRoot).GetOrCreateRootPassword();
            Assert.NotEqual(first, second);
            Assert.Contains(first, static character => character is >= 'a' and <= 'z');
            Assert.Contains(first, static character => character is >= 'A' and <= 'Z');
            Assert.Contains(first, static character => character is >= '0' and <= '9');
            Assert.Contains(first, static character => "!@#$%^&*()-_=+[]{}?".Contains(character, StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(firstRoot)) Directory.Delete(firstRoot, recursive: true);
            if (Directory.Exists(secondRoot)) Directory.Delete(secondRoot, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidPersistedRootPasswordFailsWithoutReplacingIt()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-openobserve-invalid-" + Guid.NewGuid().ToString("N"));
        var store = new OpenObserveCredentialStore(root);
        string credentialDirectory = Path.GetDirectoryName(store.RootPasswordPath)!;
        Directory.CreateDirectory(credentialDirectory);
        string passwordPath = store.RootPasswordPath;
        const string invalid = "old";
        await File.WriteAllTextAsync(passwordPath, invalid, TestContext.Current.CancellationToken).ConfigureAwait(false);
        try
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => store.GetOrCreateRootPassword());
            Assert.Contains("preserve collector data", error.Message, StringComparison.Ordinal);
            Assert.Equal(invalid, await File.ReadAllTextAsync(passwordPath, TestContext.Current.CancellationToken).ConfigureAwait(false));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
