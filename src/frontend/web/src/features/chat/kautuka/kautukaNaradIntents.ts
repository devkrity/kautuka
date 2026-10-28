import { authenticatedFetch } from '@/lib/authenticatedFetch';
import { naradCommandClient } from '@/lib/naradCommandClient';

export type NaradIntentStatus = 'accepted' | 'rejected' | 'conflict';

export type NaradIntentResponse = Readonly<{
  status: NaradIntentStatus;
  detail?: string;
  currentRevision?: number;
  currentSessionSequence?: number;
}>;

export type NaradThreadCommandStatus =
  | 'accepted'
  | 'already_applied'
  | 'not_found'
  | 'forbidden'
  | 'expired'
  | 'conflict'
  | 'unsupported'
  | 'rejected';

export type NaradThreadCommandResponse = Readonly<{
  status: NaradThreadCommandStatus;
  decision?: string;
  requestId?: string;
  resolvedCount?: number;
  detail?: string;
}>;

const NARAD_BASE = '/api/narad/v1';

function commandsPath(agentId: string, sessionId: string, command: string): string {
  return `${NARAD_BASE}/agents/${encodeURIComponent(agentId)}/sessions/${encodeURIComponent(sessionId)}/commands/${command}`;
}

function readExpectedRevision(expectedRevision: number | null | undefined): number | undefined {
  return typeof expectedRevision === 'number' ? expectedRevision : undefined;
}

async function readIntentResponse(response: Response): Promise<NaradIntentResponse> {
  const payload = (await response.json().catch(() => ({}))) as Partial<NaradIntentResponse>;
  if (!response.ok && !payload.status) {
    throw new Error(payload.detail ?? `Narad intent failed (HTTP ${response.status}).`);
  }
  return {
    status: payload.status ?? 'rejected',
    detail: payload.detail,
    currentRevision: payload.currentRevision,
    currentSessionSequence:
      typeof payload.currentSessionSequence === 'number' ? payload.currentSessionSequence : undefined,
  };
}

async function readThreadCommandResponse(response: Response): Promise<NaradThreadCommandResponse> {
  const payload = (await response.json().catch(() => ({}))) as Partial<NaradThreadCommandResponse>;
  if (!response.ok && !payload.status) {
    throw new Error(payload.detail ?? `Narad command failed (HTTP ${response.status}).`);
  }
  return {
    status: payload.status ?? 'rejected',
    decision: payload.decision,
    requestId: payload.requestId,
    resolvedCount: payload.resolvedCount,
    detail: payload.detail,
  };
}

export async function sendNaradUserMessage(
  agentId: string,
  sessionId: string,
  input: Readonly<{
    message: string;
    expectedRevision?: number | null;
    assetIds?: readonly string[];
  }>,
  signal?: AbortSignal,
): Promise<NaradIntentResponse> {
  const response = await authenticatedFetch(
    `${NARAD_BASE}/agents/${encodeURIComponent(agentId)}/sessions/${encodeURIComponent(sessionId)}/messages`,
    {
      method: 'POST',
      credentials: 'include',
      signal,
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        message: input.message,
        expectedRevision: readExpectedRevision(input.expectedRevision),
        assetIds: input.assetIds ?? [],
      }),
    },
  );
  return readIntentResponse(response);
}

export async function resolveNaradInterrupt(
  input: Readonly<{
    interruptId: string;
    decision: string;
    reason?: string;
    expectedRevision?: number | null;
  }>,
  signal?: AbortSignal,
): Promise<NaradThreadCommandResponse> {
  const result = await naradCommandClient.resolveInterrupt(
    {
      interruptId: input.interruptId,
      decision: input.decision,
      reason: input.reason,
      expectedRevision: readExpectedRevision(input.expectedRevision) ?? null,
    },
    signal,
  );
  return {
    status: result.status,
    decision: result.decision,
    requestId: result.requestId,
    resolvedCount: result.resolvedCount,
    detail: result.detail,
  };
}

export async function stopNaradActiveRoot(
  agentId: string,
  sessionId: string,
  input: Readonly<{ runId?: string; reason?: string; expectedRevision?: number | null }> = {},
  signal?: AbortSignal,
): Promise<NaradThreadCommandResponse> {
  const response = await authenticatedFetch(commandsPath(agentId, sessionId, 'stop-active-root'), {
    method: 'POST',
    credentials: 'include',
    signal,
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      ...input,
      expectedRevision: readExpectedRevision(input.expectedRevision),
    }),
  });
  return readThreadCommandResponse(response);
}

export async function retryNaradExecutionTarget(
  agentId: string,
  sessionId: string,
  input: Readonly<{ targetType: string; targetId: string; expectedRevision?: number | null }>,
  signal?: AbortSignal,
): Promise<NaradThreadCommandResponse> {
  const response = await authenticatedFetch(commandsPath(agentId, sessionId, 'retry-target'), {
    method: 'POST',
    credentials: 'include',
    signal,
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      ...input,
      expectedRevision: readExpectedRevision(input.expectedRevision),
    }),
  });
  return readThreadCommandResponse(response);
}

export async function cancelNaradGraphNode(
  agentId: string,
  sessionId: string,
  input: Readonly<{ nodeId: string; expectedRevision?: number | null }>,
  signal?: AbortSignal,
): Promise<NaradThreadCommandResponse> {
  const response = await authenticatedFetch(commandsPath(agentId, sessionId, 'cancel-graph-node'), {
    method: 'POST',
    credentials: 'include',
    signal,
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      ...input,
      expectedRevision: readExpectedRevision(input.expectedRevision),
    }),
  });
  return readThreadCommandResponse(response);
}
