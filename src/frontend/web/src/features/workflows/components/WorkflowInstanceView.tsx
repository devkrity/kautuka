import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert';
import { Button } from '@/components/ui/button';
import { PageIntro } from '@/components/layout/PageIntro';
import { PageState } from '@/components/layout/PageState';
import { apiError, useAgents } from '@/features/settings';
import { usePendingApprovals, useResolveApproval } from '@/features/approvals';
import { queryClient } from '@/lib/queryClient';
import React from 'react';
import { Link } from 'react-router-dom';
import {
  useStopWorkflowInstance,
  useWorkflow,
  useWorkflowInstance,
  useWorkflowJournal,
} from '../api/queries';
import { markTakenChoices, takenChoiceIds } from '../model/workflowDiagramMark';
import { workflowQueryKeys } from '../api/queryKeys';
import {
  WorkflowNaradProvider,
  useWorkflowLiveState,
  useWorkflowNaradAttachError,
} from '../narad/WorkflowNaradProvider';
import {
  describeWorkflowFailure,
  workflowRunStatusText,
  workflowStatusLabel,
} from '../model/workflowLabels';
import { fieldDisplayName, stepsFromDefinition } from '../model/workflowDraft';
import { workflowDisplayNumbers } from '../model/workflowCanvas';
import { WorkflowDiagram } from './WorkflowDiagram';
import { WorkflowRunTimeline } from './WorkflowRunTimeline';
import { WorkflowStatusBadge } from './WorkflowStatusBadge';
import { WorkflowStepLedger } from './WorkflowStepLedger';

interface WorkflowInstanceViewProps {
  workflowId: string;
  instanceId: string;
}

export const WorkflowInstanceView: React.FC<WorkflowInstanceViewProps> = ({
  workflowId,
  instanceId,
}) => (
  <WorkflowNaradProvider workflowId={workflowId} instanceId={instanceId}>
    <WorkflowInstanceBody workflowId={workflowId} instanceId={instanceId} />
  </WorkflowNaradProvider>
);

const WorkflowInstanceBody: React.FC<WorkflowInstanceViewProps> = ({ workflowId, instanceId }) => {
  const { data: instance, isLoading, error } = useWorkflowInstance(workflowId, instanceId);
  const definition = useWorkflow(workflowId);
  const journal = useWorkflowJournal(workflowId, instanceId);
  const live = useWorkflowLiveState(instanceId);
  const attachError = useWorkflowNaradAttachError();
  const { data: agents = [] } = useAgents();
  const stop = useStopWorkflowInstance(workflowId, instanceId);
  const approvals = usePendingApprovals('mine', {
    enabled: instance?.status === 'paused' || live?.state === 'paused',
  });
  const resolveApproval = useResolveApproval();
  const [actionError, setActionError] = React.useState<string | null>(null);
  const [changeNote, setChangeNote] = React.useState('');
  const [requestingChanges, setRequestingChanges] = React.useState(false);
  const agentName = React.useMemo(
    () => new Map(agents.map((agent) => [agent.id, agent.name])),
    [agents]
  );

  React.useEffect(() => {
    if (!live) {
      return;
    }
    void queryClient.invalidateQueries({
      queryKey: workflowQueryKeys.instance(workflowId, instanceId),
    });
    void queryClient.invalidateQueries({ queryKey: workflowQueryKeys.instanceLists() });
    void queryClient.invalidateQueries({
      queryKey: workflowQueryKeys.journal(workflowId, instanceId),
    });
  }, [instanceId, live?.lastWorkflowSequence, live?.state, workflowId]);

  const onStop = async () => {
    setActionError(null);
    try {
      await stop.mutateAsync();
    } catch (stopFailure) {
      setActionError(apiError(stopFailure));
    }
  };

  const onResolveReview = async (
    action: 'approve' | 'request_changes' | 'stop_run',
    reason?: string
  ) => {
    const pendingReview = approvals.data?.find(
      (approval) =>
        approval.instanceId === instanceId &&
        (approval.kind === 'workflow_review' || approval.toolType === 'workflow_review')
    );
    if (!pendingReview) return;
    setActionError(null);
    try {
      await resolveApproval.mutateAsync({
        approvalId: pendingReview.id,
        reason: action === 'request_changes' ? reason?.trim() : undefined,
        action,
      });
      await Promise.all([
        queryClient.invalidateQueries({
          queryKey: workflowQueryKeys.instance(workflowId, instanceId),
        }),
        queryClient.invalidateQueries({ queryKey: workflowQueryKeys.instanceLists() }),
        queryClient.invalidateQueries({
          queryKey: workflowQueryKeys.journal(workflowId, instanceId),
        }),
      ]);
    } catch (resolveFailure) {
      setActionError(apiError(resolveFailure));
    }
  };

  if (isLoading) {
    return <PageState message="Loading this run…" />;
  }

  if (error || !instance) {
    return <PageState message={error ? apiError(error) : 'Run not found.'} />;
  }

  const status = instance.status === 'running' ? (live?.state ?? instance.status) : instance.status;
  const currentNodeId = live?.currentNodeId ?? instance.currentNodeId;
  const stopRequested = live?.stopRequested ?? instance.stopRequested;
  const canStop = status === 'running';
  const canContinue = status === 'paused';
  const pendingReview = approvals.data?.find(
    (approval) =>
      approval.instanceId === instanceId &&
      (approval.kind === 'workflow_review' || approval.toolType === 'workflow_review')
  );
  const runTitle = String(instance.envelope.title ?? instance.snapshot.name);
  const failure = describeWorkflowFailure(instance.failureCode, instance.error);
  const displayNumbers = workflowDisplayNumbers(stepsFromDefinition(instance.snapshot));
  const orderedNodes = [...instance.snapshot.nodes].sort(
    (left, right) =>
      (displayNumbers.get(left.nodeId) ?? Number.MAX_SAFE_INTEGER) -
      (displayNumbers.get(right.nodeId) ?? Number.MAX_SAFE_INTEGER)
  );
  const stepNames = new Map(
    instance.snapshot.nodes.map((node) => [
      node.nodeId,
      `Step ${displayNumbers.get(node.nodeId)} · ${agentName.get(node.agentId) ?? 'Unknown agent'}`,
    ])
  );
  const fieldLabels = Object.assign(
    {},
    ...[
      instance.snapshot.inputSchema,
      instance.snapshot.workingSchema,
      ...instance.snapshot.nodes.map((node) => node.outputSchema ?? {}),
    ].map((schema) => {
      const properties = schema.properties;
      if (!properties || typeof properties !== 'object' || Array.isArray(properties)) return {};
      return Object.fromEntries(
        Object.entries(properties as Record<string, unknown>).map(([key, value]) => [
          key,
          value &&
          typeof value === 'object' &&
          'title' in value &&
          typeof value.title === 'string' &&
          value.title.trim()
            ? value.title
            : fieldDisplayName({ name: key }),
        ])
      );
    })
  );
  const currentIndex = instance.snapshot.nodes.findIndex((node) => node.nodeId === currentNodeId);
  const currentStepNumber = currentNodeId ? displayNumbers.get(currentNodeId) : undefined;
  const requestChangesAction = pendingReview?.actions.find(
    (action) => action.value === 'request_changes'
  );
  const remainingChangeRequests = pendingReview?.remainingChangeRequests ?? 0;
  const pausedWithoutReview = status === 'paused' && !pendingReview;
  const pausedLabel =
    status === 'paused'
      ? workflowRunStatusText(instance, pendingReview ? [pendingReview] : [])
      : undefined;
  const statusDescription =
    status === 'paused'
      ? pendingReview
        ? 'Waiting for your review.'
        : 'This run is paused.'
      : status === 'running'
        ? 'This workflow is running. The step journal shows its progress.'
        : 'This is one run of the workflow. The journal is the record of what happened.';
  const events = journal.data ?? [];
  const taken = takenChoiceIds(events);
  const chart = definition.data?.diagram ? markTakenChoices(definition.data.diagram, taken) : null;

  return (
    <div className="page-shell gap-6">
      <PageIntro
        kicker="Workflow run"
        title={runTitle}
        description={statusDescription}
        actions={
          <div className="flex flex-wrap gap-2">
            <Button variant="outline" className="hidden sm:inline-flex" asChild>
              <Link to={`/workflows/${workflowId}/edit`}>Edit workflow</Link>
            </Button>
            {canStop ? (
              <Button variant="destructive" onClick={() => void onStop()} disabled={stop.isPending}>
                {stop.isPending ? 'Stopping…' : 'Stop'}
              </Button>
            ) : null}
          </div>
        }
      />

      {actionError ? (
        <Alert variant="destructive">
          <AlertTitle>Could not update this run</AlertTitle>
          <AlertDescription>{actionError}</AlertDescription>
        </Alert>
      ) : null}

      {attachError ? (
        <Alert variant="destructive">
          <AlertTitle>Live journal unavailable</AlertTitle>
          <AlertDescription>{attachError}</AlertDescription>
        </Alert>
      ) : null}

      {canContinue ? (
        <Alert>
          <AlertTitle>
            {pendingReview
              ? currentIndex >= 0
                ? `Step ${currentStepNumber ?? currentIndex + 1} is ready for you to check`
                : 'Ready for you to check'
              : 'This run is paused'}
          </AlertTitle>
          <AlertDescription>
            {pendingReview
              ? 'Review the results, then choose what happens next.'
              : 'There is no open review for this step.'}
            {!pendingReview ? (
              <div className="mt-3">
                <Button
                  type="button"
                  variant="destructive"
                  onClick={() => void onStop()}
                  disabled={stop.isPending}
                >
                  {stop.isPending ? 'Stopping…' : 'Stop run'}
                </Button>
              </div>
            ) : null}
            {pendingReview ? (
              <p className="mt-2 text-xs text-muted-foreground">
                {remainingChangeRequests === 0
                  ? 'No change requests left.'
                  : `${remainingChangeRequests} more change request${remainingChangeRequests === 1 ? '' : 's'} left.`}
              </p>
            ) : null}
            {approvals.error ? <p role="alert">{apiError(approvals.error)}</p> : null}
            {pendingReview ? (
              <div className="mt-3 flex flex-wrap gap-2">
                <Button
                  type="button"
                  disabled={resolveApproval.isPending}
                  onClick={() => void onResolveReview('approve')}
                >
                  Approve
                </Button>
                {requestChangesAction && remainingChangeRequests > 0 ? (
                  <Button
                    type="button"
                    variant="outline"
                    disabled={resolveApproval.isPending}
                    onClick={() => setRequestingChanges(true)}
                  >
                    Request changes
                  </Button>
                ) : null}
                {requestingChanges ? (
                  <div className="w-full space-y-2">
                    <label className="block text-sm font-medium" htmlFor="workflow-review-note">
                      What should change?
                    </label>
                    <textarea
                      id="workflow-review-note"
                      className="min-h-20 w-full rounded-lg border border-input bg-background px-3 py-2 text-sm"
                      value={changeNote}
                      onChange={(event) => setChangeNote(event.target.value)}
                    />
                    <Button
                      type="button"
                      disabled={resolveApproval.isPending || !changeNote.trim()}
                      onClick={() => void onResolveReview('request_changes', changeNote)}
                    >
                      Send
                    </Button>
                    <Button
                      type="button"
                      variant="outline"
                      disabled={resolveApproval.isPending}
                      onClick={() => {
                        setRequestingChanges(false);
                        setChangeNote('');
                      }}
                    >
                      Cancel
                    </Button>
                  </div>
                ) : null}
                <Button
                  type="button"
                  variant="destructive"
                  disabled={resolveApproval.isPending}
                  onClick={() => void onResolveReview('stop_run')}
                >
                  Stop run
                </Button>
              </div>
            ) : approvals.isLoading ? (
              <p>Finding the approval…</p>
            ) : null}
          </AlertDescription>
        </Alert>
      ) : null}

      {failure && (status === 'error' || status === 'cancelled') ? (
        <Alert variant="destructive">
          <AlertTitle>{workflowStatusLabel[status] ?? status}</AlertTitle>
          <AlertDescription>{failure}</AlertDescription>
        </Alert>
      ) : null}

      {chart ? (
        <WorkflowDiagram
          chart={chart}
          description={taken.size > 0 ? 'The marked arrow is the path this run took.' : undefined}
        />
      ) : null}

      <div className="grid gap-6 xl:grid-cols-[minmax(0,1.4fr)_minmax(20rem,0.8fr)]">
        <section className="workspace-panel space-y-4">
          <div className="flex items-center justify-between gap-3">
            <h2 className="text-sm font-semibold">Step journal</h2>
            <WorkflowStatusBadge status={status} label={pausedLabel} />
          </div>
          {stopRequested ? <p className="text-xs text-muted-foreground">Stop requested.</p> : null}
          <ol className="space-y-3">
            {orderedNodes.map((node) => {
              const attempts = instance.steps.filter((step) => step.nodeId === node.nodeId);
              const liveStep = live?.steps.get(node.nodeId);
              const latest = attempts[attempts.length - 1];
              const stepStatus = liveStep?.status ?? latest?.status ?? 'pending';
              const stepSessionId = liveStep?.sessionId ?? latest?.sessionId ?? null;
              return (
                <WorkflowStepLedger
                  key={node.nodeId}
                  stepNumber={displayNumbers.get(node.nodeId) ?? 0}
                  node={node}
                  agentName={agentName.get(node.agentId) ?? 'Unknown agent'}
                  attempts={attempts}
                  events={events.filter((event) => event.nodeId === node.nodeId)}
                  envelope={instance.envelope}
                  fieldLabels={fieldLabels}
                  status={stepStatus}
                  pausedRunLabel={pausedLabel}
                  sessionId={stepSessionId}
                  active={currentNodeId === node.nodeId}
                />
              );
            })}
          </ol>
        </section>

        <div className="space-y-6">
          <section className="workspace-panel space-y-3">
            <h2 className="text-sm font-semibold">Run information</h2>
            <p className="text-lg font-medium leading-snug">{runTitle}</p>
            <div className="rounded-xl bg-muted/40 px-3 py-2">
              <p className="text-xs font-medium text-muted-foreground">All run values</p>
              <dl className="mt-1 space-y-1">
                {Object.entries(instance.envelope).map(([key, value]) => (
                  <div key={key} className="flex items-baseline justify-between gap-3 text-sm">
                    <dt className="text-muted-foreground">
                      {fieldLabels[key] ?? fieldDisplayName({ name: key })}
                    </dt>
                    <dd className="break-words text-right font-medium">
                      {formatEnvelopeValue(value)}
                    </dd>
                  </div>
                ))}
              </dl>
            </div>
          </section>

          <WorkflowRunTimeline
            instanceId={instanceId}
            events={events}
            stepNames={stepNames}
            isLive={status === 'running' || status === 'paused'}
            isLoading={journal.isLoading}
            error={journal.error ? apiError(journal.error) : null}
            pausedWithoutReview={pausedWithoutReview}
            pausedWithReview={status === 'paused' && Boolean(pendingReview)}
          />
        </div>
      </div>
    </div>
  );
};

function formatEnvelopeValue(value: unknown): string {
  if (value === null || value === undefined || value === '') {
    return '—';
  }
  if (typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean') {
    return String(value);
  }
  return JSON.stringify(value);
}
