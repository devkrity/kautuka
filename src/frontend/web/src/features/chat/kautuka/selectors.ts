import type {
  NaradSessionSnapshot,
  InterruptSnapshot,
  ProgressSnapshot,
  RunSnapshot,
  ToolCallSnapshot,
} from '@devkrity/narad';
import type { Approval } from '@/features/approvals';
import type { ActivityEntry } from '../types/chatTypes';
import type { ChatSessionViewApproval, ChatSessionViewTurn } from '../schemas/sessionSchema';
import {
  assertUniqueActivityProjection,
} from '../activity/activityProjection';
import {
  buildExecutionGraphSnapshot,
  type ExecutionGraphSnapshot,
} from '../types/executionGraph';
import type { KautukaNaradConnectionStatus } from './KautukaNaradProvider';

export const NARAD_PROGRESS_CATEGORY_AGENT_TURN = 'agent.turn';

export type KautukaViewField = Readonly<{
  label: string;
  value: string;
}>;

export type KautukaViewBlock =
  | { readonly type: 'text'; readonly text: string }
  | {
      readonly type: 'record';
      readonly status: string | null;
      readonly fields: readonly KautukaViewField[];
      readonly body: string | null;
    }
  | {
      readonly type: 'file';
      readonly name: string;
      readonly mediaType: string;
      readonly path: string | null;
      readonly role: 'primary' | 'source';
    }
  | { readonly type: 'actions'; readonly items: readonly { readonly name: string; readonly label: string }[] };

export type KautukaOpenView = Readonly<{
  viewId: string;
  title: string;
  blocks: readonly KautukaViewBlock[];
}>;

export type KautukaTranscriptTurn = Readonly<{
  id: string;
  sequence: number;
  user: Readonly<{
    id: string;
    content: string;
    timestamp: string;
    attachmentIds: readonly string[];
  }> | null;
  assistant: Readonly<{
    id: string;
    content: string;
    timestamp: string;
    thinking: string | null;
    streaming: boolean;
  }> | null;
  rootRunId: string | null;
  activityEntries: readonly ActivityEntry[];
  views: readonly KautukaOpenView[];
  /** Token sum of completed agent.turn progress (else terminals). Elapsed is root wall clock. */
  usage?: Readonly<{
    promptTokens: number;
    completionTokens: number;
    totalTokens: number;
    elapsedMs: number | null;
  }> | null;
}>;

export type KautukaRunSummary = Readonly<{
  activeRootRunId: string | null;
  isRunning: boolean;
  nestedRuns: readonly RunSnapshot[];
}>;

export type KautukaGraphView = Readonly<{
  graph: ExecutionGraphSnapshot | null;
  resyncRequired: boolean;
}>;

export type KautukaConnectionView = Readonly<{
  lastError: string | null;
  resyncRequired: boolean;
  sessionGap: boolean;
}>;

function graphScopeKey(sessionId: string, rootRunId: string): string {
  return `run:${sessionId}:${rootRunId}`;
}

function readInterruptInput(input: unknown): Record<string, unknown> {
  return typeof input === 'object' && input !== null ? (input as Record<string, unknown>) : {};
}

function progressStatusToActivityStatus(status: ProgressSnapshot['status']): string {
  switch (status) {
    case 'active':
      return 'started';
    case 'completed':
      return 'completed';
    case 'failed':
      return 'failed';
    case 'cancelled':
      return 'aborted';
    default:
      return status;
  }
}

function toolStatusToActivityStatus(tool: ToolCallSnapshot): string {
  if (tool.open) {
    return 'started';
  }
  switch (tool.outcome) {
    case 'success':
      return 'completed';
    case 'error':
      return 'failed';
    case 'cancelled':
      return 'aborted';
    default:
      return 'completed';
  }
}

function mapProgressToActivity(progress: ProgressSnapshot, sequence: number): ActivityEntry {
  const isAgentTurn = progress.category === NARAD_PROGRESS_CATEGORY_AGENT_TURN;
  const startedAt = progress.recordedAt;
  const completedAt =
    progress.status === 'active' ? undefined : (progress.updatedAt ?? progress.recordedAt);
  const usage = progress.usage;
  const usageLabel =
    usage && ((usage.promptTokens ?? 0) > 0 || (usage.completionTokens ?? 0) > 0)
      ? ` · in ${usage.promptTokens ?? 0} / out ${usage.completionTokens ?? 0}`
      : '';
  return {
    id: progress.progressId,
    sequence,
    kind: isAgentTurn ? 'turn' : 'action',
    status: progressStatusToActivityStatus(progress.status),
    actionKind: progress.category,
    name: `${progress.label}${usageLabel}`,
    resultJson: progress.detail,
    startedAt,
    completedAt,
  };
}

function mapToolToActivity(tool: ToolCallSnapshot, sequence: number): ActivityEntry {
  return {
    id: tool.toolCallId,
    sequence,
    kind: 'action',
    status: toolStatusToActivityStatus(tool),
    actionKind: tool.toolName.startsWith('invoke_agent') ? 'agent' : 'connector',
    name: tool.toolName,
    toolCallId: tool.toolCallId,
    parentToolCallId: tool.parentToolCallId,
    argumentsJson:
      tool.args !== undefined ? JSON.stringify(tool.args) : undefined,
    resultJson:
      tool.response !== undefined ? JSON.stringify(tool.response) : undefined,
    startedAt: tool.startedAt,
    completedAt: tool.completedAt,
  };
}

function interruptDecisionToActivityStatus(interrupt: InterruptSnapshot): string {
  if (interrupt.open) {
    return 'pending';
  }
  return approvalDecisionToActivityStatus(interrupt.decision);
}

function approvalDecisionToActivityStatus(decision: string | null | undefined): string {
  const normalized = (decision ?? '').toLowerCase();
  if (!normalized || normalized === 'pending') {
    return 'pending';
  }
  if (normalized === 'approved' || normalized === 'approve' || normalized === 'auto_approved') {
    return 'approved';
  }
  if (normalized === 'expired') {
    return 'expired';
  }
  if (
    normalized === 'stopped' ||
    normalized === 'stop_run' ||
    normalized === 'cancel' ||
    normalized === 'cancelled'
  ) {
    return 'stopped';
  }
  return 'denied';
}

function approvalBelongsToRoot(
  approval: Approval,
  snapshot: NaradSessionSnapshot,
  rootRunId: string,
): boolean {
  if (approval.runId === rootRunId) {
    return true;
  }
  if (!approval.runId) {
    return false;
  }
  return snapshot.runs.get(approval.runId)?.rootRunId === rootRunId;
}

function mapGatewayApprovalToActivity(approval: Approval, sequence: number): ActivityEntry {
  const toolType =
    approval.toolType ||
    (approval.toolName.startsWith('invoke_agent') ? 'agent' : 'connector');
  return {
    id: approval.id,
    sequence,
    kind: 'approval',
    status: approvalDecisionToActivityStatus(approval.decision),
    actionKind: toolType,
    name: approval.toolName,
    toolCallId: approval.toolCallId,
    approvalRequestId: approval.id,
    argumentsJson: approval.argumentsJson,
    requestedAt: approval.requestedAt,
    decidedAt: approval.decidedAt,
    startedAt: approval.requestedAt,
    completedAt: approval.decidedAt,
    resolvedVia: approval.resolutionSource,
  };
}

function memberProtocolDecision(
  interrupt: InterruptSnapshot,
  requestId: string,
): string | undefined {
  return interrupt.members?.find((member) => member.requestId === requestId)?.decision;
}

function mapPackedMemberToActivity(
  interrupt: InterruptSnapshot,
  requestId: string,
  sequence: number,
): ActivityEntry {
  const input = readInterruptInput(interrupt.input);
  const requested = packedInputMember(input, requestId);
  const resolved = interrupt.members?.find((member) => member.requestId === requestId);
  const toolCallId =
    resolved?.toolCallId ??
    (typeof requested?.toolCallId === 'string' ? requested.toolCallId : requestId);
  const toolName =
    resolved?.toolName ??
    (typeof requested?.toolName === 'string'
      ? requested.toolName
      : interrupt.title?.trim() || interrupt.kind);
  const toolType =
    resolved?.toolType ??
    (typeof requested?.toolType === 'string'
      ? requested.toolType
      : toolName.startsWith('invoke_agent')
        ? 'agent'
        : 'connector');
  return {
    id: requestId,
    sequence,
    kind: 'approval',
    status: approvalDecisionToActivityStatus(resolved?.decision ?? interrupt.decision),
    actionKind: toolType,
    name: toolName,
    toolCallId,
    approvalRequestId: requestId,
    argumentsJson:
      typeof requested?.argumentsJson === 'string'
        ? requested.argumentsJson
        : requested?.args !== undefined
          ? JSON.stringify(requested.args)
          : undefined,
    requestedAt: interrupt.requestedAt,
    decidedAt: interrupt.resolvedAt,
    startedAt: interrupt.requestedAt,
    completedAt: interrupt.resolvedAt,
  };
}

function packedInputMember(
  input: Record<string, unknown>,
  requestId: string,
): Record<string, unknown> | undefined {
  const members = Array.isArray(input.members) ? input.members : [];
  for (const raw of members) {
    if (typeof raw !== 'object' || raw === null) {
      continue;
    }
    const member = raw as Record<string, unknown>;
    if (member.requestId === requestId) {
      return member;
    }
  }
  return undefined;
}

function expandInterruptToActivity(interrupt: InterruptSnapshot, sequence: number): ActivityEntry[] {
  if (!interrupt.open && interrupt.members && interrupt.members.length > 1) {
    return interrupt.members.map((member, index) =>
      mapPackedMemberToActivity(interrupt, member.requestId, sequence + index),
    );
  }
  return [mapInterruptToActivity(interrupt, sequence)];
}

function closedPackedMemberIds(snapshot: NaradSessionSnapshot): Set<string> {
  const ids = new Set<string>();
  for (const interrupt of snapshot.interrupts.values()) {
    if (interrupt.open || !interrupt.members || interrupt.members.length <= 1) {
      continue;
    }
    for (const member of interrupt.members) {
      ids.add(member.requestId);
    }
  }
  return ids;
}

function mapInterruptToActivity(interrupt: InterruptSnapshot, sequence: number): ActivityEntry {
  const input = readInterruptInput(interrupt.input);
  const toolCallId =
    typeof input.toolCallId === 'string'
      ? input.toolCallId
      : typeof input.requestId === 'string'
        ? input.requestId
        : undefined;
  const toolName =
    typeof input.toolName === 'string'
      ? input.toolName
      : interrupt.title?.trim() || interrupt.kind;
  const toolType =
    typeof input.toolType === 'string'
      ? input.toolType
      : toolName.startsWith('invoke_agent')
        ? 'agent'
        : interrupt.kind === 'approval'
          ? 'connector'
          : interrupt.kind;
  const argumentsJson =
    typeof input.argumentsJson === 'string'
      ? input.argumentsJson
      : input.args !== undefined
        ? JSON.stringify(input.args)
        : undefined;

  return {
    id: interrupt.interruptId,
    sequence,
    kind: 'approval',
    status: interruptDecisionToActivityStatus(interrupt),
    actionKind: toolType,
    name: toolName,
    toolCallId,
    approvalRequestId: interrupt.interruptId,
    argumentsJson,
    requestedAt: interrupt.requestedAt,
    decidedAt: interrupt.resolvedAt,
    startedAt: interrupt.requestedAt,
    completedAt: interrupt.resolvedAt,
  };
}

function activitySortKey(entry: ActivityEntry): number {
  const raw =
    entry.startedAt ?? entry.requestedAt ?? entry.completedAt ?? entry.decidedAt ?? '';
  if (raw) {
    const parsed = Date.parse(raw);
    if (Number.isFinite(parsed)) {
      return parsed;
    }
  }
  return entry.sequence;
}

function mapInterruptToApproval(interrupt: InterruptSnapshot): ChatSessionViewApproval {
  const input = readInterruptInput(interrupt.input);
  const toolCallId =
    typeof input.toolCallId === 'string'
      ? input.toolCallId
      : typeof input.requestId === 'string'
        ? input.requestId
        : interrupt.interruptId;
  const toolName =
    typeof input.toolName === 'string'
      ? input.toolName
      : interrupt.title?.trim() || interrupt.kind;
  return {
    id: interrupt.interruptId,
    toolCallId,
    toolName,
    toolType: typeof input.toolType === 'string' ? input.toolType : interrupt.kind,
    argumentsJson:
      typeof input.argumentsJson === 'string'
        ? input.argumentsJson
        : input.args !== undefined
          ? JSON.stringify(input.args)
          : null,
    requestedAt: interrupt.requestedAt ?? '',
    decision: interrupt.open ? 'pending' : normalizeInterruptDecision(interrupt.decision),
    runId: interrupt.rootRunId,
    agentId: typeof input.agentId === 'string' ? input.agentId : null,
    outerAgentName: typeof input.outerAgentName === 'string' ? input.outerAgentName : null,
    actions: interrupt.actions.map((action) => ({ label: action, value: action })),
    checkpointResumable: false,
  };
}

function normalizeInterruptDecision(decision: string | null | undefined): string {
  const normalized = (decision ?? '').toLowerCase();
  if (!normalized) {
    return 'pending';
  }
  if (normalized === 'approve' || normalized === 'approved' || normalized === 'auto_approved') {
    return 'approved';
  }
  if (normalized === 'expired') {
    return 'expired';
  }
  if (
    normalized === 'stop_run' ||
    normalized === 'stopped' ||
    normalized === 'cancel' ||
    normalized === 'cancelled'
  ) {
    return 'stopped';
  }
  return 'denied';
}

function expandInterruptToApprovals(interrupt: InterruptSnapshot): ChatSessionViewApproval[] {
  const input = readInterruptInput(interrupt.input);
  const members = Array.isArray(input.members) ? input.members : [];
  if (members.length <= 1) {
    return [mapInterruptToApproval(interrupt)];
  }
  return members.flatMap((raw) => {
    if (typeof raw !== 'object' || raw === null) {
      return [];
    }
    const member = raw as Record<string, unknown>;
    const requestId = typeof member.requestId === 'string' ? member.requestId : interrupt.interruptId;
    const toolCallId =
      typeof member.toolCallId === 'string' ? member.toolCallId : requestId;
    const toolName =
      typeof member.toolName === 'string'
        ? member.toolName
        : interrupt.title?.trim() || interrupt.kind;
    return [
      {
        id: requestId,
        toolCallId,
        toolName,
        toolType: typeof member.toolType === 'string' ? member.toolType : interrupt.kind,
        argumentsJson:
          typeof member.argumentsJson === 'string'
            ? member.argumentsJson
            : member.args !== undefined
              ? JSON.stringify(member.args)
              : null,
        requestedAt: interrupt.requestedAt ?? '',
        decision: interrupt.open
          ? 'pending'
          : normalizeInterruptDecision(memberProtocolDecision(interrupt, requestId) ?? interrupt.decision),
        runId: interrupt.rootRunId,
        agentId: typeof input.agentId === 'string' ? input.agentId : null,
        outerAgentName: typeof input.outerAgentName === 'string' ? input.outerAgentName : null,
        actions: interrupt.actions.map((action) => ({ label: action, value: action })),
        checkpointResumable: false,
      } satisfies ChatSessionViewApproval,
    ];
  });
}

function interruptDeclaresPackedMembers(interrupt: InterruptSnapshot): boolean {
  const input = readInterruptInput(interrupt.input);
  return Array.isArray(input.members) && input.members.length > 1;
}

function reasoningForMessage(
  snapshot: NaradSessionSnapshot,
  messageId: string,
): string | null {
  const chunks = snapshot.conversation.reasoning.filter(
    (entry) => entry.messageId === messageId && entry.content.trim().length > 0,
  );
  if (chunks.length === 0) {
    return null;
  }
  return chunks.map((entry) => entry.content).join('');
}

/**
 * Approval resume issues a new assistant messageId (delivery attempt). Reasoning
 * from the pre-approval attempt must stay on the turn when the final text lands
 * on the later messageId.
 */
function mergeTurnThinking(
  existing: string | null | undefined,
  next: string | null,
): string | null {
  const left = existing?.trim() ?? '';
  const right = next?.trim() ?? '';
  if (!left && !right) {
    return null;
  }
  if (!left) {
    return right;
  }
  if (!right || left.includes(right)) {
    return left;
  }
  if (right.includes(left)) {
    return right;
  }
  return `${left}\n${right}`;
}

function messageTimestamp(timestamp: string | undefined): string {
  return timestamp ?? '';
}

function activityEntriesForRootRun(
  snapshot: NaradSessionSnapshot,
  rootRunId: string | null,
  sessionApprovals: readonly Approval[] = [],
): ActivityEntry[] {
  if (!rootRunId) {
    return [];
  }
  const progressEntries = [...snapshot.progressByRun.entries()]
    .filter(([runId]) => {
      if (runId === rootRunId) {
        return true;
      }
      return snapshot.runs.get(runId)?.rootRunId === rootRunId;
    })
    .flatMap(([, map]) => [...map.values()])
    .map((entry, index) => mapProgressToActivity(entry, index + 1));
  // Pure projection of applied tool.call.* authority — no synthesized steps.
  const toolEntries = [...snapshot.tools.values()]
    .filter((tool) => {
      if (tool.rootRunId === rootRunId) {
        return true;
      }
      // Nested child tools share the same rootRunId on Kautuka wires; keep the filter honest.
      return snapshot.runs.get(tool.runId)?.rootRunId === rootRunId;
    })
    .map((tool, index) => mapToolToActivity(tool, progressEntries.length + index + 1));
  // Packed HITL journals one interrupt. After pack close, resolved.members are the
  // per-request facts. Mid-pack still overlays REST because the interrupt is open.
  const journalMemberIds = closedPackedMemberIds(snapshot);
  const restApprovals = sessionApprovals.filter(
    (approval) =>
      approvalBelongsToRoot(approval, snapshot, rootRunId) && !journalMemberIds.has(approval.id),
  );
  const restApprovalIds = new Set(restApprovals.map((approval) => approval.id));
  const restApprovalEntries = restApprovals.map((approval, index) =>
    mapGatewayApprovalToActivity(
      approval,
      progressEntries.length + toolEntries.length + index + 1,
    ),
  );
  const interruptEntries = [...snapshot.interrupts.values()]
    .filter((interrupt) => {
      if (
        restApprovalIds.has(interrupt.interruptId) &&
        !(interrupt.members && interrupt.members.length > 1 && !interrupt.open)
      ) {
        return false;
      }
      if (interrupt.rootRunId === rootRunId) {
        return true;
      }
      const runId = interrupt.runId;
      return typeof runId === 'string' && snapshot.runs.get(runId)?.rootRunId === rootRunId;
    })
    .flatMap((interrupt, index) =>
      expandInterruptToActivity(
        interrupt,
        progressEntries.length + toolEntries.length + restApprovalEntries.length + index + 1,
      ),
    );

  // Activity is authority-only: tool rows come from tool.call.*; inventing rows after
  // approve is forbidden (gate-before-start resume must publish parent tool.call.started).
  // Maps already unique per toolCallId / interruptId / progressId; assert so UI never
  // “heals” merge bugs with ledger-side collapse.
  const ordered = [...progressEntries, ...toolEntries, ...restApprovalEntries, ...interruptEntries]
    .sort((left, right) => {
      const byTime = activitySortKey(left) - activitySortKey(right);
      if (byTime !== 0) {
        return byTime;
      }
      return left.sequence - right.sequence;
    })
    .map((entry, index) => ({ ...entry, sequence: index + 1 }));
  assertUniqueActivityProjection(ordered, 'activityEntriesForRootRun');
  return ordered;
}

function rootRunWallElapsedMs(
  snapshot: NaradSessionSnapshot,
  rootRunId: string,
): number | null {
  const root = snapshot.runs.get(rootRunId);
  if (!root?.startedAt) {
    return null;
  }
  const start = Date.parse(root.startedAt);
  if (!Number.isFinite(start)) {
    return null;
  }
  if (!root.completedAt) {
    return null;
  }
  const end = Date.parse(root.completedAt);
  if (!Number.isFinite(end) || end < start) {
    return null;
  }
  return end - start;
}

function usageForRootRun(
  snapshot: NaradSessionSnapshot,
  rootRunId: string | null,
): KautukaTranscriptTurn['usage'] {
  if (!rootRunId) {
    return null;
  }
  let promptTokens = 0;
  let completionTokens = 0;
  let reportedTotal = 0;
  let elapsedMs: number | null = null;
  let hasTurnUsage = false;
  for (const progressById of snapshot.progressByRun.values()) {
    for (const progress of progressById.values()) {
      if (progress.rootRunId !== rootRunId && progress.runId !== rootRunId) {
        continue;
      }
      if (progress.category !== NARAD_PROGRESS_CATEGORY_AGENT_TURN || !progress.usage) {
        continue;
      }
      if (progress.status === 'active') {
        continue;
      }
      const usage = progress.usage;
      if ((usage.promptTokens ?? 0) <= 0 && (usage.completionTokens ?? 0) <= 0) {
        continue;
      }
      hasTurnUsage = true;
      promptTokens += usage.promptTokens ?? 0;
      completionTokens += usage.completionTokens ?? 0;
      reportedTotal += usage.totalTokens ?? 0;
    }
  }
  if (hasTurnUsage) {
    const totalTokens = reportedTotal > 0 ? reportedTotal : promptTokens + completionTokens;
    return {
      promptTokens,
      completionTokens,
      totalTokens,
      elapsedMs: rootRunWallElapsedMs(snapshot, rootRunId),
    };
  }

  let hasUsage = false;
  for (const run of snapshot.runs.values()) {
    if (run.rootRunId !== rootRunId) {
      continue;
    }
    if (!run.usage) {
      continue;
    }
    hasUsage = true;
    promptTokens += run.usage.promptTokens ?? 0;
    completionTokens += run.usage.completionTokens ?? 0;
    reportedTotal += run.usage.totalTokens ?? 0;
    if (typeof run.usage.elapsedMs === 'number') {
      elapsedMs = (elapsedMs ?? 0) + run.usage.elapsedMs;
    } else if (run.startedAt && run.completedAt) {
      const start = Date.parse(run.startedAt);
      const end = Date.parse(run.completedAt);
      if (Number.isFinite(start) && Number.isFinite(end) && end >= start) {
        elapsedMs = (elapsedMs ?? 0) + (end - start);
      }
    }
  }
  if (!hasUsage) {
    return null;
  }
  const totalTokens = reportedTotal > 0 ? reportedTotal : promptTokens + completionTokens;
  return {
    promptTokens,
    completionTokens,
    totalTokens,
    elapsedMs,
  };
}

function resolveTurnRootRunId(
  messageRootRunId: string | undefined,
  fallbackRootRunId: string | null,
): string | null {
  return messageRootRunId ?? fallbackRootRunId;
}

export function selectPartialAssistantText(snapshot: NaradSessionSnapshot): string {
  for (let index = snapshot.conversation.messages.length - 1; index >= 0; index -= 1) {
    const message = snapshot.conversation.messages[index]!;
    if (message.role === 'assistant' && message.streaming) {
      return message.content;
    }
  }
  return '';
}

function viewsForRootRun(snapshot: NaradSessionSnapshot, rootRunId: string | null): KautukaOpenView[] {
  if (!rootRunId) {
    return [];
  }
  const toolIds = new Set(
    [...snapshot.tools.values()]
      .filter((tool) => tool.rootRunId === rootRunId)
      .map((tool) => tool.toolCallId),
  );
  const views: KautukaOpenView[] = [];
  for (const view of snapshot.views.values()) {
    if (!view.open || !view.model || typeof view.model !== 'object') {
      continue;
    }
    const model = view.model as Record<string, unknown>;
    const toolCallId = typeof model.toolCallId === 'string' ? model.toolCallId : '';
    if (!toolIds.has(toolCallId)) {
      continue;
    }
    const parsed = parseViewModel(view.viewId, model);
    if (parsed) {
      views.push(parsed);
    }
  }
  return views;
}

function parseViewModel(viewId: string, model: Record<string, unknown>): KautukaOpenView | null {
  const title = typeof model.title === 'string' ? model.title : 'View';
  if (!Array.isArray(model.blocks)) {
    return { viewId, title, blocks: [] };
  }
  const blocks: KautukaViewBlock[] = [];
  for (const entry of model.blocks) {
    if (!entry || typeof entry !== 'object') {
      continue;
    }
    const block = entry as Record<string, unknown>;
    if (block.type === 'text' && typeof block.text === 'string') {
      blocks.push({ type: 'text', text: block.text });
    } else if (block.type === 'record') {
      const fields = Array.isArray(block.fields)
        ? block.fields.flatMap((item) => {
            if (!item || typeof item !== 'object') {
              return [];
            }
            const field = item as Record<string, unknown>;
            if (typeof field.label !== 'string' || typeof field.value !== 'string' || !field.value.trim()) {
              return [];
            }
            return [{ label: field.label, value: field.value }];
          })
        : [];
      const body = typeof block.body === 'string' && block.body.trim() ? block.body : null;
      const status = typeof block.status === 'string' && block.status.trim() ? block.status : null;
      if (fields.length > 0 || body) {
        blocks.push({ type: 'record', status, fields, body });
      }
    } else if (block.type === 'file' && typeof block.name === 'string') {
      blocks.push({
        type: 'file',
        name: block.name,
        mediaType: typeof block.mediaType === 'string' ? block.mediaType : 'application/octet-stream',
        path: typeof block.path === 'string' ? block.path : null,
        role: block.role === 'source' ? 'source' : 'primary',
      });
    } else if (block.type === 'actions' && Array.isArray(block.items)) {
      const items = block.items.flatMap((item) => {
        if (!item || typeof item !== 'object') {
          return [];
        }
        const action = item as Record<string, unknown>;
        if (typeof action.name !== 'string' || typeof action.label !== 'string') {
          return [];
        }
        return [{ name: action.name, label: action.label }];
      });
      if (items.length > 0) {
        blocks.push({ type: 'actions', items });
      }
    }
  }
  return { viewId, title, blocks };
}

export function selectTranscriptTurns(
  snapshot: NaradSessionSnapshot,
  sessionApprovals: readonly Approval[] = [],
): readonly KautukaTranscriptTurn[] {
  const turns: KautukaTranscriptTurn[] = [];
  let sequence = 0;
  let currentTurn: KautukaTranscriptTurn | null = null;
  let currentTurnRootRunId: string | null = null;

  const flushTurn = (): void => {
    if (currentTurn) {
      const rootRunId = currentTurnRootRunId;
      turns.push({
        ...currentTurn,
        rootRunId,
        activityEntries: activityEntriesForRootRun(snapshot, rootRunId, sessionApprovals),
        views: viewsForRootRun(snapshot, rootRunId),
        usage: usageForRootRun(snapshot, rootRunId),
      });
      currentTurn = null;
      currentTurnRootRunId = null;
    }
  };

  // A second root can start before the first finishes. Hydration or replay can
  // then place the first assistant after the second user; group by the user
  // turn's root identity rather than attaching it to whichever user came last.
  const userTurnOrder = new Map<string, number>();
  snapshot.conversation.messages.forEach((message, index) => {
    if (message.role === 'user' && message.rootRunId && !userTurnOrder.has(message.rootRunId)) {
      userTurnOrder.set(message.rootRunId, index);
    }
  });
  const orderedMessages = snapshot.conversation.messages
    .map((message, index) => ({ message, index }))
    .sort((left, right) => {
      const leftOrder = left.message.rootRunId
        ? (userTurnOrder.get(left.message.rootRunId) ?? left.index)
        : left.index;
      const rightOrder = right.message.rootRunId
        ? (userTurnOrder.get(right.message.rootRunId) ?? right.index)
        : right.index;
      return leftOrder - rightOrder || left.index - right.index;
    });
  for (const { message } of orderedMessages) {
    if (message.role === 'user') {
      flushTurn();
      sequence += 1;
      currentTurnRootRunId = resolveTurnRootRunId(message.rootRunId, null);
      currentTurn = {
        id: message.messageId,
        sequence,
        user: {
          id: message.messageId,
          content: message.content,
          timestamp: messageTimestamp(message.timestamp),
          attachmentIds: message.attachmentIds ?? [],
        },
        assistant: null,
        rootRunId: currentTurnRootRunId,
        activityEntries: [],
        views: [],
      };
      continue;
    }

    if (message.role === 'assistant') {
      if (!currentTurn) {
        sequence += 1;
        currentTurnRootRunId = resolveTurnRootRunId(message.rootRunId, currentTurnRootRunId);
        currentTurn = {
          id: message.messageId,
          sequence,
          user: null,
          assistant: null,
          rootRunId: currentTurnRootRunId,
          activityEntries: [],
          views: [],
        };
      } else if (message.rootRunId) {
        currentTurnRootRunId = message.rootRunId;
      }
      currentTurn = {
        ...currentTurn,
        rootRunId: currentTurnRootRunId,
        assistant: {
          id: message.messageId,
          content: message.content,
          timestamp: messageTimestamp(message.timestamp),
          thinking: mergeTurnThinking(
            currentTurn.assistant?.thinking,
            reasoningForMessage(snapshot, message.messageId),
          ),
          streaming: message.streaming === true,
        },
        activityEntries: activityEntriesForRootRun(snapshot, currentTurnRootRunId, sessionApprovals),
        views: viewsForRootRun(snapshot, currentTurnRootRunId),
      };
    }
  }

  flushTurn();
  return attributeMissingTurnRootRuns(snapshot, turns, sessionApprovals);
}

/**
 * When hydrate/history lacks message.rootRunId, attach each orphan root run to
 * turns in order so tooling still lands on the same turn the user saw live.
 */
function attributeMissingTurnRootRuns(
  snapshot: NaradSessionSnapshot,
  turns: KautukaTranscriptTurn[],
  sessionApprovals: readonly Approval[] = [],
): KautukaTranscriptTurn[] {
  const claimed = new Set(
    turns.map((turn) => turn.rootRunId).filter((id): id is string => Boolean(id)),
  );
  const orphanRoots = [...snapshot.runs.values()]
    .filter((run) => run.runId === run.rootRunId && !claimed.has(run.rootRunId))
    .sort((a, b) => {
      const aStart = a.startedAt ? Date.parse(a.startedAt) : 0;
      const bStart = b.startedAt ? Date.parse(b.startedAt) : 0;
      return aStart - bStart;
    });
  if (orphanRoots.length === 0) {
    return turns;
  }

  let orphanIndex = 0;
  return turns.map((turn) => {
    if (turn.rootRunId || orphanIndex >= orphanRoots.length) {
      return turn;
    }
    const rootRunId = orphanRoots[orphanIndex]!.rootRunId;
    orphanIndex += 1;
    return {
      ...turn,
      rootRunId,
      activityEntries: activityEntriesForRootRun(snapshot, rootRunId, sessionApprovals),
      views: viewsForRootRun(snapshot, rootRunId),
      usage: usageForRootRun(snapshot, rootRunId),
    };
  });
}

function activeRootSubtreeIsRunning(snapshot: NaradSessionSnapshot): boolean {
  const activeRootRunId = snapshot.activeRootRunId;
  if (!activeRootRunId) {
    return false;
  }
  return [...snapshot.runs.values()].some(
    (run) =>
      run.rootRunId === activeRootRunId &&
      (run.state === 'running' || run.state === 'paused'),
  );
}

export function selectRunSummary(snapshot: NaradSessionSnapshot): KautukaRunSummary {
  const activeRootRunId = snapshot.activeRootRunId;
  const nestedRuns = activeRootRunId
    ? [...snapshot.runs.values()].filter(
        (run) => run.rootRunId === activeRootRunId && run.runId !== activeRootRunId,
      )
    : [];
  return {
    activeRootRunId,
    isRunning: activeRootSubtreeIsRunning(snapshot),
    nestedRuns,
  };
}

export function selectOpenInterrupts(snapshot: NaradSessionSnapshot): readonly ChatSessionViewApproval[] {
  return [...snapshot.interrupts.values()]
    .filter((interrupt) => interrupt.open)
    .flatMap(expandInterruptToApprovals);
}

export function selectOpenInterruptsForActiveRoot(
  snapshot: NaradSessionSnapshot,
): readonly ChatSessionViewApproval[] {
  const activeRootRunId = snapshot.activeRootRunId;
  return [...snapshot.interrupts.values()]
    .filter(
      (interrupt) =>
        interrupt.open && (!activeRootRunId || interrupt.rootRunId === activeRootRunId),
    )
    .flatMap(expandInterruptToApprovals);
}

export function mapGatewayApprovalToChatApproval(approval: Approval): ChatSessionViewApproval {
  return {
    id: approval.id,
    toolCallId: approval.toolCallId,
    toolName: approval.toolName,
    toolType: approval.toolType,
    argumentsJson: approval.argumentsJson ?? null,
    requestedAt: approval.requestedAt,
    decision: approval.decision ?? 'pending',
    runId: approval.runId ?? null,
    agentId: approval.agentId ?? null,
    outerAgentName: approval.outerAgentName ?? null,
    actions: approval.actions.map((action) => ({ label: action.label, value: action.value })),
    checkpointResumable: approval.checkpointResumable,
  };
}

export function mergeOpenInterruptsWithPendingApprovals(
  snapshot: NaradSessionSnapshot,
  pendingApprovals: readonly Approval[],
): readonly ChatSessionViewApproval[] {
  const fromJournal = selectOpenInterrupts(snapshot);
  const packedJournal = [...snapshot.interrupts.values()].some(
    (interrupt) => interrupt.open && interruptDeclaresPackedMembers(interrupt),
  );
  const fromRest = pendingApprovals
    .filter((approval) => !approval.decision)
    .map(mapGatewayApprovalToChatApproval);
  if (packedJournal && fromJournal.length > 0) {
    const restLoaded = pendingApprovals.length > 0;
    if (!restLoaded) {
      return fromJournal;
    }
    const decidedIds = new Set(
      pendingApprovals
        .filter((approval) => Boolean(approval.decision))
        .map((approval) => approval.id),
    );
    return fromJournal.filter((card) => !decidedIds.has(card.id));
  }
  if (fromRest.length > 0) {
    return fromRest;
  }
  return fromJournal;
}

export function selectActiveRootGraph(snapshot: NaradSessionSnapshot): KautukaGraphView {
  // Only the active (running/paused) root owns session chrome. After a run finishes the
  // client clears activeRootRunId; falling back to an arbitrary graph left reload with a
  // completed “Execution progress” list that live chat never showed as the task timeline.
  const rootRunId = snapshot.activeRootRunId;
  if (!rootRunId) {
    return { graph: null, resyncRequired: snapshot.resyncRequired };
  }

  const run = snapshot.runs.get(rootRunId);
  if (run && run.state !== 'running' && run.state !== 'paused') {
    return { graph: null, resyncRequired: snapshot.resyncRequired };
  }

  const scope = snapshot.graphs.get(graphScopeKey(snapshot.sessionId, rootRunId));
  if (!scope) {
    return { graph: null, resyncRequired: snapshot.resyncRequired };
  }
  return {
    graph: buildExecutionGraphSnapshot({
      rootRunId,
      revision: scope.revision,
      rootNodeId: scope.rootNodeId ?? 'root',
      nodes: scope.nodes,
      edges: scope.edges,
    }),
    resyncRequired: scope.resyncRequired || snapshot.resyncRequired,
  };
}

export function selectProgressForActiveRoot(
  snapshot: NaradSessionSnapshot,
  sessionApprovals: readonly Approval[] = [],
): readonly ActivityEntry[] {
  return activityEntriesForRootRun(snapshot, snapshot.activeRootRunId, sessionApprovals);
}

export function selectConnectionView(snapshot: NaradSessionSnapshot): KautukaConnectionView {
  return {
    lastError: snapshot.lastError,
    resyncRequired: snapshot.resyncRequired,
    sessionGap: snapshot.sessionGap,
  };
}

export function selectComposerLocked(
  _snapshot: NaradSessionSnapshot,
  localDeliveryPending: boolean,
  connectionStatus: KautukaNaradConnectionStatus,
): boolean {
  // Queue admission is authenticated HTTP, independent of transcript/SSE recovery.
  return localDeliveryPending || connectionStatus === 'unauthorized';
}

export function shallowArrayEqual<T>(previous: readonly T[], next: readonly T[]): boolean {
  if (previous.length !== next.length) {
    return false;
  }
  for (let index = 0; index < previous.length; index += 1) {
    if (!Object.is(previous[index], next[index])) {
      return false;
    }
  }
  return true;
}
