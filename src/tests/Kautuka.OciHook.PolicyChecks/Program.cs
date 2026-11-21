using Kautuka.OciHook;

if (args.Length != 0 && args[0] == "child")
{
    if (args[1] == "sleep") { await Task.Delay(TimeSpan.FromSeconds(30)); return; }
    if (args[1] == "large") { Console.Write(new string('x', 65536)); return; }
    Console.Write(args[1] + ":" + await Console.In.ReadToEndAsync());
    return;
}

var empty = new Dictionary<string, HookPolicy.Infrastructure>();
var owners = new Dictionary<string, string?>();
var workload = new HookPolicy.State("workload", 123, "workload");
void Check(string expected, HookPolicy.Decision actual)
{
    if (actual.Reason != expected) throw new InvalidOperationException($"Expected {expected}, got {actual.Reason}");
}
Check("missing_pid", HookPolicy.Decide(null, [], empty, owners, null));
Check("no_ip", HookPolicy.Decide(workload, [], empty, owners, null));
Check("workload_off_network", HookPolicy.Decide(workload, ["192.0.2.1"], empty, owners, null));
Check("outside_boundary", HookPolicy.Decide(new("other", 123), ["192.0.2.1"], empty, owners, null));
Check("missing_allowlist", HookPolicy.Decide(workload, ["10.247.1.5"], empty, owners, "100", false));
Check("workload_rules", HookPolicy.Decide(workload, ["10.247.1.5"], empty, owners, "100"));
Check("shared_netns", HookPolicy.Decide(workload, ["10.247.1.5"], empty, new Dictionary<string, string?> { ["100"] = "other" }, "100"));
Check("reserved_range", HookPolicy.Decide(workload, ["10.247.0.9"], empty, owners, "100"));
var infra = new Dictionary<string, HookPolicy.Infrastructure> { ["boundary"] = new("egress-boundary", ["10.247.0.2", "10.248.0.2"], [["10.247.0.2", "10.248.0.2", "10.248.0.3"]]) };
Check("infra_allowlisted", HookPolicy.Decide(new("boundary", 123), ["10.248.0.3", "10.247.0.2", "10.248.0.2"], infra, owners, "100"));


var parsedState = HookInput.ParseState("""{"id":"boundary","pid":123,"annotations":{"io.kautuka.role":"workload"}}""");
var parsedAllowlist = HookInput.ParseAllowlist("""{"version":1,"entries":[{"id":"boundary","role":"egress-boundary","expected_ipv4":["10.247.0.2","10.248.0.2"],"expected_ipv4_alternates":[["10.247.0.2","10.248.0.2","10.248.0.3"]]}]}""");
Check("infra_allowlisted", HookPolicy.Decide(parsedState, ["10.247.0.2", "10.248.0.2"], parsedAllowlist, owners, "100"));
var dictionaryFormat = HookInput.ParseAllowlist("""{"boundary":["10.247.0.2","10.248.0.2"]}""");
Check("infra_allowlisted", HookPolicy.Decide(parsedState, ["10.247.0.2", "10.248.0.2"], dictionaryFormat, owners, "100"));
void Reject(Action action)
{
    try { action(); }
    catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or InvalidOperationException) { return; }
    throw new InvalidOperationException("Expected invalid input to be rejected.");
}
Reject(() => HookInput.ParseState("""{"pid":"123"}"""));
Reject(() => HookInput.ParseState("""{"pid":0}"""));
Reject(() => HookInput.ParseAllowlist("""{"entries":[{"id":"a"},{"id":"a"}]}"""));
Reject(() => HookInput.ParseAllowlist("""{"entries":[{"id":"a","expected_ipv4_alternates":null}]}"""));
Reject(() => HookInput.ParseAllowlist("""{"entries":[{"id":"a","expected_ipv4":[null]}]}"""));
Reject(() => HookInput.ParseState("""{"pid":123,"pid":124}"""));
if (OperatingSystem.IsLinux())
{
    string temp = Path.Combine(Path.GetTempPath(), "kautuka-hook-check-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temp);
    try
    {
        string path = Path.Combine(temp, "allowlist.json");
        if (AllowlistFile.Read(path).Reason != "missing_allowlist") throw new InvalidOperationException("Missing allowlist accepted.");
        File.WriteAllText(path, """{"entries":[]}""");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        if (!AllowlistFile.Read(path).Valid) throw new InvalidOperationException("Valid allowlist rejected.");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        if (AllowlistFile.Read(path).Reason != "bad_allowlist_mode") throw new InvalidOperationException("Mode validation failed.");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        string link = Path.Combine(temp, "link.json");
        File.CreateSymbolicLink(link, path);
        if (AllowlistFile.Read(link).Valid) throw new InvalidOperationException("Symlink accepted.");
        File.WriteAllText(path, new string(' ', 1024 * 1024 + 1));
        if (AllowlistFile.Read(path).Reason != "allowlist_too_large") throw new InvalidOperationException("Oversized allowlist accepted.");
        File.WriteAllText(path, "{");
        if (AllowlistFile.Read(path).Valid) throw new InvalidOperationException("Malformed JSON accepted.");
    }
    finally { Directory.Delete(temp, recursive: true); }
}

var expectedHashes = new[] { "dd85f4170874ab91c0e05aaffb7b9ce718fb826d7a6f80d8e19832085531f512", "e78a9f44c8f2a76fd454812286ed543afb41915c27bc445533f04f5c4501f9fb" };
var renderedHashes = new[] { "a06e5991f449d8641f327f24b97491a361dc2a2dd71879575793ee644e06a7f5", "23ca1bd42b1aa35b2e1ffdf94b4b860c75a1d6cbd54ba89b04460f386aeb5e40" };
for (int i = 0; i < 2; i++)
{
    bool proxy = i == 1;
    string actual = Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess.WorkloadRulesetRenderer.ComputeExpectedCanonicalRulesetHash("10.247.1.5", proxy);
    if (actual != expectedHashes[i]) throw new InvalidOperationException($"Canonical hash mismatch: {actual}");
    string rendered = Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess.WorkloadRulesetRenderer.Render("10.247.1.5", proxy).ReplaceLineEndings("\n");
    string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rendered)));
    if (hash != renderedHashes[i]) throw new InvalidOperationException($"Rendered rules mismatch: {hash}");
}
string template = Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess.BoundaryRulesetRenderer.ResolveTemplate("kautuka_workload.nft.json.tmpl").Replace("{{IP}}", "10.247.1.5", StringComparison.Ordinal);
var nft = System.Text.Json.Nodes.JsonNode.Parse(template)!;
var nftEntries = nft["nftables"]!.AsArray();
nftEntries.Insert(0, System.Text.Json.Nodes.JsonNode.Parse("""{"metainfo":{"version":"test"}}"""));
var first = nftEntries[1]!.AsObject();
first["handle"] = 123;
string transientHash = Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess.BoundaryRulesetRenderer.ComputeCanonicalNftJsonHash(nft.ToJsonString(), Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess.WorkloadRulesetRenderer.RequiredWorkloadComments, "10.247.1.5");
if (transientHash != expectedHashes[0]) throw new InvalidOperationException("Transient nft metadata changed canonical hash.");
Reject(() => Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess.BoundaryRulesetRenderer.ComputeCanonicalNftJsonHash(template, ["absent_comment"], "10.247.1.5"));
Reject(() => Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess.BoundaryRulesetRenderer.ComputeCanonicalNftJsonHash(template, [], "10.247.99.99"));

string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing process path.");
string[] childPrefix = string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase)
    ? [Environment.GetCommandLineArgs()[0]] : [];
var processResult = await HookProcess.RunAsync(executable, [.. childPrefix, "child", "two words;literal"], "stdin", TimeSpan.FromSeconds(10), 1024);
if (processResult.ExitCode != 0 || processResult.Stdout != "two words;literal:stdin" || processResult.Stderr != "")
    throw new InvalidOperationException("Process argument or input handling failed.");
try
{
    await HookProcess.RunAsync(executable, [.. childPrefix, "child", "sleep"], null, TimeSpan.FromMilliseconds(300), 1024);
    throw new InvalidOperationException("Process timeout not enforced.");
}
catch (OperationCanceledException) { }
try
{
    await HookProcess.RunAsync(executable, [.. childPrefix, "child", "large"], null, TimeSpan.FromSeconds(10), 1024);
    throw new InvalidOperationException("Process output limit not enforced.");
}
catch (InvalidDataException) { }
var addresses = IpAddressLookup.Parse("""[{"addr_info":[{"local":"127.0.0.1"},{"local":"10.247.1.5"}]},{"addr_info":[{"local":"10.248.0.2"}]}]""");
if (!addresses.SequenceEqual(["10.247.1.5", "10.248.0.2"])) throw new InvalidOperationException("IP address parsing failed.");
Reject(() => IpAddressLookup.Parse("""[{"addr_info":{}}]"""));
Reject(() => IpAddressLookup.Parse("""[{"addr_info":[{"local":123}]}]"""));
if (ProcessIdentity.ParseStartTicks("invalid") is not null) throw new InvalidOperationException("Invalid stat accepted.");
if (OperatingSystem.IsLinux())
{
    var identity = ProcessIdentity.TryRead(Environment.ProcessId) ?? throw new InvalidOperationException("Unable to read current process identity.");
    if (!ProcessIdentity.IsLive(identity.Pid, identity.NetworkNamespaceInode, identity.StartTicks)) throw new InvalidOperationException("Live process not recognized.");
    if (ProcessIdentity.IsLive(identity.Pid, identity.NetworkNamespaceInode, identity.StartTicks + 1)) throw new InvalidOperationException("Mismatched process identity accepted.");
    if (ProcessIdentity.IsLive(int.MaxValue, null, null)) throw new InvalidOperationException("Missing process accepted.");
}
