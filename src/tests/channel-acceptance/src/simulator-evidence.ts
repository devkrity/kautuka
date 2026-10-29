import type { SimulatorTranscriptEntry } from './model.js';

/** Retains safe simulator projections while losslessly coalescing adjacent polling repeats. */
export function compactSimulatorTranscript(value: unknown): readonly SimulatorTranscriptEntry[] {
  if (!Array.isArray(value)) return [];
  const compacted: SimulatorTranscriptEntry[] = [];
  for (const candidate of value) {
    const entry = project(candidate);
    if (!entry) continue;
    const previous = compacted.at(-1);
    if (previous && key(previous) === key(entry)) compacted[compacted.length - 1] = { ...previous, repeatCount: (previous.repeatCount ?? 1) + 1 };
    else compacted.push(entry);
  }
  return compacted;
}

function project(value: unknown): SimulatorTranscriptEntry | undefined {
  if (!value || typeof value !== 'object') return undefined;
  const entry = value as Record<string, unknown>;
  if (!string(entry.direction) || !string(entry.provider) || !string(entry.operation)) return undefined;
  const actionTokens = Array.isArray(entry.actionTokens) ? entry.actionTokens.filter(string) : undefined;
  return {
    direction: string(entry.direction), provider: string(entry.provider), operation: string(entry.operation),
    payloadSha256: string(entry.payloadSha256), payloadBytes: number(entry.payloadBytes), actionTokens,
    transportMessageId: typeof entry.transportMessageId === 'string' ? entry.transportMessageId : null, repeatCount: 1,
  };
}

function key(entry: SimulatorTranscriptEntry): string {
  return JSON.stringify([entry.direction, entry.provider, entry.operation, entry.payloadSha256, entry.transportMessageId, entry.actionTokens]);
}

function string(value: unknown): string | undefined { return typeof value === 'string' ? value : undefined; }
function number(value: unknown): number | undefined { return typeof value === 'number' && Number.isFinite(value) ? value : undefined; }
