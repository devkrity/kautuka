using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.OciHook;

if (!OperatingSystem.IsLinux())
{
    await Console.Error.WriteLineAsync("The Kautuka OCI hook requires Linux.").ConfigureAwait(false);
    return 1;
}

string? stateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
if (string.IsNullOrWhiteSpace(stateHome))
    stateHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
if (!Path.IsPathFullyQualified(stateHome))
{
    await Console.Error.WriteLineAsync("The OCI hook state directory must be absolute.").ConfigureAwait(false);
    return 1;
}

string stateDirectory = Path.Combine(stateHome, "kautuka", "nac");
string ownersPath = Path.Combine(stateDirectory, "netns-owners.json");
string lockPath = Path.Combine(stateDirectory, "netns-owners.lock");
DateTimeOffset startedAt = DateTimeOffset.UtcNow;
long started = Stopwatch.GetTimestamp();
HookPolicy.State? state = null;
List<string> addresses = [];
ulong? inode = null;
string rulesHash = string.Empty;
string failure = "invalid_state";

try
{
    failure = "child_signal_setup_failed";
    HookSignals.EnableChildExitNotification();
    failure = "invalid_state";
    state = HookInput.ParseState(ReadState());
    if (string.IsNullOrWhiteSpace(state.Id))
        return Finish(false, "invalid_state");

    failure = "missing_netns_inode";
    using var target = HookNamespace.Open(state.Pid);
    inode = target.Identity.NetworkNamespaceInode;

    failure = "no_ip";
    string ipTool = FindSystemTool("ip");
    HookProcess.Result addressResult = await target.RunAsync(ipTool, ["-j", "-4", "addr", "show"]).ConfigureAwait(false);
    if (addressResult.ExitCode != 0)
        return Finish(false, failure);
    addresses = IpAddressLookup.Parse(addressResult.Stdout);

    failure = "owners_unreadable";
    var allowlist = AllowlistFile.Read(Path.Combine(stateDirectory, "infra-allowlist.json"));
    var owners = OwnerRegistry.Read(ownersPath);
    var decision = HookPolicy.Decide(state, addresses, allowlist.Entries, owners.Ids,
        inode.Value.ToString(CultureInfo.InvariantCulture), allowlist.Valid, allowlist.Reason, owners.Valid, owners.Reason,
        allowPendingGatewayAddress: true);
    if (!decision.Allowed)
        return Finish(false, decision.Reason);

    if (decision.Reason is "infra_allowlisted" or "outside_boundary")
        return Finish(true, decision.Reason);

    failure = "owners_write_failed";
    OwnerRegistry.Claim(ownersPath, lockPath, inode.Value, state.Id, state.Pid, target.Identity.StartTicks);

    failure = "apply_failed";
    bool packageProxy = decision.Reason == "package_proxy_rules";
    bool applianceGateway = decision.Reason == "appliance_gateway_rules";
    string address = applianceGateway ? WorkloadRulesetRenderer.ApplianceGatewayIp : addresses[0];
    string rules = WorkloadRulesetRenderer.Render(address, packageProxy, applianceGateway);
    string nftTool = FindSystemTool("nft");
    var apply = await target.RunAsync(nftTool, ["-f", "-"], rules).ConfigureAwait(false);
    if (apply.ExitCode != 0)
        return Finish(false, failure);

    var verify = await target.RunAsync(nftTool, ["-j", "list", "table", "inet", WorkloadRulesetRenderer.WorkloadTableName]).ConfigureAwait(false);
    if (verify.ExitCode != 0)
        return Finish(false, failure);
    string observedHash = BoundaryRulesetRenderer.ComputeCanonicalNftJsonHash(
        verify.Stdout, applianceGateway ? WorkloadRulesetRenderer.RequiredApplianceGatewayComments : WorkloadRulesetRenderer.RequiredWorkloadComments, address);
    if (observedHash != WorkloadRulesetRenderer.ComputeExpectedCanonicalRulesetHash(address, packageProxy, applianceGateway))
        return Finish(false, failure);
    if (!ProcessIdentity.IsLive(state.Pid, inode, target.Identity.StartTicks))
        return Finish(false, "container_identity_changed");

    rulesHash = observedHash;
    return Finish(true, decision.Reason);
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
    or InvalidDataException or JsonException or DecoderFallbackException or OperationCanceledException
    or System.ComponentModel.Win32Exception)
{
    return Finish(false, failure);
}

int Finish(bool allowed, string reason)
{
    try
    {
        HookAuditLog.Write(stateDirectory, state, addresses, inode, allowed, reason, rulesHash, startedAt, Stopwatch.GetElapsedTime(started));
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
    {
        Console.Error.WriteLine("The Kautuka OCI hook could not write its audit record.");
        if (allowed && reason is "workload_rules" or "package_proxy_rules" or "appliance_gateway_rules")
            return 1;
    }

    if (!allowed)
        Console.Error.WriteLine($"Kautuka OCI hook refused container creation ({reason}).");
    return allowed ? 0 : 1;
}

static string ReadState()
{
    const int limit = 1024 * 1024;
    byte[] bytes = new byte[limit + 1];
    using Stream input = Console.OpenStandardInput();
    int length = input.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
    if (length > limit)
        throw new InvalidDataException("OCI state exceeded the input limit.");
    return new UTF8Encoding(false, true).GetString(bytes, 0, length);
}

static string FindSystemTool(string name)
{
    foreach (string directory in new[] { "/usr/bin", "/usr/sbin" })
    {
        string path = Path.Combine(directory, name);
        if (File.Exists(path))
            return path;
    }

    throw new FileNotFoundException("A system OCI hook dependency is missing.", name);
}
