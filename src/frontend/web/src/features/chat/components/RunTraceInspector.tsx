import { Alert } from '@/components/ui/alert';
import { Button } from '@/components/ui/button';
import React from 'react';
import { ExternalLink, LoaderCircle, X } from 'lucide-react';
import type { RunTraceSpan, RunTraceView } from '../api/runTrace';
import {
  axisTicks,
  buildTraceTimeline,
  formatTraceDuration,
} from '../api/runTraceTimeline';

function prettyPayload(raw: string | null | undefined): string {
  if (!raw) return '';
  try {
    return JSON.stringify(JSON.parse(raw), null, 2);
  } catch {
    return raw;
  }
}

function statusDotClass(status: string): string {
  switch (status) {
    case 'running':
      return 'bg-attention';
    case 'failed':
      return 'bg-destructive';
    case 'skipped':
      return 'bg-muted-foreground/50';
    default:
      return 'bg-done';
  }
}

function barTone(span: RunTraceSpan): string {
  if (span.status === 'failed') {
    return 'bg-destructive/80 ring-destructive/30';
  }
  if (span.status === 'running') {
    return 'bg-attention/85 ring-attention/25';
  }
  if (span.status === 'skipped') {
    return 'bg-muted-foreground/35 ring-border';
  }
  switch (span.type) {
    case 'run':
      return 'bg-[hsl(var(--primary))]/75 ring-[hsl(var(--primary))]/25';
    case 'tool':
      return 'bg-[hsl(var(--secondary))]/80 ring-[hsl(var(--secondary))]/25';
    case 'run.nested':
      return 'bg-[hsl(var(--tertiary))]/80 ring-[hsl(var(--tertiary))]/25';
    case 'run.terminal':
      return 'bg-done/70 ring-done/20';
    default:
      return 'bg-[hsl(var(--primary))]/55 ring-border';
  }
}

export const RunTraceInspector: React.FC<{
  open: boolean;
  loading: boolean;
  error: string | null;
  trace: RunTraceView | null;
  onClose(): void;
}> = ({ open, loading, error, trace, onClose }) => {
  const [selectedId, setSelectedId] = React.useState<string | null>(null);

  React.useEffect(() => {
    if (!open) {
      setSelectedId(null);
    }
  }, [open]);

  React.useEffect(() => {
    if (!open) return;
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') onClose();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [open, onClose]);

  if (!open) {
    return null;
  }

  const spans = trace?.spans ?? [];
  const byId = new Map(spans.map((span) => [span.id, span]));
  const { lanes, totalMs, untimed } = buildTraceTimeline(spans);
  const ticks = axisTicks(totalMs);
  const selected = selectedId ? byId.get(selectedId) : null;
  const selectedPayload = prettyPayload(selected?.payloadJson);
  const selectedLane = lanes.find((lane) => lane.span.id === selectedId) ?? null;

  return (
    <div
      className="fixed inset-0 z-50 flex items-end justify-center bg-black/40 p-4 sm:items-center"
      data-testid="run-trace-inspector"
      role="dialog"
      aria-modal="true"
      aria-label="Run trace"
    >
      <div className="flex max-h-[90vh] w-full max-w-5xl flex-col overflow-hidden rounded-2xl border border-border bg-background shadow-xl">
        <div className="flex items-center justify-between gap-3 border-b border-border px-4 py-3">
          <div>
            <div className="text-sm font-semibold text-foreground">Run trace</div>
            <div className="text-[11px] text-muted-foreground">
              {trace?.runId ? `Run ${trace.runId}` : 'Loading…'}
              {trace?.traceId ? ` · ${trace.traceId}` : ''}
            </div>
            {trace?.summary ? (
              <div className="mt-1 text-[11px] text-muted-foreground" data-testid="run-trace-summary">
                {`In ${trace.summary.promptTokens} · Out ${trace.summary.completionTokens}`}
                {trace.summary.elapsedMs != null
                  ? ` · ${formatTraceDuration(trace.summary.elapsedMs)}`
                  : ''}
                {trace.summary.toolCount > 0 ? ` · ${trace.summary.toolCount} tools` : ''}
              </div>
            ) : null}
          </div>
          <Button
            type="button"
            variant="ghost"
            size="icon-sm"
            onClick={onClose}
            aria-label="Close run trace"
          >
            <X className="h-4 w-4" />
          </Button>
        </div>

        <div className="min-h-0 flex-1 overflow-auto p-4">
          {loading ? (
            <div className="flex items-center gap-2 text-sm text-muted-foreground">
              <LoaderCircle className="h-4 w-4 animate-spin" />
              Loading durable run context…
            </div>
          ) : null}
          {error ? (
            <Alert variant="destructive" className="px-3 py-2">
              {error}
            </Alert>
          ) : null}
          {!loading && !error && spans.length === 0 ? (
            <div className="text-sm text-muted-foreground">No trace spans for this run.</div>
          ) : null}

          {!loading && !error && lanes.length > 0 ? (
            <div className="space-y-3" data-testid="run-trace-timeline">
              <div className="flex items-end gap-3">
                <div className="section-label w-[11.5rem] shrink-0">
                  Span
                </div>
                <div className="relative min-w-0 flex-1 pb-1">
                  <div className="relative h-5 border-b border-border/80">
                    {ticks.map((tick) => {
                      const left = (tick / totalMs) * 100;
                      return (
                        <div
                          key={tick}
                          className="absolute top-0 flex h-full -translate-x-1/2 flex-col items-center"
                          style={{ left: `${left}%` }}
                        >
                          <span className="mt-auto mb-0.5 text-[9px] tabular-nums text-muted-foreground">
                            {formatTraceDuration(tick)}
                          </span>
                          <span className="h-1.5 w-px bg-border" />
                        </div>
                      );
                    })}
                  </div>
                </div>
                <div className="section-label w-16 shrink-0 text-right">
                  Dur
                </div>
              </div>

              <div className="space-y-1">
                {lanes.map((lane) => {
                  const leftPct = (lane.offsetMs / totalMs) * 100;
                  const widthPct = Math.max(
                    lane.durationMs <= 0 ? 0.4 : (lane.durationMs / totalMs) * 100,
                    0.35,
                  );
                  const selectedRow = selectedId === lane.span.id;
                  return (
                    <button
                      key={lane.span.id}
                      type="button"
                      onClick={() => setSelectedId(lane.span.id)}
                      className={`group flex w-full items-center gap-3 rounded-lg px-1 py-1 text-left transition-colors ${
                        selectedRow ? 'bg-muted' : 'hover:bg-muted/60'
                      }`}
                      data-span-id={lane.span.id}
                      data-span-type={lane.span.type}
                      data-span-status={lane.span.status}
                      aria-pressed={selectedRow}
                    >
                      <div
                        className="flex w-[11.5rem] shrink-0 items-center gap-1.5"
                        style={{ paddingLeft: Math.min(lane.depth, 4) * 10 }}
                      >
                        <span
                          className={`h-1.5 w-1.5 shrink-0 rounded-full ${statusDotClass(lane.span.status)}`}
                        />
                        <span className="rounded bg-muted px-1 py-0.5 font-mono text-[9px] uppercase tracking-wide text-muted-foreground">
                          {lane.span.type}
                        </span>
                        <span className="min-w-0 truncate text-[11px] font-medium text-foreground/90">
                          {lane.span.name}
                        </span>
                      </div>

                      <div className="relative h-7 min-w-0 flex-1 overflow-hidden rounded-md bg-muted/40 ring-1 ring-inset ring-border/40">
                        {ticks.slice(1, -1).map((tick) => (
                          <span
                            key={`grid-${lane.span.id}-${tick}`}
                            className="pointer-events-none absolute inset-y-0 w-px bg-border/50"
                            style={{ left: `${(tick / totalMs) * 100}%` }}
                          />
                        ))}
                        <span
                          className={`absolute top-1 bottom-1 rounded-md ring-1 transition-[box-shadow] ${barTone(lane.span)} ${
                            selectedRow ? 'shadow-md ring-2 ring-foreground/30' : ''
                          }`}
                          style={{
                            left: `${leftPct}%`,
                            width: `${Math.min(widthPct, 100 - leftPct)}%`,
                            minWidth: lane.durationMs <= 0 ? 6 : undefined,
                          }}
                          title={`${lane.span.name} · ${formatTraceDuration(lane.durationMs)}`}
                        />
                      </div>

                      <div className="w-16 shrink-0 text-right text-[10px] tabular-nums text-muted-foreground">
                        {formatTraceDuration(lane.durationMs || lane.span.latencyMs)}
                      </div>
                    </button>
                  );
                })}
              </div>

              <div className="flex flex-wrap gap-3 pt-1 text-[10px] text-muted-foreground">
                <span className="inline-flex items-center gap-1.5">
                  <span className="h-2 w-4 rounded-sm bg-[hsl(var(--primary))]/75" /> Run
                </span>
                <span className="inline-flex items-center gap-1.5">
                  <span className="h-2 w-4 rounded-sm bg-[hsl(var(--secondary))]/80" /> Tool
                </span>
                <span className="inline-flex items-center gap-1.5">
                  <span className="h-2 w-4 rounded-sm bg-[hsl(var(--tertiary))]/80" /> Nested
                </span>
                <span className="inline-flex items-center gap-1.5">
                  <span className="h-2 w-4 rounded-sm bg-done/70" /> Terminal
                </span>
                <span className="ml-auto tabular-nums">Window {formatTraceDuration(totalMs)}</span>
              </div>
            </div>
          ) : null}

          {!loading && !error && untimed.length > 0 ? (
            <div className="mt-4 rounded-xl border border-border/70 bg-muted/20 p-3">
              <div className="section-label mb-2">
                Untimed spans
              </div>
              <div className="space-y-1">
                {untimed.map((span) => (
                  <button
                    key={span.id}
                    type="button"
                    onClick={() => setSelectedId(span.id)}
                    className={`flex w-full items-center gap-2 rounded-lg px-2 py-1.5 text-left text-xs hover:bg-muted/70 ${
                      selectedId === span.id ? 'bg-muted' : ''
                    }`}
                    data-span-id={span.id}
                    data-span-type={span.type}
                  >
                    <span className={`h-2 w-2 shrink-0 rounded-full ${statusDotClass(span.status)}`} />
                    <span className="rounded bg-muted px-1.5 py-0.5 font-mono text-[10px] uppercase tracking-wide text-muted-foreground">
                      {span.type}
                    </span>
                    <span className="min-w-0 flex-1 truncate font-medium text-foreground/90">{span.name}</span>
                    {span.latencyMs != null ? (
                      <span className="shrink-0 text-[10px] text-muted-foreground">
                        {formatTraceDuration(span.latencyMs)}
                      </span>
                    ) : null}
                  </button>
                ))}
              </div>
            </div>
          ) : null}

          {selected ? (
            <div className="mt-4 rounded-xl border border-border/70 bg-muted/30 p-3">
              <div className="mb-1 flex flex-wrap items-baseline justify-between gap-2">
                <div className="section-label">
                  {selected.type} · {selected.name}
                </div>
                <div className="text-[11px] tabular-nums text-muted-foreground">
                  {selectedLane
                    ? `${formatTraceDuration(selectedLane.offsetMs)} → ${formatTraceDuration(selectedLane.offsetMs + selectedLane.durationMs)} · ${formatTraceDuration(selectedLane.durationMs)}`
                    : selected.latencyMs != null
                      ? formatTraceDuration(selected.latencyMs)
                      : null}
                </div>
              </div>
              {selectedPayload ? (
                <pre className="max-h-64 overflow-auto whitespace-pre-wrap break-all text-[11px] leading-relaxed text-foreground/85">
                  {selectedPayload}
                </pre>
              ) : (
                <div className="text-xs text-muted-foreground">No payload for this span.</div>
              )}
            </div>
          ) : !loading && !error && lanes.length > 0 ? (
            <div className="mt-4 text-xs text-muted-foreground">
              Select a span to inspect payload and offsets.
            </div>
          ) : null}
        </div>

        <div className="flex items-center justify-between gap-3 border-t border-border px-4 py-3">
          <div className="text-[11px] text-muted-foreground">
            Waterflow timeline from public Narad authority (wall-clock starts/ends). Detailed APM remains
            in OpenObserve.
          </div>
          {trace?.openObserveUrl ? (
            <a
              href={trace.openObserveUrl}
              target="_blank"
              rel="noreferrer"
              className="inline-flex items-center gap-1 rounded-lg border border-border px-2.5 py-1.5 text-xs font-medium text-foreground hover:bg-muted"
            >
              Open in OpenObserve
              <ExternalLink className="h-3.5 w-3.5" />
            </a>
          ) : null}
        </div>
      </div>
    </div>
  );
};
