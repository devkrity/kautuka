import React from 'react';
import { isAxiosError } from 'axios';
import {
  ArrowRight,
  Check,
  Globe,
  LockKeyhole,
  RefreshCw,
  Search,
  ShieldCheck,
  ShieldOff,
} from 'lucide-react';
import { PageIntro } from '@/components/layout/PageIntro';
import { PageState } from '@/components/layout/PageState';
import { Alert } from '@/components/ui/alert';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import { NetworkAudit } from '@/features/network-access/NetworkAudit';
import { NetworkSource } from '@/features/network-access/NetworkSource';
import { LocalNetworkChoice } from '@/features/network-access/LocalNetworkChoice';
import { destinationScope } from '@/features/network-access/destinationScope';
import { useCurrentUser } from '@/features/auth';
import {
  identityKey,
  identityLabel,
  useNetworkAccess,
  useNetworkAccessChange,
  useNetworkAudit,
  type NetworkRule,
} from '@/features/network-access/networkAccess';

type View = 'requests' | 'permissions' | 'activity';

export function NetworkAccessPage() {
  const { data: currentUser } = useCurrentUser();
  const query = useNetworkAccess();
  const audit = useNetworkAudit();
  const change = useNetworkAccessChange();
  const [view, setView] = React.useState<View>('requests');
  const [search, setSearch] = React.useState('');
  const [reviewing, setReviewing] = React.useState<{
    id: string;
    revision: number;
    allowPrivateNetwork: boolean;
  } | null>(null);
  const [selected, setSelected] = React.useState('');
  const [destination, setDestination] = React.useState('');
  const [port, setPort] = React.useState('443');
  const [allowed, setAllowed] = React.useState('allow');
  const [allowPrivateNetwork, setAllowPrivateNetwork] = React.useState(false);
  const [editing, setEditing] = React.useState(false);
  const [editRevision, setEditRevision] = React.useState<number | null>(null);
  const reviewPanel = React.useRef<HTMLDivElement>(null);
  React.useEffect(() => {
    if (reviewing?.id) reviewPanel.current?.focus();
  }, [reviewing?.id]);
  const data = query.data;
  const matches = (value: string) => value.toLowerCase().includes(search.trim().toLowerCase());
  const pending = data?.requests.filter((item) => item.status === 'pending') ?? [];
  const decisions = data?.requests.filter((item) => item.status !== 'pending') ?? [];
  const rules = data?.rules ?? [];
  const identities = new Map(
    [
      ...(data?.sources ?? []),
      ...(data?.defaults.map((item) => item.identity) ?? []),
      ...rules.map((item) => item.identity),
      ...(data?.requests.map((item) => item.identity) ?? []),
    ].map((identity) => [identityKey(identity), identity])
  );
  const chosen = identities.get(selected);
  const busy = change.isPending || !data?.enabled;
  const restriction = (host: string) =>
    destinationScope(host) === 'metadata'
      ? 'Cloud-metadata access is always blocked, including for administrators.'
      : destinationScope(host) === 'blocked'
        ? 'This is a reserved or platform-control address and cannot be approved here.'
        : destinationScope(host) === 'private' && currentUser?.role !== 'admin'
          ? 'This is a private-network address. An administrator must approve LAN access.'
          : null;
  const gaps = audit.data?.pages[0]?.coverageGaps ?? 0;
  const error = change.error
    ? isAxiosError(change.error) && change.error.response?.status === 409
      ? 'The policy changed while you were editing. The latest policy has been loaded; review it and try again.'
      : isAxiosError(change.error) && change.error.response?.status === 403
        ? 'You do not have permission to approve this destination. Ask an administrator to review it.'
        : isAxiosError(change.error) && change.error.response?.status === 400
          ? 'This destination is not permitted or is invalid. Use an exact hostname or IPv4 address and a TCP port from 1 to 65535.'
          : 'The change could not be saved or applied. Refresh to check its current status before retrying.'
    : null;
  const edit = (rule?: NetworkRule) => {
    setEditRevision(data?.revision ?? null);
    setSelected(rule ? identityKey(rule.identity) : '');
    setDestination(rule?.destination ?? '');
    setPort(String(rule?.port ?? 443));
    setAllowed(rule?.allowed === false ? 'block' : 'allow');
    setAllowPrivateNetwork(rule?.allowPrivateNetwork ?? false);
    setEditing(true);
    setView('permissions');
  };
  const pendingCount = Math.max(data?.pendingRequestCount ?? 0, pending.length);
  const status = !data?.enabled
    ? 'Network control is disabled'
    : data.activationState === 'active'
      ? 'Policy active'
      : data.activationState === 'failed'
        ? 'Changes could not be applied'
        : 'Applying changes';
  const tabs = [
    { id: 'requests' as const, label: 'Requests', count: pendingCount },
    { id: 'permissions' as const, label: 'Permissions' },
    { id: 'activity' as const, label: 'Activity' },
  ];

  return (
    <div className="page-shell">
      <PageIntro
        kicker="Outbound access"
        title="Network access"
        description="Review blocked connections and manage outbound permissions."
      />
      {query.isPending && <PageState message="Loading network policy..." />}
      {query.isError && (
        <Alert variant="destructive">
          Network policy could not be loaded.{' '}
          <Button variant="outline" onClick={() => void query.refetch()}>
            Retry
          </Button>
        </Alert>
      )}
      {data && (
        <div className="space-y-5">
          <section className="workspace-panel space-y-4 !p-4 sm:!p-6" aria-label="Policy status">
            <div className="flex flex-wrap items-start justify-between gap-4">
              <div className="flex min-w-0 flex-1 items-start gap-3">
                <span
                  className={`rounded-xl p-2.5 ${data.enabled && data.activationState === 'active' ? 'bg-done/10 text-done' : 'bg-attention/10 text-attention'}`}
                >
                  {data.enabled ? (
                    <ShieldCheck size={22} aria-hidden="true" />
                  ) : (
                    <ShieldOff size={22} aria-hidden="true" />
                  )}
                </span>
                <div className="min-w-0 space-y-1">
                  <h2 className="font-semibold">{status}</h2>
                  <p className="max-w-2xl text-sm text-muted-foreground">
                    {!data.enabled
                      ? 'This instance is not enforcing outbound network policy. Start the network-controlled Linux appliance to use these controls.'
                      : data.activationState === 'active'
                        ? 'New outbound destinations need approval.'
                        : 'Saved changes are not confirmed active yet. Wait for the policy to become active before retrying.'}
                  </p>
                </div>
              </div>
              <Button
                variant="outline"
                size="sm"
                aria-label="Refresh network policy"
                onClick={() => {
                  void query.refetch();
                  void audit.refetch();
                }}
                disabled={query.isFetching}
              >
                <RefreshCw size={14} aria-hidden="true" />
                <span className="hidden sm:inline">Refresh</span>
              </Button>
            </div>
            <p className="text-xs text-muted-foreground sm:hidden">
              {pendingCount} pending · {Math.max(data.ruleCount, rules.length)} custom rules ·{' '}
              {Math.max(data.sourceCount, data.sources.length)} components
            </p>
            <div className="hidden grid-cols-3 gap-3 sm:grid">
              {[
                ['Needs a decision', pendingCount],
                ['Custom rules', Math.max(data.ruleCount, rules.length)],
                ['Components', Math.max(data.sourceCount, data.sources.length)],
              ].map(([label, count]) => (
                <div
                  key={label}
                  className="rounded-lg border border-border bg-background/50 px-4 py-3"
                >
                  <p className="text-xs font-medium text-muted-foreground">{label}</p>
                  <p className="mt-1 text-2xl font-semibold tabular-nums">{count}</p>
                </div>
              ))}
            </div>
            {data.lastError && <Alert variant="destructive">{data.lastError}</Alert>}
            {data.dnsRefreshError && (
              <Alert>
                <p>DNS refresh needs attention. Name resolution may be impaired; an administrator should check the appliance resolver.</p>
                {currentUser?.role === 'admin' && <p className="mt-1">{data.dnsRefreshError}</p>}
              </Alert>
            )}
            {data.maxPrivateNetworks > 0 && data.activePrivateNetworks >= 0 && (
              <p className="text-sm text-muted-foreground">
                Private networks in use: {data.activePrivateNetworks} of {data.maxPrivateNetworks}.
                Core, management, setup, and agent scopes share this limit.
              </p>
            )}
            {error && (
              <Alert variant="destructive" role="alert">
                {error}
              </Alert>
            )}
            {change.isSuccess && !error && (
              <p role="status" className="text-sm text-muted-foreground">
                {data.activationState === 'active'
                  ? change.variables?.allowed
                    ? 'Access allowed. Retry the original action when you are ready.'
                    : 'Destination blocked for new connections.'
                  : 'Decision saved. Waiting for policy activation.'}
              </p>
            )}
            {gaps > 0 && view !== 'activity' && (
              <Alert>
                Audit has gaps. Missing a request? Check Activity and retry the original action.
              </Alert>
            )}
            {(data.requestCapacityReached || pendingCount >= 500) && (
              <Alert>
                Network requests have reached the 500-group limit. Review the connection audit for
                additional blocked destinations and resolve existing requests.
              </Alert>
            )}
          </section>

          <div className="flex flex-wrap items-center justify-between gap-3">
            <div
              role="tablist"
              aria-label="Network access views"
              className="flex rounded-xl border border-border bg-muted/40 p-1"
              onKeyDown={(event) => {
                if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
                event.preventDefault();
                const index = tabs.findIndex((tab) => tab.id === view);
                const next =
                  event.key === 'Home'
                    ? 0
                    : event.key === 'End'
                      ? tabs.length - 1
                      : (index + (event.key === 'ArrowRight' ? 1 : -1) + tabs.length) % tabs.length;
                document.getElementById(`network-tab-${tabs[next]!.id}`)?.focus();
                setView(tabs[next]!.id);
              }}
            >
              {tabs.map((tab) => (
                <Button
                  key={tab.id}
                  id={`network-tab-${tab.id}`}
                  role="tab"
                  aria-selected={view === tab.id}
                  aria-controls={`network-panel-${tab.id}`}
                  tabIndex={view === tab.id ? 0 : -1}
                  variant={view === tab.id ? 'secondary' : 'ghost'}
                  size="sm"
                  onClick={() => {
                    setView(tab.id);
                    setSearch('');
                  }}
                >
                  {tab.label}
                  {tab.count !== undefined && (
                    <span className="rounded-md bg-background px-1.5 text-xs tabular-nums">
                      {tab.count}
                    </span>
                  )}
                </Button>
              ))}
            </div>
            <label className="relative w-full sm:w-72">
              <Search
                size={16}
                aria-hidden="true"
                className="pointer-events-none absolute left-3 top-3 text-muted-foreground"
              />
              <span className="sr-only">Search network access</span>
              <Input
                className="pl-9"
                value={search}
                onChange={(event) => setSearch(event.target.value)}
                placeholder="Search components or destinations"
              />
            </label>
          </div>

          <div
            id={`network-panel-${view}`}
            role="tabpanel"
            aria-labelledby={`network-tab-${view}`}
            className="space-y-4"
          >
            {view === 'requests' && (
              <>
                <div>
                  <h2 className="text-lg font-semibold">Pending network approvals</h2>
                  <p className="mt-1 text-sm text-muted-foreground">
                    Check who is connecting and where before allowing access.
                  </p>
                </div>
                {pending.length === 0 && (
                  <div className="workspace-panel py-10 text-center">
                    <Check size={28} className="mx-auto text-done" aria-hidden="true" />
                    <h3 className="mt-3 font-semibold">
                      {pendingCount > 0
                        ? 'More requests are available to load.'
                        : 'No pending network approvals.'}
                    </h3>
                    <p className="mx-auto mt-2 max-w-lg text-sm text-muted-foreground">
                      {pendingCount > 0
                        ? 'Use Load more below to find the requests awaiting a decision.'
                        : 'New captured blocks appear here. If an action failed but no request appears, check Activity and retry the action.'}
                    </p>
                  </div>
                )}
                {pending
                  .filter((request) =>
                    matches(
                      `${request.destination} ${request.port} ${identityLabel(request.identity)}`
                    )
                  )
                  .sort((a, b) => b.lastSeenAtUtc.localeCompare(a.lastSeenAtUtc))
                  .map((request) => (
                    <article key={request.id} className="workspace-panel space-y-4">
                      <div className="flex flex-wrap items-start justify-between gap-3">
                        <NetworkSource identity={request.identity} />
                        <Badge variant="attention">Blocked · needs a decision</Badge>
                      </div>
                      <div className="flex items-center gap-3 rounded-lg border border-border bg-muted/30 p-3">
                        <Globe
                          size={20}
                          aria-hidden="true"
                          className="hidden shrink-0 text-muted-foreground sm:block"
                        />
                        <div className="min-w-0">
                          <p className="break-all font-mono text-sm font-semibold">
                            {request.destination}:{request.port}
                          </p>
                          <p className="mt-1 text-xs text-muted-foreground">
                            Exact destination · TCP port {request.port}
                          </p>
                        </div>
                      </div>
                      <p className="text-xs text-muted-foreground">
                        {request.attempts.toLocaleString()} blocked attempt
                        {request.attempts === 1 ? '' : 's'} · Last seen{' '}
                        {new Date(request.lastSeenAtUtc).toLocaleString()}
                      </p>
                      {restriction(request.destination) && (
                        <Alert>{restriction(request.destination)}</Alert>
                      )}
                      {reviewing?.id === request.id ? (
                        <div
                          ref={reviewPanel}
                          tabIndex={-1}
                          role="group"
                          aria-label="Review endpoint permission"
                          className="space-y-3 rounded-lg border border-primary/30 bg-primary/5 p-4"
                        >
                          <h3 className="font-semibold">Allow this destination?</h3>
                          <p className="text-sm">
                            This gives <strong>{identityLabel(request.identity)}</strong> ongoing
                            access to{' '}
                            <strong className="break-all">
                              {request.destination}:{request.port}
                            </strong>{' '}
                            until you block it.
                          </p>
                          {request.identity.scopeType === 'core' && (
                            <p className="text-sm">
                              This is a shared service permission. Calls made through that service
                              share its access; this is not a per-agent grant. Remote HTTP connectors
                              also need permission for their caller and connector.
                            </p>
                          )}
                          <p className="text-sm text-muted-foreground">
                            This grants access to this exact destination and port. Retry the
                            original action after the policy becomes active.
                          </p>
                          <p className="text-sm text-muted-foreground">
                            {reviewing.allowPrivateNetwork
                              ? 'This approval includes local-network access for this destination.'
                              : 'This approval is for public destinations only. LAN access requires an explicit administrator choice.'}
                          </p>
                          {currentUser?.role === 'admin' && (
                            <LocalNetworkChoice
                              checked={reviewing.allowPrivateNetwork}
                              disabled={busy}
                              onChange={(checked) =>
                                setReviewing(
                                  (current) =>
                                    current && { ...current, allowPrivateNetwork: checked }
                                )
                              }
                            />
                          )}
                          {destinationScope(request.destination) === 'private' &&
                            !reviewing.allowPrivateNetwork && (
                              <p className="text-sm text-muted-foreground">
                                Select local-network access to allow this private address.
                              </p>
                            )}
                          <div className="flex flex-wrap gap-2">
                            <Button
                              disabled={
                                busy ||
                                !!restriction(request.destination) ||
                                (destinationScope(request.destination) === 'private' &&
                                  !reviewing.allowPrivateNetwork)
                              }
                              onClick={() =>
                                change.mutate(
                                  {
                                    kind: 'decision',
                                    requestId: request.id,
                                    allowed: true,
                                    expectedRevision: reviewing.revision,
                                    allowPrivateNetwork:
                                      currentUser?.role === 'admin' &&
                                      reviewing.allowPrivateNetwork,
                                  },
                                  {
                                    onSuccess: () => setReviewing(null),
                                    onError: () => setReviewing(null),
                                  }
                                )
                              }
                            >
                              Confirm allow
                            </Button>
                            <Button
                              variant="outline"
                              disabled={change.isPending}
                              onClick={() => setReviewing(null)}
                            >
                              Cancel
                            </Button>
                          </div>
                        </div>
                      ) : (
                        <div className="flex flex-wrap gap-2">
                          <Button
                            disabled={busy || !!restriction(request.destination)}
                            onClick={() =>
                              setReviewing({
                                id: request.id,
                                revision: data.revision,
                                allowPrivateNetwork: false,
                              })
                            }
                          >
                            Allow
                            <ArrowRight size={14} aria-hidden="true" />
                          </Button>
                          <Button
                            variant="outline"
                            disabled={busy}
                            onClick={() =>
                              change.mutate({
                                kind: 'decision',
                                requestId: request.id,
                                allowed: false,
                                expectedRevision: data.revision,
                              })
                            }
                          >
                            Keep blocked
                          </Button>
                        </div>
                      )}
                    </article>
                  ))}
                {pending.length > 0 &&
                  !pending.some((request) =>
                    matches(
                      `${request.destination} ${request.port} ${identityLabel(request.identity)}`
                    )
                  ) && (
                    <p className="text-sm text-muted-foreground">No requests match your search.</p>
                  )}
              </>
            )}

            {view === 'permissions' && (
              <section className="workspace-panel space-y-4" aria-labelledby="network-rules">
                <div className="flex flex-wrap items-center justify-between gap-3">
                  <div>
                    <h2 id="network-rules" className="text-lg font-semibold">
                      Policy rules
                    </h2>
                    <p className="mt-1 text-sm text-muted-foreground">
                      Control future connections for a specific component and destination.
                    </p>
                    <p className="mt-1 text-sm text-muted-foreground">
                      Container and remote HTTP connectors have scoped permissions. Remote HTTP
                      calls also need a compatible shared Gateway route; a connector approval
                      does not grant Gateway local-network access.
                    </p>
                  </div>
                  <Button onClick={() => edit()} disabled={busy}>
                    Add rule
                  </Button>
                </div>
                {editing && (
                  <form
                    className="grid gap-3 rounded-xl border border-border bg-muted/20 p-4 sm:grid-cols-2"
                    onSubmit={(event) => {
                      event.preventDefault();
                      if (chosen && editRevision !== null)
                        change.mutate(
                          {
                            kind: 'rule',
                            identity: chosen,
                            destination: destination.trim(),
                            port: Number(port),
                            allowed: allowed === 'allow',
                            expectedRevision: editRevision,
                            allowPrivateNetwork:
                              allowed === 'allow' &&
                              currentUser?.role === 'admin' &&
                              allowPrivateNetwork,
                          },
                          {
                            onSuccess: () => setEditing(false),
                            onError: (error) => {
                              if (isAxiosError(error) && error.response?.status === 409)
                                setEditing(false);
                            },
                          }
                        );
                    }}
                  >
                    <label className="space-y-1 text-sm sm:col-span-2">
                      Component
                      <Select
                        required
                        value={selected}
                        onChange={(event) => setSelected(event.target.value)}
                        disabled={busy}
                      >
                        <option value="">Choose a component</option>
                        {[...identities].map(([key, identity]) => (
                          <option key={key} value={key}>
                            {identityLabel(identity)}
                          </option>
                        ))}
                      </Select>
                    </label>
                    {chosen?.scopeType === 'core' && (
                      <p className="text-sm text-muted-foreground sm:col-span-2">
                        This permission belongs to a shared Kautuka service, including remote
                        connectors it calls.
                      </p>
                    )}
                    {allowed === 'allow' && (
                      <div className="sm:col-span-2 space-y-2">
                        <p className="text-sm text-muted-foreground">
                          {allowPrivateNetwork && currentUser?.role === 'admin'
                            ? 'This rule includes local-network access.'
                            : 'This rule allows public destinations only.'}
                        </p>
                        {currentUser?.role === 'admin' && (
                          <LocalNetworkChoice
                            checked={allowPrivateNetwork}
                            disabled={busy}
                            onChange={setAllowPrivateNetwork}
                          />
                        )}
                      </div>
                    )}
                    <label className="space-y-1 text-sm">
                      Hostname or IPv4 address
                      <Input
                        required
                        value={destination}
                        onChange={(event) => setDestination(event.target.value)}
                        placeholder="api.example.com"
                        disabled={busy}
                      />
                    </label>
                    <label className="space-y-1 text-sm">
                      TCP port
                      <Input
                        required
                        type="number"
                        min={1}
                        max={65535}
                        step={1}
                        value={port}
                        onChange={(event) => setPort(event.target.value)}
                        disabled={busy}
                      />
                    </label>
                    <label className="space-y-1 text-sm">
                      Access
                      <Select
                        value={allowed}
                        onChange={(event) => setAllowed(event.target.value)}
                        disabled={busy}
                      >
                        <option value="allow">Allow</option>
                        <option value="block">Block</option>
                      </Select>
                    </label>
                    <div className="flex flex-wrap items-end gap-2">
                      <Button
                        type="submit"
                        disabled={
                          busy ||
                          !chosen ||
                          (allowed === 'allow' &&
                            (!!restriction(destination) ||
                              (destinationScope(destination) === 'private' &&
                                !allowPrivateNetwork)))
                        }
                      >
                        Save rule
                      </Button>
                      <Button type="button" variant="outline" onClick={() => setEditing(false)}>
                        Cancel
                      </Button>
                    </div>
                    {allowed === 'allow' && restriction(destination) && (
                      <Alert className="sm:col-span-2">{restriction(destination)}</Alert>
                    )}
                    <p className="text-xs text-muted-foreground sm:col-span-2">
                      Use exact hostnames for TLS connections and IPv4 addresses for plain HTTP or
                      other TCP traffic. No URLs or wildcards. Changing a rule does not terminate an
                      existing connection.
                    </p>
                  </form>
                )}
                {rules.length === 0 && (
                  <p className="text-sm text-muted-foreground">
                    No custom rules. Built-in access is listed below.
                  </p>
                )}
                {rules
                  .filter((rule) => matches(`${rule.destination} ${identityLabel(rule.identity)}`))
                  .map((rule) => (
                    <article
                      key={rule.id}
                      className="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-border p-4"
                    >
                      <div className="min-w-0 space-y-2">
                        <p className="break-all font-medium">
                          {rule.allowed ? 'Allow' : 'Block'} {rule.destination}:{rule.port}
                        </p>
                        {rule.allowed && (
                          <Badge variant={rule.allowPrivateNetwork ? 'attention' : 'muted'}>
                            {rule.allowPrivateNetwork
                              ? 'Includes private-network access'
                              : 'Public destinations only'}
                          </Badge>
                        )}
                        <NetworkSource identity={rule.identity} />
                      </div>
                      <Button variant="outline" disabled={busy} onClick={() => edit(rule)}>
                        Edit rule
                      </Button>
                    </article>
                  ))}
                {data.defaults.length > 0 && (
                  <details>
                    <summary className="cursor-pointer text-sm font-semibold">
                      Default access ({data.defaults.length})
                    </summary>
                    <p className="my-3 text-sm text-muted-foreground">
                      Built-in permissions keep Kautuka services working. An explicit block
                      overrides a default.
                    </p>
                    <div className="space-y-2">
                      {data.defaults
                        .filter((rule) =>
                          matches(`${rule.destination} ${identityLabel(rule.identity)}`)
                        )
                        .map((rule) => (
                          <div
                            key={`${identityKey(rule.identity)}:${rule.destination}:${rule.port}`}
                            className="flex flex-wrap items-center justify-between gap-3 rounded-lg border border-border p-3"
                          >
                            <div className="min-w-0">
                              <p className="break-all text-sm font-medium">
                                {rule.destination}:{rule.port}
                              </p>
                              <p className="text-sm text-muted-foreground">
                                {identityLabel(rule.identity)}
                              </p>
                            </div>
                            <Button variant="outline" disabled={busy} onClick={() => edit(rule)}>
                              Override default
                            </Button>
                          </div>
                        ))}
                    </div>
                  </details>
                )}
              </section>
            )}

            {view === 'activity' && (
              <>
                <NetworkAudit search={search} />
                <section className="workspace-panel space-y-3" aria-labelledby="network-history">
                  <h2 id="network-history" className="text-lg font-semibold">
                    Approval history
                  </h2>
                  {decisions.length === 0 && (
                    <p className="text-sm text-muted-foreground">No network decisions yet.</p>
                  )}
                  {decisions
                    .filter((request) =>
                      matches(`${request.destination} ${identityLabel(request.identity)}`)
                    )
                    .map((request) => (
                      <div
                        key={request.id}
                        className="space-y-2 rounded-lg border border-border p-3"
                      >
                        <p className="break-all text-sm font-medium">
                          {request.status === 'allowed' ? 'Allowed' : 'Kept blocked'}:{' '}
                          {request.destination}:{request.port}
                        </p>
                        <NetworkSource identity={request.identity} />
                      </div>
                    ))}
                </section>
              </>
            )}
          </div>
          {query.hasNextPage && (
            <div className="space-y-2">
              <p className="text-xs text-muted-foreground">
                More policy records are available. Search currently covers loaded records.
              </p>
              <Button
                variant="outline"
                disabled={query.isFetchingNextPage}
                onClick={() => void query.fetchNextPage()}
              >
                Load more policy and requests
              </Button>
            </div>
          )}
          <div className="flex items-start gap-2 text-xs text-muted-foreground">
            <LockKeyhole size={14} className="mt-0.5 shrink-0" aria-hidden="true" />
            <p>
              Containers on the same network can reach each other directly. Only outbound traffic is
              controlled here. Permissions apply to new connections; approving access never repeats
              an action.
            </p>
          </div>
        </div>
      )}
    </div>
  );
}
