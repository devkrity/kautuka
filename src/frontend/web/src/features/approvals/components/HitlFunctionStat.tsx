import React from 'react';
import { useHitlStats, type HitlStatsQuery } from '../api/queries';
import { describeHitlStatsError, formatHitlCounts, findHitlStat, rubberStampLabel } from '../utils/hitlStats';
import { HitlDeniedCallsDrawer } from './HitlDeniedCallsDrawer';

interface HitlFunctionStatProps {
  agentId?: string;
  mcpServerId?: string;
  skillId?: string;
  toolName?: string;
  label?: string;
}

export function HitlFunctionStat({
  agentId,
  mcpServerId,
  skillId,
  toolName,
  label,
}: Readonly<HitlFunctionStatProps>) {
  const query: HitlStatsQuery = { agentId, mcpServerId, skillId };
  const statsQuery = useHitlStats(query);
  const item = findHitlStat(statsQuery.data?.items, { toolName, skillId, mcpServerId });
  const [deniedOpen, setDeniedOpen] = React.useState(false);

  if (statsQuery.isError) {
    return <span className="text-[11px] text-destructive">{describeHitlStatsError(statsQuery.error)}</span>;
  }

  if (!item) {
    return null;
  }

  const stamp = rubberStampLabel(item);
  const catalogAgents =
    !agentId && item.agentCount > 0 ? ` · ${item.agentCount} agent${item.agentCount === 1 ? '' : 's'}` : '';
  const text = `${formatHitlCounts(item)}${stamp ? ` · ${stamp}` : ''}${catalogAgents}`;
  const canInspectDenied = item.denied > 0;

  return (
    <>
      <button
        type="button"
        className="text-[11px] text-muted-foreground hover:text-foreground disabled:hover:text-muted-foreground"
        disabled={!canInspectDenied}
        title={canInspectDenied ? 'View denied calls' : undefined}
        onClick={() => {
          if (canInspectDenied) {
            setDeniedOpen(true);
          }
        }}
      >
        {text}
      </button>
      <HitlDeniedCallsDrawer
        open={deniedOpen}
        query={{ ...query, toolName: skillId ? undefined : toolName }}
        title={label ?? toolName ?? skillId ?? 'Denied calls'}
        onClose={() => setDeniedOpen(false)}
      />
    </>
  );
}
