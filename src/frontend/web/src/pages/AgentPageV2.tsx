import { MarkdownRenderer } from '@/components/MarkdownRenderer';
import { PageState } from '@/components/layout/PageState';
import { WorkspaceSidebar } from '@/components/layout/WorkspaceSidebar';
import { Alert } from '@/components/ui/alert';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogTitle } from '@/components/ui/dialog';
import { useCurrentUser } from '@/features/auth';
import { AgentFilesPanel, downloadAgentFile, openDownloadedFile } from '@/features/agent-files';
import {
  ActivityLedger,
  ConversationAttachmentChips,
  CopyButton,
  ExecutionGraphPanel,
  KautukaNaradProvider,
  ApprovalCard,
  MessageAttachmentComposer,
  SessionQueue,
  createSession,
  fetchRunTrace,
  listSessionsForAgent,
  renderMetrics,
  formatSessionTimestamp,
  formatTime,
  RunTraceInspector,
  uploadConversationAsset,
  useComposerAttachments,
  useKautukaNaradConnectionStatus,
  useKautukaNaradContext,
  useKautukaNaradIntents,
  useKautukaActiveRootGraph,
  useKautukaComposerLocked,
  useKautukaMergedOpenInterrupts,
  useKautukaPartialAssistantText,
  useKautukaRunSummary,
  useKautukaTranscriptTurns,
  useSessionAssets,
  type ActivityEntry,
  type ChatMetrics,
  type ChatSessionViewApproval,
  type ConversationAssetPayload,
  type KautukaTranscriptTurn,
  type PendingApproval,
  type RunTraceView,
  type Session,
  classifyExecutionGraphPresentation,
  formatNaradIntentFailure,
  isNaradIntentAccepted,
} from '@/features/chat';
import {
  AgentConfigurationDialog,
  ROLE_ADMIN,
  useAgents,
} from '@/features/settings';
import type { AgentSummary } from '@/features/settings';
import { HitlWorkspaceSummary } from '@/features/approvals';
import { cn } from '@/lib/utils';
import {
  ArrowUp,
  Folder,
  LoaderCircle,
  Plus,
  Settings,
  Square,
  ScanSearch,
} from 'lucide-react';
import React from 'react';
import { useNavigate, useParams, useSearchParams } from 'react-router-dom';

const sessionTitleParts = (title: string) => {
  const match = /^\[(Chat|Autopilot|Tool|Telegram|Slack)\]\s*/i.exec(title);
  const kind = match?.[1]?.toLowerCase();
  const cleanTitle = title.replace(/^\[(?:Chat|Autopilot|Tool|Telegram|Slack)\]\s*/i, '').trim();
  const labels: Record<string, string> = {
    autopilot: 'Autopilot',
    tool: 'Tool',
    telegram: 'Telegram',
    slack: 'Slack',
  };
  return {
    kind: kind ? labels[kind] ?? 'Chat' : 'Chat',
    title: cleanTitle || 'Untitled session',
  };
};

const mapApproval = (approval: ChatSessionViewApproval): PendingApproval => ({
  requestId: approval.id,
  toolCallId: approval.toolCallId,
  toolName: approval.toolName,
  argumentsJson: approval.argumentsJson ?? undefined,
  requestedAt: approval.requestedAt,
  decidedAt: approval.decidedAt ?? undefined,
  status:
    approval.decision === 'approved'
      ? 'approved'
      : approval.decision === 'denied'
        ? 'denied'
        : approval.decision === 'expired'
          ? 'expired'
          : approval.decision === 'stopped'
            ? 'stopped'
            : 'pending',
  resolvedByLabel: approval.decidedByDisplayName ?? approval.decidedBy ?? undefined,
  resolvedVia: approval.resolutionSource ?? undefined,
  runId: approval.runId ?? undefined,
  checkpointId: approval.checkpointId ?? undefined,
  agentId: approval.agentId ?? undefined,
  outerSessionId: approval.outerSessionId ?? undefined,
  outerAgentId: approval.outerAgentId ?? undefined,
  toolType: approval.toolType,
  mcpServerId: approval.mcpServerId ?? undefined,
  callerToolCallId: approval.callerToolCallId ?? undefined,
  callerToolName: approval.callerToolName ?? undefined,
  checkpointIndex: approval.checkpointIndex ?? undefined,
  checkpointBoundaryType: approval.checkpointBoundaryType ?? undefined,
  checkpointCreatedAt: approval.checkpointCreatedAt ?? undefined,
  checkpointResumable: approval.checkpointResumable,
  actions: approval.actions,
});

const TurnRunStatusChip: React.FC<{
  label: string;
  testId?: string;
}> = ({ label, testId = 'turn-run-status' }) => (
  <Badge variant="info" className="gap-1" data-testid={testId} title={label}>
    <LoaderCircle className="h-3 w-3 animate-spin" />
    {label}
  </Badge>
);

const OpenViewCard: React.FC<{
  agentId: string;
  view: KautukaTranscriptTurn['views'][number];
}> = ({ agentId, view }) => {
  const intents = useKautukaNaradIntents();
  const record = view.blocks.find((block) => block.type === 'record');
  const files = view.blocks.flatMap((block) => (block.type === 'file' ? [block] : []));
  const actions = view.blocks.flatMap((block) => (block.type === 'actions' ? [block] : []));
  const prose = record
    ? []
    : view.blocks.flatMap((block) => (block.type === 'text' ? [block] : []));
  const openFile = async (path: string) => {
    const downloaded = await downloadAgentFile(agentId, path);
    await openDownloadedFile(downloaded.blob, downloaded.fileName);
  };
  return (
    <section className="mt-3 overflow-hidden rounded-2xl border border-border/70 bg-card" data-testid="open-view">
      <div className="flex items-baseline justify-between gap-3 border-b border-border/60 px-3 py-2">
        <div className="truncate text-sm font-semibold text-foreground">{view.title}</div>
        {record?.type === 'record' && record.status ? (
          <span className="text-[11px] font-medium text-muted-foreground">{record.status}</span>
        ) : null}
      </div>
      {record?.type === 'record' ? (
        <div className="space-y-3 px-3 py-3">
          {record.fields.length > 0 ? (
            <dl className="space-y-1">
              {record.fields.map((field) => (
                <div key={field.label} className="grid grid-cols-[4.5rem_1fr] gap-2 text-sm">
                  <dt className="text-muted-foreground">{field.label}</dt>
                  <dd className="min-w-0 truncate">{field.value}</dd>
                </div>
              ))}
            </dl>
          ) : null}
          {record.body ? (
            <div className="max-h-96 overflow-y-auto whitespace-pre-wrap text-sm leading-relaxed">
              {record.body}
            </div>
          ) : null}
        </div>
      ) : null}
      {prose.map((block, index) => (
        <p key={`${view.viewId}-text-${index}`} className="whitespace-pre-wrap px-3 py-3 text-sm leading-relaxed">
          {block.text}
        </p>
      ))}
      {files.some((file) => file.role === 'primary') ? (
        <div className="flex flex-wrap gap-2 px-3 py-3">
          {files.filter((file) => file.role === 'primary').map((file) => (
            <FileChip key={`${view.viewId}-${file.name}`} file={file} onOpen={openFile} />
          ))}
        </div>
      ) : null}
      {files.some((file) => file.role === 'source') ? (
        <div className="flex flex-wrap gap-3 px-3 pb-3">
          {files.filter((file) => file.role === 'source').map((file) => (
            <button
              key={`${view.viewId}-source-${file.name}`}
              type="button"
              className="text-xs text-muted-foreground underline disabled:no-underline"
              disabled={!file.path}
              onClick={() => file.path && void openFile(file.path)}
            >
              Open {file.name}
            </button>
          ))}
        </div>
      ) : null}
      {actions.map((block) => (
        <div key={`${view.viewId}-actions`} className="flex flex-wrap gap-2 border-t border-border/60 px-3 py-2">
          {block.items.map((item) => (
            <Button
              key={item.name}
              type="button"
              variant="secondary"
              onClick={() => void intents.sendUserMessage(`${item.label}\n\nView action ${item.name} on ${view.viewId}.`)}
            >
              {item.label}
            </Button>
          ))}
        </div>
      ))}
    </section>
  );
};

const FileChip: React.FC<{
  file: Extract<KautukaTranscriptTurn['views'][number]['blocks'][number], { type: 'file' }>;
  onOpen(path: string): Promise<void>;
}> = ({ file, onOpen }) => {
  const extension = file.name.includes('.') ? file.name.split('.').pop()?.slice(0, 4).toUpperCase() : 'FILE';
  return (
    <button
      type="button"
      className="inline-flex max-w-full items-center gap-2 rounded-lg border border-border bg-background px-2.5 py-1.5 text-left hover:bg-accent disabled:opacity-60"
      disabled={!file.path}
      onClick={() => file.path && void onOpen(file.path)}
    >
      <span className="text-[10px] font-semibold uppercase tracking-wide text-muted-foreground">{extension}</span>
      <span className="truncate text-sm">{file.name}</span>
    </button>
  );
};

const TurnCard: React.FC<{
  turn: KautukaTranscriptTurn;
  agentId: string;
  agentName: string;
  agentIcon: string;
  agentColor: string;
  sessionId: string | null;
  assetsById: ReadonlyMap<string, ConversationAssetPayload>;
  isLatestActive: boolean;
  /** Session chrome status for the live root, or null when this turn is not live. */
  runStatusLabel: string | null;
  liveText: string;
  onOpenTrace(runId: string): void;
}> = ({ turn, agentId, agentName, agentIcon, agentColor, sessionId, assetsById, isLatestActive, runStatusLabel, liveText, onOpenTrace }) => {
  const assistantContent =
    turn.assistant?.content?.trim() ||
    (isLatestActive ? liveText : '');
  const reasoning = turn.assistant?.thinking?.trim() || null;
  const hasActivity = turn.activityEntries.length > 0;
  const showAssistant = Boolean(assistantContent) || Boolean(reasoning) || isLatestActive || hasActivity;
  const rootRunLive = isLatestActive && Boolean(runStatusLabel);
  const traceRunId = turn.rootRunId?.trim() || null;
  const usageLabel = turn.usage
    ? renderMetrics({
        tokenIn: turn.usage.promptTokens,
        tokenOut: turn.usage.completionTokens,
        elapsedMs: turn.usage.elapsedMs ?? 0,
      } satisfies ChatMetrics)
    : null;
  return (
    <div className="space-y-5">
      {turn.user ? (
        <article className="group/msg ml-auto max-w-3xl rounded-3xl bg-muted px-5 py-4 shadow-sm">
          <div className="section-label mb-2">
            You · {formatTime(turn.user.timestamp)}
          </div>
          {turn.user.content.trim() ? (
            <div className="whitespace-pre-wrap text-sm leading-relaxed">{turn.user.content}</div>
          ) : null}
          <ConversationAttachmentChips
            attachmentIds={turn.user.attachmentIds}
            assetsById={assetsById}
          />
          {turn.user.content.trim() ? (
            <CopyButton text={turn.user.content} align="end" />
          ) : null}
        </article>
      ) : null}
      {showAssistant ? (
        <article className="group/msg mr-auto max-w-4xl rounded-3xl border border-border/50 border-l-4 bg-card px-5 py-4 shadow-sm" style={{ borderLeftColor: agentColor }}>
          <div className="mb-3 flex items-center justify-between gap-3">
            <div className="flex min-w-0 items-center gap-2">
              <span className="flex h-6 w-6 shrink-0 items-center justify-center rounded-full text-sm" style={{ backgroundColor: `${agentColor}20` }} aria-hidden="true">{agentIcon}</span>
              <span className="truncate text-xs font-semibold text-foreground">{agentName}</span>
              <span className="section-label">Assistant {turn.assistant ? `· ${formatTime(turn.assistant.timestamp)}` : ''}</span>
            </div>
            <div className="flex items-center gap-2">
              {usageLabel && !isLatestActive ? (
                <span
                  className="inline-flex items-center rounded-full border border-border/70 px-2 py-1 text-[11px] font-medium text-muted-foreground"
                  data-testid="turn-usage"
                  title="Token usage for this root run"
                >
                  {usageLabel}
                </span>
              ) : null}
              {traceRunId && sessionId ? (
                <Button
                  type="button"
                  title="Inspect run trace"
                  aria-label="Inspect run trace"
                  data-testid="run-trace-open"
                  onClick={() => onOpenTrace(traceRunId)}
                  variant="outline"
                  size="xs"
                  className="rounded-full text-muted-foreground"
                >
                  <ScanSearch className="h-3 w-3" />
                  Trace
                </Button>
              ) : null}
              {/*
                Root run liveness (same source as workspace chrome). Do not gate on empty
                activity — after approve the ledger may only show terminal HITL rows while
                tool.call.started / nested work is still in flight.
              */}
              {rootRunLive && runStatusLabel ? (
                <TurnRunStatusChip label={runStatusLabel} />
              ) : null}
            </div>
          </div>
          {reasoning ? (
            <details className="mb-3 rounded-lg border border-border/50 bg-muted/30 px-3 py-2 text-sm">
              <summary className="section-label cursor-pointer">
                Reasoning
              </summary>
              <div className="mt-2 whitespace-pre-wrap text-muted-foreground">{reasoning}</div>
            </details>
          ) : null}
          {assistantContent ? (
            <MarkdownRenderer content={assistantContent} />
          ) : rootRunLive && !hasActivity ? (
            <div className="text-sm text-muted-foreground" data-testid="turn-working-placeholder">
              The assistant is working...
            </div>
          ) : null}
          {turn.views.map((view) => (
            <OpenViewCard key={view.viewId} agentId={agentId} view={view} />
          ))}
          {hasActivity ? (
            <ActivityLedger
              activityEntries={[...turn.activityEntries]}
              isLive={isLatestActive}
              rootRunLive={rootRunLive}
            />
          ) : null}
          {usageLabel && isLatestActive && turn.usage ? (
            <div className="mt-3 text-[11px] text-muted-foreground" data-testid="turn-usage-live">
              {usageLabel}
            </div>
          ) : null}
          {assistantContent ? <CopyButton text={assistantContent} align="start" /> : null}
        </article>
      ) : null}
    </div>
  );
};

const PendingAssistantTurn: React.FC<{ liveText: string; runStatusLabel: string; agentName: string; agentIcon: string; agentColor: string }> = ({
  liveText,
  runStatusLabel,
  agentName,
  agentIcon,
  agentColor,
}) => (
  <article className="mr-auto max-w-4xl rounded-3xl border border-border/50 border-l-4 bg-card px-5 py-4 shadow-sm" style={{ borderLeftColor: agentColor }}>
    <div className="mb-3 flex items-center justify-between gap-3">
      <div className="flex min-w-0 items-center gap-2">
        <span className="flex h-6 w-6 shrink-0 items-center justify-center rounded-full text-sm" style={{ backgroundColor: `${agentColor}20` }} aria-hidden="true">{agentIcon}</span>
        <span className="truncate text-xs font-semibold text-foreground">{agentName}</span>
        <span className="section-label">Assistant</span>
      </div>
      <TurnRunStatusChip label={runStatusLabel} />
    </div>
    {liveText ? (
      <MarkdownRenderer content={liveText} />
    ) : (
      <div className="text-sm text-muted-foreground" data-testid="turn-working-placeholder">
        The assistant is working...
      </div>
    )}
  </article>
);

const ApprovalDock: React.FC<{
  approvals: ChatSessionViewApproval[];
  resolvingRequestIds: ReadonlySet<string>;
  onDecision(approval: ChatSessionViewApproval, approved: boolean): void;
  onStopRun(approval: ChatSessionViewApproval): void;
}> = ({ approvals, resolvingRequestIds, onDecision, onStopRun }) => {
  if (approvals.length === 0) return null;
  const anyResolving = resolvingRequestIds.size > 0;
  return (
    <div className="rounded-2xl border border-attention/30 bg-attention/10 p-3 shadow-xl">
      <div className="section-label mb-2 text-attention">
        Approval required
      </div>
      <div className="space-y-3">
        {approvals.map((approval) => (
          <ApprovalCard
            key={approval.id}
            approval={mapApproval(approval)}
            actionsDisabled={anyResolving || resolvingRequestIds.has(approval.id)}
            contextLabel={
              approval.outerAgentName
                ? `Requested by ${approval.outerAgentName} while the agent is running.`
                : undefined
            }
            onDecide={(approved) => onDecision(approval, approved)}
            onStopRun={() => onStopRun(approval)}
          />
        ))}
      </div>
    </div>
  );
};

type AgentWorkspaceProps = Readonly<{
  selectedAgent: AgentSummary;
  sessionId: string;
  setError: React.Dispatch<React.SetStateAction<string | null>>;
  isFilesOpen: boolean;
  setIsFilesOpen: React.Dispatch<React.SetStateAction<boolean>>;
}>;

const AgentWorkspace: React.FC<AgentWorkspaceProps> = ({
  selectedAgent,
  sessionId,
  setError,
  isFilesOpen,
  setIsFilesOpen,
}) => {
  const { localDeliveryPending, attachSnapshot } = useKautukaNaradContext();
  const connectionStatus = useKautukaNaradConnectionStatus();
  const intents = useKautukaNaradIntents();
  const turns = useKautukaTranscriptTurns();
  const liveText = useKautukaPartialAssistantText();
  const runSummary = useKautukaRunSummary();
  const openInterrupts = useKautukaMergedOpenInterrupts(sessionId);
  const graphView = useKautukaActiveRootGraph();
  const composerLocked = useKautukaComposerLocked(localDeliveryPending);
  const [draft, setDraft] = React.useState('');
  const [resolvingRequestIds, setResolvingRequestIds] = React.useState<ReadonlySet<string>>(() => new Set());
  const [stoppingRunId, setStoppingRunId] = React.useState<string | null>(null);
  const [traceOpen, setTraceOpen] = React.useState(false);
  const [traceLoading, setTraceLoading] = React.useState(false);
  const [traceError, setTraceError] = React.useState<string | null>(null);
  const [traceView, setTraceView] = React.useState<RunTraceView | null>(null);
  const composerAttachments = useComposerAttachments(
    React.useCallback(
      async (file: File, origin: 'userUpload' | 'clipboardPaste') =>
        uploadConversationAsset(
          sessionId,
          file,
          origin,
          file.type.startsWith('image/') ? 'inlinePreview' : 'download'
        ),
      [sessionId]
    )
  );
  const turnAttachmentIds = React.useMemo(
    () => [...new Set(turns.flatMap((turn) => turn.user?.attachmentIds ?? []))],
    [turns]
  );
  const sessionAssetsQuery = useSessionAssets(sessionId);
  const turnAssetsQuery = useSessionAssets(sessionId, turnAttachmentIds);
  const assetsById = React.useMemo(() => {
    const next = new Map<string, ConversationAssetPayload>();
    for (const asset of sessionAssetsQuery.data ?? []) {
      next.set(asset.id, asset);
    }
    for (const asset of turnAssetsQuery.data ?? []) {
      next.set(asset.id, asset);
    }
    return next;
  }, [sessionAssetsQuery.data, turnAssetsQuery.data]);

  React.useEffect(() => {
    if (connectionStatus === 'unauthorized') {
      setError('Session access denied.');
    } else if (
      connectionStatus === 'error' &&
      !attachSnapshot.live &&
      !attachSnapshot.backfilling
    ) {
      setError('Live session connection failed.');
    } else if (connectionStatus === 'live' || connectionStatus === 'attaching') {
      setError(null);
    }
  }, [attachSnapshot.backfilling, attachSnapshot.live, connectionStatus, setError]);

  const trackResolution = React.useCallback((requestId: string, promise: Promise<unknown>) => {
    setResolvingRequestIds((current) => new Set([...current, requestId]));
    void promise.finally(() => {
      setResolvingRequestIds((current) => {
        const next = new Set(current);
        next.delete(requestId);
        return next;
      });
    });
  }, []);

  const handleOpenTrace = React.useCallback(
    async (runId: string) => {
      setTraceOpen(true);
      setTraceLoading(true);
      setTraceError(null);
      setTraceView(null);
      try {
        setTraceView(await fetchRunTrace(selectedAgent.id, sessionId, runId));
      } catch (err: unknown) {
        setTraceError(err instanceof Error ? err.message : String(err));
      } finally {
        setTraceLoading(false);
      }
    },
    [selectedAgent.id, sessionId]
  );

  const sendInFlightRef = React.useRef(false);
  const handleSend = async () => {
    if (sendInFlightRef.current || composerLocked || composerAttachments.hasUploading) return;
    const submittedDraft = draft;
    const prompt = draft.trim();
    const readyAssets = composerAttachments.readyAssets;
    if (!prompt && readyAssets.length === 0) return;
    setError(null);
    const assetsToSend = [...readyAssets];
    const submittedAttachmentIds = new Set(composerAttachments.items.filter(item => item.status === 'ready').map(item => item.localId));
    sendInFlightRef.current = true;
    try {
      const result = await intents.sendUserMessage(prompt, assetsToSend);
      if (!isNaradIntentAccepted(result.status)) {
        setError(formatNaradIntentFailure(result));
      } else {
        setDraft(current => current === submittedDraft ? '' : current);
        for (const id of submittedAttachmentIds) composerAttachments.remove(id);
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to send message');
    } finally {
      sendInFlightRef.current = false;
    }
  };

  const handleApprovalDecision = (approval: ChatSessionViewApproval, approved: boolean) => {
    trackResolution(
      approval.id,
      intents
        .resolveInterrupt({
          interruptId: approval.id,
          decision: approved ? 'approve' : 'deny',
        })
        .then((result) => {
          if (!isNaradIntentAccepted(result.status)) {
            setError(formatNaradIntentFailure(result));
          }
        })
        .catch((err: unknown) =>
          setError(err instanceof Error ? err.message : 'Failed to submit approval')
        ),
    );
  };

  const handleStopRun = (approval: ChatSessionViewApproval) => {
    trackResolution(
      approval.id,
      intents
        .resolveInterrupt({
          interruptId: approval.id,
          decision: 'stop_run',
          reason: 'stopped',
        })
        .then((result) => {
          if (!isNaradIntentAccepted(result.status)) {
            setError(formatNaradIntentFailure(result));
          }
        })
        .catch((err: unknown) =>
          setError(err instanceof Error ? err.message : 'Failed to stop run')
        ),
    );
  };

  const handleStopActiveRun = async () => {
    const activeRunId = runSummary.activeRootRunId;
    if (!activeRunId || stoppingRunId !== null) {
      return;
    }

    setStoppingRunId(activeRunId);
    try {
      const result = await intents.stopActiveRoot({ runId: activeRunId });
      if (result.status === 'accepted' || result.status === 'already_applied') {
        intents.abortLocalDelivery();
      } else {
        setError(result.detail ?? 'Failed to stop run');
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to stop run');
    } finally {
      setStoppingRunId((current) => (current === activeRunId ? null : current));
    }
  };

  const latestTurnId = turns.length > 0 ? turns[turns.length - 1]?.id ?? null : null;
  const dockApprovals = openInterrupts.filter(
    (approval: ChatSessionViewApproval) => !approval.decision || approval.decision === 'pending',
  );
  const awaitingUserApproval = dockApprovals.length > 0;
  const liveActive = runSummary.isRunning || localDeliveryPending;
  // Banner tracks the attach coordinator only — not sticky reducer sessionGap (that caused flicker).
  const resyncBannerVisible =
    attachSnapshot.backfilling ||
    attachSnapshot.resyncRequired ||
    connectionStatus === 'error' ||
    connectionStatus === 'unauthorized' ||
    (connectionStatus === 'attaching' && !attachSnapshot.live);
  const resyncBannerMessage = attachSnapshot.backfilling || attachSnapshot.resyncRequired
    ? 'Syncing session history…'
    : connectionStatus === 'error'
      ? 'Live connection lost. Reconnecting…'
      : connectionStatus === 'unauthorized'
        ? 'Session access denied.'
        : 'Connecting to live session…';
  const runActive = awaitingUserApproval || liveActive;
  const runStatusLabel = awaitingUserApproval
    ? 'Waiting for approval'
    : runSummary.isRunning || localDeliveryPending
      ? 'Working'
      : 'Ready';
  const executionGraphPresentation = classifyExecutionGraphPresentation(graphView.graph);

  return (
    <>
      <div className="workspace-grid-fill relative w-full grid-cols-1">
        <section className="flex min-h-0 flex-col">
          <div className="sticky top-0 z-10 mb-4 flex items-center justify-end border-b border-border/60 bg-background/95 py-3 backdrop-blur">
            <div className="ml-auto flex items-center gap-3 text-xs text-muted-foreground">
              <span
                className={cn(
                  'inline-flex items-center gap-1 font-semibold',
                  runActive ? 'text-primary' : 'text-muted-foreground'
                )}
              >
                {runActive ? (
                  <LoaderCircle className="h-3.5 w-3.5 animate-spin" />
                ) : (
                  <span className="h-2 w-2 rounded-full bg-muted-foreground" />
                )}
                {runStatusLabel}
              </span>
            </div>
          </div>

          <div className="min-h-0 flex-1 space-y-8 overflow-y-auto px-2 pb-48">
            {resyncBannerVisible ? (
              <Alert variant="attention" data-testid="session-resync-banner">
                {resyncBannerMessage}
              </Alert>
            ) : null}
            {executionGraphPresentation !== 'hidden' ? (
              <ExecutionGraphPanel
                graph={graphView.graph}
                presentation={executionGraphPresentation}
                resyncNeeded={graphView.resyncRequired}
                intents={{
                  retryTarget: intents.retryTarget,
                  cancelGraphNode: intents.cancelGraphNode,
                }}
                disabled={composerLocked}
                onCommandError={(message) => setError(message)}
              />
            ) : null}
            {turns.map((turn) => (
              <TurnCard
                key={turn.id}
                turn={turn}
                agentId={selectedAgent.id}
                agentName={selectedAgent.name}
                agentIcon={selectedAgent.persona?.icon ?? '🤖'}
                agentColor={selectedAgent.persona?.color ?? '#0F766E'}
                sessionId={sessionId}
                assetsById={assetsById}
                isLatestActive={runActive && turn.id === latestTurnId}
                runStatusLabel={runActive && turn.id === latestTurnId ? runStatusLabel : null}
                liveText={turn.id === latestTurnId ? liveText : ''}
                onOpenTrace={handleOpenTrace}
              />
            ))}
            {runActive && !latestTurnId && !liveText.trim() ? (
              <PendingAssistantTurn liveText={liveText} runStatusLabel={runStatusLabel} agentName={selectedAgent.name} agentIcon={selectedAgent.persona?.icon ?? '🤖'} agentColor={selectedAgent.persona?.color ?? '#0F766E'} />
            ) : null}
            {turns.length === 0 && !runActive ? (
              <div className="rounded-3xl border border-dashed border-border px-6 py-10 text-center text-muted-foreground">
                <p className="font-medium text-foreground">
                  Ask the agent to work, then review approvals and activity as a task timeline.
                </p>
                <p className="mt-2">Send a message below to get started.</p>
              </div>
            ) : null}
          </div>

          <div className="sticky bottom-0 z-30 border-t border-border/60 bg-background/95 px-2 py-4 backdrop-blur">
            {dockApprovals.length > 0 ? (
              <div className="mb-3">
                <ApprovalDock
                  approvals={dockApprovals}
                  resolvingRequestIds={resolvingRequestIds}
                  onDecision={handleApprovalDecision}
                  onStopRun={(approval) => handleStopRun(approval)}
                />
              </div>
            ) : null}
            <SessionQueue agentId={selectedAgent.id} sessionId={sessionId} />
            <MessageAttachmentComposer
              items={composerAttachments.items}
              disabled={composerLocked}
              onAddFiles={composerAttachments.addFiles}
              onRemove={composerAttachments.remove}
            >
              <textarea
                value={draft}
                onChange={(event) => setDraft(event.target.value)}
                onKeyDown={(event) => {
                  if (event.key === 'Enter' && !event.shiftKey) {
                    event.preventDefault();
                    void handleSend();
                  }
                }}
                disabled={composerLocked}
                placeholder={`Message ${selectedAgent.name}...`}
                className="max-h-40 min-h-12 flex-1 resize-none bg-transparent px-2 py-2 text-sm outline-none disabled:opacity-60"
              />
              {runSummary.isRunning || localDeliveryPending ? (
                <Button
                  variant="destructive"
                  size="icon"
                  disabled={stoppingRunId !== null}
                  onClick={() => void handleStopActiveRun()}
                >
                  {stoppingRunId !== null ? (
                    <LoaderCircle className="h-4 w-4 animate-spin" />
                  ) : (
                    <Square className="h-4 w-4" />
                  )}
                </Button>
              ) : null}
                <Button
                  size="icon"
                  disabled={
                    composerLocked
                    || composerAttachments.hasUploading
                    || (!draft.trim() && composerAttachments.readyAssets.length === 0)
                  }
                  onClick={() => void handleSend()}
                >
                  <ArrowUp className="h-4 w-4" />
                </Button>
            </MessageAttachmentComposer>
          </div>
        </section>
      </div>
      <Dialog open={isFilesOpen} onOpenChange={setIsFilesOpen}>
        <DialogContent className="flex h-[85vh] w-[95vw] max-w-5xl flex-col overflow-hidden p-0 [&>button]:hidden">
          <DialogTitle className="sr-only">Files explorer</DialogTitle>
          <AgentFilesPanel
            agent={{
              id: selectedAgent.id,
              name: selectedAgent.name,
              sandboxEnabled: selectedAgent.sandbox?.enabled === true,
            }}
            conversationAssets={sessionAssetsQuery.data ?? []}
            onClose={() => setIsFilesOpen(false)}
            className="h-full min-h-0 rounded-none border-0 shadow-none"
          />
        </DialogContent>
      </Dialog>
      <RunTraceInspector
        open={traceOpen}
        loading={traceLoading}
        error={traceError}
        trace={traceView}
        onClose={() => setTraceOpen(false)}
      />
    </>
  );
};

export const AgentPageV2: React.FC = () => {
  const navigate = useNavigate();
  const params = useParams();
  const [searchParams, setSearchParams] = useSearchParams();
  const requestedSessionId = searchParams.get('sessionId')?.trim() || null;
  const { data: currentUser } = useCurrentUser();
  const { data: agents = [], isLoading: agentsLoading } = useAgents();
  const routeAgentId = params.agentId?.trim() || null;
  const [selectedAgentId, setSelectedAgentId] = React.useState<string | null>(routeAgentId);
  const resolvedAgentId = routeAgentId ?? selectedAgentId;
  const selectedAgent =
    agents.find((agent) => agent.id === resolvedAgentId) ??
    (!resolvedAgentId ? agents[0] ?? null : null);
  const [sessions, setSessions] = React.useState<Session[]>([]);
  const [sessionId, setSessionId] = React.useState<string | null>(requestedSessionId);
  const [error, setError] = React.useState<string | null>(null);
  const [isAgentSettingsOpen, setIsAgentSettingsOpen] = React.useState(false);
  const [isFilesOpen, setIsFilesOpen] = React.useState(false);
  const [createAgentOpen, setCreateAgentOpen] = React.useState(false);
  const newSessionDraftRef = React.useRef(false);
  const loadedAgentIdRef = React.useRef<string | null>(null);
  const dialogAgentIdRef = React.useRef<string | null>(null);
  const dialogSessionIdRef = React.useRef<string | null>(null);

  // URL is the source of truth. /workspace without :agentId must canonicalize once;
  // an agents-list refetch (window focus) must not reset the dropdown to agents[0].
  React.useEffect(() => {
    if (routeAgentId) {
      setSelectedAgentId(routeAgentId);
      return;
    }
    if (agentsLoading || agents.length === 0) {
      return;
    }
    const fallbackId =
      selectedAgentId && agents.some((agent) => agent.id === selectedAgentId)
        ? selectedAgentId
        : agents[0]!.id;
    setSelectedAgentId(fallbackId);
    const search = searchParams.toString();
    navigate(
      {
        pathname: `/workspace/${encodeURIComponent(fallbackId)}`,
        search: search ? `?${search}` : '',
      },
      { replace: true },
    );
  }, [agents, agentsLoading, navigate, routeAgentId, searchParams, selectedAgentId]);

  React.useEffect(() => {
    if (!requestedSessionId || newSessionDraftRef.current) {
      return;
    }
    newSessionDraftRef.current = false;
    setSessionId(requestedSessionId);
  }, [requestedSessionId]);

  const selectSession = React.useCallback((next: React.SetStateAction<string | null>) => {
    setSessionId((current) => {
      const resolved = typeof next === 'function' ? next(current) : next;
      newSessionDraftRef.current = resolved === null;
      return resolved;
    });
  }, []);

  const refreshSessions = React.useCallback(async (agentId: string) => {
    const nextSessions = await listSessionsForAgent(agentId);
    setSessions(nextSessions);
    setSessionId((current) => {
      if (newSessionDraftRef.current) {
        return null;
      }
      if (requestedSessionId && nextSessions.some((session) => session.id === requestedSessionId)) {
        return requestedSessionId;
      }
      if (current && nextSessions.some((session) => session.id === current)) {
        return current;
      }
      return nextSessions[0]?.id ?? null;
    });
  }, [requestedSessionId]);

  React.useEffect(() => {
    if (!sessionId) {
      if (searchParams.has('sessionId')) {
        const next = new URLSearchParams(searchParams);
        next.delete('sessionId');
        setSearchParams(next, { replace: true });
      }
      return;
    }

    if (searchParams.get('sessionId') === sessionId) {
      return;
    }

    const next = new URLSearchParams(searchParams);
    next.set('sessionId', sessionId);
    setSearchParams(next, { replace: true });
  }, [searchParams, sessionId, setSearchParams]);

  React.useEffect(() => {
    if (!selectedAgent?.id) return;

    const agentId = selectedAgent.id;
    if (loadedAgentIdRef.current !== null && loadedAgentIdRef.current !== agentId) {
      // Agent identity changed (dropdown, back/forward, or soft URL nav). Drop any
      // "New session" draft belonging to the previous agent and refuse a foreign sessionId.
      newSessionDraftRef.current = false;
      setSessionId(null);
      setSessions([]);
    }
    loadedAgentIdRef.current = agentId;

    void refreshSessions(agentId).catch((err: unknown) =>
      setError(err instanceof Error ? err.message : 'Failed to load sessions')
    );
  }, [refreshSessions, selectedAgent?.id]);

  const handleSelectAgent = (agentId: string) => {
    if (!agentId || agentId === selectedAgentId) {
      return;
    }
    setSelectedAgentId(agentId);
    navigate({ pathname: `/workspace/${encodeURIComponent(agentId)}`, search: '' }, { replace: true });
  };

  const handleNewSession = React.useCallback(async () => {
    if (!selectedAgent) return;
    const session = await createSession('agent', selectedAgent.id);
    newSessionDraftRef.current = false;
    setSessions((current) => [session, ...current.filter((candidate) => candidate.id !== session.id)]);
    setSessionId(session.id);
  }, [selectedAgent]);

  const selectedAgentCanConfigure = (agent: AgentSummary) =>
    currentUser != null &&
    (agent.scope === 'system'
      ? currentUser.role === ROLE_ADMIN
      : currentUser.id === agent.ownerUserId);
  const renderSession = (session: Session) => {
    const presentation = sessionTitleParts(session.title);
    const isSelected = sessionId === session.id;
    return (
      <button
        key={session.id}
        type="button"
        title={session.title}
        aria-current={isSelected ? 'page' : undefined}
        onClick={() => selectSession(session.id)}
        className={cn(
          'w-full rounded-md px-2.5 py-2 text-left text-sm transition-colors',
          isSelected ? 'bg-primary/10 font-medium text-primary' : 'text-foreground hover:bg-muted'
        )}
      >
        <span className="flex min-w-0 items-center justify-between gap-2">
          <span className="min-w-0 truncate">{presentation.title}</span>
          <span className="shrink-0 rounded bg-muted px-1.5 py-0.5 text-[10px] font-medium text-muted-foreground">
            {presentation.kind}
          </span>
        </span>
        <span className="mt-0.5 block text-xs font-normal text-muted-foreground">
          {formatSessionTimestamp(session.updatedAt)}
        </span>
      </button>
    );
  };

  React.useEffect(() => {
    const nextAgentId = selectedAgent?.id ?? null;
    if (dialogAgentIdRef.current !== null && dialogAgentIdRef.current !== nextAgentId) {
      setIsAgentSettingsOpen(false);
      setIsFilesOpen(false);
    }
    dialogAgentIdRef.current = nextAgentId;
  }, [selectedAgent?.id]);

  React.useEffect(() => {
    if (dialogSessionIdRef.current !== null && dialogSessionIdRef.current !== sessionId) {
      setIsFilesOpen(false);
    }
    dialogSessionIdRef.current = sessionId;
  }, [sessionId]);

  if (agentsLoading) {
    return <PageState message="Loading agents..." />;
  }

  return (
    <div className="page-shell-fill max-w-none px-0 sm:px-2">
      <div className="workspace-grid-fill grid-rows-[auto_minmax(0,1fr)] grid-cols-1 gap-3 lg:grid-rows-1 lg:grid-cols-[21rem_minmax(0,1fr)]">
        <WorkspaceSidebar
          title="Agents"
          ariaLabel="Agents and sessions"
          addLabel="New Agent"
          selectedId={selectedAgent?.id}
          onAdd={() => setCreateAgentOpen(true)}
          onSelect={handleSelectAgent}
          items={agents.map((agent: AgentSummary) => ({
            id: agent.id,
            name: agent.name,
            color: agent.persona?.color ?? '#0F766E',
            icon: agent.persona?.icon ?? '🤖',
            actions: selectedAgent?.id === agent.id ? (
              <>
                <Button type="button" variant="ghost" size="icon" title="Files" aria-label="Files" disabled={!sessionId} onClick={() => setIsFilesOpen(true)}>
                  <Folder className="h-4 w-4" />
                </Button>
                {selectedAgentCanConfigure(agent) ? (
                  <Button type="button" variant="ghost" size="icon" title="Configure agent" aria-label="Configure agent" onClick={() => setIsAgentSettingsOpen(true)}>
                    <Settings className="h-4 w-4" />
                  </Button>
                ) : null}
                <HitlWorkspaceSummary agentId={agent.id} compact onConfigure={selectedAgentCanConfigure(agent) ? () => setIsAgentSettingsOpen(true) : undefined} />
              </>
            ) : null,
          }))}
          renderChildren={() => (
            <>
              <div className="flex items-center justify-between gap-2 px-1 py-1">
                <span className="text-xs font-semibold text-muted-foreground">Sessions</span>
                <Button type="button" variant="ghost" size="sm" className="h-7 px-2 text-xs" aria-label="New session" title="New session" onClick={() => void handleNewSession()}>
                  <Plus className="mr-1 h-3.5 w-3.5" />
                  New session
                </Button>
              </div>
              {[...sessions].sort((left, right) => Date.parse(right.updatedAt) - Date.parse(left.updatedAt)).map(renderSession)}
              {sessions.length === 0 ? <p className="px-2.5 py-2 text-xs text-muted-foreground">No sessions yet.</p> : null}
            </>
          )}
        />

        <main className="flex min-h-0 min-w-0 flex-col">
          <div className="mb-3 flex shrink-0 items-center gap-3 border-b border-border/60 pb-3">
            <span
              className="flex h-10 w-10 shrink-0 items-center justify-center rounded-full text-xl"
              style={{ backgroundColor: `${selectedAgent?.persona?.color ?? '#0F766E'}20` }}
              aria-hidden="true"
            >
              {selectedAgent?.persona?.icon ?? '🤖'}
            </span>
            <h1 title={selectedAgent?.name} className="truncate text-2xl font-bold leading-tight text-foreground">
              {selectedAgent?.name ?? 'Select Agent'}
            </h1>
          </div>
          {error ? (
            <Alert variant="destructive" className="mb-3 shrink-0">
              {error}
            </Alert>
          ) : null}
          {selectedAgent && sessionId ? (
            <KautukaNaradProvider agentId={selectedAgent.id} sessionId={sessionId}>
              <AgentWorkspace
                key={sessionId}
                selectedAgent={selectedAgent}
                sessionId={sessionId}
                setError={setError}
                isFilesOpen={isFilesOpen}
                setIsFilesOpen={setIsFilesOpen}
              />
            </KautukaNaradProvider>
          ) : (
            <div className="flex min-h-0 flex-1 items-center justify-center rounded-2xl border border-dashed border-border px-6 py-10 text-center text-muted-foreground">
              <div className="max-w-lg space-y-2">
                <p className="font-medium text-foreground">
                  Ask the agent to work, then review approvals and activity as a task timeline.
                </p>
                <p>
                  {selectedAgent
                    ? sessions.length > 0
                      ? 'Choose a session from the sidebar, or create a new one to begin.'
                      : 'Create a new session from the sidebar to begin.'
                    : 'Select an agent to begin.'}
                </p>
              </div>
            </div>
          )}
        </main>
      </div>
      {currentUser && selectedAgent ? (
        <AgentConfigurationDialog
          open={isAgentSettingsOpen}
          actor={{ id: currentUser.id, role: currentUser.role }}
          mode="edit"
          agentId={selectedAgent.id}
          onSaved={(savedAgentId) => {
            setIsAgentSettingsOpen(false);
            navigate(`/workspace/${encodeURIComponent(savedAgentId)}`, { replace: true });
          }}
          onDeleted={() => {
            setIsAgentSettingsOpen(false);
            navigate('/workspace', { replace: true });
          }}
          onClose={() => setIsAgentSettingsOpen(false)}
        />
      ) : null}
      {currentUser ? (
        <AgentConfigurationDialog
          open={createAgentOpen}
          actor={{ id: currentUser.id, role: currentUser.role }}
          mode="create"
          onSaved={(savedAgentId) => {
            navigate(`/workspace/${encodeURIComponent(savedAgentId)}`, { replace: true });
          }}
          onDeleted={() => {
            navigate('/workspace', { replace: true });
          }}
          onClose={() => setCreateAgentOpen(false)}
        />
      ) : null}
    </div>
  );
};
