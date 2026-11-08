import { Alert } from '@/components/ui/alert';
import { Button } from '@/components/ui/button';
import { identityLabel, useNetworkAudit } from './networkAccess';
import { NetworkSource } from './NetworkSource';

const outcomeLabel = (outcome: string) =>
  outcome === 'denied'
    ? 'Blocked'
    : outcome === 'connected'
      ? 'Connected'
      : outcome === 'closed'
        ? 'Connection ended'
        : 'Diagnostic';

export function NetworkAudit({ search = '' }: { search?: string }) {
  const query = useNetworkAudit();
  const latest = query.data?.pages[0];
  const rows = new Map<string, NonNullable<typeof latest>['events'][number]>();
  for (const page of query.data?.pages ?? []) {
    for (const event of page.events) if (!rows.has(event.id)) rows.set(event.id, event);
  }
  return (
    <section className="workspace-panel space-y-3" aria-labelledby="network-audit">
      <h2 id="network-audit" className="text-lg font-semibold">
        Connection audit
      </h2>
      <p className="text-sm text-muted-foreground">
        Recent connections observed by the outbound proxy. History is limited; downtime or log
        rotation can omit records. Traffic blocked by Linux is not recorded individually here.
      </p>
      {query.isPending && (
        <p className="text-sm text-muted-foreground">Loading connection history...</p>
      )}
      {query.isError && (
        <Alert variant="destructive">
          Connection history could not be loaded.{' '}
          <Button variant="outline" onClick={() => void query.refetch()}>
            Retry audit
          </Button>
        </Alert>
      )}
      {latest?.lastError && <Alert variant="destructive">{latest.lastError}</Alert>}
      {latest && latest.coverageGaps > 0 && (
        <Alert>
          Some connection records are missing because log collection or retention reached its
          limits.
        </Alert>
      )}
      {latest && rows.size === 0 && (
        <p className="text-sm text-muted-foreground">No connection records yet.</p>
      )}
      {[...rows.values()]
        .filter((event) =>
          `${event.destination} ${event.port} ${event.identity ? identityLabel(event.identity) : ''} ${outcomeLabel(event.outcome)}`
            .toLowerCase()
            .includes(search.toLowerCase())
        )
        .map((event) => (
          <article key={event.id} className="rounded-xl border border-border p-3 space-y-1">
            <p className="font-medium break-all">
              {outcomeLabel(event.outcome)} · {event.destination}
              {event.port > 0 ? `:${event.port}` : ''}
            </p>
            {event.identity ? (
              <NetworkSource identity={event.identity} />
            ) : (
              <p className="text-sm text-muted-foreground">Unattributed source</p>
            )}
            <p className="text-sm text-muted-foreground">
              {new Date(event.observedAtUtc).toLocaleString()} ·{' '}
              {event.bytesReceived.toLocaleString()} bytes from component ·{' '}
              {event.bytesSent.toLocaleString()} bytes to component
              {event.durationMilliseconds > 0
                ? ` · ${event.durationMilliseconds.toLocaleString()} ms`
                : ''}
            </p>
          </article>
        ))}
      {query.hasNextPage && (
        <Button
          variant="outline"
          disabled={query.isFetchingNextPage}
          onClick={() => void query.fetchNextPage()}
        >
          Load older connections
        </Button>
      )}
    </section>
  );
}
