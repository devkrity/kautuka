import React from 'react';
import { Link } from 'react-router-dom';
import { Check, ExternalLink, X } from 'lucide-react';
import { parseApiTimestamp } from '@/lib/utils';
import { Button } from '@/components/ui/button';
import type { Approval } from '../schemas/approvalSchema';
import {
  computeRemaining,
  formatRelativeTime,
  outerSessionHref,
  prettyJson,
  sessionHref,
} from '../hooks/useApprovalsPage';

interface PendingApprovalCardProps {
  item: Approval;
  busy: boolean;
  onResolve: (
    requestId: string,
    action: 'approve' | 'deny' | 'stop_run' | 'request_changes',
    reason?: string
  ) => void;
}

export function PendingApprovalCard({ item, busy, onResolve }: PendingApprovalCardProps) {
  const [requestingChanges, setRequestingChanges] = React.useState(false);
  const [changeNote, setChangeNote] = React.useState('');
  const workflowReview = item.kind === 'workflow_review' || item.toolType === 'workflow_review';
  const requestChangesAction = item.actions.find((action) => action.value === 'request_changes');
  const remainingChanges = item.remainingChangeRequests ?? 0;
  const [workflowDetails, setWorkflowDetails] = React.useState<{
    name: string;
    step: number;
    results: Array<{ label: string; value: string }>;
  }>();
  React.useEffect(() => {
    const workflowId = item.workflowId;
    const instanceId = item.instanceId;
    const nodeId = item.nodeId;
    if (!workflowReview || !workflowId || !instanceId || !nodeId) return;
    let cancelled = false;
    void import('@/features/workflows')
      .then(
        async ({
          getWorkflowInstance,
          pointerToField,
          stepsFromDefinition,
          workflowDisplayNumbers,
        }) => ({
          workflow: await getWorkflowInstance(workflowId, instanceId),
          pointerToField,
          stepsFromDefinition,
          workflowDisplayNumbers,
        })
      )
      .then(({ workflow, pointerToField, stepsFromDefinition, workflowDisplayNumbers }) => {
        if (cancelled) return;
        const nodeIndex = workflow.snapshot.nodes.findIndex((node) => node.nodeId === nodeId);
        const node = workflow.snapshot.nodes[nodeIndex];
        let proposed: Record<string, unknown> = {};
        try {
          const args = JSON.parse(item.argumentsJson ?? '{}') as { proposed?: unknown };
          if (args.proposed && typeof args.proposed === 'object' && !Array.isArray(args.proposed))
            proposed = args.proposed as Record<string, unknown>;
        } catch {
          /* Unreadable review details are omitted. */
        }
        const schemaFields = [
          workflow.snapshot.inputSchema,
          workflow.snapshot.workingSchema,
          node?.outputSchema ?? {},
        ];
        const results = (node?.maps ?? []).map((map) => {
          const source = pointerToField(map.from);
          const target = pointerToField(map.to);
          const definition = schemaFields
            .map((schema) => {
              const properties = schema.properties;
              const propertyMap =
                properties && typeof properties === 'object' && !Array.isArray(properties)
                  ? (properties as Record<string, unknown>)
                  : schema;
              return propertyMap[source] ?? propertyMap[target];
            })
            .find((value) => value && typeof value === 'object') as
            Record<string, unknown> | undefined;
          const raw = proposed[target];
          const value =
            typeof raw === 'boolean'
              ? raw
                ? 'Yes'
                : 'No'
              : raw == null
                ? '—'
                : typeof raw === 'string' || typeof raw === 'number'
                  ? String(raw)
                  : (JSON.stringify(raw) ?? '—');
          return {
            label:
              typeof definition?.title === 'string' ? definition.title : target.replace(/_/g, ' '),
            value,
          };
        });
        const displayNumbers = workflowDisplayNumbers(stepsFromDefinition(workflow.snapshot));
        setWorkflowDetails({
          name: workflow.snapshot.name,
          step: displayNumbers.get(nodeId) ?? nodeIndex + 1,
          results,
        });
      })
      .catch(() => {
        if (!cancelled) setWorkflowDetails(undefined);
      });
    return () => {
      cancelled = true;
    };
  }, [workflowReview, item.workflowId, item.instanceId, item.nodeId, item.argumentsJson]);
  const link = sessionHref(item);
  const outerLink = outerSessionHref(item);

  return (
    <div
      className="rounded-xl border border-border bg-card p-4"
      aria-busy={busy}
      data-testid="pending-approval"
      data-approval-request-id={item.id}
      data-run-id={item.runId}
      data-outer-session-id={item.outerSessionId}
      data-tool-name={workflowReview ? undefined : item.toolName}
    >
      <div className="mb-2 flex flex-wrap items-center justify-between gap-2">
        <div className="flex items-center gap-2">
          {workflowReview ? (
            <span className="font-semibold text-foreground">
              {workflowDetails?.name ?? 'Workflow'}
              {workflowDetails
                ? ` · Step ${workflowDetails.step} (${item.agentName ?? 'Agent'})`
                : item.agentName
                  ? ` · ${item.agentName}`
                  : ''}
            </span>
          ) : (
            <>
              <span className="rounded-full bg-attention/15 px-2 py-0.5 text-xs font-semibold text-attention">
                {item.toolType.toUpperCase()}
              </span>
              <span className="font-semibold text-foreground">{item.toolName}</span>
            </>
          )}
          {item.mcpServerId ? (
            <span className="text-xs text-muted-foreground">MCP: {item.mcpServerId}</span>
          ) : null}
          {item.outerAgentId || item.outerSessionId ? (
            <span className="rounded-full bg-primary/15 px-2 py-0.5 text-xs font-semibold text-primary">
              Outer run
            </span>
          ) : null}
        </div>
        <div className="text-xs text-muted-foreground">
          {formatRelativeTime(item.requestedAt)} · {computeRemaining(item.expiresAt)}
        </div>
      </div>

      {item.argumentsJson && !workflowReview ? (
        <pre className="mb-3 max-h-40 overflow-auto rounded-lg bg-muted/40 p-2 text-[11px] leading-relaxed text-foreground/80 whitespace-pre-wrap break-all">
          {prettyJson(item.argumentsJson)}
        </pre>
      ) : null}

      {workflowReview ? (
        <p className="mb-3 text-xs text-muted-foreground">
          {remainingChanges === 0
            ? 'No change requests left.'
            : `${remainingChanges} more change request${remainingChanges === 1 ? '' : 's'} left.`}
        </p>
      ) : null}

      {workflowReview && workflowDetails?.results.length ? (
        <dl className="mb-3 space-y-1 rounded-lg bg-muted/40 p-3 text-sm">
          {workflowDetails.results.map((result) => (
            <div key={result.label} className="flex justify-between gap-3">
              <dt className="text-muted-foreground">{result.label}</dt>
              <dd className="text-right font-medium">{result.value}</dd>
            </div>
          ))}
        </dl>
      ) : null}

      <div className="mb-3 flex flex-wrap gap-4 text-xs text-muted-foreground">
        <span>Requested: {parseApiTimestamp(item.requestedAt).toLocaleString()}</span>
        {!workflowReview && item.agentName ? (
          <span>Agent: {item.agentName}</span>
        ) : !workflowReview && item.agentId ? (
          <span>Agent: {item.agentId}</span>
        ) : null}
        {item.outerAgentId ? (
          <span>Part of: {item.outerAgentName ?? item.outerAgentId}</span>
        ) : null}
      </div>

      <div className="flex flex-wrap items-center gap-2">
        {item.actions
          .filter(
            (action) =>
              action.value !== 'request_changes' ||
              !workflowReview ||
              (requestChangesAction !== undefined && remainingChanges > 0)
          )
          .map((action) => {
            const isPositive = action.style === 'primary' || action.style === 'success';
            const isDestructive = action.style === 'danger';
            const needsRunId = action.requiresRunId;
            if (needsRunId && !item.runId) return null;
            return (
              <Button
                key={action.value}
                type="button"
                size="xs"
                variant={
                  isPositive
                    ? 'default'
                    : isDestructive && action.value === 'stop_run'
                      ? 'destructive'
                      : 'secondary'
                }
                className="gap-1"
                data-approval-action={action.value}
                disabled={busy}
                onClick={() => {
                  if (action.value === 'approve') onResolve(item.id, 'approve');
                  else if (action.value === 'request_changes') setRequestingChanges(true);
                  else if (action.value === 'deny') onResolve(item.id, 'deny');
                  else if (action.value === 'stop_run') onResolve(item.id, 'stop_run');
                }}
              >
                {isPositive ? <Check className="h-3 w-3" /> : <X className="h-3 w-3" />}
                {workflowReview && action.value === 'request_changes'
                  ? action.label.replace(/\s+\(\d+ left\)$/, '')
                  : action.label}
              </Button>
            );
          })}
        {item.workflowId && item.instanceId ? (
          <Button asChild variant="secondary" size="xs">
            <Link to={`/workflows/${item.workflowId}/instances/${item.instanceId}`}>
              View workflow run
              <ExternalLink className="h-3 w-3" />
            </Link>
          </Button>
        ) : null}
        {link ? (
          <Button asChild variant="secondary" size="xs">
            <Link to={link}>
              View session
              <ExternalLink className="h-3 w-3" />
            </Link>
          </Button>
        ) : null}
        {outerLink ? (
          <Button asChild variant="secondary" size="xs" className="ml-auto">
            <Link to={outerLink}>
              View outer session
              <ExternalLink className="h-3 w-3" />
            </Link>
          </Button>
        ) : null}
      </div>
      {requestingChanges ? (
        <div className="mt-3 space-y-2">
          <label className="block text-xs font-medium" htmlFor={`review-note-${item.id}`}>
            What should change?
          </label>
          <textarea
            id={`review-note-${item.id}`}
            className="min-h-20 w-full rounded-lg border border-input bg-background px-3 py-2 text-sm"
            value={changeNote}
            onChange={(event) => setChangeNote(event.target.value)}
          />
          <Button
            type="button"
            size="xs"
            disabled={busy || !changeNote.trim()}
            onClick={() => onResolve(item.id, 'request_changes', changeNote.trim())}
          >
            Send
          </Button>
          <Button
            type="button"
            size="xs"
            variant="outline"
            disabled={busy}
            onClick={() => {
              setRequestingChanges(false);
              setChangeNote('');
            }}
          >
            Cancel
          </Button>
        </div>
      ) : null}
    </div>
  );
}
