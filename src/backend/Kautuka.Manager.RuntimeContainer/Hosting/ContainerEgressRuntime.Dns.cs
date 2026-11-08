namespace Kautuka.Manager.RuntimeContainer.Hosting;

public sealed partial class ContainerEgressRuntime
{
    private string[] dnsBoundaryUpstreams = [];
    private DnsRevision? dnsLastGood;
    private DnsRevision? dnsStartup;
    private DnsTransactionState? dnsTransaction;
    private string? dnsRefreshError;
    internal Func<CancellationToken, Task<string[]>>? HostDnsReaderForTests { get; set; }

    public string? DnsRefreshError => Volatile.Read(ref dnsRefreshError);

    // Called under the topology gate, before the first boundary or DNS service startup.
    private async Task PrepareDnsStartupAsync(CancellationToken cancellationToken)
    {
        if (!options.FollowHostDns)
        {
            NetworkDestinationPolicy.ValidateDnsUpstreams(options.DnsUpstreams);
            dnsBoundaryUpstreams = [.. options.DnsUpstreams];
            return;
        }
        var store = new DnsTransactionStore(configurationRoot);
        dnsTransaction = store.Read();
        if (dnsTransaction is null)
        {
            if (File.Exists(Path.Combine(configurationRoot, "Corefile")))
                throw new InvalidOperationException("Existing DNS configuration has no host-follow recovery state. Retain explicit DNS settings; automatic conversion is not supported.");
            string[] upstreams = await ReadHostDnsAsync(cancellationToken).ConfigureAwait(false);
            dnsStartup = NewDnsRevision(upstreams);
            dnsTransaction = new DnsTransactionState(null, dnsStartup);
            store.Write(dnsTransaction);
        }
        else dnsStartup = dnsTransaction.LastGood ?? dnsTransaction.Pending
            ?? throw new InvalidDataException("DNS transaction has no recoverable configuration.");
        dnsLastGood = dnsTransaction.LastGood;
        dnsBoundaryUpstreams = DnsUnion(dnsTransaction.LastGood, dnsTransaction.Pending);
        await WriteAsync("dns-probe-resolv.conf", "nameserver 127.0.0.1\noptions timeout:1 attempts:1\n", cancellationToken).ConfigureAwait(false);
    }

    private async Task<string[]> ReadHostDnsAsync(CancellationToken cancellationToken)
    {
        if (HostDnsReaderForTests is { } reader)
        {
            string[] supplied = await reader(cancellationToken).ConfigureAwait(false);
            NetworkDestinationPolicy.ValidateDnsUpstreams(supplied);
            return supplied;
        }
        string resolver = await File.ReadAllTextAsync("/etc/resolv.conf", cancellationToken).ConfigureAwait(false);
        const string systemdPath = "/run/systemd/resolve/resolv.conf";
        string? systemd = File.Exists(systemdPath) ? await File.ReadAllTextAsync(systemdPath, cancellationToken).ConfigureAwait(false) : null;
        return HostDnsUpstreams.Resolve(resolver, systemd);
    }

    private static DnsRevision NewDnsRevision(string[] upstreams)
    {
        CoreDnsConfiguration configuration = CoreDnsConfiguration.Create(upstreams);
        return new DnsRevision(configuration.Corefile, configuration.CanaryName, configuration.CanaryAddress, upstreams);
    }

    private static string[] DnsUnion(DnsRevision? first, DnsRevision? second)
        => [.. (first?.Upstreams ?? []).Concat(second?.Upstreams ?? []).Distinct(StringComparer.Ordinal)];

    private async Task ConfirmDnsStartupAsync(CancellationToken cancellationToken)
    {
        if (!options.FollowHostDns) return;
        DnsRevision confirmed = dnsStartup ?? throw new InvalidOperationException("DNS startup configuration is missing.");
        await WaitForDnsCanaryAsync(confirmed, cancellationToken).ConfigureAwait(false);
        var store = new DnsTransactionStore(configurationRoot);
        if (dnsLastGood is null) store.Write(new DnsTransactionState(confirmed, null));
        else if (dnsTransaction?.Pending is not null)
        {
            dnsBoundaryUpstreams = [.. confirmed.Upstreams];
            try { await ApplyBoundaryAsync(cancellationToken).ConfigureAwait(false); }
            catch { dnsBoundaryUpstreams = DnsUnion(dnsTransaction.LastGood, dnsTransaction.Pending); throw; }
            store.Write(new DnsTransactionState(confirmed, null));
        }
        dnsLastGood = confirmed;
        dnsTransaction = new DnsTransactionState(confirmed, null);
    }

    public async Task RefreshHostDnsAsync(CancellationToken cancellationToken)
    {
        if (!Enabled || !options.FollowHostDns) return;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ready) return;
            try
            {
                await ReconcileHostDnsAsync(cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref dnsRefreshError, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Volatile.Write(ref dnsRefreshError, "Host DNS refresh failed; DNS configuration recovery may be pending: " + ex.Message);
                throw;
            }
        }
        finally { gate.Release(); }
    }

    private async Task TryRefreshDnsAtStartupAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ReconcileHostDnsAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref dnsRefreshError, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Volatile.Write(ref dnsRefreshError, "Host DNS refresh failed; DNS configuration recovery may be pending: " + ex.Message);
        }
    }

    private async Task ReconcileHostDnsAsync(CancellationToken cancellationToken)
    {
        DnsRevision old = dnsLastGood ?? throw new InvalidOperationException("No confirmed DNS configuration exists.");
        if (dnsTransaction?.Pending is { } alternate)
        {
            string[] recoveryUnion = DnsUnion(old, alternate);
            NetworkAddressPool.Parse(options.NetworkPool).ValidateResolvers(recoveryUnion);
            dnsBoundaryUpstreams = recoveryUnion;
            await ApplyBoundaryAsync(cancellationToken).ConfigureAwait(false);
            await WriteAsync("Corefile", old.Corefile, cancellationToken).ConfigureAwait(false);
            await WaitForDnsCanaryAsync(old, cancellationToken).ConfigureAwait(false);
            dnsBoundaryUpstreams = [.. old.Upstreams];
            try { await ApplyBoundaryAsync(cancellationToken).ConfigureAwait(false); }
            catch { dnsBoundaryUpstreams = DnsUnion(old, alternate); throw; }
            new DnsTransactionStore(configurationRoot).Write(new DnsTransactionState(old, null));
            dnsTransaction = new DnsTransactionState(old, null);
        }
        string[] desired = await ReadHostDnsAsync(cancellationToken).ConfigureAwait(false);
        if (old.Upstreams.SequenceEqual(desired, StringComparer.Ordinal)) return;
        DnsRevision candidate = NewDnsRevision(desired);
        var store = new DnsTransactionStore(configurationRoot);
        string[] candidateUnion = DnsUnion(old, candidate);
        // Reject a conflicting resolver before changing even the in-memory boundary.
        NetworkAddressPool.Parse(options.NetworkPool).ValidateResolvers(candidateUnion);
        dnsBoundaryUpstreams = candidateUnion;
        await ApplyBoundaryAsync(cancellationToken).ConfigureAwait(false);
        dnsTransaction = new DnsTransactionState(old, candidate);
        store.Write(dnsTransaction);
        try
        {
            await WriteAsync("Corefile", candidate.Corefile, cancellationToken).ConfigureAwait(false);
            await WaitForDnsCanaryAsync(candidate, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Keep the union until the restored active configuration is positively confirmed.
            try
            {
                await WriteAsync("Corefile", old.Corefile, CancellationToken.None).ConfigureAwait(false);
                await WaitForDnsCanaryAsync(old, CancellationToken.None).ConfigureAwait(false);
                dnsBoundaryUpstreams = [.. old.Upstreams];
                await ApplyBoundaryAsync(CancellationToken.None).ConfigureAwait(false);
                store.Write(new DnsTransactionState(old, null));
                dnsTransaction = new DnsTransactionState(old, null);
            }
            catch { dnsBoundaryUpstreams = DnsUnion(old, candidate); }
            throw;
        }
        // Pending now records the previous revision until its firewall allowance is retired.
        var accepted = new DnsTransactionState(candidate, old);
        store.Write(accepted);
        dnsTransaction = accepted;
        dnsLastGood = candidate;
        dnsBoundaryUpstreams = [.. candidate.Upstreams];
        try { await ApplyBoundaryAsync(cancellationToken).ConfigureAwait(false); }
        catch { dnsBoundaryUpstreams = DnsUnion(candidate, old); throw; }
        store.Write(new DnsTransactionState(candidate, null));
        dnsTransaction = new DnsTransactionState(candidate, null);
    }

    private async Task WaitForDnsCanaryAsync(DnsRevision revision, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (true)
            {
                try
                {
                    PodmanCommandResult query = await HelperAsync("/usr/bin/getent", ["-s", "dns", "ahostsv4", revision.CanaryName], deadline.Token, dnsProbe: true).ConfigureAwait(false);
                    if (query.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                        .Any(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() == revision.CanaryAddress)) return;
                }
                catch (InvalidOperationException) { }
                await Task.Delay(TimeSpan.FromSeconds(2), deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("CoreDNS did not acknowledge the intended configuration before the reload deadline.");
        }
    }
}
