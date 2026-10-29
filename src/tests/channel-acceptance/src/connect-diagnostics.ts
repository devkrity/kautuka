import type { AcceptanceEvidence, ConnectDiagnostic } from './model.js';

/** Projects only correlation fields from Narad intents; it never retains payload content. */
export function projectConnectDiagnostic(method: string, url: string, body: unknown, timestampMs = Date.now()): ConnectDiagnostic | undefined {
  const pathname = new URL(url).pathname;
  const messageMatch = pathname.match(/^\/api\/narad\/v1\/agents\/[^/]+\/sessions\/[^/]+\/messages$/);
  const resolveMatch = pathname.match(/^\/api\/narad\/v1\/commands\/resolve-interrupt$/);
  if (method !== 'POST' || (!messageMatch && !resolveMatch)) return undefined;

  const record = objectRecord(body);
  const resumeEntries = readResumeEntries(record);
  const projectedResume = resumeEntries.length === 1
    ? resumeEntries[0]
    : resumeEntries.length > 1
      ? { interruptId: `${resumeEntries.length}-entries`, status: 'resolved' as const }
      : undefined;

  return compact({
    timestampMs,
    threadId: stringValue(record?.sessionId) ?? sessionFromPath(pathname),
    runId: stringValue(record?.runId),
    resume: projectedResume,
    endpoint: messageMatch ? 'message' as const : 'resolve-interrupt' as const,
  });
}

export function recordConnectDiagnostic(evidence: AcceptanceEvidence, diagnostic: ConnectDiagnostic): void {
  evidence.addConnectDiagnostic(diagnostic);
  if (diagnostic.threadId) evidence.addIdentifier({ kind: 'correlation', value: diagnostic.threadId });
  if (diagnostic.runId) evidence.addIdentifier({ kind: 'run', value: diagnostic.runId });
  if (diagnostic.resume?.interruptId) evidence.addIdentifier({ kind: 'wait', value: diagnostic.resume.interruptId });
}

function sessionFromPath(pathname: string): string | undefined {
  const match = pathname.match(/\/sessions\/([^/]+)\//);
  return match?.[1];
}

function readResumeEntries(record: Record<string, unknown> | undefined): Array<{ interruptId?: string; status?: string }> {
  if (!record) return [];
  const raw = record.resume ?? record.resumeEntry;
  if (Array.isArray(raw)) {
    return raw.map((entry) => objectRecord(entry)).filter((entry): entry is Record<string, unknown> => entry !== undefined)
      .map((entry) => compact({ interruptId: stringValue(entry.interruptId), status: stringValue(entry.status) }));
  }
  const single = objectRecord(raw);
  return single ? [compact({ interruptId: stringValue(single.interruptId), status: stringValue(single.status) })] : [];
}

function objectRecord(value: unknown): Record<string, unknown> | undefined {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : undefined;
}

function stringValue(value: unknown): string | undefined {
  return typeof value === 'string' ? value : undefined;
}

function compact<T extends Record<string, unknown>>(value: T): T {
  return Object.fromEntries(Object.entries(value).filter(([, item]) => item !== undefined)) as T;
}
