import React from 'react';
import type { KautukaNaradIntents } from '../kautuka/KautukaNaradProvider';
import {
  classifyExecutionGraphPresentation,
  executionGraphPanelTitle,
  type ExecutionGraphPresentation,
  type ExecutionGraphSnapshot,
} from '../types/executionGraph';
import {
  formatNaradIntentFailure,
  isNaradIntentAccepted,
} from '../kautuka/kautukaNaradIntentStatus';

const statusLabel = new Map<string, string>([
  ['pending', 'Pending'],
  ['running', 'Running'],
  ['waiting', 'Waiting'],
  ['completed', 'Completed'],
  ['failed', 'Failed'],
  ['cancelled', 'Cancelled'],
]);

const statusClassName = (status: string): string => {
  switch (status) {
    case 'running':
      return 'text-primary';
    case 'waiting':
      return 'text-attention';
    case 'completed':
      return 'text-done';
    case 'failed':
      return 'text-destructive';
    default:
      return 'text-muted-foreground';
  }
};

const cancellableStatuses = new Set(['running', 'waiting']);
const retryableStatuses = new Set(['failed', 'cancelled']);

export type ExecutionGraphPanelProps = Readonly<{
  graph: ExecutionGraphSnapshot | null;
  resyncNeeded: boolean;
  intents: Pick<KautukaNaradIntents, 'cancelGraphNode' | 'retryTarget'>;
  disabled?: boolean;
  presentation?: ExecutionGraphPresentation;
  onCommandError?: (message: string) => void;
}>;

export const ExecutionGraphPanel: React.FC<ExecutionGraphPanelProps> = ({
  graph,
  resyncNeeded,
  intents,
  disabled = false,
  presentation,
  onCommandError,
}) => {
  const resolvedPresentation = presentation ?? classifyExecutionGraphPresentation(graph);
  const panelTitle = executionGraphPanelTitle(resolvedPresentation);

  const handleCommandResult = React.useCallback(
    (response: { status: string; detail?: string }) => {
      if (!isNaradIntentAccepted(response.status)) {
        onCommandError?.(formatNaradIntentFailure(response));
      }
    },
    [onCommandError],
  );

  if (resolvedPresentation === 'hidden' || !graph) {
    return null;
  }

  const nodes = graph.nodes.filter((node) => !node.nodeId.startsWith('join:') || node.status !== 'pending');

  return (
    <section
      aria-label={panelTitle}
      aria-busy={resyncNeeded ? true : undefined}
      data-testid="execution-graph-panel"
      data-presentation={resolvedPresentation}
      className="rounded-2xl border border-border/60 bg-card px-4 py-3"
    >
      <header className="mb-3">
        <h2 className="text-sm font-semibold">{panelTitle}</h2>
        <div className="text-xs text-muted-foreground">
          Graph revision {graph.revision}
          {resyncNeeded ? ' · resync pending' : ''}
        </div>
      </header>
      <ul
        role="list"
        data-testid="execution-graph-node-list"
        className="space-y-2"
      >
        {nodes.map((node) => {
          const canCancel =
            cancellableStatuses.has(node.status) &&
            node.cancellableTarget?.kind === 'run' &&
            Boolean(node.cancellableTarget.id);
          const canRetry =
            retryableStatuses.has(node.status) &&
            node.retryTarget?.kind === 'approval' &&
            Boolean(node.retryTarget.id);

          return (
            <li
              key={node.nodeId}
              data-testid={`execution-graph-node-${node.nodeId}`}
              className="rounded-xl border border-border/60 px-3 py-2 text-sm"
            >
              <div className="flex items-start justify-between gap-3">
                <div className="min-w-0">
                  <div className="truncate font-semibold">{node.label ?? node.nodeId}</div>
                  <div className="text-xs text-muted-foreground">
                    {node.kind}
                    {node.fanOutGroupId ? ` · ${node.fanOutGroupId}` : ''}
                  </div>
                  {node.waitOwner ? (
                    <div className="mt-1 text-xs text-attention">
                      Waiting on {node.waitOwner.type}:{node.waitOwner.id}
                    </div>
                  ) : null}
                  {node.partialOutcome?.summary ? (
                    <div className="mt-1 text-xs text-muted-foreground">{node.partialOutcome.summary}</div>
                  ) : null}
                </div>
                <div className="flex flex-col items-end gap-2">
                  <span className={`text-xs font-semibold ${statusClassName(node.status)}`}>
                    {statusLabel.get(node.status) ?? node.status}
                  </span>
                  <div className="flex gap-2">
                    {canCancel ? (
                      <button
                        type="button"
                        disabled={disabled}
                        aria-label={`Cancel ${node.label ?? node.nodeId}`}
                        data-testid={`cancel-graph-node-${node.nodeId}`}
                        className="rounded-md border border-border px-2 py-1 text-xs"
                        onClick={() => {
                          void intents
                            .cancelGraphNode({ nodeId: node.nodeId })
                            .then(handleCommandResult)
                            .catch((error: unknown) =>
                              onCommandError?.(
                                error instanceof Error ? error.message : 'Failed to cancel graph node',
                              ),
                            );
                        }}
                      >
                        Cancel
                      </button>
                    ) : null}
                    {canRetry ? (
                      <button
                        type="button"
                        disabled={disabled}
                        aria-label={`Retry ${node.label ?? node.nodeId}`}
                        data-testid={`retry-target-${node.retryTarget!.kind}-${node.retryTarget!.id}`}
                        className="rounded-md border border-border px-2 py-1 text-xs"
                        onClick={() => {
                          void intents
                            .retryTarget({
                              targetType: node.retryTarget!.kind,
                              targetId: node.retryTarget!.id,
                            })
                            .then(handleCommandResult)
                            .catch((error: unknown) =>
                              onCommandError?.(
                                error instanceof Error ? error.message : 'Failed to retry target',
                              ),
                            );
                        }}
                      >
                        Retry
                      </button>
                    ) : null}
                  </div>
                </div>
              </div>
            </li>
          );
        })}
      </ul>
    </section>
  );
};
