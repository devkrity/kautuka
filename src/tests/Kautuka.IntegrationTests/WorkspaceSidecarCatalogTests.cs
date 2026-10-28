using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class WorkspaceSidecarCatalogTests
{
    [Fact]
    public void LoadDefaultsRequiredForReadyToTrueWhenFieldMissing()
    {
        string tempRoot = CreateTempDirectory();

        try
        {
            string path = Path.Combine(tempRoot, "sidecars.json");
            File.WriteAllText(path, """
                {
                  "sidecars": [
                    {
                      "name": "required-sidecar",
                      "image": "docker.io/example/required:latest",
                      "ports": [],
                      "environment": {},
                      "command": [],
                      "buildContext": null,
                      "dockerfile": null,
                      "healthCheck": null
                    }
                  ]
                }
                """);

            WorkspaceSidecarCatalog catalog = WorkspaceSidecarCatalogLoader.Load(path);

            Assert.Single(catalog.Sidecars);
            Assert.True(catalog.Sidecars[0].RequiredForReady);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void LoadReadsRequiredForReadyWhenConfigured()
    {
        string tempRoot = CreateTempDirectory();

        try
        {
            string path = Path.Combine(tempRoot, "sidecars.json");
            File.WriteAllText(path, """
                {
                  "sidecars": [
                    {
                      "name": "optional-sidecar",
                      "image": "docker.io/example/optional:latest",
                      "ports": [],
                      "environment": {},
                      "command": [],
                      "buildContext": null,
                      "dockerfile": null,
                      "requiredForReady": false,
                      "healthCheck": null
                    }
                  ]
                }
                """);

            WorkspaceSidecarCatalog catalog = WorkspaceSidecarCatalogLoader.Load(path);

            Assert.Single(catalog.Sidecars);
            Assert.False(catalog.Sidecars[0].RequiredForReady);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void LoadReadsSidecarPolicyWhenConfigured()
    {
        string tempRoot = CreateTempDirectory();

        try
        {
            string path = Path.Combine(tempRoot, "sidecars.json");
            File.WriteAllText(path, """
                                {
                                    "sidecars": [
                                        {
                                            "name": "inference",
                                            "image": "docker.io/example/inference:latest",
                                            "ports": [],
                                            "environment": {},
                                            "command": [],
                                            "buildContext": null,
                                            "dockerfile": null,
                                            "requiredForReady": false,
                                            "policy": "always",
                                            "healthCheck": null
                                        }
                                    ]
                                }
                                """);

            WorkspaceSidecarCatalog catalog = WorkspaceSidecarCatalogLoader.Load(path);

            Assert.Single(catalog.Sidecars);
            Assert.Equal("always", catalog.Sidecars[0].Policy);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void LoadReturnsEmptyWhenJsonIsInvalid()
    {
        string tempRoot = CreateTempDirectory();

        try
        {
            string path = Path.Combine(tempRoot, "sidecars.json");
            File.WriteAllText(path, "{ \"sidecars\": [ { \"name\": \"oops\" } ] ] }");

            WorkspaceSidecarCatalog catalog = WorkspaceSidecarCatalogLoader.Load(path);

            Assert.Empty(catalog.Sidecars);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void LoadReadsHostBindAddressWhenConfigured()
    {
        string tempRoot = CreateTempDirectory();

        try
        {
            string path = Path.Combine(tempRoot, "sidecars.json");
            File.WriteAllText(path, """
                {
                  "sidecars": [
                    {
                      "name": "openobserve",
                      "image": "public.ecr.aws/zinclabs/openobserve:latest",
                      "ports": [
                        {
                          "hostPort": 20215,
                          "containerPort": 5080,
                          "hostBindAddress": "127.0.0.1"
                        }
                      ],
                      "environment": {},
                      "command": [],
                      "buildContext": null,
                      "dockerfile": null,
                      "requiredForReady": false,
                      "healthCheck": null
                    }
                  ]
                }
                """);

            WorkspaceSidecarCatalog catalog = WorkspaceSidecarCatalogLoader.Load(path);

            Assert.Single(catalog.Sidecars);
            Assert.Equal("127.0.0.1", catalog.Sidecars[0].Ports[0].HostBindAddress);
            Assert.Equal(20215, catalog.Sidecars[0].Ports[0].HostPort);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void LoadReadsBuildContextForHealthcheckWrappers()
    {
        string tempRoot = CreateTempDirectory();

        try
        {
            string pkgsProxyContext = Path.Combine(tempRoot, "sidecar-assets", "pkgs-proxy");
            Directory.CreateDirectory(pkgsProxyContext);
            File.WriteAllText(Path.Combine(pkgsProxyContext, "Dockerfile"), "FROM scratch\n");

            string path = Path.Combine(tempRoot, "sidecars.json");
            File.WriteAllText(path, """
                {
                  "sidecars": [
                    {
                      "name": "kautuka-pkgs-proxy",
                      "image": "localhost/kautuka-pkgs-proxy:local",
                      "ports": [],
                      "environment": {},
                      "command": [],
                      "buildContext": "sidecar-assets/pkgs-proxy",
                      "dockerfile": null,
                      "requiredForReady": false,
                      "healthCheck": null
                    }
                  ]
                }
                """);

            WorkspaceSidecarCatalog catalog = WorkspaceSidecarCatalogLoader.Load(path);

            Assert.Single(catalog.Sidecars);
            Assert.Equal(
                Path.GetFullPath(pkgsProxyContext),
                catalog.Sidecars[0].BuildContext);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void CommittedCatalogSealsUnpublishedPkgsProxy()
    {
        string path = Path.Combine(FindRepoRoot(), "src", "backend", "Kautuka.Manager", "sidecars.json");
        WorkspaceSidecarCatalog catalog = WorkspaceSidecarCatalogLoader.Load(path);

        WorkspaceSidecarDefinition proxy = Assert.Single(catalog.Sidecars, sidecar =>
            string.Equals(sidecar.Name, PkgsProxyCacheDiagnosticsService.SidecarName, StringComparison.Ordinal));
        Assert.Empty(proxy.Ports);
        Assert.Equal("localhost/kautuka-pkgs-proxy:local", proxy.Image);
        Assert.Contains("pkgs-proxy", proxy.BuildContext, StringComparison.Ordinal);
        Assert.Equal(":20101", proxy.Environment["PROXY_LISTEN"]);
        Assert.Equal("http://kautuka-pkgs-proxy:20101", proxy.Environment["PROXY_BASE_URL"]);
        Assert.DoesNotContain(
            catalog.Sidecars,
            sidecar => sidecar.Name.Contains("verdaccio", StringComparison.OrdinalIgnoreCase)
                || sidecar.Name.Contains("proxpi", StringComparison.OrdinalIgnoreCase));
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"kautuka-sidecar-catalog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "backend", "Kautuka.Manager", "sidecars.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }
}
