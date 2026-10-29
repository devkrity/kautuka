import type { NaradRecord } from './narad-types.js';

export type NaradCaptureRecord = NaradRecord & {
  readonly endpointKind?: 'stream' | 'watch' | 'workspace-watch' | 'events';
};

export type NaradStreamCapture = Readonly<{
  records(): readonly NaradCaptureRecord[];
  byEndpoint(): Readonly<Record<string, readonly NaradCaptureRecord[]>>;
  markBoundary(): void;
  assertRunTerminalOrdering(): void;
  assertReconnectSequenceIntegrity(): void;
  assertProgressReplayPrivacy(): void;
  assertWatchRevisionMonotonic(): void;
}>;

export const captureFields = [
  'type',
  'protocolVersion',
  'eventId',
  'sessionId',
  'sessionSequence',
  'rootRunId',
  'runId',
  'sequence',
  'privacy',
  'progressId',
  'revision',
  'workspaceId',
  'toolCallId',
  'toolName',
  'kind',
] as const;

export function sanitizeCaptureRecord(
  payload: Record<string, unknown>,
  endpointKind?: NaradCaptureRecord['endpointKind'],
): NaradCaptureRecord {
  const sanitized: Record<string, unknown> = { type: payload.type };
  for (const field of captureFields) {
    if (field in payload) sanitized[field] = payload[field];
  }
  if (endpointKind) sanitized.endpointKind = endpointKind;
  return sanitized as NaradCaptureRecord;
}

export function endpointKindFromPathname(pathname: string): NaradCaptureRecord['endpointKind'] | undefined {
  if (/\/sessions\/[^/]+\/stream$/.test(pathname)) return 'stream';
  if (/\/sessions\/[^/]+\/watch$/.test(pathname)) return 'watch';
  if (/\/workspace\/watch$/.test(pathname)) return 'workspace-watch';
  if (/\/sessions\/[^/]+\/events$/.test(pathname)) return 'events';
  return undefined;
}
