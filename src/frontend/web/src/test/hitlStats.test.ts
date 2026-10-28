import {
  buildHitlStatsRequest,
  formatHitlCounts,
  HITL_RUBBER_STAMP_MIN,
  rubberStampLabel,
  findHitlStat,
  describeHitlStatsError,
  describeHitlStatsWindow,
  groupHitlStatsByRevision,
  visibleHitlStatsGroups,
} from '@/features/approvals';
import type { HitlApprovalStatItem } from '@/features/approvals';
import { describe, expect, it } from 'vitest';

function stat(partial: Partial<HitlApprovalStatItem>): HitlApprovalStatItem {
  return {
    toolName: 'fetch',
    toolType: 'connector',
    approved: 0,
    denied: 0,
    agentCount: 1,
    ...partial,
  };
}

describe('HITL approval insight copy', () => {
  it('formats counts without recommending a policy change', () => {
    expect(formatHitlCounts(stat({ approved: 12, denied: 0 }))).toBe('12 approved · 0 denied');
    expect(rubberStampLabel(stat({ approved: HITL_RUBBER_STAMP_MIN, denied: 0 }))).toBe(
      'Approved every time (n=10)'
    );
    expect(rubberStampLabel(stat({ approved: HITL_RUBBER_STAMP_MIN - 1, denied: 0 }))).toBeNull();
    expect(rubberStampLabel(stat({ approved: 20, denied: 1 }))).toBeNull();
  });

  it('matches skill stats by skillId rather than platform tool name', () => {
    const items = [
      stat({
        toolName: 'repo-inspector',
        toolType: 'skill',
        skillId: 'repo-inspector',
        approved: 4,
        denied: 1,
      }),
    ];
    expect(findHitlStat(items, { skillId: 'repo-inspector' })?.denied).toBe(1);
    expect(findHitlStat(items, { toolName: 'run_skill_script' })).toBeUndefined();
  });

  it('routes stats to the owning resource path', () => {
    expect(buildHitlStatsRequest({ agentId: 'agent-1' }).path).toBe('/agents/agent-1/stats');
    expect(buildHitlStatsRequest({ agentId: 'agent-1', skillId: 'roll-dice' })).toEqual({
      path: '/agents/agent-1/stats',
      params: { skillId: 'roll-dice' },
    });
    expect(buildHitlStatsRequest({ mcpServerId: 'github' }).path).toBe('/connectors/github/stats');
    expect(buildHitlStatsRequest({ skillId: 'roll-dice' }).path).toBe('/skills/roll-dice/stats');
    expect(buildHitlStatsRequest({ agentId: 'agent-1', days: 'all' }).params).toEqual({
      days: 'all',
    });
    expect(
      buildHitlStatsRequest({
        agentId: 'agent-1',
        toolName: 'fetch',
        agentDefinitionRevision: 3,
      }).params
    ).toEqual({ toolName: 'fetch', agentDefinitionRevision: 3 });
  });

  it('groups by config version with ungrouped last', () => {
    const groups = groupHitlStatsByRevision([
      stat({ toolName: 'a', agentDefinitionRevision: 2 }),
      stat({ toolName: 'b' }),
      stat({ toolName: 'c', agentDefinitionRevision: 5 }),
    ]);
    expect(groups.map((group) => group.revision)).toEqual([5, 2, null]);
    expect(describeHitlStatsWindow('all')).toBe('all time');
    expect(describeHitlStatsWindow(7)).toBe('last 7 days');
  });

  it('keeps ungrouped rows after config versions appear', () => {
    const flat = visibleHitlStatsGroups([stat({ toolName: 'a' }), stat({ toolName: 'b' })]);
    expect(flat.hasVersionedData).toBe(false);
    expect(flat.groups).toHaveLength(1);
    expect(flat.groups[0]?.label).toBe('');

    const versioned = visibleHitlStatsGroups([
      stat({ toolName: 'a', agentDefinitionRevision: 2 }),
      stat({ toolName: 'b' }),
      stat({ toolName: 'c', agentDefinitionRevision: 5 }),
    ]);
    expect(versioned.hasVersionedData).toBe(true);
    expect(versioned.groups.map((group) => group.revision)).toEqual([5, 2, null]);
  });

  it('rolls connector stats up when only mcpServerId is provided', () => {
    const items = [
      stat({ toolName: 'fetch', mcpServerId: 'github', approved: 4, denied: 1, agentCount: 2 }),
      stat({ toolName: 'search', mcpServerId: 'github', approved: 3, denied: 0, agentCount: 1 }),
      stat({ toolName: 'other', mcpServerId: 'slack', approved: 9, denied: 9, agentCount: 5 }),
    ];
    expect(findHitlStat(items, { mcpServerId: 'github' })).toEqual({
      toolName: 'github',
      toolType: 'connector',
      approved: 7,
      denied: 1,
      agentCount: 2,
      mcpServerId: 'github',
      lastDeniedAt: undefined,
    });
    expect(
      findHitlStat(
        [stat({ toolName: 'kautuka_web_fetch', approved: 68, denied: 0, mcpServerId: undefined })],
        { mcpServerId: 'kautuka-web-tools' }
      )?.approved
    ).toBe(68);
    expect(findHitlStat(items, { mcpServerId: 'github', toolName: 'fetch' })?.approved).toBe(4);
  });

  it('describes HTTP failures without claiming the feature is absent', () => {
    expect(
      describeHitlStatsError({
        response: { status: 404, data: { error: 'Agent not found' } },
      })
    ).toBe('Could not load approval stats (404): Agent not found');
    expect(describeHitlStatsError(new Error('Expected object, received string'))).toContain(
      'Expected object, received string'
    );
  });
});
