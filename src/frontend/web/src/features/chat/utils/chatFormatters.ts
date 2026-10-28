import type {
  ChatMessage,
  ChatMetrics,
  ActivityEntry,
  ExecutedAction,
  RunState,
  PendingApproval,
  LiveToolCall,
} from '../types/chatTypes';
import type { Approval } from '@/features/approvals';
import { createClientUuid } from '@/lib/utils';

export const makeMsg = (
  role: 'user' | 'assistant',
  content: string,
  compact = false
): ChatMessage => ({
  id: `${role}-${createClientUuid()}`,
  role,
  content,
  timestamp: new Date().toISOString(),
  compact,
});

export const isCompactCommand = (text: string) => text.trim().toLowerCase().startsWith('/compact');

const approvalWaitMessages = new Set([
  '[Waiting for tool approval to continue this run.]',
]);

const isApprovalWaitMessage = (content: string | null | undefined): boolean =>
  approvalWaitMessages.has((content ?? '').trim());

const isFinalActivityStatus = (status: string | null | undefined): boolean =>
  ['completed', 'approved', 'denied', 'failed', 'aborted', 'expired'].includes(status ?? '');

// Session refresh can lag behind live SSE; preserve final local evidence for matching ids only.
const mergeActivityEntriesForRefresh = (
  serverEntries: ActivityEntry[] | undefined,
  localEntries: ActivityEntry[] | undefined
): ActivityEntry[] | undefined => {
  if ((serverEntries?.length ?? 0) === 0) {
    return localEntries && localEntries.length > 0 ? localEntries : undefined;
  }

  if ((localEntries?.length ?? 0) === 0) {
    return serverEntries;
  }

  const localById = new Map(localEntries?.map((entry) => [entry.id, entry]));
  return serverEntries?.map((serverEntry) => {
    const localEntry = localById.get(serverEntry.id);
    if (!localEntry) {
      return serverEntry;
    }

    if (isFinalActivityStatus(localEntry.status) && !isFinalActivityStatus(serverEntry.status)) {
      return localEntry;
    }

    return {
      ...serverEntry,
      ...(!serverEntry.argumentsJson && localEntry.argumentsJson
        ? { argumentsJson: localEntry.argumentsJson }
        : {}),
      ...(!serverEntry.resultJson && localEntry.resultJson
        ? { resultJson: localEntry.resultJson }
        : {}),
    };
  });
};

export const parseMetrics = (value: unknown): ChatMetrics | undefined => {
  if (!value || typeof value !== 'object') return undefined;
  const record = value as Record<string, unknown>;
  const tokenIn = Number(record.tokenIn ?? 0);
  const tokenOut = Number(record.tokenOut ?? 0);
  const elapsedMs = Number(record.elapsedMs ?? 0);
  if (!Number.isFinite(tokenIn) || !Number.isFinite(tokenOut) || !Number.isFinite(elapsedMs)) {
    return undefined;
  }
  return {
    tokenIn: Math.max(0, Math.round(tokenIn)),
    tokenOut: Math.max(0, Math.round(tokenOut)),
    elapsedMs: Math.max(0, Math.round(elapsedMs)),
  };
};

export const formatTokenCount = (value: number): string => {
  if (!Number.isFinite(value)) return '0';
  const rounded = Math.max(0, Math.round(value));
  return new Intl.NumberFormat('en-US', {
    notation: 'compact',
    maximumFractionDigits: 1,
  }).format(rounded);
};

export const formatElapsed = (elapsedMs: number): string => {
  if (elapsedMs < 1_000) return `${elapsedMs}ms`;
  if (elapsedMs < 60_000) return `${(elapsedMs / 1_000).toFixed(2)}s`;
  const minutes = Math.floor(elapsedMs / 60_000);
  const seconds = Math.floor((elapsedMs % 60_000) / 1_000);
  return `${minutes}.${String(seconds).padStart(2, '0')}`;
};

export const renderMetrics = (metrics?: ChatMetrics): string => {
  if (!metrics) return '';
  return `In ${formatTokenCount(metrics.tokenIn)} · Out ${formatTokenCount(metrics.tokenOut)} · ${formatElapsed(metrics.elapsedMs)}`;
};

export const truncateText = (value: string, max = 180): string => {
  const text = value.trim().split(/\s+/).join(' ');
  if (text.length <= max) return text;
  return `${text.slice(0, max)}...`;
};

const safeText = (value: unknown): string => {
  if (typeof value === 'string') return value;
  if (typeof value === 'number' || typeof value === 'boolean') return String(value);
  return '';
};

export const parseExecutedActions = (value: unknown): ExecutedAction[] | undefined => {
  if (!Array.isArray(value) || value.length === 0) return undefined;
  const actions = value
    .filter((entry) => entry && typeof entry === 'object')
    .map((entry) => {
      const record = entry as Record<string, unknown>;
      const toolCallIds = Array.isArray(record.toolCallIds)
        ? record.toolCallIds.map((item) => safeText(item)).filter(Boolean)
        : undefined;
      return {
        id: safeText(record.id),
        kind: safeText(record.kind) || 'system',
        name: safeText(record.name),
        status: safeText(record.status) || 'started',
        toolCallIds: toolCallIds?.length ? toolCallIds : undefined,
        approvalRequestId: safeText(record.approvalRequestId) || undefined,
        continuationRunId: safeText(record.continuationRunId) || undefined,
        argumentsJson: safeText(record.argumentsJson) || undefined,
        resultJson: safeText(record.resultJson) || undefined,
        startedAt: safeText(record.startedAt) || undefined,
        completedAt: safeText(record.completedAt) || undefined,
      } satisfies ExecutedAction;
    })
    .filter((action) => action.id && action.name);

  return actions.length > 0 ? actions : undefined;
};

export const parseActivityEntries = (value: unknown): ActivityEntry[] | undefined => {
  if (!Array.isArray(value) || value.length === 0) return undefined;
  const entries = value
    .filter((entry) => entry && typeof entry === 'object')
    .map((entry) => {
      const record = entry as Record<string, unknown>;
      const toolCallIds = Array.isArray(record.toolCallIds)
        ? record.toolCallIds.map((item) => safeText(item)).filter(Boolean)
        : undefined;
      const sequence = Number(record.sequence ?? 0);
      const kind = safeText(record.kind);
      return {
        id: safeText(record.id),
        sequence: Number.isFinite(sequence) ? Math.round(sequence) : 0,
        kind: (['action', 'approval', 'checkpoint'].includes(kind)
          ? kind
          : 'action') as ActivityEntry['kind'],
        status: safeText(record.status) || 'completed',
        actionKind: safeText(record.actionKind) || undefined,
        name: safeText(record.name) || undefined,
        toolCallIds: toolCallIds?.length ? toolCallIds : undefined,
        toolCallId: safeText(record.toolCallId) || undefined,
        parentToolCallId: safeText(record.parentToolCallId) || undefined,
        approvalRequestId: safeText(record.approvalRequestId) || undefined,
        argumentsJson: safeText(record.argumentsJson) || undefined,
        resultJson: safeText(record.resultJson) || undefined,
        startedAt: safeText(record.startedAt) || undefined,
        completedAt: safeText(record.completedAt) || undefined,
        requestedAt: safeText(record.requestedAt) || undefined,
        decidedAt: safeText(record.decidedAt) || undefined,
        resolvedVia: safeText(record.resolvedVia) || undefined,
        checkpointIndex:
          typeof record.checkpointIndex === 'number' ? record.checkpointIndex : undefined,
        checkpointId: safeText(record.checkpointId) || undefined,
        checkpointBoundaryType: safeText(record.checkpointBoundaryType) || undefined,
        checkpointCreatedAt: safeText(record.checkpointCreatedAt) || undefined,
        checkpointResumable: Boolean(record.checkpointResumable),
      } satisfies ActivityEntry;
    })
    .filter((entry) => entry.id && entry.kind);

  return entries.length > 0 ? entries : undefined;
};

export const parseRunState = (value: unknown): RunState | undefined => {
  if (!value || typeof value !== 'object') return undefined;
  const record = value as Record<string, unknown>;
  const phase = safeText(record.phase);
  if (
    ![
      'executing',
      'awaiting_tool_approval',
      'completed',
      'aborted',
      'failed',
    ].includes(phase)
  ) {
    return undefined;
  }

  return {
    phase: phase as RunState['phase'],
    waitStateReason: safeText(record.waitStateReason) || undefined,
    continuationRunId: safeText(record.continuationRunId) || undefined,
  };
};

export const parseToolCalls = (value: unknown): LiveToolCall[] | undefined => {
  if (!Array.isArray(value) || value.length === 0) return undefined;

  const toolCalls = value
    .filter((entry) => entry && typeof entry === 'object')
    .map((entry) => {
      const record = entry as Record<string, unknown>;
      const toolCallId = safeText(record.toolCallId);
      const toolName = safeText(record.toolName);
      const agentId = safeText(record.agentId);
      const argumentsJson = safeText(record.argumentsJson);
      const resultJson = safeText(record.resultJson);
      const status = safeText(record.status) === 'done' ? 'done' : 'calling';
      const actionKind = safeText(record.actionKind);
      const parentToolCallId = safeText(record.parentToolCallId);
      const startedAt = safeText(record.startedAt);
      const completedAt = safeText(record.completedAt);

      return {
        toolCallId,
        toolName,
        agentId: agentId || undefined,
        parentToolCallId: parentToolCallId || undefined,
        argumentsJson: argumentsJson || undefined,
        resultJson: resultJson || undefined,
        status,
        actionKind: (['connector', 'skill', 'agent', 'superexec', 'system'].includes(actionKind)
          ? actionKind
          : 'system') as LiveToolCall['actionKind'],
        startedAt: startedAt || undefined,
        completedAt: completedAt || undefined,
      } satisfies LiveToolCall;
    })
    .filter((toolCall) => toolCall.toolCallId && toolCall.toolName);

  return toolCalls.length > 0 ? toolCalls : undefined;
};

export const preserveLocalAssistantRuntimeState = (
  incomingMessages: ChatMessage[],
  localMessages: ChatMessage[]
): ChatMessage[] => {
  if (incomingMessages.length === 0 || localMessages.length === 0) {
    return incomingMessages;
  }

  const localMessageById = new Map(localMessages.map((message) => [message.id, message]));
  const hasLocalAssistantRuntimeState = (message: ChatMessage): boolean =>
    message.role === 'assistant' &&
    ((message.toolCalls?.length ?? 0) > 0 ||
      (message.executedActions?.length ?? 0) > 0 ||
      (message.activityEntries?.length ?? 0) > 0 ||
      Boolean(message.run));
  const localAssistantMessagesWithRuntimeState = localMessages.filter(
    (message) => message.role === 'assistant' && hasLocalAssistantRuntimeState(message)
  );
  const consumedLocalMessageIds = new Set<string>();

  const buildAssistantSignature = (message: ChatMessage): string => {
    const content = message.content.trim();
    const thinking = String(message.thinking ?? '').trim();
    const compact = message.compact ? 'compact' : 'full';
    return `${message.role}|${content}|${thinking}|${compact}`;
  };

  const localAssistantMessagesBySignature = new Map<string, ChatMessage[]>();
  for (const message of localAssistantMessagesWithRuntimeState) {
    const signature = buildAssistantSignature(message);
    const existing = localAssistantMessagesBySignature.get(signature) ?? [];
    existing.push(message);
    localAssistantMessagesBySignature.set(signature, existing);
  }

  const takeLocalAssistantMatch = (
    message: ChatMessage,
    index: number
  ): ChatMessage | undefined => {
    const idMatch = localMessageById.get(message.id);
    if (idMatch?.role === 'assistant' && hasLocalAssistantRuntimeState(idMatch)) {
      consumedLocalMessageIds.add(idMatch.id);
      return idMatch;
    }

    const signatureMatches =
      localAssistantMessagesBySignature.get(buildAssistantSignature(message)) ?? [];
    const signatureMatch = signatureMatches.find(
      (candidate) => !consumedLocalMessageIds.has(candidate.id)
    );
    if (signatureMatch) {
      consumedLocalMessageIds.add(signatureMatch.id);
      return signatureMatch;
    }

    const positionalMatch = localMessages[index];
    if (
      positionalMatch?.role === message.role &&
      positionalMatch.role === 'assistant' &&
      hasLocalAssistantRuntimeState(positionalMatch)
    ) {
      consumedLocalMessageIds.add(positionalMatch.id);
      return positionalMatch;
    }

    const fallbackMatch = localAssistantMessagesWithRuntimeState.find(
      (candidate) => !consumedLocalMessageIds.has(candidate.id)
    );
    if (fallbackMatch) {
      consumedLocalMessageIds.add(fallbackMatch.id);
      return fallbackMatch;
    }

    return undefined;
  };

  return incomingMessages.map((message, index) => {
    const serverHasToolCalls = (message.toolCalls?.length ?? 0) > 0;
    const serverHasExecutedActions = (message.executedActions?.length ?? 0) > 0;

    const localMessage =
      message.role === 'assistant'
        ? takeLocalAssistantMatch(message, index)
        : localMessageById.get(message.id);

    if (!localMessage) {
      return message;
    }

    const preservedToolCalls =
      !serverHasToolCalls && (localMessage.toolCalls?.length ?? 0) > 0
        ? localMessage.toolCalls
        : serverHasToolCalls && (localMessage.toolCalls?.length ?? 0) > 0
          ? message.toolCalls?.map((serverToolCall) => {
              const localToolCall = localMessage.toolCalls?.find(
                (candidate) => candidate.toolCallId === serverToolCall.toolCallId
              );
              if (!localToolCall) {
                return serverToolCall;
              }

              return {
                ...serverToolCall,
                ...(serverToolCall.status === 'calling' && localToolCall.status === 'done'
                  ? { status: 'done' as const }
                  : {}),
                ...(!serverToolCall.argumentsJson && localToolCall.argumentsJson
                  ? { argumentsJson: localToolCall.argumentsJson }
                  : {}),
                ...(!serverToolCall.resultJson && localToolCall.resultJson
                  ? { resultJson: localToolCall.resultJson }
                  : {}),
              };
            })
          : undefined;
    const preservedExecutedActions =
      !serverHasExecutedActions && (localMessage.executedActions?.length ?? 0) > 0
        ? localMessage.executedActions
        : undefined;
    const preservedActivityEntries = mergeActivityEntriesForRefresh(
      message.activityEntries,
      localMessage.activityEntries
    );
    const preservedRun = !message.run ? localMessage.run : undefined;

    if (
      !preservedToolCalls &&
      !preservedExecutedActions &&
      !preservedActivityEntries &&
      !preservedRun
    ) {
      return message;
    }

    return {
      ...message,
      ...(preservedToolCalls ? { toolCalls: preservedToolCalls } : {}),
      ...(preservedRun ? { run: preservedRun } : {}),
      ...(preservedExecutedActions ? { executedActions: preservedExecutedActions } : {}),
      ...(preservedActivityEntries ? { activityEntries: preservedActivityEntries } : {}),
    };
  });
};

export const reconcileRefreshedMessages = (
  incomingMessages: ChatMessage[],
  localMessages: ChatMessage[]
): ChatMessage[] => {
  // Session persistence can briefly lag behind a completed stream. Never let
  // that stale snapshot roll the visible conversation back.
  if (incomingMessages.length < localMessages.length) {
    return localMessages;
  }

  const messagesWithLocalActivity = preserveLocalAssistantRuntimeState(
    incomingMessages,
    localMessages
  );

  return messagesWithLocalActivity.map((message, index) => {
    const localMessage =
      localMessages.find((candidate) => candidate.id === message.id) ?? localMessages[index];
    if (message.role !== 'assistant' || localMessage?.role !== 'assistant') {
      return message;
    }

    return {
      ...message,
      ...(!message.content.trim() && localMessage.content.trim()
        ? { content: localMessage.content }
        : {}),
      ...(!message.thinking?.trim() && localMessage.thinking?.trim()
        ? { thinking: localMessage.thinking }
        : {}),
    };
  });
};

const mergeToolCalls = (
  left: LiveToolCall[] | undefined,
  right: LiveToolCall[] | undefined
): LiveToolCall[] | undefined => {
  const merged = new Map<string, LiveToolCall>();
  for (const toolCall of [...(left ?? []), ...(right ?? [])]) {
    const existing = merged.get(toolCall.toolCallId);
    merged.set(toolCall.toolCallId, {
      ...existing,
      ...toolCall,
      status: existing?.status === 'done' || toolCall.status === 'done' ? 'done' : 'calling',
      argumentsJson: toolCall.argumentsJson ?? existing?.argumentsJson,
      resultJson: toolCall.resultJson ?? existing?.resultJson,
    });
  }
  return merged.size > 0 ? [...merged.values()] : undefined;
};

const mergeUniqueByFingerprint = <T>(
  left: T[] | undefined,
  right: T[] | undefined,
  fingerprint: (value: T) => string
): T[] | undefined => {
  const merged = new Map<string, T>();
  for (const value of [...(left ?? []), ...(right ?? [])]) {
    merged.set(fingerprint(value), value);
  }
  return merged.size > 0 ? [...merged.values()] : undefined;
};

const mergeAssistantRunMessages = (
  left: ChatMessage,
  right: ChatMessage,
  activeMessageId?: string | null
): ChatMessage => {
  const contentParts = [left.content, right.content]
    .map((content) => content.trim())
    .filter((content) => content && !isApprovalWaitMessage(content));
  const thinkingParts = [left.thinking, right.thinking]
    .map((thinking) => thinking?.trim() ?? '')
    .filter(Boolean);
  const activeMessage =
    activeMessageId && right.id === activeMessageId
      ? right
      : activeMessageId && left.id === activeMessageId
        ? left
        : null;
  const mergedRun = right.run ?? left.run;
  const mergedActivityEntries = mergeUniqueByFingerprint(
    left.activityEntries,
    right.activityEntries,
    (entry) => entry.id
  );

  return {
    ...left,
    ...(activeMessage
      ? {
          id: activeMessage.id,
          timestamp: activeMessage.timestamp,
          sequence: activeMessage.sequence ?? left.sequence,
        }
      : {}),
    content: [...new Set(contentParts)].join('\n\n'),
    thinking: [...new Set(thinkingParts)].join('\n\n') || undefined,
    compact: Boolean(left.compact || right.compact),
    traceId: right.traceId ?? left.traceId,
    metrics:
      left.metrics || right.metrics
        ? {
            tokenIn: (left.metrics?.tokenIn ?? 0) + (right.metrics?.tokenIn ?? 0),
            tokenOut: (left.metrics?.tokenOut ?? 0) + (right.metrics?.tokenOut ?? 0),
            elapsedMs: (left.metrics?.elapsedMs ?? 0) + (right.metrics?.elapsedMs ?? 0),
          }
        : undefined,
    toolCalls: mergeToolCalls(left.toolCalls, right.toolCalls),
    executedActions: mergeUniqueByFingerprint(
      left.executedActions,
      right.executedActions,
      (action) => action.id
    ),
    activityEntries: mergedActivityEntries,
    run: mergedRun,
    assets: mergeUniqueByFingerprint(left.assets, right.assets, (asset) => asset.id),
    source: right.source ?? left.source,
    internal: Boolean(left.internal && right.internal),
  };
};

export const groupAssistantRunMessages = (
  messages: ChatMessage[],
  activeMessageId?: string | null
): ChatMessage[] => {
  const grouped: ChatMessage[] = [];

  for (const message of messages) {
    const previous = grouped[grouped.length - 1];
    if (message.role === 'assistant' && previous?.role === 'assistant') {
      grouped[grouped.length - 1] = mergeAssistantRunMessages(previous, message, activeMessageId);
      continue;
    }

    grouped.push(message);
  }

  return grouped;
};

export const sortChatMessagesByBackendSequence = (messages: ChatMessage[]): ChatMessage[] => {
  if (messages.length < 2) {
    return messages;
  }

  return [...messages].sort((left, right) => {
    const leftSequence = left.sequence ?? Number.MAX_SAFE_INTEGER;
    const rightSequence = right.sequence ?? Number.MAX_SAFE_INTEGER;
    if (leftSequence !== rightSequence) {
      return leftSequence - rightSequence;
    }

    if (left.timestamp !== right.timestamp) {
      return left.timestamp.localeCompare(right.timestamp);
    }

    return left.id.localeCompare(right.id);
  });
};

const insertMessageByTimestamp = (messages: ChatMessage[], message: ChatMessage): ChatMessage[] => {
  if (message.sequence != null) {
    return sortChatMessagesByBackendSequence([...messages, message]);
  }

  const insertionIndex = messages.findIndex((candidate) => candidate.timestamp > message.timestamp);

  if (insertionIndex === -1) {
    return [...messages, message];
  }

  return [...messages.slice(0, insertionIndex), message, ...messages.slice(insertionIndex)];
};

const chatMessageFingerprint = (message: ChatMessage): string =>
  JSON.stringify({
    id: message.id,
    role: message.role,
    content: message.content,
    thinking: message.thinking ?? null,
    timestamp: message.timestamp,
    compact: Boolean(message.compact),
    metrics: message.metrics ?? null,
    run: message.run ?? null,
    executedActions: message.executedActions ?? null,
    activityEntries: message.activityEntries ?? null,
    toolCalls: message.toolCalls ?? null,
    source: message.source ?? null,
    assets: message.assets ?? null,
    traceId: message.traceId ?? null,
  });

export const areChatMessagesEquivalent = (left: ChatMessage[], right: ChatMessage[]): boolean => {
  if (left.length !== right.length) {
    return false;
  }

  return left.every(
    (message, index) => chatMessageFingerprint(message) === chatMessageFingerprint(right[index]!)
  );
};

export const approvalResolutionSource = (record: Approval): string | undefined => {
  const reason = String(record.reason ?? '').toLowerCase();
  if (reason.includes('chat-ui') || reason.includes('chat ui') || reason.includes('run-stopped')) {
    return 'chat';
  }

  const decidedBy = String(record.decidedBy ?? '').trim();
  if (decidedBy.startsWith('user-') || decidedBy.startsWith('bypass:user-')) {
    return 'approval_api';
  }

  if (decidedBy) {
    return 'channel_callback';
  }

  return undefined;
};

export const approvalStatus = (record: Approval): PendingApproval['status'] => {
  if (record.decision === 'approved') return 'approved';
  if (record.decision === 'denied') return 'denied';
  if (record.decision === 'expired') return 'expired';
  if (record.decision === 'stopped') return 'stopped';
  return 'pending';
};

export const mapPendingApproval = (record: Approval): PendingApproval => ({
  requestId: record.id,
  toolCallId: record.toolCallId,
  toolName: record.toolName,
  argumentsJson: record.argumentsJson,
  requestedAt: record.requestedAt,
  decidedAt: record.decidedAt,
  status: approvalStatus(record),
  resolvedByLabel: record.decidedByDisplayName ?? record.decidedBy ?? undefined,
  resolvedVia: record.resolutionSource ?? approvalResolutionSource(record),
  runId: record.runId ?? undefined,
  checkpointId: record.checkpointId ?? undefined,
  agentId: record.agentId ?? undefined,
  outerSessionId: record.outerSessionId ?? undefined,
  outerAgentId: record.outerAgentId ?? undefined,
  toolType: record.toolType,
  mcpServerId: record.mcpServerId ?? undefined,
  callerToolCallId: record.callerToolCallId ?? undefined,
  callerToolName: record.callerToolName ?? undefined,
  checkpointIndex: record.checkpointIndex,
  checkpointBoundaryType: record.checkpointBoundaryType ?? undefined,
  checkpointCreatedAt: record.checkpointCreatedAt ?? undefined,
  checkpointResumable: record.checkpointResumable,
});
