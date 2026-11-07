import React from 'react';
import { isAxiosError } from 'axios';
import { PageIntro } from '@/components/layout/PageIntro';
import { PageState } from '@/components/layout/PageState';
import { Alert } from '@/components/ui/alert';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import { NetworkAudit } from '@/features/network-access/NetworkAudit';
import {
  identityKey,
  identityLabel,
  useNetworkAccess,
  useNetworkAccessChange,
  type NetworkRule,
} from '@/features/network-access/networkAccess';

export function NetworkAccessPage() {
  const query = useNetworkAccess();
  const change = useNetworkAccessChange();
  const [selected, setSelected] = React.useState('');
  const [destination, setDestination] = React.useState('');
  const [port, setPort] = React.useState('443');
  const [allowed, setAllowed] = React.useState('allow');
  const data = query.data;
  const pending = data?.requests.filter((item) => item.status === 'pending') ?? [];
  const decisions = data?.requests.filter((item) => item.status !== 'pending') ?? [];
  const rules = data?.rules ?? [];
  const identities = new Map(
    [
      ...(data?.sources ?? []),
      ...(data?.defaults.map((item) => item.identity) ?? []),
      ...(data?.rules.map((item) => item.identity) ?? []),
      ...(data?.requests.map((item) => item.identity) ?? []),
    ].map((identity) => [identityKey(identity), identity])
  );
  const chosenIdentity = identities.get(selected);
  const busy = change.isPending || !data?.enabled;
  const error = change.error
    ? isAxiosError(change.error) && change.error.response?.status === 409
      ? 'The policy changed while you were editing. The latest policy has been loaded; review it and try again.'
      : isAxiosError(change.error) && change.error.response?.status === 400
        ? 'Use an exact hostname or IPv4 address and a TCP port from 1 to 65535. URLs, wildcards and IPv6 are not supported.'
        : 'The change could not be saved or applied. Refresh to check its current status before retrying.'
    : null;
  const edit = (rule: NetworkRule) => {
    setSelected(identityKey(rule.identity));
    setDestination(rule.destination);
    setPort(String(rule.port));
    setAllowed(rule.allowed ? 'allow' : 'block');
  };

  return (
    <div className="page-shell">
      <PageIntro
        kicker="Outbound access"
        title="Network access"
        description="Review blocked connections and choose which destinations each component can reach."
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
        <div className="space-y-6">
          <section className="workspace-panel space-y-2" aria-label="Policy status">
            <h2 className="text-lg font-semibold">
              {!data.enabled
                ? 'Network control is disabled'
                : data.activationState === 'active'
                  ? 'Policy active'
                  : data.activationState === 'failed'
                    ? 'Changes could not be applied'
                    : 'Applying changes'}
            </h2>
            <p className="text-sm text-muted-foreground">
              Allowed connections must be retried by the component. Approving access does not resume
              a tool or repeat an action.
            </p>
            {data.lastError && <Alert variant="destructive">{data.lastError}</Alert>}
            {error && (
              <Alert variant="destructive" role="alert">
                {error}
              </Alert>
            )}
          </section>

          <section className="workspace-panel space-y-3" aria-labelledby="network-pending">
            <h2 id="network-pending" className="text-lg font-semibold">
              Pending network approvals
            </h2>
            {(data.requestCapacityReached ||
              Math.max(data.pendingRequestCount, pending.length) >= 500) && (
              <Alert>
                Network requests have reached the 500-group limit. Review the connection audit for
                additional blocked destinations and resolve existing requests.
              </Alert>
            )}
            {pending.length === 0 && (
              <p className="text-sm text-muted-foreground">No pending network approvals.</p>
            )}
            {pending.map((request) => (
              <article key={request.id} className="rounded-xl border border-border p-4 space-y-3">
                <p className="font-medium break-all">
                  {request.destination}:{request.port}
                </p>
                <p className="text-sm text-muted-foreground break-all">
                  {identityLabel(request.identity)}
                </p>
                <p className="text-sm text-muted-foreground">
                  {request.attempts} blocked attempt(s). Last seen{' '}
                  {new Date(request.lastSeenAtUtc).toLocaleString()}.
                </p>
                <div className="flex flex-wrap gap-2">
                  <Button
                    disabled={busy}
                    onClick={() =>
                      change.mutate({
                        kind: 'decision',
                        requestId: request.id,
                        allowed: true,
                        expectedRevision: data.revision,
                      })
                    }
                  >
                    Allow
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
              </article>
            ))}
            {query.hasNextPage && (
              <Button
                variant="outline"
                disabled={query.isFetchingNextPage}
                onClick={() => void query.fetchNextPage()}
              >
                Load more policy and requests
              </Button>
            )}
          </section>

          <section className="workspace-panel space-y-4" aria-labelledby="network-rules">
            <h2 id="network-rules" className="text-lg font-semibold">
              Policy rules
            </h2>
            <p className="text-sm text-muted-foreground">
              Rules apply to one component and an exact hostname or IPv4 address and TCP port. All
              other destinations stay blocked.
            </p>
            <p className="text-sm text-muted-foreground">
              Hostname rules match TLS SNI. For other TCP connections, including plain HTTP, use an
              IPv4 address and port.
            </p>
            <form
              className="grid gap-3 sm:grid-cols-2"
              onSubmit={(event) => {
                event.preventDefault();
                if (chosenIdentity)
                  change.mutate({
                    kind: 'rule',
                    identity: chosenIdentity,
                    destination: destination.trim(),
                    port: Number(port),
                    allowed: allowed === 'allow',
                    expectedRevision: data.revision,
                  });
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
              <div className="flex items-end">
                <Button type="submit" disabled={busy || !chosenIdentity}>
                  Save rule
                </Button>
              </div>
            </form>
            {rules.map((rule) => (
              <article
                key={rule.id}
                className="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-border p-3"
              >
                <div className="min-w-0">
                  <p className="break-all">
                    {rule.allowed ? 'Allow' : 'Block'} {rule.destination}:{rule.port}
                  </p>
                  <p className="text-sm text-muted-foreground break-all">
                    {identityLabel(rule.identity)}
                  </p>
                </div>
                <Button variant="outline" disabled={busy} onClick={() => edit(rule)}>
                  Edit rule
                </Button>
              </article>
            ))}
            {data.defaults.length > 0 && (
              <details>
                <summary className="cursor-pointer text-sm font-medium">
                  Default access ({data.defaults.length})
                </summary>
                <div className="mt-3 space-y-2">
                  {data.defaults.map((rule) => (
                    <div
                      key={`${identityKey(rule.identity)}:${rule.destination}:${rule.port}`}
                      className="rounded-xl border border-border p-3 space-y-2"
                    >
                      <p className="text-sm break-all">
                        {identityLabel(rule.identity)} → {rule.destination}:{rule.port}
                      </p>
                      <Button variant="outline" disabled={busy} onClick={() => edit(rule)}>
                        Override default
                      </Button>
                    </div>
                  ))}
                </div>
              </details>
            )}
          </section>

          <section className="workspace-panel space-y-3" aria-labelledby="network-history">
            <h2 id="network-history" className="text-lg font-semibold">
              Approval history
            </h2>
            {decisions.length === 0 && (
              <p className="text-sm text-muted-foreground">No network decisions yet.</p>
            )}
            {decisions.map((request) => (
              <div
                key={request.id}
                className="rounded-xl border border-border p-3 text-sm break-all"
              >
                {request.status === 'allowed' ? 'Allowed' : 'Kept blocked'}: {request.destination}:
                {request.port}
                <p className="text-muted-foreground">{identityLabel(request.identity)}</p>
              </div>
            ))}
          </section>
          <NetworkAudit />
          <p className="text-sm text-muted-foreground">
            Containers on the same network can reach each other directly. Only outbound traffic is
            controlled.
          </p>
        </div>
      )}
    </div>
  );
}
