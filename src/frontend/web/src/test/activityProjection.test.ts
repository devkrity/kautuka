import { describe, expect, it } from 'vitest';
import {
  activityEntryProjectionIdentity,
  activityEntryRenderKey,
  assertUniqueActivityProjection,
  orderActivityEntriesForDisplay,
} from '@/features/chat/activity/activityProjection';
import type { ActivityEntry } from '@/features/chat/types/chatTypes';

function entry(partial: ActivityEntry): ActivityEntry {
  return partial;
}

describe('activityProjection', () => {
  it('treats approval and tool with the same toolCallId as distinct identities', () => {
    const approval = entry({
      id: 'apr-1',
      sequence: 1,
      kind: 'approval',
      status: 'approved',
      toolCallId: 'call-1',
      approvalRequestId: 'apr-1',
    });
    const tool = entry({
      id: 'call-1',
      sequence: 2,
      kind: 'action',
      status: 'started',
      toolCallId: 'call-1',
    });

    expect(activityEntryProjectionIdentity(approval)).toBe('approval:apr-1');
    expect(activityEntryProjectionIdentity(tool)).toBe('tool:call-1');
    expect(activityEntryRenderKey(approval)).not.toBe(activityEntryRenderKey(tool));
    expect(() => assertUniqueActivityProjection([approval, tool])).not.toThrow();
  });

  it('fails closed on true same-kind duplicates instead of collapsing', () => {
    const once = entry({
      id: 'call-1',
      sequence: 1,
      kind: 'action',
      status: 'started',
      toolCallId: 'call-1',
    });
    const again = entry({
      id: 'call-1-dup',
      sequence: 2,
      kind: 'action',
      status: 'completed',
      toolCallId: 'call-1',
    });

    expect(() => assertUniqueActivityProjection([once, again])).toThrow(/Duplicate.*tool:call-1/);
  });

  it('orders for display by sequence only (no parent-tree reshape, no collapse)', () => {
    const child = entry({
      id: 'child',
      sequence: 1,
      kind: 'action',
      status: 'started',
      toolCallId: 'child-1',
      parentToolCallId: 'parent-1',
      name: 'child',
    });
    const parent = entry({
      id: 'parent',
      sequence: 2,
      kind: 'action',
      status: 'started',
      toolCallId: 'parent-1',
      name: 'parent',
    });
    const approval = entry({
      id: 'apr',
      sequence: 3,
      kind: 'approval',
      status: 'approved',
      toolCallId: 'parent-1',
      approvalRequestId: 'apr',
    });

    // Child may start reporting before parent sequence assignment is rebased; ledger trusts
    // sequence numbers from the fold, never re-parents by parentToolCallId DFS.
    const ordered = orderActivityEntriesForDisplay([approval, parent, child]);
    expect(ordered.map((e) => e.id)).toEqual(['child', 'parent', 'apr']);
    expect(ordered).toHaveLength(3);
  });
});
