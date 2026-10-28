import { useMutation, useQuery } from '@tanstack/react-query';
import { ChevronDown, ListOrdered, Trash2, X } from 'lucide-react';
import { useState } from 'react';
import apiClient from '@/lib/apiClient';

type QueueSnapshot = {
  messages: { id: string; order: number; content: string }[];
  throughOrder: number;
};

export function SessionQueue({ agentId, sessionId }: { agentId: string; sessionId: string }) {
  const [expanded, setExpanded] = useState(true);
  const url = `/narad/v1/agents/${encodeURIComponent(agentId)}/sessions/${encodeURIComponent(sessionId)}/queue`;
  const queue = useQuery({
    queryKey: ['session-queue', agentId, sessionId],
    queryFn: async ({ signal }) => (await apiClient.get<QueueSnapshot>(url, { signal })).data,
    refetchInterval: 1000,
  });
  const remove = useMutation({
    mutationFn: async (body: { messageId: string } | { throughOrder: number }) =>
      apiClient.post(`${url}/remove`, body),
    onSettled: () => queue.refetch(),
  });
  if (queue.isError)
    return (
      <p role="status" className="mb-2 text-xs text-destructive">
        Unable to load waiting messages.
      </p>
    );
  if (!queue.data?.messages.length) return null;
  const count = queue.data.messages.length;
  return (
    <section aria-label="Message queue" className="mb-2 space-y-2">
      {expanded ? (
        <div className="overflow-hidden rounded-2xl border border-border/70 bg-background shadow-sm">
          <div className="flex items-center justify-between gap-3 px-3 pb-1 pt-2 text-xs text-muted-foreground">
            <span aria-live="polite">{count} queued</span>
            <div className="flex items-center gap-2">
              <span
                className="hidden sm:inline"
                title="Messages run in order after the active run finishes, including approvals."
              >
                Runs in order
              </span>
              <button
                type="button"
                disabled={remove.isPending}
                onClick={() => remove.mutate({ throughOrder: queue.data.throughOrder })}
                className="rounded px-1 py-0.5 transition-colors hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-40"
              >
                Clear queue
              </button>
              <button
                type="button"
                aria-label="Hide queued messages"
                onClick={() => setExpanded(false)}
                className="rounded p-1 transition-colors hover:bg-muted hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
              >
                <X className="h-3.5 w-3.5" aria-hidden="true" />
              </button>
            </div>
          </div>
          <ol
            id="session-queue-items"
            className="max-h-40 space-y-0.5 overflow-y-auto px-1.5 pb-1.5"
            aria-label="Waiting messages in execution order"
          >
            {queue.data.messages.map((message, index) => (
              <li
                key={message.id}
                className="group flex min-h-8 items-center gap-2 rounded-lg px-2 transition-colors hover:bg-muted/70 focus-within:bg-muted/70"
              >
                <span
                  className="w-3 shrink-0 text-center text-[11px] tabular-nums text-muted-foreground/70"
                  aria-hidden="true"
                >
                  {index + 1}
                </span>
                <span
                  className="min-w-0 flex-1 truncate text-xs text-foreground"
                  title={message.content || 'Message with attachments'}
                >
                  {message.content || 'Message with attachments'}
                </span>
                <button
                  type="button"
                  disabled={remove.isPending}
                  aria-label={`Remove waiting message ${index + 1}`}
                  title="Remove from queue"
                  onClick={() => remove.mutate({ messageId: message.id })}
                  className="shrink-0 rounded-md p-1.5 text-muted-foreground transition-all hover:bg-background hover:text-destructive focus-visible:opacity-100 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-40 sm:opacity-0 sm:group-hover:opacity-100 sm:group-focus-within:opacity-100"
                >
                  <Trash2 className="h-3.5 w-3.5" aria-hidden="true" />
                </button>
              </li>
            ))}
          </ol>
        </div>
      ) : null}
      <button
        type="button"
        aria-label={`Queued ${count}`}
        aria-expanded={expanded}
        aria-controls="session-queue-items"
        onClick={() => setExpanded((value) => !value)}
        className="inline-flex h-7 items-center gap-1.5 rounded-full border border-border/70 bg-background px-2.5 text-xs text-muted-foreground transition-colors hover:bg-muted hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
      >
        <ListOrdered className="h-3 w-3" aria-hidden="true" />
        <span>Queued</span>
        <span className="tabular-nums">{count}</span>
        <ChevronDown
          className={`h-3 w-3 transition-transform ${expanded ? 'rotate-180' : ''}`}
          aria-hidden="true"
        />
      </button>
      {remove.isError ? (
        <p role="alert" className="px-1 text-xs text-destructive">
          Unable to update the queue. Please try again.
        </p>
      ) : null}
    </section>
  );
}
