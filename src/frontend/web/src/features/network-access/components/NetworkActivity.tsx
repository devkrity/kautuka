import React from 'react';
import { Badge } from '@/components/ui/badge';
import { formatDestinationDisplay } from '../destinationScope';
import type { NetworkAccessActivityDto } from '../networkAccess';

interface NetworkActivityProps {
  entries: NetworkAccessActivityDto[];
  search?: string;
}

export function NetworkActivity({ entries, search = '' }: NetworkActivityProps) {
  const filtered = React.useMemo(() => {
    const q = search.trim().toLowerCase();
    if (!q) return entries;
    return entries.filter(
      (e) =>
        e.destination.toLowerCase().includes(q) ||
        (e.userDisplayName && e.userDisplayName.toLowerCase().includes(q)) ||
        (e.agentDisplayName && e.agentDisplayName.toLowerCase().includes(q)) ||
        (e.connectorDisplayName && e.connectorDisplayName.toLowerCase().includes(q)) ||
        (e.reason && e.reason.toLowerCase().includes(q))
    );
  }, [entries, search]);

  return (
    <section className="space-y-3" aria-labelledby="network-activity-heading">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h3 id="network-activity-heading" className="text-lg font-semibold">
          Connection activity
        </h3>
        <span className="text-xs text-muted-foreground">{filtered.length} entries</span>
      </div>
      <p className="text-sm text-muted-foreground">
        Recent network access decisions observed across agents and connectors.
      </p>

      {filtered.length === 0 ? (
        <p className="text-sm text-muted-foreground py-4">No recent activity records.</p>
      ) : (
        <div className="space-y-2.5">
          {filtered.map((entry, idx) => {
            const formattedDest = formatDestinationDisplay(entry.destination, entry.port);
            const formattedDate = new Date(entry.timestampUtc).toLocaleString();
            const actorDesc = [
              entry.userDisplayName,
              entry.agentDisplayName,
              entry.connectorDisplayName ? `(${entry.connectorDisplayName})` : '',
            ]
              .filter(Boolean)
              .join(' · ');

            return (
              <article
                key={`${entry.timestampUtc}-${entry.destination}-${idx}`}
                className="rounded-xl border border-border bg-card p-3.5 shadow-sm space-y-1.5"
              >
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <div className="flex flex-wrap items-center gap-2">
                    <Badge variant={entry.decision === 'allow' ? 'default' : 'secondary'}>
                      {entry.decision === 'allow' ? 'Allowed' : 'Blocked'}
                    </Badge>
                    <span className="font-medium text-sm text-foreground break-all">
                      {formattedDest}
                    </span>
                    {entry.openInternetBadge && (
                      <Badge variant="attention">Open internet</Badge>
                    )}
                    {entry.compatibilityMode && (
                      <Badge variant="attention">Compatibility mode (relaxed settings)</Badge>
                    )}
                  </div>
                  <span className="text-xs text-muted-foreground">{formattedDate}</span>
                </div>

                {actorDesc && (
                  <p className="text-xs text-muted-foreground">{actorDesc}</p>
                )}

                {entry.reason && (
                  <p className="text-xs text-muted-foreground italic">{entry.reason}</p>
                )}
              </article>
            );
          })}
        </div>
      )}
    </section>
  );
}
