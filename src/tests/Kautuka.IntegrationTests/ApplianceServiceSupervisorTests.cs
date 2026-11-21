using System.Text.Json;

using Kautuka.Hosting;
using Kautuka.Manager.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting;

using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class ApplianceServiceSupervisorTests
{
    private const string Id = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Role = "credentialshost";

    [Fact]
    public async Task RefusesToAdoptOrRemoveAContainerOwnedByAnotherProcess()
    {
        var podman = new PodmanCommandRunner((args, _) => Task.FromResult(args[1] switch
        {
            "exists" => new PodmanCommandResult(0, "", ""),
            "inspect" => new PodmanCommandResult(0, $$$"""[{"Id":"{{{Id}}}","Config":{"Labels":null}}]""", ""),
            _ => throw new InvalidOperationException($"Unexpected Podman call: {string.Join(' ', args)}"),
        }));
        var release = new VerifiedApplianceRelease("v1.2.3", "1.2.3", new ApplianceDeploymentDescriptor(1, 1,
            new Dictionary<string, string> { [Role] = "localhost/kautuka-credentials@sha256:" + Id }));
        using var supervisor = CreateSupervisor(podman, release, Path.GetTempPath());

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => supervisor.EnsureRunningAsync(TestContext.Current.CancellationToken));
        Assert.Contains("outside appliance supervision", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LiveCredentialsHostRestartsWithItsExistingTrustAndVault()
    {
        string? fixture = Environment.GetEnvironmentVariable("KAUTUKA_CREDENTIALS_CONTAINER_TEST_ROOT");
        Assert.SkipUnless(OperatingSystem.IsLinux() && !string.IsNullOrEmpty(fixture),
            "Requires a dedicated Linux rootless Podman fixture and service archives.");
        string root = Path.GetFullPath(fixture!);
        string runtime = Path.Combine(root, "runtime");
        Assert.False(Directory.Exists(runtime));
        Directory.CreateDirectory(Path.Combine(runtime, "credentialsHost"));
        Directory.CreateDirectory(Path.Combine(runtime, Role));
        string writerLock = Path.Combine(runtime, "writer.lock");
        await File.WriteAllTextAsync(writerLock, string.Empty, TestContext.Current.CancellationToken);
        string images = Path.Combine(root, "service-images");
        byte[] deployment = await File.ReadAllBytesAsync(Path.Combine(images, "appliance-deployment.json"),
            TestContext.Current.CancellationToken);
        using var manifest = JsonDocument.Parse(deployment);
        string image = manifest.RootElement.GetProperty("services").GetProperty(Role).GetProperty("image").GetString()!;
        var release = new VerifiedApplianceRelease("v1.2.3", "1.2.3",
            new ApplianceDeploymentDescriptor(1, 1, new Dictionary<string, string> { [Role] = image }));
        var engine = new PodmanCommandRunner();
        string[] isolatedStore = ["--storage-driver", "vfs", "--root", Path.Combine(runtime, "store"),
            "--runroot", Path.Combine(runtime, "run")];
        var podman = new PodmanCommandRunner((args, token) => engine.RunAsync(
            [.. isolatedStore, .. args.Select(arg => arg == "/run/kautuka-db-writer.lock:/run/kautuka-db-writer.lock:ro,z"
                ? $"{writerLock}:/run/kautuka-db-writer.lock:ro,z" : arg)], 30, token));
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        PodmanCommandResult load = await podman.RunAsync(["load", "--input", Path.Combine(images, Role + ".tar")], 30, cancellation);
        Assert.True(load.ExitCode == 0, load.StdErr);

        using var supervisor = CreateSupervisor(podman, release, runtime);
        try
        {
            string first = await supervisor.EnsureRunningAsync(cancellation);
            string socket = Path.Combine(runtime, Role, "credentialshost.sock");
            string transport = Path.Combine(runtime, "credentialsHost", "transport");
            await WaitForReadinessAsync(socket, transport, cancellation);
            byte[] originalCa = await File.ReadAllBytesAsync(Path.Combine(transport, ".ca.cer"), cancellation);
            Assert.NotEmpty(originalCa);
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(runtime, "credentialsHost", "vault")));
            Assert.Equal(0, (await podman.RunAsync(["stop", first], 30, cancellation)).ExitCode);
            Assert.Equal(first, await supervisor.EnsureRunningAsync(cancellation));
            await WaitForReadinessAsync(socket, transport, cancellation);
            Assert.Equal(originalCa, await File.ReadAllBytesAsync(Path.Combine(transport, ".ca.cer"), cancellation));
        }
        catch (Exception ex)
        {
            PodmanCommandResult logs = await podman.RunAsync(["logs", "kautuka-appliance-credentialshost"], 10, CancellationToken.None);
            throw new InvalidOperationException($"{ex.Message}\nCredentialsHost logs:\n{logs.StdOut}\n{logs.StdErr}", ex);
        }
        finally
        {
            await podman.RunAsync(["rm", "--force", "--ignore", "--time", "0", "kautuka-appliance-credentialshost"],
                20, CancellationToken.None);
        }
    }

    private static ApplianceServiceSupervisor CreateSupervisor(PodmanCommandRunner podman, VerifiedApplianceRelease release, string root)
        => new(podman, release, root, root, root, NullLogger<ApplianceServiceSupervisor>.Instance);

    private static async Task WaitForReadinessAsync(string socket, string transport, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            if (await ApplianceCredentialsReadiness.CheckAsync(socket, transport, cancellationToken))
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
        Assert.Fail("CredentialsHost did not answer the authenticated health probe.");
    }
}
