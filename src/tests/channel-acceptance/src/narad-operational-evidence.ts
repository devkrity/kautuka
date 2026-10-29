import type { Page } from '@playwright/test';
import type { AcceptanceEvidence, LifecycleAssertion } from './model.js';
import { naradEndpointKind, parseSseRecords } from './narad-sse.js';
import { isNaradEndpoint, recordClass, terminalTypes, type NaradRecord } from './narad-types.js';

type LifecycleRevision = Readonly<{ readonly eventId: string; readonly eventType: string; readonly rootRunId: string | null; readonly sessionSequence: number | null; readonly awaitingAuthority: boolean; }>;
const lifecycleTypes = new Set(['run.paused', 'interrupt.resolved', 'run.finished', 'run.error', 'run.cancelled']);

export function projectNaradLifecycle(sse: string): readonly LifecycleRevision[] {
  return parseSseRecords(sse).flatMap((record) => projectRecord(record));
}

export function recordNaradLifecycle(evidence: AcceptanceEvidence, revisions: readonly LifecycleRevision[]): void {
  const unique = new Map(revisions.map((revision) => [revision.eventId, revision]));
  const entries = [...unique.values()];
  for (const entry of entries) {
    evidence.addIdentifier({ kind: 'event', value: entry.eventId });
    if (entry.rootRunId) evidence.addIdentifier({ kind: 'run', value: entry.rootRunId });
  }
  for (const group of groupByRun(entries)) {
    const ordered = [...group].sort((left, right) => (left.sessionSequence ?? Number.MAX_SAFE_INTEGER) - (right.sessionSequence ?? Number.MAX_SAFE_INTEGER));
    const sequences = ordered.filter((entry) => entry.sessionSequence !== null).map((entry) => entry.sessionSequence!);
    if (sequences.length === ordered.length && sequences.length > 0) {
      evidence.addCanonicalRange({ firstEventId: ordered[0]!.eventId, lastEventId: ordered.at(-1)!.eventId, firstSequence: Math.min(...sequences), lastSequence: Math.max(...sequences) });
    }
    const assertion = validateRun(ordered);
    if (assertion) evidence.addLifecycleAssertion(assertion);
  }
}

export async function observeNaradLifecycle(page: Page, evidence: AcceptanceEvidence): Promise<{ complete(): Promise<void> }> {
  let failure: unknown;
  const revisions: LifecycleRevision[] = [];
  await page.exposeBinding('__kautukaNaradLifecycle', (_source, data: unknown) => {
    try { if (typeof data === 'string') revisions.push(...projectNaradLifecycle(data)); }
    catch (error) { failure ??= error; }
  });
  await page.addInitScript(() => {
    const originalFetch = globalThis.fetch;
    globalThis.fetch = async (...args) => {
      const response = await originalFetch(...args);
      const address = typeof args[0] === 'string' ? args[0] : args[0] instanceof Request ? args[0].url : '';
      const pathname = new URL(address, globalThis.location.origin).pathname;
      if (!pathname.startsWith('/api/narad/v1/sessions/') || !/(?:stream|watch)$/.test(pathname)) return response;
      const reader = response.clone().body?.getReader();
      if (!reader) return response;
      void (async () => {
        let buffer = '';
        const decoder = new TextDecoder();
        while (true) {
          const next = await reader.read();
          if (next.done) return;
          buffer += decoder.decode(next.value, { stream: true });
          const frames = buffer.split(/\r?\n\r?\n/); buffer = frames.pop() ?? '';
          for (const frame of frames) await (globalThis as typeof globalThis & { __kautukaNaradLifecycle: (value: string) => Promise<void> }).__kautukaNaradLifecycle(frame);
        }
      })().catch(() => undefined);
      return response;
    };
  });
  return { async complete() { if (failure) throw failure; recordNaradLifecycle(evidence, revisions); } };
}

function projectRecord(record: NaradRecord): LifecycleRevision[] {
  if (recordClass(record) !== 'authority' || typeof record.eventId !== 'string' || !lifecycleTypes.has(record.type)) return [];
  return [{ eventId: record.eventId, eventType: record.type, rootRunId: typeof record.rootRunId === 'string' ? record.rootRunId : null, sessionSequence: typeof record.sessionSequence === 'number' ? record.sessionSequence : null, awaitingAuthority: record.type === 'run.paused' }];
}

function groupByRun(revisions: readonly LifecycleRevision[]): LifecycleRevision[][] {
  return [...revisions.reduce((groups, revision) => {
    if (revision.rootRunId) groups.set(revision.rootRunId, [...(groups.get(revision.rootRunId) ?? []), revision]);
    return groups;
  }, new Map<string, LifecycleRevision[]>()).values()];
}

function validateRun(revisions: readonly LifecycleRevision[]): LifecycleAssertion | undefined {
  const terminal = revisions.filter((entry) => terminalTypes.has(entry.eventType));
  if (terminal.length > 1) throw new Error(`Narad lifecycle emitted duplicate terminal results for run ${revisions[0]?.rootRunId}.`);
  if (revisions.at(-1)?.awaitingAuthority) throw new Error(`Narad lifecycle ended run ${revisions[0]?.rootRunId} with an unresolved authority wait.`);
  if (terminal.length === 1) return { runId: revisions[0]!.rootRunId!, outcome: 'terminal-once', eventIds: revisions.map((entry) => entry.eventId) };
  const resolved = revisions.filter((entry) => entry.eventType === 'interrupt.resolved');
  if (resolved.length > 0) return { runId: revisions[0]!.rootRunId!, outcome: 'wait-resolved', eventIds: resolved.map((entry) => entry.eventId) };
  return undefined;
}

export function isNaradStreamPath(pathname: string): boolean {
  return isNaradEndpoint(pathname) && naradEndpointKind(pathname) !== undefined;
}
