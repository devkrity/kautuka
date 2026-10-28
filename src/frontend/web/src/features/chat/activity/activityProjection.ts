import type { ActivityEntry } from '../types/chatTypes';

/**
 * Stable identity for one projected activity fact.
 * Approvals and tools may share a toolCallId (gate-before-start HITL) but must
 * never share an identity — they are distinct journal facts.
 */
export function activityEntryProjectionIdentity(entry: ActivityEntry): string {
  if (entry.kind === 'approval') {
    return `approval:${entry.approvalRequestId ?? entry.id}`;
  }
  if (entry.kind === 'checkpoint') {
    return `checkpoint:${entry.checkpointId ?? entry.id}`;
  }
  if (entry.kind === 'turn') {
    return `turn:${entry.id}`;
  }
  // action (tool.call.* or public progress projected as action rows)
  if (entry.toolCallId) {
    return `tool:${entry.toolCallId}`;
  }
  return `action:${entry.id}`;
}

/**
 * Selector / fold contract: one identity → one row.
 * Do not heal duplicates here — fail closed so producers are fixed, not hidden.
 */
export function assertUniqueActivityProjection(
  entries: readonly ActivityEntry[],
  context = 'activity projection',
): void {
  const seen = new Set<string>();
  for (const entry of entries) {
    const key = activityEntryProjectionIdentity(entry);
    if (seen.has(key)) {
      throw new Error(`Duplicate ${context}: ${key}`);
    }
    seen.add(key);
  }
}

/**
 * Presentation-only chronology. Uniqueness is owned by Narad fold / selectors;
 * the ledger must not collapse identities.
 */
export function orderActivityEntriesForDisplay(
  entries: readonly ActivityEntry[],
): ActivityEntry[] {
  return [...entries].sort((left, right) => {
    if (left.sequence !== right.sequence) {
      return left.sequence - right.sequence;
    }
    return left.id.localeCompare(right.id);
  });
}

/** React / list key that cannot collide when approval and tool share toolCallId. */
export function activityEntryRenderKey(entry: ActivityEntry): string {
  return `activity:${activityEntryProjectionIdentity(entry)}`;
}
