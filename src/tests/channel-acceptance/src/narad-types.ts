export const NARAD_BASE = '/api/narad/v1';
export const NARAD_PROTOCOL = 'narad/v1';

export const coreAuthorityTypes = new Set([
  'run.started', 'run.paused', 'run.resumed', 'run.stop.requested', 'run.finished', 'run.error', 'run.cancelled',
  'interrupt.requested', 'interrupt.resolved', 'tool.call.started', 'tool.call.finished', 'session.revision', 'run.revision',
]);
export const controlTypes = new Set(['client.attached', 'server.heartbeat', 'client.heartbeat', 'client.tool.execute', 'client.tool.finish']);
export const hydrateTypes = new Set(['messages.snapshot', 'graph.snapshot', 'graph.delta']);
export const terminalTypes = new Set(['run.finished', 'run.error', 'run.cancelled']);
export const watchTypes = new Set(['session.revision', 'run.revision', 'workflow.revision', 'node.revision', 'workspace.revision', 'session.created', 'session.updated', 'session.archived']);

export type NaradRecord = Readonly<Record<string, unknown>> & { readonly type: string; readonly protocolVersion?: string };
export type NaradSseFrame = Readonly<{ readonly event?: string; readonly id?: string; readonly data: string }>;

export function recordClass(record: NaradRecord): 'control' | 'hydrate' | 'watch' | 'authority' | 'profile' {
  if (controlTypes.has(record.type)) return 'control';
  if (hydrateTypes.has(record.type)) return 'hydrate';
  if (watchTypes.has(record.type)) return 'watch';
  if (coreAuthorityTypes.has(record.type)) return 'authority';
  return 'profile';
}

export function parseNaradPayload(raw: string): NaradRecord | undefined {
  try {
    const payload = JSON.parse(raw) as NaradRecord;
    return typeof payload.type === 'string' ? payload : undefined;
  } catch { return undefined; }
}

export function isNaradEndpoint(pathname: string): boolean {
  return pathname.startsWith(NARAD_BASE);
}
