using System.IO.Compression;
using System.Text;

using Kautuka.Gateway.Domain.Connectors;

using Microsoft.Extensions.Configuration;

namespace Kautuka.IntegrationTests;

public sealed class McpbPackageServiceTests
{
    [Fact]
    public async Task InspectAsyncValidManifestMapsToConnectorUpsert()
    {
        string manifest =
            """
            {
              "manifest_version": "0.4",
              "name": "Acme MCP",
              "display_name": "Acme MCP Bundle",
              "version": "1.2.3",
              "description": "Acme tools",
              "author": { "name": "Acme" },
              "user_config": {
                "api_key": {
                  "type": "string",
                  "title": "API key",
                  "description": "API key"
                }
              },
              "server": {
                "type": "node",
                "entry_point": "server/index.js",
                "mcp_config": {
                  "command": "node",
                  "args": ["${__dirname}/server/index.js", "${user_config.api_key}"],
                  "env": {
                    "ACME_TOKEN": "${user_config.api_key}"
                  }
                }
              }
            }
            """;

        string sqlitePath = CreateTempSqlitePath();
        McpbPackageService service = CreateService(sqlitePath);
        await using MemoryStream bundle = CreateBundle(manifest);

        McpbConnectorInspection inspection = await service
            .InspectAsync(bundle, "acme.mcpb", TestContext.Current.CancellationToken)
            .ConfigureAwait(false);

        Assert.Equal("acme-mcp", inspection.ServerId);
        Assert.Equal("0.4", inspection.ManifestVersion);
        Assert.Equal("node", inspection.ServerType);
        Assert.Equal("Acme MCP", inspection.BundleName);
        Assert.Equal("Acme MCP Bundle", inspection.Connector.Name);
        Assert.Equal("Acme tools", inspection.Connector.Description);
        Assert.Equal("container", inspection.Connector.Transport);
        Assert.Equal(McpCommandTransportMapper.NodeImage, inspection.Connector.ContainerImage);
        Assert.Equal("node", inspection.Connector.ContainerCommand);
        Assert.NotNull(inspection.Connector.ContainerArgs);
        Assert.Equal(["${__dirname}/server/index.js", "${user_config.api_key}"], inspection.Connector.ContainerArgs);
        Assert.False(string.IsNullOrWhiteSpace(inspection.Connector.BundlePath));
        Assert.Equal("node", inspection.Connector.ServerType);
        Assert.Equal("oci", inspection.Connector.RuntimeArtifactType);
        Assert.False(string.IsNullOrWhiteSpace(inspection.Connector.RuntimeArtifactSourceDigest));
        Assert.Equal("pending", inspection.Connector.RuntimeArtifactBuildStatus);
        Assert.NotNull(inspection.Connector.EnvKeys);
        Assert.Equal(["api_key"], inspection.Connector.EnvKeys);
    }

    [Fact]
    public async Task InspectAsyncPersistsBundleReadableByUnixGroup()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string manifest =
            """
            {
              "manifest_version": "0.4",
              "name": "Acme MCP",
              "display_name": "Acme MCP Bundle",
              "version": "1.2.3",
              "description": "Acme tools",
              "author": { "name": "Acme" },
              "server": {
                "type": "node",
                "entry_point": "server/index.js",
                "mcp_config": {
                  "command": "node",
                  "args": ["${__dirname}/server/index.js"]
                }
              }
            }
            """;

        string sqlitePath = CreateTempSqlitePath();
        McpbPackageService service = CreateService(sqlitePath);
        await using MemoryStream bundle = CreateBundle(manifest);

        McpbConnectorInspection inspection = await service
            .InspectAsync(bundle, "acme.mcpb", TestContext.Current.CancellationToken)
            .ConfigureAwait(false);

        string bundlePath = inspection.Connector.BundlePath ?? throw new InvalidOperationException("Expected persisted bundle path.");
        UnixFileMode fileMode = File.GetUnixFileMode(bundlePath);
        Assert.Equal(UnixFileMode.GroupRead, fileMode & UnixFileMode.GroupRead);

        string connectorDirectory = Path.GetDirectoryName(bundlePath) ?? throw new InvalidOperationException("Expected connector directory.");
        string bundleRoot = Path.GetDirectoryName(connectorDirectory) ?? throw new InvalidOperationException("Expected mcpb-bundles directory.");
        Assert.Equal(UnixFileMode.GroupExecute, File.GetUnixFileMode(connectorDirectory) & UnixFileMode.GroupExecute);
        Assert.Equal(UnixFileMode.GroupExecute, File.GetUnixFileMode(bundleRoot) & UnixFileMode.GroupExecute);
        Assert.Equal(UnixFileMode.SetGroup, File.GetUnixFileMode(bundleRoot) & UnixFileMode.SetGroup);
    }

    [Fact]
    public async Task InspectAsyncUnsupportedFieldThrowsDeterministicError()
    {
        string manifest =
            """
            {
              "manifest_version": "0.3",
              "name": "Acme MCP",
              "version": "1.0.0",
              "description": "Acme tools",
              "author": { "name": "Acme" },
              "server": {
                "type": "node",
                "entry_point": "server/index.js",
                "mcp_config": {
                  "command": "node"
                }
              },
              "x_extension": true
            }
            """;

        string sqlitePath = CreateTempSqlitePath();
        McpbPackageService service = CreateService(sqlitePath);
        await using MemoryStream bundle = CreateBundle(manifest);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.InspectAsync(bundle, "acme.mcpb", TestContext.Current.CancellationToken));

        Assert.Contains("unsupported fields/extensions", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("x_extension", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InspectAsyncDeprecatedDxtVersionIsRejected()
    {
        string manifest =
            """
            {
              "dxt_version": "0.4",
              "name": "Acme MCP",
              "version": "1.0.0",
              "description": "Acme tools",
              "author": { "name": "Acme" },
              "server": {
                "type": "node",
                "entry_point": "server/index.js",
                "mcp_config": { "command": "node" }
              }
            }
            """;

        McpbPackageService service = CreateService(CreateTempSqlitePath());
        await using MemoryStream bundle = CreateBundle(manifest);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.InspectAsync(bundle, "acme.mcpb", TestContext.Current.CancellationToken));

        Assert.Contains("dxt_version", ex.Message, StringComparison.Ordinal);
        Assert.Contains("unsupported fields/extensions", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InspectAsyncMissingManifestRejectsBundle()
    {
        string sqlitePath = CreateTempSqlitePath();
        McpbPackageService service = CreateService(sqlitePath);
        await using MemoryStream bundle = CreateBundleWithEntries(
          static archive =>
          {
              ZipArchiveEntry entry = archive.CreateEntry("README.txt", CompressionLevel.SmallestSize);
              using Stream stream = entry.Open();
              byte[] bytes = Encoding.UTF8.GetBytes("no manifest here");
              stream.Write(bytes, 0, bytes.Length);
          });

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
          () => service.InspectAsync(bundle, "acme.mcpb", TestContext.Current.CancellationToken));

        Assert.Contains("manifest.json is required", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InspectAsyncUnsupportedManifestVersionRejectsBundle()
    {
        string manifest =
          """
          {
            "manifest_version": "0.9",
            "name": "Acme MCP",
            "version": "1.0.0",
            "description": "Acme tools",
            "author": { "name": "Acme" },
            "server": {
            "type": "node",
            "entry_point": "server/index.js",
            "mcp_config": {
              "command": "node"
            }
            }
          }
          """;

        string sqlitePath = CreateTempSqlitePath();
        McpbPackageService service = CreateService(sqlitePath);
        await using MemoryStream bundle = CreateBundle(manifest);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
          () => service.InspectAsync(bundle, "acme.mcpb", TestContext.Current.CancellationToken));

        Assert.Contains("Unsupported manifest version", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InspectAsyncZipSlipPathRejectsBundle()
    {
        string manifest =
          """
          {
            "manifest_version": "0.3",
            "name": "Acme MCP",
            "version": "1.0.0",
            "description": "Acme tools",
            "author": { "name": "Acme" },
            "server": {
            "type": "node",
            "entry_point": "server/index.js",
            "mcp_config": {
              "command": "node"
            }
            }
          }
          """;

        string sqlitePath = CreateTempSqlitePath();
        McpbPackageService service = CreateService(sqlitePath);
        await using MemoryStream bundle = CreateBundleWithEntries(
          archive =>
          {
              ZipArchiveEntry unsafeEntry = archive.CreateEntry("../manifest.json", CompressionLevel.SmallestSize);
              using Stream stream = unsafeEntry.Open();
              byte[] bytes = Encoding.UTF8.GetBytes(manifest);
              stream.Write(bytes, 0, bytes.Length);
          });

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
          () => service.InspectAsync(bundle, "acme.mcpb", TestContext.Current.CancellationToken));

        Assert.Contains("Invalid archive entry path", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InspectAsyncSymlinkEntryRejectsBundle()
    {
        string manifest =
          """
          {
            "manifest_version": "0.3",
            "name": "Acme MCP",
            "version": "1.0.0",
            "description": "Acme tools",
            "author": { "name": "Acme" },
            "server": {
            "type": "node",
            "entry_point": "server/index.js",
            "mcp_config": {
              "command": "node"
            }
            }
          }
          """;

        string sqlitePath = CreateTempSqlitePath();
        McpbPackageService service = CreateService(sqlitePath);
        await using MemoryStream bundle = CreateBundleWithEntries(
          archive =>
          {
              ZipArchiveEntry manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.SmallestSize);
              using (Stream manifestStream = manifestEntry.Open())
              {
                  byte[] bytes = Encoding.UTF8.GetBytes(manifest);
                  manifestStream.Write(bytes, 0, bytes.Length);
              }

              ZipArchiveEntry symlink = archive.CreateEntry("link-to-server", CompressionLevel.SmallestSize);
              symlink.ExternalAttributes = 0xA000 << 16;
          });

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
          () => service.InspectAsync(bundle, "acme.mcpb", TestContext.Current.CancellationToken));

        Assert.Contains("Symlink entry", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InspectAsyncNonMcpbExtensionRejectsFileType()
    {
        string manifest =
            """
            {
              "manifest_version": "0.3",
              "name": "Acme MCP",
              "version": "1.0.0",
              "description": "Acme tools",
              "author": { "name": "Acme" },
              "server": {
                "type": "node",
                "entry_point": "server/index.js",
                "mcp_config": {
                  "command": "node"
                }
              }
            }
            """;

        string sqlitePath = CreateTempSqlitePath();
        McpbPackageService service = CreateService(sqlitePath);
        await using MemoryStream bundle = CreateBundle(manifest);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.InspectAsync(bundle, "acme.zip", TestContext.Current.CancellationToken));

        Assert.Contains("Only .mcpb", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InspectAsyncRejectsUvBundlesThatReferenceBundleDirPlaceholder()
    {
        string manifest =
            """
            {
              "manifest_version": "0.4",
              "name": "Splose MCP",
              "version": "0.1.0",
              "description": "Splose tools",
              "author": { "name": "Acme" },
              "server": {
                "type": "uv",
                "entry_point": "splose_mcp/server.py",
                "mcp_config": {
                  "command": "uv",
                  "args": ["run", "--directory", "${__dirname}", "-m", "splose_mcp.server"]
                }
              }
            }
            """;

        string sqlitePath = CreateTempSqlitePath();
        McpbPackageService service = CreateService(sqlitePath);
        await using MemoryStream bundle = CreateBundle(manifest);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.InspectAsync(bundle, "splose.mcpb", TestContext.Current.CancellationToken));

        Assert.Contains("server.type 'uv' cannot reference ${__dirname}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InspectAsyncBinaryManifestMapsNativeMailConnector()
    {
        string manifest =
            """
            {
              "manifest_version": "0.4",
              "name": "kautuka-m365-mail",
              "display_name": "Microsoft 365 Mail",
              "version": "0.1.0",
              "description": "Read, draft, and send Microsoft 365 mail.",
              "author": { "name": "Kautuka" },
              "user_config": {
                "KAUTUKA_M365_REFRESH_TOKEN": {
                  "type": "string",
                  "title": "Microsoft sign-in",
                  "sensitive": true,
                  "required": true
                }
              },
              "server": {
                "type": "binary",
                "entry_point": "kautuka-m365-mail",
                "mcp_config": {
                  "command": "${__dirname}/kautuka-m365-mail",
                  "args": []
                }
              }
            }
            """;

        string sqlitePath = CreateTempSqlitePath();
        McpbPackageService service = CreateService(sqlitePath);
        await using MemoryStream bundle = CreateBundle(manifest);

        McpbConnectorInspection inspection = await service
            .InspectAsync(bundle, "kautuka-m365-mail.mcpb", TestContext.Current.CancellationToken)
            .ConfigureAwait(false);

        Assert.Equal("kautuka-m365-mail", inspection.ServerId);
        Assert.Equal("binary", inspection.ServerType);
        Assert.Equal(McpCommandTransportMapper.BinaryImage, inspection.Connector.ContainerImage);
        Assert.Equal("${__dirname}/kautuka-m365-mail", inspection.Connector.ContainerCommand);
        Assert.Equal(["KAUTUKA_M365_REFRESH_TOKEN"], inspection.Connector.EnvKeys!);
    }

    private static MemoryStream CreateBundleWithEntries(Action<ZipArchive> configure)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            configure(archive);
        }

        stream.Position = 0;
        return stream;
    }

    private static MemoryStream CreateBundle(string manifestJson)
    {
        return CreateBundleWithEntries(
          archive =>
          {
              ZipArchiveEntry manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.SmallestSize);
              using Stream entryStream = manifestEntry.Open();
              byte[] bytes = Encoding.UTF8.GetBytes(manifestJson);
              entryStream.Write(bytes, 0, bytes.Length);
          });
    }

    private static McpbPackageService CreateService(string sqlitePath)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kautuka:ContainerStateRoot"] = Path.GetDirectoryName(sqlitePath) ?? throw new InvalidOperationException("Expected sqlite path to include a parent directory."),
            })
            .Build();

        var database = new KautukaDatabase(configuration);
        database.EnsureReady();
        return new McpbPackageService(database);
    }

    private static string CreateTempSqlitePath()
        => Path.Combine(Path.GetTempPath(), "kautuka-mcpb-tests", Guid.NewGuid().ToString("N"), "kautuka.db");
}
