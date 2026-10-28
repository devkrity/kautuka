import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import React from 'react';
import { Clock3, ExternalLink, Trash2 } from 'lucide-react';
import { parseApiTimestamp } from '@/lib/utils';
import type { MemoriesSession } from '../schemas/memorySchema';

interface SessionHistoryProps {
  sessions: MemoriesSession[];
  expandedSession: string | null;
  deletingSessionId: string | null;
  onToggle: (sessionId: string) => void;
  onOpen: (sessionId: string) => void;
  onDelete: (sessionId: string) => void;
}

export function SessionHistory({
  sessions,
  expandedSession,
  deletingSessionId,
  onToggle,
  onOpen,
  onDelete,
}: SessionHistoryProps) {
  return (
    <div className="workspace-block">
      <div className="mb-4 flex items-center justify-between">
        <h2 className="text-base font-semibold">
          Session History
          <span className="ml-2 text-sm font-normal text-muted-foreground">
            ({sessions.length})
          </span>
        </h2>
      </div>

      {sessions.length === 0 ? (
        <p className="text-sm text-muted-foreground">No session history yet.</p>
      ) : (
        <div className="space-y-3">
          {sessions.map((s) => (
            <div key={s.id} className="overflow-hidden rounded-xl border border-border/80">
              <div className="flex w-full items-center justify-between gap-2 p-3">
                <button
                  type="button"
                  onClick={() => onToggle(s.id)}
                  className="flex flex-1 items-center gap-3 min-w-0 text-left"
                >
                  <div className="flex items-center gap-3 min-w-0">
                    <span className="truncate text-sm font-medium text-foreground">{s.title}</span>
                    {s.compacted && (
                      <Badge variant="attention" className="shrink-0">
                        compacted
                      </Badge>
                    )}
                  </div>
                </button>

                <div className="flex items-center gap-3 shrink-0">
                  <span className="text-xs text-muted-foreground">
                    {s.messages?.length ?? 0} msgs
                  </span>
                  <span className="inline-flex items-center gap-1 text-xs text-muted-foreground">
                    <Clock3 className="h-3 w-3" />
                    {parseApiTimestamp(s.updatedAt).toLocaleDateString()}
                  </span>
                  <Button
                    type="button"
                    variant="ghost"
                    size="icon-sm"
                    title="Open session in chat"
                    onClick={() => onOpen(s.id)}
                    className="text-muted-foreground hover:text-primary"
                  >
                    <ExternalLink className="h-3.5 w-3.5" />
                  </Button>
                  <Button
                    type="button"
                    variant="ghost"
                    size="icon-sm"
                    title="Delete session"
                    disabled={deletingSessionId === s.id}
                    onClick={() => onDelete(s.id)}
                    className="text-muted-foreground hover:text-destructive"
                  >
                    <Trash2 className="h-3.5 w-3.5" />
                  </Button>
                  <span className="text-xs text-muted-foreground">
                    {expandedSession === s.id ? '▲' : '▼'}
                  </span>
                </div>
              </div>

              {expandedSession === s.id && (
                <div className="space-y-2 border-t border-border/70 bg-muted/35 p-3">
                  {(s.messages ?? []).slice(-4).map((msg) => (
                    <div
                      key={msg.id}
                      className={`flex ${msg.role === 'user' ? 'justify-end' : 'justify-start'}`}
                    >
                      <div
                        className={`max-w-xl rounded-xl px-3 py-2 text-xs ${msg.role === 'user' ? 'bg-primary-fill text-primary-foreground' : 'border border-border bg-background text-foreground'}`}
                      >
                        <div className="mb-0.5 font-semibold uppercase tracking-wide opacity-60">
                          {msg.role}
                        </div>
                        <div className="whitespace-pre-wrap line-clamp-4">{msg.content}</div>
                      </div>
                    </div>
                  ))}
                </div>
              )}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
