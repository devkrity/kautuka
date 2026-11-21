using System.Collections.Concurrent;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Sandbox.Models;

namespace Kautuka.Manager.RuntimeContainer.Sandbox;

/// <summary>
/// Podman-backed sandbox tool for Linux appliance agent workspaces.
/// </summary>
public sealed class PodmanContainerTool(
    PodmanCommandRunner podmanCommandRunner,
    Hosting.NetworkAccess.WorkloadAddressAllocator? addressAllocator = null,
    Kautuka.NetworkAccessController.IWorkloadRegistry? workloadRegistry = null,
    Hosting.NetworkAccess.WorkloadPostStartVerifier? postStartVerifier = null,
    Hosting.NetworkAccess.WorkloadLaunchGate? workloadLaunchGate = null,
    WorkspaceDesiredStateStore? desiredStateStore = null,
    RuntimeImageAllowlist? runtimeImageAllowlist = null) : IContainerTool
{
    private readonly ConcurrentDictionary<string, byte> primedWorkspaces = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> driftNotes = new();
    private readonly AsyncKeyedLockPool containerLocks = new();
    private readonly PodmanCommandRunner podmanCommandRunner = podmanCommandRunner;
    private readonly Hosting.NetworkAccess.WorkloadAddressAllocator? addressAllocator = addressAllocator;
    private readonly Kautuka.NetworkAccessController.IWorkloadRegistry? workloadRegistry = workloadRegistry;
    private readonly Hosting.NetworkAccess.WorkloadPostStartVerifier? postStartVerifier = postStartVerifier;
    private readonly Hosting.NetworkAccess.WorkloadLaunchGate? workloadLaunchGate = workloadLaunchGate;
    private readonly WorkspaceDesiredStateStore? desiredStateStore = desiredStateStore;
    private readonly RuntimeImageAllowlist runtimeImageAllowlist = runtimeImageAllowlist ?? RuntimeImageAllowlist.Empty;

    public IReadOnlyList<string> TakeDriftNotes()
    {
        var notes = new List<string>();
        while (driftNotes.TryDequeue(out string? note))
        {
            notes.Add(note);
        }

        return notes;
    }

    public Task PrimeWorkspaceAsync(ContainerContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        context = NormalizeDesiredRuntimeContext(context, requireDesired: true);
        return PrimeDirectoriesAsync(context);
    }

    public async Task<ContainerContext> RebuildWorkspaceAsync(ContainerContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        context = NormalizeDesiredRuntimeContext(context, requireDesired: true);
        if (context.HasReadOnlyWorkspace())
            throw new InvalidOperationException("A skill cannot rebuild the agent workspace.");
        string containerName = BuildContainerName(context);
        await using var lease = (await containerLocks.AcquireAsync(containerName, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        await StopAndDeleteContainerCoreAsync(context, cancellationToken).ConfigureAwait(false);
        RecreateDirectory(context.HostWorkspacePath);
        primedWorkspaces[context.HostWorkspacePath] = 0;
        return context;
    }

    public async Task<bool> DeleteWorkspaceAsync(ContainerContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        context = NormalizeDesiredRuntimeContext(context);
        if (context.HasReadOnlyWorkspace())
            throw new InvalidOperationException("A skill cannot delete the agent workspace.");
        string containerName = BuildContainerName(context);
        await using var lease = (await containerLocks.AcquireAsync(containerName, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        await StopAndDeleteContainerCoreAsync(context, cancellationToken).ConfigureAwait(false);
        if (!Directory.Exists(context.HostWorkspacePath))
        {
            return false;
        }

        Directory.Delete(context.HostWorkspacePath, recursive: true);
        primedWorkspaces.TryRemove(context.HostWorkspacePath, out _);
        return true;
    }

    public async Task<OciSandboxSession> SpawnSandboxAsync(ContainerContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        context = NormalizeDesiredRuntimeContext(context, requireDesired: true);
        return await EnsureContainerRunningAsync(context, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContainerExecutionResult> RunShellAsync(
        ContainerContext context,
        string command,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
        => await RunShellWithEnvironmentAsync(
            context,
            command,
            new Dictionary<string, string>(StringComparer.Ordinal),
            timeoutSeconds,
            cancellationToken).ConfigureAwait(false);

    public async Task<ContainerExecutionResult> RunShellWithEnvironmentAsync(
        ContainerContext context,
        string command,
        IReadOnlyDictionary<string, string> environment,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        context = NormalizeDesiredRuntimeContext(context, requireDesired: true);
        ArgumentNullException.ThrowIfNull(command);

        OciSandboxSession session = await EnsureContainerRunningAsync(context, cancellationToken).ConfigureAwait(false);
        return await ExecuteInContainerAsync(context, session.ContainerName, command, timeoutSeconds, environment, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OciSandboxSession> InspectSandboxAsync(ContainerContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        context = NormalizeDesiredRuntimeContext(context, requireDesired: true);
        return (await InspectContainerStateAsync(context, cancellationToken).ConfigureAwait(false)).Session;
    }

    public async Task CloseSandboxAsync(ContainerContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        context = NormalizeDesiredRuntimeContext(context);
        await StopAndDeleteContainerAsync(context, cancellationToken).ConfigureAwait(false);
    }

    public static string BuildContainerName(ContainerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        string scopeType = NormalizeContainerSegment(context.ScopeType);
        string scopeId = NormalizeContainerSegment(context.ScopeId);

        if (string.IsNullOrWhiteSpace(scopeType))
        {
            scopeType = "agent";
        }

        if (string.IsNullOrWhiteSpace(scopeId))
        {
            scopeId = "default";
        }

        string name = $"kautuka-{scopeType}-{scopeId}";
        string userId = NormalizeContainerSegment(context.UserId);
        return string.IsNullOrWhiteSpace(userId) ? name : $"{name}-{userId}";
    }

    private static void RecreateDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
        EnsureWorkspaceDirectoryPermissions(path);
    }

    private Task PrimeDirectoriesAsync(ContainerContext context)
    {
        if (primedWorkspaces.ContainsKey(context.HostWorkspacePath))
        {
            return Task.CompletedTask;
        }

        Directory.CreateDirectory(context.HostWorkspacePath);
        EnsureWorkspaceDirectoryPermissions(context.HostWorkspacePath);
        primedWorkspaces.TryAdd(context.HostWorkspacePath, 0);
        return Task.CompletedTask;
    }

    private static void EnsureWorkspaceDirectoryPermissions(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        const UnixFileMode mode =
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute |
            UnixFileMode.GroupRead |
            UnixFileMode.GroupWrite |
            UnixFileMode.GroupExecute;

        DirectoryInfo directory = new(path);
        directory.UnixFileMode = mode;
    }

    private async Task<OciSandboxSession> EnsureContainerRunningAsync(ContainerContext context, CancellationToken cancellationToken)
    {
        context = NormalizeDesiredRuntimeContext(context, requireDesired: true);
        Hosting.NetworkAccess.IWorkloadLaunchLease acquiredLaunchLease = workloadLaunchGate is null
            ? NoopLaunchLease.Instance
            : await workloadLaunchGate.AcquireLaunchLeaseAsync(context.UserId, cancellationToken).ConfigureAwait(false);
        await using var launchLease = acquiredLaunchLease.ConfigureAwait(false);
        using CancellationTokenSource launchCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            acquiredLaunchLease.CancellationToken);
        cancellationToken = launchCancellation.Token;
        string containerName = BuildContainerName(context);
        await using var containerLease = (await containerLocks
            .AcquireAsync(containerName, cancellationToken)
            .ConfigureAwait(false)).ConfigureAwait(false);
        context = NormalizeDesiredRuntimeContext(context, requireDesired: true);
        await PrimeDirectoriesAsync(context).ConfigureAwait(false);

        ContainerInspection inspection = await InspectContainerStateAsync(context, cancellationToken).ConfigureAwait(false);
        try
        {
            context = NormalizeDesiredRuntimeContext(context, requireDesired: true);
        }
        catch
        {
            QuarantineInspectedSandboxBeforeMutation(context, inspection, forceQuarantine: true);
            throw;
        }

        bool matchesDesiredSpec = inspection.Matches(context);
        QuarantineInspectedSandboxBeforeMutation(context, inspection);
        if (inspection.Session.Running && matchesDesiredSpec)
        {
            await EnsureExistingSandboxVerifiedAsync(context, inspection, cancellationToken).ConfigureAwait(false);
            return inspection.Session;
        }

        if (inspection.Exists && !matchesDesiredSpec)
        {
            string reason = string.IsNullOrEmpty(inspection.Drift) ? "its spec label changed" : inspection.Drift;
            driftNotes.Enqueue(inspection.Session.ContainerName + " was replaced because " + reason + ".");
        }

        if (inspection.Exists)
        {
            if (matchesDesiredSpec)
            {
                OciSandboxSession? started = await TryStartExistingContainerAsync(context, inspection, cancellationToken).ConfigureAwait(false);
                if (started is not null)
                {
                    ContainerInspection afterStart = await InspectContainerStateAsync(context, cancellationToken).ConfigureAwait(false);
                    await EnsureExistingSandboxVerifiedAsync(context, afterStart, cancellationToken).ConfigureAwait(false);
                    return started;
                }
            }

            ContainerExecutionResult removal = await RunPodmanAsync(["rm", "-f", inspection.Session.ContainerName], 15, cancellationToken, inspection.Session.ContainerName).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (removal.ExitCode != 0)
            {
                throw new ContainerInspectionUnavailableException($"Sandbox '{containerName}' removal did not complete. Recreation is deferred.");
            }
        }

        string allocatedIp = (addressAllocator ?? throw new InvalidOperationException("The Manager workload address allocator is unavailable."))
            .AllocateNextIp(workloadRegistry);

        Kautuka.NetworkAccessController.RegistryEntry desiredEntry = BuildSandboxRegistryEntry(context, allocatedIp, containerName);
        Kautuka.NetworkAccessController.RegistryEntry quarantinedEntry = desiredEntry with { Egress = "deny-all", Attribution = "none" };
        if (workloadRegistry is not null && !workloadRegistry.TryRegisterQuarantined(quarantinedEntry))
        {
            throw new InvalidOperationException($"Sandbox '{containerName}' could not claim its allocated workload address without replacing another registry identity.");
        }

        IReadOnlyList<string> runArguments = BuildRunArguments(
            context,
            containerName,
            allocatedIp,
            workloadLaunchGate?.IsBraked(context.UserId) ?? true);
        ContainerExecutionResult createResult = await RunPodmanAsync(
            runArguments,
            context.DefaultTimeoutSeconds,
            cancellationToken,
            containerName,
            context.ContainerEnvironment).ConfigureAwait(false);

        bool hasCreatedId = Hosting.PodmanContainerIdentity.TryReadCanonicalLaunchOutput(createResult.StdOut, out string createdId);
        if (hasCreatedId && workloadRegistry is not null
            && !workloadRegistry.UpdateContainerId(allocatedIp, containerName, createdId))
        {
            throw new InvalidOperationException($"Sandbox '{containerName}' started, but its immutable container identity could not be bound to the quarantined workload entry.");
        }

        if (createResult.ExitCode != 0)
        {
            if (hasCreatedId)
            {
                _ = workloadRegistry?.TryQuarantine(allocatedIp, createdId);
            }

            throw new InvalidOperationException(PodmanFailureFormatter.FormatFailure(
                $"Podman run for container '{containerName}'",
                createResult.ExitCode,
                createResult.StdOut,
                createResult.StdErr));
        }

        if (!hasCreatedId && workloadRegistry is not null)
        {
            throw new InvalidOperationException($"Podman run for sandbox '{containerName}' completed without a canonical cidfile identity; the workload remains quarantined.");
        }

        if (postStartVerifier is null && workloadRegistry is not null)
        {
            throw new InvalidOperationException($"Sandbox '{containerName}' cannot be authorized because post-start verification is unavailable.");
        }

        if (postStartVerifier is not null)
        {
            await postStartVerifier.VerifyWorkloadAsync(
                hasCreatedId ? createdId : containerName,
                allocatedIp,
                compatProfile: null,
                registry: workloadRegistry,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (hasCreatedId)
        {
            PromoteSandboxAfterVerification(context, allocatedIp, createdId);
        }

        return new OciSandboxSession(containerName, true, context.ScopeType, context.ScopeId, context.UserId);
    }

    private async Task EnsureExistingSandboxVerifiedAsync(
        ContainerContext context,
        ContainerInspection inspection,
        CancellationToken cancellationToken)
    {
        if (!Hosting.PodmanContainerIdentity.IsCanonicalContainerId(inspection.CanonicalContainerId)
            || string.IsNullOrWhiteSpace(inspection.WorkloadIp))
        {
            throw new ContainerInspectionUnavailableException("The existing sandbox did not expose a canonical container ID and workload IP; it remains quarantined.");
        }

        string containerId = inspection.CanonicalContainerId!;
        string ip = inspection.WorkloadIp;
        Kautuka.NetworkAccessController.RegistryEntry desired = BuildSandboxRegistryEntry(context, ip, containerId);
        if (workloadRegistry is null)
        {
            if (postStartVerifier is not null)
            {
                await postStartVerifier.VerifyWorkloadAsync(containerId, ip, null, null, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        Kautuka.NetworkAccessController.RegistryEntry? current = workloadRegistry.FindByIp(ip);
        if (current is null)
        {
            Kautuka.NetworkAccessController.RegistryEntry candidate = desired with { Egress = "deny-all", Attribution = "none" };
            if (!workloadRegistry.TryRegisterQuarantined(candidate))
            {
                throw new ContainerInspectionUnavailableException("The existing sandbox workload address is already owned by another registry identity.");
            }

            current = candidate;
        }

        if (current != (desired with { Egress = current.Egress, Attribution = current.Attribution, RegisteredUtc = current.RegisteredUtc }))
        {
            throw new ContainerInspectionUnavailableException("The existing sandbox identity does not match the current trusted desired context.");
        }

        if (workloadRegistry.IsVerified(ip, containerId) && current.Egress == desired.Egress && current.Attribution == desired.Attribution)
        {
            try
            {
                EnsureSandboxContextIsCurrent(context);
            }
            catch
            {
                QuarantineInspectedSandboxBeforeMutation(context, inspection, forceQuarantine: true);
                throw;
            }

            return;
        }

        if (current.Egress != "deny-all" || current.Attribution != "none")
        {
            if (!workloadRegistry.TryQuarantine(ip, containerId))
            {
                throw new ContainerInspectionUnavailableException("The existing sandbox identity changed before it could be quarantined.");
            }

            current = workloadRegistry.FindByIp(ip);
        }

        if (postStartVerifier is null)
        {
            throw new InvalidOperationException("Sandbox post-start verification is unavailable; the existing workload remains quarantined.");
        }

        await postStartVerifier.VerifyWorkloadAsync(containerId, ip, null, workloadRegistry, cancellationToken: cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        workloadLaunchGate?.EnsureLaunchAllowed(context.UserId);
        PromoteSandboxAfterVerification(context, ip, containerId);
    }

    private void QuarantineInspectedSandboxBeforeMutation(
        ContainerContext context,
        ContainerInspection inspection,
        bool forceQuarantine = false)
    {
        if (workloadRegistry is null
            || !Hosting.PodmanContainerIdentity.IsCanonicalContainerId(inspection.CanonicalContainerId))
        {
            return;
        }

        Kautuka.NetworkAccessController.RegistryEntry? current = string.IsNullOrWhiteSpace(inspection.WorkloadIp)
            ? null
            : workloadRegistry.FindByIp(inspection.WorkloadIp);
        if (current is null || !string.Equals(current.ContainerId, inspection.CanonicalContainerId, StringComparison.Ordinal))
        {
            current = workloadRegistry.GetAll().FirstOrDefault(entry => string.Equals(entry.ContainerId, inspection.CanonicalContainerId, StringComparison.Ordinal));
        }

        if (current is null || !string.Equals(current.ContainerId, inspection.CanonicalContainerId, StringComparison.Ordinal))
        {
            return;
        }

        Kautuka.NetworkAccessController.RegistryEntry desired = BuildSandboxRegistryEntry(
            context,
            current.Ip,
            inspection.CanonicalContainerId!) with
        { RegisteredUtc = current.RegisteredUtc };
        if (!forceQuarantine
            && !string.IsNullOrWhiteSpace(inspection.WorkloadIp)
            && inspection.Session.Running
            && inspection.Matches(context)
            && workloadRegistry.IsVerified(current.Ip, inspection.CanonicalContainerId!)
            && current == desired)
        {
            return;
        }

        if (current.Egress != "deny-all" || current.Attribution != "none")
        {
            _ = workloadRegistry.TryQuarantine(current.Ip, inspection.CanonicalContainerId!);
        }
    }

    private void PromoteSandboxAfterVerification(ContainerContext context, string ip, string containerId)
    {
        if (workloadRegistry is null)
        {
            return;
        }

        Kautuka.NetworkAccessController.RegistryEntry? current = workloadRegistry.FindByIp(ip);
        if (current is null || !string.Equals(current.ContainerId, containerId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The sandbox registry identity changed before verification could be promoted.");
        }

        Kautuka.NetworkAccessController.RegistryEntry desired = BuildSandboxRegistryEntry(context, ip, containerId)
            with
        { RegisteredUtc = current.RegisteredUtc };
        workloadLaunchGate?.EnsureLaunchAllowed(context.UserId);
        EnsureSandboxContextIsCurrent(context);
        if (!workloadRegistry.TryPromoteVerified(current, desired))
        {
            throw new InvalidOperationException("The sandbox registry identity or desired policy changed before verification could be promoted.");
        }

    }

    private void EnsureSandboxContextIsCurrent(ContainerContext context)
    {
        if (desiredStateStore is null)
        {
            return;
        }

        WorkspaceDesiredStateSnapshot snapshot = desiredStateStore.Snapshot;
        ContainerContext? latest = snapshot.DesiredWorkspaces.FirstOrDefault(candidate =>
            string.Equals(candidate.ScopeType, context.ScopeType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.ScopeId, context.ScopeId, StringComparison.Ordinal)
            && string.Equals(candidate.UserId, context.UserId, StringComparison.Ordinal));
        if (latest is null)
        {
            throw new InvalidOperationException("Sandbox desired state changed during verification; the workload remains quarantined.");
        }

        ContainerContext expectedRuntimeContext = NormalizeDesiredRuntimeContext(context, requireDesired: true, capturedSnapshot: snapshot);
        if (expectedRuntimeContext.DesiredStateVersion != context.DesiredStateVersion
            || !string.Equals(expectedRuntimeContext.Image, context.Image, StringComparison.Ordinal)
            || !string.Equals(expectedRuntimeContext.SpecFingerprint, context.SpecFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Sandbox desired state changed during verification; the workload remains quarantined.");
        }
    }

    private ContainerContext NormalizeDesiredRuntimeContext(
        ContainerContext context,
        bool requireDesired = false,
        WorkspaceDesiredStateSnapshot? capturedSnapshot = null)
    {
        if (desiredStateStore is null)
        {
            return context;
        }

        WorkspaceDesiredStateSnapshot snapshot = capturedSnapshot ?? desiredStateStore.Snapshot;
        ContainerContext? latest = snapshot.DesiredWorkspaces.FirstOrDefault(candidate =>
            string.Equals(candidate.ScopeType, context.ScopeType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.ScopeId, context.ScopeId, StringComparison.Ordinal)
            && string.Equals(candidate.UserId, context.UserId, StringComparison.Ordinal));
        if (latest is null)
        {
            if (requireDesired)
            {
                throw new InvalidOperationException("Workspace identity is no longer present in accepted desired state; refusing sandbox launch or execution.");
            }

            return context;
        }

        string[] connectorIds = snapshot.DesiredSandboxConnectorBindings
            .Where(binding => string.Equals(binding.ScopeType, context.ScopeType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(binding.ScopeId, context.ScopeId, StringComparison.Ordinal))
            .SelectMany(static binding => binding.ConnectorIds)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        ContainerContext normalized = latest with
        {
            Image = runtimeImageAllowlist.SelectReleasedSandboxImage(latest.Image),
            DesiredStateVersion = snapshot.AcceptedVersion,
        };
        return normalized with { SpecFingerprint = WorkspaceContainerSpecFingerprint.Compute(normalized, connectorIds) };
    }

    private async Task<ContainerExecutionResult> ExecuteInContainerAsync(
        ContainerContext context,
        string containerName,
        string command,
        int? timeoutSeconds,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken)
    {
        string? environmentFile = null;
        try
        {
            var arguments = new List<string> { "exec" };
            if (environment is { Count: > 0 })
            {
                environmentFile = await WriteEphemeralEnvironmentFileAsync(environment, cancellationToken).ConfigureAwait(false);
                arguments.Add("--env-file");
                arguments.Add(environmentFile);
            }

            arguments.Add(containerName);
            arguments.Add("sh");
            arguments.Add("-lc");
            arguments.Add(command);

            return await RunPodmanAsync(
                arguments,
                timeoutSeconds ?? context.DefaultTimeoutSeconds,
                cancellationToken,
                containerName).ConfigureAwait(false);
        }
        finally
        {
            if (environmentFile is not null)
            {
                DeleteSecretFile(environmentFile);
            }
        }
    }

    internal static async Task<string> WriteEphemeralEnvironmentFileAsync(
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(Path.GetTempPath(), $"kautuka-env-{Guid.NewGuid():N}");
        byte[]? content = null;
        try
        {
            var builder = new StringBuilder();
            foreach ((string key, string value) in environment.OrderBy(static item => item.Key, StringComparer.Ordinal))
            {
                ValidateEnvironmentEntry(key, value);
                builder.Append(key).Append('=').Append(value).Append('\n');
            }

            content = Encoding.UTF8.GetBytes(builder.ToString());
            using FileStream stream = CreateOwnerOnlyFile(path);
            RestrictSecretFile(path);

            await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return path;
        }
        catch
        {
            if (File.Exists(path))
            {
                DeleteSecretFile(path);
            }

            throw;
        }
        finally
        {
            if (content is not null)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(content);
            }
        }
    }

    private static FileStream CreateOwnerOnlyFile(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 4096,
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(path, options);
    }

    private static void RestrictSecretFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                WindowsIdentity.GetCurrent().User!,
                FileSystemRights.Read | FileSystemRights.Write,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void ValidateEnvironmentEntry(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key)
            || !(char.IsLetter(key[0]) || key[0] == '_')
            || key.Skip(1).Any(static character => !(char.IsLetterOrDigit(character) || character == '_')))
        {
            throw new ArgumentException("Environment variable names must be portable identifiers.", nameof(key));
        }

        if (value.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            throw new ArgumentException($"Environment value for '{key}' contains a forbidden control character.", nameof(value));
        }
    }

    private static void DeleteSecretFile(string path)
    {
        try
        {
            long length = new FileInfo(path).Length;
            if (length > 0)
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
                byte[] zeros = new byte[Math.Min(length, 8192)];
                long remaining = length;
                while (remaining > 0)
                {
                    int count = (int)Math.Min(remaining, zeros.Length);
                    stream.Write(zeros, 0, count);
                    remaining -= count;
                }

                stream.Flush(flushToDisk: true);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    private async Task<ContainerInspection> InspectContainerStateAsync(ContainerContext context, CancellationToken cancellationToken)
    {
        string containerName = BuildContainerName(context);
        string? inspectJson = await PodmanContainerInspector.ReadAsync(podmanCommandRunner, containerName, cancellationToken).ConfigureAwait(false);
        if (inspectJson is null)
        {
            return new ContainerInspection(
                new OciSandboxSession(containerName, false, context.ScopeType, context.ScopeId, context.UserId),
                false,
                null,
                0,
                string.Empty,
                null,
                null,
                null);
        }

        string? canonicalContainerId = null;
        string? workloadIp = null;
        if (Hosting.PodmanContainerIdentity.TryRead(inspectJson, out string inspectedId, out string inspectedIp))
        {
            canonicalContainerId = inspectedId;
            workloadIp = inspectedIp;
        }

        string? imageId = await TryReadImageIdAsync(QualifyLocalImage(context.Image), cancellationToken).ConfigureAwait(false);
        string? state = null;
        long desiredVersion = 0;
        string fingerprint = string.Empty;
        string? drift = "the container inspect could not be read";
        try
        {
            if (LiveContainerSpec.TryReadLabels(inspectJson, out state, out desiredVersion, out fingerprint))
            {
                drift = LiveContainerSpec.DescribeDrift(inspectJson, LiveContainerSpec.ForSandbox(context, imageId));
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new ContainerInspectionUnavailableException($"Sandbox '{containerName}' returned an invalid inspection. Container changes are deferred.", ex);
        }

        bool running = string.Equals(state, "running", StringComparison.OrdinalIgnoreCase);
        return new ContainerInspection(
            new OciSandboxSession(containerName, running, context.ScopeType, context.ScopeId, context.UserId),
            true,
            state,
            desiredVersion,
            fingerprint,
            drift,
            canonicalContainerId,
            workloadIp);
    }

    private async Task<string?> TryReadImageIdAsync(string image, CancellationToken cancellationToken)
    {
        ContainerExecutionResult result = await RunPodmanAsync(
            ["image", "inspect", "--format", "{{.Id}}", image],
            10,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
        {
            throw new ContainerInspectionUnavailableException("Sandbox image could not be inspected. Container changes are deferred.");
        }

        return result.StdOut.Trim();
    }

    private async Task StopAndDeleteContainerAsync(ContainerContext context, CancellationToken cancellationToken)
    {
        string containerName = BuildContainerName(context);
        await using var lease = (await containerLocks.AcquireAsync(containerName, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        await StopAndDeleteContainerCoreAsync(context, cancellationToken).ConfigureAwait(false);
    }

    private async Task StopAndDeleteContainerCoreAsync(ContainerContext context, CancellationToken cancellationToken)
    {
        ContainerInspection inspection = await InspectContainerStateAsync(context, cancellationToken).ConfigureAwait(false);
        if (!inspection.Exists)
        {
            return;
        }

        ContainerExecutionResult removeResult = await RunPodmanAsync(
            ["rm", "-f", inspection.Session.ContainerName],
            15,
            CancellationToken.None,
            inspection.Session.ContainerName).ConfigureAwait(false);
        ContainerInspection observed = await InspectContainerStateAsync(context, cancellationToken).ConfigureAwait(false);
        if (removeResult.ExitCode != 0 || observed.Exists)
        {
            throw new InvalidOperationException(PodmanFailureFormatter.FormatFailure(
                $"Remove sandbox '{inspection.Session.ContainerName}'",
                removeResult.ExitCode,
                removeResult.StdOut,
                removeResult.StdErr));
        }

        if (workloadRegistry is not null)
        {
            var existing = workloadRegistry.GetAll().FirstOrDefault(e => string.Equals(e.ContainerId, inspection.Session.ContainerName, StringComparison.Ordinal));
            if (existing is not null)
            {
                workloadRegistry.Deregister(existing.Ip, inspection.Session.ContainerName);
            }
        }
    }

    private async Task<OciSandboxSession?> TryStartExistingContainerAsync(
        ContainerContext context,
        ContainerInspection inspection,
        CancellationToken cancellationToken)
    {
        ContainerInspection current = inspection;
        if (string.Equals(current.State, "stopping", StringComparison.OrdinalIgnoreCase))
        {
            current = await WaitForContainerToLeaveStoppingStateAsync(context, cancellationToken).ConfigureAwait(false);
            if (current.Session.Running)
            {
                return current.Session;
            }

            if (!current.Exists)
            {
                return null;
            }
        }

        ContainerExecutionResult startResult = await RunPodmanAsync(
            ["start", current.Session.ContainerName],
            context.DefaultTimeoutSeconds,
            cancellationToken,
            current.Session.ContainerName).ConfigureAwait(false);

        ContainerInspection afterStart = await InspectContainerStateAsync(context, cancellationToken).ConfigureAwait(false);
        if (startResult.ExitCode == 0 || afterStart.Session.Running)
        {
            return afterStart.Session;
        }

        return null;
    }

    private async Task<ContainerInspection> WaitForContainerToLeaveStoppingStateAsync(
        ContainerContext context,
        CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(1, context.DefaultTimeoutSeconds));
        ContainerInspection current = await InspectContainerStateAsync(context, cancellationToken).ConfigureAwait(false);

        while (current.Exists && string.Equals(current.State, "stopping", StringComparison.OrdinalIgnoreCase) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            current = await InspectContainerStateAsync(context, cancellationToken).ConfigureAwait(false);
        }

        return current;
    }

    public static IReadOnlyList<string> BuildRunArguments(
        ContainerContext context,
        string containerName,
        string allocatedIp,
        bool isBraked)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);

        ArgumentException.ThrowIfNullOrWhiteSpace(allocatedIp);
        string ip = allocatedIp;
        var envList = new List<KeyValuePair<string, string>>();
        foreach (KeyValuePair<string, string> kv in context.ContainerEnvironment)
        {
            if (!string.IsNullOrWhiteSpace(kv.Value))
            {
                // Pass --env KEY (without value) so Podman inherits values from ProcessStartInfo.Environment without CLI exposure.
                envList.Add(new KeyValuePair<string, string>(kv.Key, string.Empty));
            }
        }

        string image = QualifyLocalImage(context.Image);
        bool hasUserAgentIdentity = !string.IsNullOrWhiteSpace(context.UserId)
            && HasAgentIdentity(context)
            && !string.IsNullOrWhiteSpace(context.ScopeId);

        var spec = new Hosting.NetworkAccess.WorkloadContainerSpec(containerName, image, ip)
        {
            UserId = context.UserId ?? string.Empty,
            AgentId = HasAgentIdentity(context) ? context.AgentId ?? context.ScopeId : string.Empty,
            ExecContext = "agent-sandbox",
            Kind = "agent-sandbox",
            Egress = hasUserAgentIdentity ? "normal" : "deny-all",
            Persistent = true,
            WorkingDirectory = context.ContainerWorkspacePath,
            Mounts = [new Hosting.NetworkAccess.WorkloadMount(context.HostWorkspacePath, context.ContainerWorkspacePath, ReadOnly: context.HasReadOnlyWorkspace())],
            AllowedMountRoots = context.WorkspaceBaseRoot is null ? [] : [context.WorkspaceBaseRoot],
            // The hardened default user is 65532, while /home/node in the base image
            // belongs to UID 1000. Mount the home path itself so the sandbox user can
            // reach its connector install directory without changing its UID or caps.
            TmpfsMounts = ["/tmp:rw,noexec,nosuid", "/home/node:rw,nodev,nosuid,mode=1777"],
            DesiredStateVersion = context.DesiredStateVersion,
            SpecFingerprint = context.SpecFingerprint,
            ManagerRole = "agent",
            StartupGateOpen = true,
            IsBraked = isBraked,
            CpuLimit = context.DefaultCpuLimit,
            MemoryLimit = !string.IsNullOrWhiteSpace(context.DefaultMemoryLimit) ? context.DefaultMemoryLimit : null,
            PidsLimit = 256,
            Environment = envList,
            AddHosts = [$"kautuka-pkgs-proxy:{Kautuka.NetworkAccessController.NetworkAddressClassifier.PackageProxyServiceAddress}"],
        };

        return spec.Render().ToList();
    }

    /// <summary>
    /// Returns the image name qualified with the 'localhost/' registry prefix when the name is
    /// a Podman short name (contains no '/'). Short names without a registry prefix cause
    /// 'podman run' to fail on systems that have no unqualified-search-registries configured.
    /// </summary>
    private static string QualifyLocalImage(string image)
        => image.Contains('/', StringComparison.Ordinal) ? image : $"localhost/{image}";

    private static bool HasAgentIdentity(ContainerContext context)
        => string.Equals(context.ScopeType, "agent", StringComparison.OrdinalIgnoreCase)
            || (context.IsSkillScope() && !string.IsNullOrWhiteSpace(context.AgentId));

    internal static Kautuka.NetworkAccessController.RegistryEntry BuildSandboxRegistryEntry(
        ContainerContext context,
        string allocatedIp,
        string containerName)
    {
        ArgumentNullException.ThrowIfNull(context);
        bool hasUserIdentity = !string.IsNullOrWhiteSpace(context.UserId);
        bool hasAgentScope = HasAgentIdentity(context) && !string.IsNullOrWhiteSpace(context.ScopeId);
        bool hasAgentIdentity = hasUserIdentity && hasAgentScope;
        return new Kautuka.NetworkAccessController.RegistryEntry(
            allocatedIp,
            hasUserIdentity ? context.UserId : null,
            hasAgentScope ? context.AgentId ?? context.ScopeId : null,
            containerName,
            "agent-sandbox",
            null,
            "agent-sandbox",
            hasAgentIdentity ? "normal" : "deny-all",
            hasAgentIdentity ? "user-agent" : "none",
            null,
            DateTimeOffset.UtcNow);
    }

    private static string NormalizeContainerSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        bool previousWasDash = false;
        foreach (char character in value.Trim())
        {
            char normalized = char.IsLetterOrDigit(character)
                ? char.ToLowerInvariant(character)
                : '-';

            if (normalized == '-')
            {
                if (previousWasDash)
                {
                    continue;
                }

                previousWasDash = true;
            }
            else
            {
                previousWasDash = false;
            }

            builder.Append(normalized);
        }

        return builder.ToString().Trim('-');
    }

    private async Task<ContainerExecutionResult> RunPodmanAsync(
        IReadOnlyList<string> args,
        int timeoutSeconds,
        CancellationToken cancellationToken,
        string? containerName = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        PodmanCommandResult result = await this.podmanCommandRunner
            .RunAsync(args, timeoutSeconds, cancellationToken, environment)
            .ConfigureAwait(false);

        if (result.TerminationReason is ProcessTerminationReason.TimedOut or ProcessTerminationReason.CallerCancelled)
        {
            return new ContainerExecutionResult(
                result.TerminationReason == ProcessTerminationReason.CallerCancelled ? "cancelled" : "failed",
                result.ExitCode,
                result.StdOut,
                string.IsNullOrWhiteSpace(result.StdErr)
                    ? result.TerminationReason == ProcessTerminationReason.CallerCancelled ? "Command cancelled." : "Command timed out."
                    : result.StdErr,
                containerName);
        }

        return new ContainerExecutionResult(
            result.ExitCode == 0 ? "completed" : "failed",
            result.ExitCode,
            result.StdOut,
            result.StdErr,
            containerName);
    }

    private sealed record ContainerInspection(
        OciSandboxSession Session,
        bool Exists,
        string? State,
        long DesiredStateVersion,
        string SpecFingerprint,
        string? Drift,
        string? CanonicalContainerId = null,
        string? WorkloadIp = null)
    {
        public bool Matches(ContainerContext context)
            => string.IsNullOrEmpty(Drift)
                && DesiredStateVersion == context.DesiredStateVersion
                && !string.IsNullOrWhiteSpace(context.SpecFingerprint)
                && string.Equals(SpecFingerprint, context.SpecFingerprint, StringComparison.Ordinal);
    }

    private sealed class NoopLaunchLease : Hosting.NetworkAccess.IWorkloadLaunchLease
    {
        public static NoopLaunchLease Instance { get; } = new();

        public CancellationToken CancellationToken => CancellationToken.None;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
