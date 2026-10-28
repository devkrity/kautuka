import type { Page } from '@playwright/test';
import {
  assertCaptureProgressReplayPrivacy,
  assertCaptureReconnectSequenceIntegrity,
  assertCaptureRunTerminalOrdering,
  assertCaptureWatchRevisionMonotonic,
} from './narad-capture-assertions.js';
import { installNaradFetchCaptureHook } from './narad-stream-capture-hook.js';
import { naradEndpointKind } from './narad-sse.js';
import {
  endpointKindFromPathname,
  sanitizeCaptureRecord,
  type NaradCaptureRecord,
  type NaradStreamCapture,
} from './narad-stream-capture-types.js';
import { isNaradEndpoint, recordClass, type NaradRecord } from './narad-types.js';

/** Observes Narad SSE records from stream/history/watch without retaining sensitive payload content. */
export async function installNaradStreamCapture(page: Page): Promise<NaradStreamCapture> {
  const records: NaradCaptureRecord[] = [];
  const boundaries: number[] = [];
  await page.exposeBinding('__kautukaNaradRecord', (_source, record: unknown) => {
    if (record && typeof record === 'object' && typeof (record as NaradRecord).type === 'string') {
      records.push(record as NaradCaptureRecord);
    }
  });
  await installNaradFetchCaptureHook(page);
  return {
    records: () => [...records],
    byEndpoint: () => Object.fromEntries([...records.reduce((groups, record) => {
      const key = record.endpointKind ?? String(record.sessionId ?? 'control');
      groups.set(key, [...(groups.get(key) ?? []), record]);
      return groups;
    }, new Map<string, NaradCaptureRecord[]>())]),
    markBoundary() { boundaries.push(records.length); },
    assertRunTerminalOrdering() {
      assertCaptureRunTerminalOrdering(records, boundaries[boundaries.length - 1] ?? 0);
    },
    assertReconnectSequenceIntegrity() {
      assertCaptureReconnectSequenceIntegrity(records, boundaries[boundaries.length - 1] ?? 0);
    },
    assertProgressReplayPrivacy() {
      assertCaptureProgressReplayPrivacy(records, boundaries[boundaries.length - 1] ?? 0);
    },
    assertWatchRevisionMonotonic() {
      assertCaptureWatchRevisionMonotonic(records);
    },
  };
}

export function projectNaradRecordsFromResponse(pathname: string, sse: string): readonly NaradCaptureRecord[] {
  if (!isNaradEndpoint(pathname)) return [];
  return parseSseRecordsMinimal(sse, naradEndpointKind(pathname) ?? endpointKindFromPathname(pathname));
}

function parseSseRecordsMinimal(sse: string, endpointKind?: NaradCaptureRecord['endpointKind']): readonly NaradCaptureRecord[] {
  return sse.split(/\r?\n\r?\n/).flatMap((block) => {
    const data = block.split(/\r?\n/).filter((line) => line.startsWith('data:')).map((line) => line.slice(5).trim()).join('');
    if (!data) return [];
    try {
      const payload = JSON.parse(data) as Record<string, unknown>;
      if (typeof payload.type !== 'string') return [];
      return [sanitizeCaptureRecord(payload, endpointKind)];
    } catch {
      return [];
    }
  });
}

export function naradRecordSummary(records: readonly NaradRecord[]): readonly string[] {
  return records.map((record) => `${recordClass(record)}:${record.type}`);
}

export type { NaradCaptureRecord, NaradStreamCapture } from './narad-stream-capture-types.js';
