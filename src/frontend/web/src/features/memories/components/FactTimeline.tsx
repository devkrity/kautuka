import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import React from 'react';
import { Clock3, ExternalLink, MemoryStick, Pencil, Trash2 } from 'lucide-react';

interface TimelineFact {
  key: string;
  id: string | null;
  title: string;
  summary: string;
  category: string;
  source: string;
  confidence: number | null;
  updatedAt: Date | null;
  sessionId: string | null;
}

interface FactTimelineProps {
  selectedName?: string;
  facts: TimelineFact[];
  editingFactId: string | null;
  editDraft: { title: string; content: string; category: string } | null;
  savingFactId: string | null;
  deletingFactId: string | null;
  sessionIdSet: Set<string>;
  onEditDraftChange: (next: { title: string; content: string; category: string } | null) => void;
  onStartEdit: (fact: TimelineFact) => void;
  onCancelEdit: () => void;
  onSaveFact: (factId: string) => void;
  onDeleteFact: (factId: string) => void;
  onOpenSession: (sessionId: string) => void;
}

export function FactTimeline({
  selectedName,
  facts,
  editingFactId,
  editDraft,
  savingFactId,
  deletingFactId,
  sessionIdSet,
  onEditDraftChange,
  onStartEdit,
  onCancelEdit,
  onSaveFact,
  onDeleteFact,
  onOpenSession,
}: Readonly<FactTimelineProps>) {
  return (
    <div className="workspace-block">
      <div className="mb-4 flex items-center justify-between">
        <h2 className="text-base font-semibold">
          <span className="inline-flex items-center gap-2">
            <MemoryStick className="h-4 w-4 text-primary" />
            Fact Timeline for {selectedName}
          </span>
          <span className="ml-2 text-sm font-normal text-muted-foreground">({facts.length})</span>
        </h2>
      </div>

      {facts.length === 0 ? (
        <p className="text-sm text-muted-foreground">
          No durable facts captured yet. Use explicit memory statements.
        </p>
      ) : (
        <div className="space-y-3">
          {facts.map((fact) => (
            <div key={fact.key} className="overflow-hidden rounded-xl border border-border/80">
              {editingFactId === fact.key && editDraft ? (
                <div className="space-y-2 p-3">
                  <input
                    type="text"
                    value={editDraft.title}
                    onChange={(e) => onEditDraftChange({ ...editDraft, title: e.target.value })}
                    placeholder="Title"
                    className="w-full rounded-lg border border-border bg-background px-2 py-1 text-sm"
                  />
                  <textarea
                    value={editDraft.content}
                    onChange={(e) => onEditDraftChange({ ...editDraft, content: e.target.value })}
                    rows={3}
                    placeholder="Content"
                    className="w-full rounded-lg border border-border bg-background px-2 py-1 text-sm resize-none"
                  />
                  <input
                    type="text"
                    value={editDraft.category}
                    onChange={(e) => onEditDraftChange({ ...editDraft, category: e.target.value })}
                    placeholder="Category"
                    className="w-full rounded-lg border border-border bg-background px-2 py-1 text-sm"
                  />
                  <div className="flex justify-end gap-2">
                    <Button type="button" variant="outline" size="xs" onClick={onCancelEdit}>
                      Cancel
                    </Button>
                    <Button
                      type="button"
                      size="xs"
                      disabled={savingFactId === fact.key}
                      onClick={() => fact.id && onSaveFact(fact.id)}
                    >
                      {savingFactId === fact.key ? 'Saving…' : 'Save'}
                    </Button>
                  </div>
                </div>
              ) : (
                <div className="flex w-full items-start justify-between gap-2 p-3">
                  <div className="min-w-0 flex-1">
                    {fact.title && (
                      <p className="mb-0.5 text-xs font-semibold uppercase tracking-wide text-muted-foreground">
                        {fact.title}
                      </p>
                    )}
                    <p className="text-sm font-medium text-foreground whitespace-pre-wrap">
                      {fact.summary}
                    </p>
                    <p className="mt-1 text-xs text-muted-foreground">
                      [{fact.category}] · source: {fact.source}
                    </p>
                  </div>
                  <div className="flex flex-col items-end gap-2 shrink-0">
                    <div className="flex items-center gap-2">
                      {fact.id && (
                        <>
                          <Button
                            type="button"
                            variant="ghost"
                            size="icon-sm"
                            title="Edit fact"
                            onClick={() => onStartEdit(fact)}
                            className="text-muted-foreground"
                          >
                            <Pencil className="h-3.5 w-3.5" />
                          </Button>
                          <Button
                            type="button"
                            variant="ghost"
                            size="icon-sm"
                            title="Delete fact"
                            disabled={deletingFactId === fact.id}
                            onClick={() => fact.id && onDeleteFact(fact.id)}
                            className="text-muted-foreground hover:text-destructive"
                          >
                            <Trash2 className="h-3.5 w-3.5" />
                          </Button>
                        </>
                      )}
                    </div>
                    <div className="flex flex-wrap items-center justify-end gap-2">
                      {fact.confidence !== null && (
                        <Badge variant="muted">
                          {(fact.confidence * 100).toFixed(0)}% confidence
                        </Badge>
                      )}
                      {fact.updatedAt && (
                        <span className="inline-flex items-center gap-1 text-xs text-muted-foreground">
                          <Clock3 className="h-3 w-3" />
                          {fact.updatedAt.toLocaleDateString()}
                        </span>
                      )}
                      {fact.sessionId && sessionIdSet.has(fact.sessionId) && (
                        <Button
                          type="button"
                          variant="link"
                          size="xs"
                          title="View source session"
                          onClick={() => {
                            if (fact.sessionId) {
                              onOpenSession(fact.sessionId);
                            }
                          }}
                          className="h-auto px-0"
                        >
                          <ExternalLink className="h-3 w-3" />
                          View session
                        </Button>
                      )}
                      {fact.sessionId && !sessionIdSet.has(fact.sessionId) && (
                        <span className="text-xs text-muted-foreground/60 italic">
                          session deleted
                        </span>
                      )}
                    </div>
                  </div>
                </div>
              )}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
