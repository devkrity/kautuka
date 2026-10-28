export interface ChatMetrics {
  tokenIn: number;
  tokenOut: number;
  elapsedMs: number;
}

export interface ExecutedAction {
  id: string;
  kind: 'connector' | 'skill' | 'agent' | 'superexec' | 'system' | string;
  name: string;
  status: 'started' | 'completed' | 'failed' | 'aborted' | string;
  toolCallIds?: string[];
  approvalRequestId?: string;
  continuationRunId?: string;
  argumentsJson?: string;
  resultJson?: string;
  startedAt?: string;
  completedAt?: string;
}

export interface RunState {
  phase:
    | 'executing'
    | 'awaiting_tool_approval'
    | 'completed'
    | 'aborted'
    | 'failed';
  waitStateReason?: string;
  continuationRunId?: string;
}

export interface ActiveRunState {
  runId: string;
  sessionId: string;
  targetType: 'agent';
  targetId: string;
  userId: string;
  message?: string | null;
  lastSequence: number;
  startedAt: string;
  status: 'active' | 'completed' | string;
}

export interface LiveToolCall {
  toolCallId: string;
  toolName: string;
  agentId?: string;
  parentToolCallId?: string | null;
  argumentsJson?: string;
  resultJson?: string;
  status: 'calling' | 'done';
  actionKind?: 'connector' | 'skill' | 'agent' | 'superexec' | 'system';
  startedAt?: string;
  completedAt?: string;
}

export interface PendingApproval {
  requestId: string;
  toolCallId: string;
  toolName: string;
  argumentsJson?: string;
  requestedAt?: string;
  decidedAt?: string;
  status: 'pending' | 'approved' | 'denied' | 'expired' | 'stopped' | 'completed' | 'failed';
  resolvedByLabel?: string;
  resolvedVia?: string;
  runId?: string;
  checkpointId?: string;
  agentId?: string;
  outerSessionId?: string;
  outerAgentId?: string;
  toolType?: string;
  mcpServerId?: string;
  callerToolCallId?: string;
  callerToolName?: string;
  checkpointIndex?: number;
  checkpointBoundaryType?: string;
  checkpointCreatedAt?: string;
  checkpointResumable?: boolean;
  actions?: ReadonlyArray<{ label: string; value: string }>;
}

export interface ConversationAsset {
  id: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  origin: 'userUpload' | 'clipboardPaste' | 'agentOutput';
  disposition: 'inlinePreview' | 'download';
  downloadUrl: string;
  createdAt: string;
  previewUrl?: string;
}

export interface ParsedAssetStatus {
  assetId: string;
  sessionId: string;
  status: string;
  parserName: string;
  parserVersion: string;
  warning?: string | null;
  error?: string | null;
  updatedAt?: string | null;
}

export interface ComposerAsset {
  localId: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  origin: 'userUpload' | 'clipboardPaste';
  status: 'uploading' | 'ready' | 'error';
  asset?: ConversationAsset;
  previewUrl?: string;
  errorMessage?: string;
}

export interface ChatMessage {
  id: string;
  role: 'user' | 'assistant' | 'system';
  content: string;
  sequence?: number;
  thinking?: string | null;
  timestamp: string;
  compact?: boolean;
  metrics?: ChatMetrics;
  run?: RunState | null;
  executedActions?: ExecutedAction[];
  activityEntries?: ActivityEntry[];
  toolCalls?: LiveToolCall[];
  source?: string | null;
  assets?: ConversationAsset[];
  internal?: boolean;
  traceId?: string | null;
}

export interface ActivityEntry {
  id: string;
  sequence: number;
  kind: 'action' | 'approval' | 'checkpoint' | 'turn';
  status: string;
  actionKind?: string;
  name?: string;
  toolCallIds?: string[];
  toolCallId?: string;
  parentToolCallId?: string;
  approvalRequestId?: string;
  argumentsJson?: string;
  resultJson?: string;
  startedAt?: string;
  completedAt?: string;
  requestedAt?: string;
  decidedAt?: string;
  resolvedVia?: string;
  checkpointIndex?: number;
  checkpointId?: string;
  checkpointBoundaryType?: string;
  checkpointCreatedAt?: string;
  checkpointResumable?: boolean;
}

export interface SessionRecord {
  id: string;
  targetType: 'agent';
  targetId: string;
  title: string;
  createdAt: string;
  updatedAt: string;
  messages: ChatMessage[];
  compacted: boolean;
  compactSummary?: string | null;
  estimatedContextTokens?: number | null;
  lastEstimatedAt?: string | null;
  lastCompactedAt?: string | null;
  lastCompactionReason?: string | null;
  activeRun?: ActiveRunState | null;
}

export interface ChatTarget {
  type: 'agent';
  id: string;
  name: string;
  sandboxEnabled: boolean;
}
