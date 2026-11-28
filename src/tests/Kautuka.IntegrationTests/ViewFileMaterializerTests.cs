using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class ViewFileMaterializerTests
{
    [Theory]
    [InlineData("view-files")]
    [InlineData("hash")]
    [InlineData("file")]
    public void ViewFileMaterializationRefusesSymbolicLinks(string component)
    {
        string root = CreateWorkspaceDirectory();
        string outside = CreateWorkspaceDirectory();
        try
        {
            string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("f1"u8))[..12];
            string views = Path.Combine(root, "view-files");
            string folder = Path.Combine(views, hash);
            string target = Path.Combine(outside, "notes.txt");
            File.WriteAllText(target, "original");
            if (component == "view-files")
                Directory.CreateSymbolicLink(views, outside);
            else if (component == "hash")
            {
                Directory.CreateDirectory(views);
                Directory.CreateSymbolicLink(folder, outside);
            }
            else
            {
                Directory.CreateDirectory(folder);
                File.CreateSymbolicLink(Path.Combine(folder, "notes.txt"), target);
            }
            string result = ViewFileMaterializer.Materialize(ViewFileResult, root);
            Assert.Contains("\"unavailable\":true", result, StringComparison.Ordinal);
            Assert.DoesNotContain("\"path\"", result, StringComparison.Ordinal);
            Assert.Equal("original", File.ReadAllText(target));
            Assert.Single(Directory.EnumerateFileSystemEntries(outside));
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(outside, true);
        }
    }

    private const string ViewFileResult = """
        {"structured_content":{"view":{"blocks":[{"type":"file","fileId":"f1"}]},"files":[{"fileId":"f1","name":"notes.txt","base64":"aGVsbG8="}]}}
        """;

    [Theory]
    [InlineData(4, 1, false)]
    [InlineData(5, 0, false)]
    [InlineData(5, 1, true)]
    public void Version027ViewFileObeysConfiguredLimitsAndReplacesExistingFile(int bytes, int count, bool accepted)
    {
        string root = CreateWorkspaceDirectory();
        try
        {
            string result = ViewFileMaterializer.Materialize(ViewFileResult, root);
            Assert.Contains("view-files/", result, StringComparison.Ordinal);
            string path = Assert.Single(Directory.GetFiles(root, "*.txt", SearchOption.AllDirectories));
            File.WriteAllText(path, "previous");
            result = ViewFileMaterializer.Materialize(ViewFileResult, root, new ViewFileOptions { MaxFileBytes = bytes, MaxFilesPerResult = count });
            Assert.Equal(accepted ? "hello" : "previous", File.ReadAllText(path));
            Assert.Equal(!accepted, result.Contains("\"unavailable\":true", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void MaterializerStoresBytesBesideTheView()
    {
        string root = CreateWorkspaceDirectory();
        try
        {
            const string result = """
                {"structured_content":{"view":{"blocks":[{"type":"file","fileId":"f1","name":"notes.txt","mediaType":"text/plain"}]},"files":[{"fileId":"f1","name":"notes.txt","base64":"aGVsbG8="}]}}
                """;
            string stored = ViewFileMaterializer.Materialize(result, root);
            Assert.DoesNotContain("aGVsbG8=", stored, StringComparison.Ordinal);
            Assert.DoesNotContain("\"files\"", stored, StringComparison.Ordinal);
            Assert.Contains("view-files/", stored, StringComparison.Ordinal);
            string saved = Assert.Single(Directory.GetFiles(root, "notes.txt", SearchOption.AllDirectories));
            Assert.Equal("hello", File.ReadAllText(saved));

            string stripped = ViewFileMaterializer.Materialize(result, hostDirectory: null);
            Assert.Contains("\"unavailable\":true", stripped, StringComparison.Ordinal);
            Assert.DoesNotContain("aGVsbG8=", stripped, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DescriptorFilesUsesGenericErrorMessagesAndRequiresUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<PlatformNotSupportedException>(() => DescriptorFiles.OpenAbsoluteDirectory("/private/tmp", requireRootOwnership: false));
            return;
        }

        InvalidDataException relativeError = Assert.Throws<InvalidDataException>(() => DescriptorFiles.OpenAbsoluteDirectory("relative", requireRootOwnership: false));
        Assert.DoesNotContain("trusted appliance release input", relativeError.Message, StringComparison.OrdinalIgnoreCase);

        InvalidDataException traversalError = Assert.Throws<InvalidDataException>(() => DescriptorFiles.OpenAbsoluteDirectory("/tmp/../etc", requireRootOwnership: false));
        Assert.DoesNotContain("trusted appliance release input", traversalError.Message, StringComparison.OrdinalIgnoreCase);

        string missingPath = (OperatingSystem.IsMacOS() ? "/private/tmp/kautuka-missing-" : "/tmp/kautuka-missing-") + Guid.NewGuid().ToString("N");
        FileNotFoundException missingError = Assert.Throws<FileNotFoundException>(() => DescriptorFiles.OpenAbsoluteDirectory(missingPath, requireRootOwnership: false));
        Assert.DoesNotContain("trusted appliance release input", missingError.Message, StringComparison.OrdinalIgnoreCase);

        string root = CreateWorkspaceDirectory();
        Directory.CreateDirectory(root);
        try
        {
            string regularFile = Path.Combine(root, "plain.txt");
            File.WriteAllText(regularFile, "data");
            using var dirHandle = DescriptorFiles.OpenAbsoluteDirectory(root, requireRootOwnership: false);
            IOException typeMismatch = Assert.Throws<IOException>(() => DescriptorFiles.OpenChecked(dirHandle, "plain.txt", DescriptorFiles.FileFlags, DescriptorFiles.DirectoryType, requireRootOwnership: false));
            Assert.DoesNotContain("trusted appliance release input", typeMismatch.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ViewFileMaterializationAllowsSandboxGroupReadWithoutWriteOrEscape()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string root = Path.Combine("/tmp", "kautuka-view-perms-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        uint previousUmask = SetProcessUmask(0x3f); // 0077 restrictive service umask
        try
        {
            // Simulate an existing 0700 view-files directory from before the permission fix.
            string viewsDir = Path.Combine(root, "view-files");
            Directory.CreateDirectory(viewsDir);
            File.SetUnixFileMode(viewsDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            string materialized = ViewFileMaterializer.Materialize(ViewFileResult, root);
            Assert.Contains("view-files/", materialized, StringComparison.Ordinal);
        }
        finally
        {
            _ = SetProcessUmask(previousUmask);
        }

        try
        {
            string viewsDir = Path.Combine(root, "view-files");
            string hashDir = Assert.Single(Directory.GetDirectories(viewsDir));
            string filePath = Path.Combine(hashDir, "notes.txt");

            UnixFileMode expectedDirMode =
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
            UnixFileMode expectedFileMode =
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;

            Assert.Equal(expectedDirMode, File.GetUnixFileMode(viewsDir));
            Assert.Equal(expectedDirMode, File.GetUnixFileMode(hashDir));
            Assert.Equal(expectedFileMode, File.GetUnixFileMode(filePath));

            // Prove inside a rootless user namespace matching Podman's --user 65532:65532 --group-add=0 mapping
            // (where host UID/GID map to 0:0 and a child drops to 65532:65532 with supplementary group 0)
            // that the sandbox user can read notes.txt, cannot write to it, and cannot replace it.
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "unshare",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("--user");
            startInfo.ArgumentList.Add("--map-root-user");
            startInfo.ArgumentList.Add("python3");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("""
                import errno, os, pathlib, sys
                target = pathlib.Path(sys.argv[1])
                try:
                    os.setgroups([0])
                    os.setgid(65532)
                    os.setuid(65532)
                except OSError as exc:
                    if exc.errno in (errno.EINVAL, errno.EPERM):
                        print("SKIP_SUBUID")
                        sys.exit(0)
                    raise
                assert target.read_text(encoding="utf-8") == "hello"
                try:
                    target.write_text("tampered", encoding="utf-8")
                    sys.exit("expected EACCES on write")
                except PermissionError:
                    pass
                try:
                    (target.parent / "extra.txt").write_text("tampered", encoding="utf-8")
                    sys.exit("expected EACCES on directory write")
                except PermissionError:
                    pass
                print("SANDBOX_READ_OK")
                """);
            startInfo.ArgumentList.Add(filePath);

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process is not null)
            {
                string stdout = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
                string stderr = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
                await process.WaitForExitAsync(TestContext.Current.CancellationToken);
                if (process.ExitCode == 0)
                {
                    Assert.True(
                        stdout.Contains("SANDBOX_READ_OK", StringComparison.Ordinal)
                        || stdout.Contains("SKIP_SUBUID", StringComparison.Ordinal),
                        $"Unexpected userns output: stdout={stdout}; stderr={stderr}");
                }
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

#pragma warning disable SYSLIB1054
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.SafeDirectories)]
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "umask")]
    private static extern uint SetProcessUmask(uint mask);
#pragma warning restore SYSLIB1054

    [Fact]
    public void FileCountLimitKeepsFirstFileAndMarksTheNextUnavailable()
    {
        string root = CreateWorkspaceDirectory();
        try
        {
            const string result = """
                {"structured_content":{"view":{"blocks":[{"type":"file","fileId":"first"},{"type":"file","fileId":"second"}]},"files":[{"fileId":"first","name":"first.txt","base64":"aGVsbG8="},{"fileId":"second","name":"second.txt","base64":"aGVsbG8="}]}}
                """;
            var saved = System.Text.Json.Nodes.JsonNode.Parse(ViewFileMaterializer.Materialize(result, root,
                new ViewFileOptions { MaxFilesPerResult = 1 }))!;
            var blocks = saved["structured_content"]!["view"]!["blocks"]!;
            Assert.NotNull(blocks[0]!["path"]);
            Assert.True(blocks[1]!["unavailable"]!.GetValue<bool>());
            Assert.NotEmpty(blocks[1]!["unavailableReason"]!.GetValue<string>());
            Assert.Null(blocks[1]!["path"]);
            Assert.Equal("hello", File.ReadAllText(Assert.Single(Directory.GetFiles(root, "*.txt", SearchOption.AllDirectories))));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string CreateWorkspaceDirectory()
        => Directory.CreateDirectory(Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(),
            "kautuka-view-test-" + Guid.NewGuid().ToString("N"))).FullName;
}
