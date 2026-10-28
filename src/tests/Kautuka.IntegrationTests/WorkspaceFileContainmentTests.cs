using System.Diagnostics;

using Kautuka.Gateway.Domain.AgentFiles;

namespace Kautuka.IntegrationTests;

public sealed class WorkspaceFileContainmentTests
{
    [Theory]
    [InlineData("read")]
    [InlineData("overwrite")]
    [InlineData("create")]
    public async Task ParentReplacedAfterAuthorizationCannotReachOutsideFile(string operation)
    {
        using var fixture = new FileFixture();
        string leaf = operation == "create" ? "new.txt" : "sentinel.txt";
        Assert.True(WorkspaceFile.TryResolveUnderRoot(fixture.Workspace, "parent/" + leaf, out string resolved));
        Directory.Move(fixture.Parent, fixture.Parked);
        await CreateLinkAsync(fixture.Parent, fixture.Outside);
        try
        {
            Assert.Throws<IOException>(() =>
            {
                using FileStream stream = operation == "read"
                    ? WorkspaceFile.OpenRead(resolved)
                    : WorkspaceFile.OpenWrite(resolved, operation == "create" ? FileMode.CreateNew : FileMode.Create);
            });
            Assert.Equal("OUTSIDE", await File.ReadAllTextAsync(fixture.OutsideFile, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(Path.Combine(fixture.Outside, "new.txt")));
        }
        finally
        {
            Directory.Delete(fixture.Parent);
            Directory.Move(fixture.Parked, fixture.Parent);
        }
    }

    [Fact]
    public void OpenFileDoesNotLockUnrelatedDirectories()
    {
        using var fixture = new FileFixture();
        using FileStream stream = WorkspaceFile.OpenRead(fixture.InsideFile);
        string moved = fixture.Outside + "-moved";
        Directory.Move(fixture.Outside, moved);
        Directory.Move(moved, fixture.Outside);
        using var reader = new StreamReader(stream);
        Assert.Equal("INSIDE", reader.ReadToEnd());
    }

    [Fact]
    public void OrdinaryReadCreateAndOverwriteStillWork()
    {
        using var fixture = new FileFixture();
        string created = Path.Combine(fixture.Parent, "new.txt");
        using (FileStream stream = WorkspaceFile.OpenWrite(created, FileMode.CreateNew))
        {
            stream.Write("long original value"u8);
        }

        Assert.Throws<IOException>(() => WorkspaceFile.OpenWrite(created, FileMode.CreateNew));
        using (FileStream stream = WorkspaceFile.OpenWrite(created, FileMode.Create))
        {
            stream.Write("new"u8);
        }

        using FileStream read = WorkspaceFile.OpenRead(created);
        using var reader = new StreamReader(read);
        Assert.Equal("new", reader.ReadToEnd());
        Assert.Equal("OUTSIDE", File.ReadAllText(fixture.OutsideFile));
    }

    [Fact]
    public async Task HardLinkedOutsideFileCannotBeTruncated()
    {
        using var fixture = new FileFixture();
        string link = Path.Combine(fixture.Parent, "alias.txt");
        await CreateLinkAsync(link, fixture.OutsideFile, directoryLink: false);
        Assert.Throws<IOException>(() => WorkspaceFile.OpenWrite(link, FileMode.Create));
        Assert.Equal("OUTSIDE", await File.ReadAllTextAsync(fixture.OutsideFile, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LinuxConcurrentParentSwapsNeverReadOrModifyOutsideFiles()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux openat2 race validation.");
        using var fixture = new FileFixture();
        using var stop = new CancellationTokenSource();
        int swaps = 0;
        Task attacker = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                Directory.Move(fixture.Parent, fixture.Parked);
                try
                {
                    Directory.CreateSymbolicLink(fixture.Parent, fixture.Outside);
                    Interlocked.Increment(ref swaps);
                    Thread.Yield();
                }
                finally
                {
                    if (new DirectoryInfo(fixture.Parent).LinkTarget is not null)
                    {
                        Directory.Delete(fixture.Parent);
                    }

                    Directory.Move(fixture.Parked, fixture.Parent);
                }
            }
        }, TestContext.Current.CancellationToken);
        try
        {
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref swaps) > 0, TimeSpan.FromSeconds(5)));
            for (int index = 0; index < 1000; index++)
            {
                try
                {
                    using FileStream stream = WorkspaceFile.OpenRead(fixture.InsideFile);
                    using var reader = new StreamReader(stream);
                    Assert.Equal("INSIDE", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
                }
                catch (IOException) { }

                try
                {
                    using FileStream stream = WorkspaceFile.OpenWrite(fixture.InsideFile, FileMode.Create);
                    stream.Write("INSIDE"u8);
                }
                catch (IOException) { }

                try
                {
                    using FileStream stream = WorkspaceFile.OpenWrite(Path.Combine(fixture.Parent, "new.txt"), FileMode.Create);
                    stream.Write("INSIDE"u8);
                }
                catch (IOException) { }
            }
        }
        finally
        {
            await stop.CancelAsync();
            await attacker;
        }

        Assert.True(swaps > 0);
        Assert.Equal("OUTSIDE", await File.ReadAllTextAsync(fixture.OutsideFile, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(fixture.Outside, "new.txt")));
    }

    private static async Task CreateLinkAsync(string link, string target, bool directoryLink = true)
    {
        using Process process = FileSystemLinkFixture.Start(link, target, directoryLink)
            ?? throw new InvalidOperationException("Could not create test link.");
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        string output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        string error = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, output + error);
    }

    private sealed class FileFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "kautuka-file-containment-" + Guid.NewGuid().ToString("N"));

        public FileFixture()
        {
            Directory.CreateDirectory(Parent);
            Directory.CreateDirectory(Outside);
            File.WriteAllText(InsideFile, "INSIDE");
            File.WriteAllText(OutsideFile, "OUTSIDE");
        }

        public string Workspace => Path.Combine(root, "workspace");
        public string Parent => Path.Combine(Workspace, "parent");
        public string Parked => Path.Combine(Workspace, "parked");
        public string Outside => Path.Combine(root, "outside");
        public string InsideFile => Path.Combine(Parent, "sentinel.txt");
        public string OutsideFile => Path.Combine(Outside, "sentinel.txt");

        public void Dispose() => Directory.Delete(root, recursive: true);
    }
}
