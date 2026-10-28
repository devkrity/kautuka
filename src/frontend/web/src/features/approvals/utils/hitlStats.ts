import type { HitlStatsWindow } from '../api/approvals';
import type { HitlApprovalStatItem } from '../schemas/approvalSchema';

export const HITL_RUBBER_STAMP_MIN = 10;

export function hitlDecisionTotal(item: Pick<HitlApprovalStatItem, 'approved' | 'denied'>): number {
  return item.approved + item.denied;
}

export function formatHitlCounts(item: Pick<HitlApprovalStatItem, 'approved' | 'denied'>): string {
  return `${item.approved} approved · ${item.denied} denied`;
}

export function describeHitlStatsWindow(days: HitlStatsWindow | number | undefined): string {
  if (days === 'all' || days === 0) return 'all time';
  if (days === 7) return 'last 7 days';
  return `last ${days ?? 30} days`;
}

export function rubberStampLabel(
  item: Pick<HitlApprovalStatItem, 'approved' | 'denied'>
): string | null {
  const total = hitlDecisionTotal(item);
  if (item.denied === 0 && total >= HITL_RUBBER_STAMP_MIN) {
    return `Approved every time (n=${total})`;
  }
  return null;
}

export function describeHitlStatsError(error: unknown): string {
  const response = (error as { response?: { status?: number; data?: unknown } } | undefined)?.response;
  const status = response?.status;
  const data = response?.data;
  const gatewayError =
    data && typeof data === 'object'
      ? typeof (data as { error?: unknown }).error === 'string'
        ? (data as { error: string }).error
        : typeof (data as { detail?: unknown }).detail === 'string'
          ? (data as { detail: string }).detail
          : null
      : null;
  if (status != null && gatewayError) {
    return `Could not load approval stats (${status}): ${gatewayError}`;
  }
  if (status != null) {
    return `Could not load approval stats (${status}).`;
  }
  if (error instanceof Error && error.message.length > 0) {
    return `Could not load approval stats: ${error.message}`;
  }
  return 'Could not load approval stats.';
}

export function findHitlStat(
  items: HitlApprovalStatItem[] | undefined,
  match: { toolName?: string; skillId?: string; mcpServerId?: string }
): HitlApprovalStatItem | undefined {
  if (!items || items.length === 0) {
    return undefined;
  }

  if (match.skillId) {
    return items.find(
      (item) => item.skillId === match.skillId || item.toolName === match.skillId
    );
  }

  if (match.toolName) {
    return items.find((item) => {
      if (item.toolName !== match.toolName) {
        return false;
      }
      if (match.mcpServerId && item.mcpServerId && item.mcpServerId !== match.mcpServerId) {
        return false;
      }
      return true;
    });
  }

  if (match.mcpServerId) {
    return rollupHitlStatsForConnector(items, match.mcpServerId);
  }

  return undefined;
}

/** Sum tool-level connector stats into one card-level row (skills already aggregate by skillId). */
export function rollupHitlStatsForConnector(
  items: HitlApprovalStatItem[],
  mcpServerId: string
): HitlApprovalStatItem | undefined {
  // Connector catalog responses are already scoped; include legacy rows with null mcpServerId.
  const matched = items.filter(
    (item) => !item.mcpServerId || item.mcpServerId === mcpServerId
  );
  if (matched.length === 0) {
    return undefined;
  }
  if (matched.length === 1) {
    return {
      ...matched[0]!,
      mcpServerId: matched[0]!.mcpServerId ?? mcpServerId,
    };
  }

  let approved = 0;
  let denied = 0;
  let agentCount = 0;
  let lastDeniedAt: string | undefined;
  for (const item of matched) {
    approved += item.approved;
    denied += item.denied;
    agentCount = Math.max(agentCount, item.agentCount);
    if (
      item.lastDeniedAt &&
      (!lastDeniedAt || item.lastDeniedAt.localeCompare(lastDeniedAt) > 0)
    ) {
      lastDeniedAt = item.lastDeniedAt;
    }
  }

  return {
    toolName: mcpServerId,
    toolType: 'connector',
    approved,
    denied,
    agentCount,
    mcpServerId,
    lastDeniedAt,
  };
}

export type HitlStatsRevisionGroup = {
  revision: number | null;
  label: string;
  items: HitlApprovalStatItem[];
};

export function groupHitlStatsByRevision(items: HitlApprovalStatItem[]): HitlStatsRevisionGroup[] {
  const byKey = new Map<string, HitlStatsRevisionGroup>();
  for (const item of items) {
    const revision = item.agentDefinitionRevision ?? null;
    const key = revision == null ? 'ungrouped' : String(revision);
    let group = byKey.get(key);
    if (!group) {
      group = {
        revision,
        label:
          revision == null
            ? 'Earlier decisions (no config version recorded)'
            : `Config version ${revision}`,
        items: [],
      };
      byKey.set(key, group);
    }
    group.items.push(item);
  }

  return [...byKey.values()].sort((a, b) => {
    if (a.revision == null && b.revision == null) return 0;
    if (a.revision == null) return 1;
    if (b.revision == null) return -1;
    return b.revision - a.revision;
  });
}

/** Flat table until any stamped row exists; once versions appear, keep ungrouped last. */
export function visibleHitlStatsGroups(items: HitlApprovalStatItem[]): {
  hasVersionedData: boolean;
  groups: HitlStatsRevisionGroup[];
} {
  const groups = groupHitlStatsByRevision(items);
  const hasVersionedData = groups.some((group) => group.revision != null);
  if (!hasVersionedData) {
    return {
      hasVersionedData: false,
      groups:
        items.length === 0
          ? []
          : [
              {
                revision: null,
                label: '',
                items,
              },
            ],
    };
  }

  return {
    hasVersionedData: true,
    groups,
  };
}
