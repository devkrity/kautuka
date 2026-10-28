import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { ScanSearch } from 'lucide-react';
import React from 'react';
import type { HitlStatsWindow } from '../api/approvals';
import { useHitlStats } from '../api/queries';
import type { HitlApprovalStatItem } from '../schemas/approvalSchema';
import {
  describeHitlStatsError,
  describeHitlStatsWindow,
  hitlDecisionTotal,
  rubberStampLabel,
  visibleHitlStatsGroups,
} from '../utils/hitlStats';
import { HitlDeniedCallsDrawer } from './HitlDeniedCallsDrawer';

interface HitlWorkspaceSummaryProps {
  agentId: string;
  onConfigure?: () => void;
  compact?: boolean;
}

const WINDOW_OPTIONS: { value: HitlStatsWindow; label: string }[] = [
  { value: 7, label: '7 days' },
  { value: 30, label: '30 days' },
  { value: 'all', label: 'All time' },
];

export function HitlWorkspaceSummary({ agentId, onConfigure, compact = false }: Readonly<HitlWorkspaceSummaryProps>) {
  const [windowDays, setWindowDays] = React.useState<HitlStatsWindow>(30);
  const statsQuery = useHitlStats({ agentId, days: windowDays });
  const items = statsQuery.data?.items ?? [];
  const { hasVersionedData, groups } = visibleHitlStatsGroups(items);
  const [open, setOpen] = React.useState(false);
  const [deniedTool, setDeniedTool] = React.useState<{
    toolName: string;
    skillId?: string;
    agentDefinitionRevision?: number | null;
  } | null>(null);

  const windowLabel = describeHitlStatsWindow(statsQuery.data?.windowDays ?? windowDays);

  return (
    <>
      <Button
        variant={compact ? 'ghost' : 'outline'}
        size={compact ? 'icon' : 'sm'}
        title="Stats"
        aria-label="Stats"
        onClick={() => setOpen(true)}
      >
        <ScanSearch className={compact ? 'h-4 w-4' : 'mr-2 h-4 w-4'} />
        {!compact ? 'Stats' : null}
      </Button>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent className="max-h-[85vh] max-w-3xl overflow-y-auto">
          <DialogHeader>
            <DialogTitle>Stats for this agent</DialogTitle>
          </DialogHeader>
          <div className="mt-2 flex flex-wrap items-center justify-between gap-3">
            <div>
              <h3 className="text-sm font-medium text-foreground">Approvals</h3>
              <p className="text-xs text-muted-foreground">
                Human approve/deny counts for this agent ({windowLabel}). This does not change Ask vs
                Trusted.
              </p>
            </div>
            <div className="flex flex-wrap gap-1" role="group" aria-label="Stats window">
              {WINDOW_OPTIONS.map((option) => (
                <Button
                  key={String(option.value)}
                  type="button"
                  size="sm"
                  variant={windowDays === option.value ? 'default' : 'outline'}
                  onClick={() => setWindowDays(option.value)}
                >
                  {option.label}
                </Button>
              ))}
            </div>
          </div>
          {statsQuery.isError ? (
            <p className="mt-3 text-sm text-destructive">{describeHitlStatsError(statsQuery.error)}</p>
          ) : statsQuery.isLoading ? (
            <p className="mt-3 text-sm text-muted-foreground">Loading approval stats…</p>
          ) : items.length === 0 ? (
            <p className="mt-3 text-sm text-muted-foreground">
              No human approve/deny decisions for this agent in the {windowLabel}.
            </p>
          ) : (
            <div className="mt-3 space-y-4">
              {!hasVersionedData ? (
                <p className="text-xs text-muted-foreground">
                  These decisions were recorded before config-version tracking. New approve/deny
                  decisions will appear under their agent config version.
                </p>
              ) : null}
              {groups.map((group) => (
                <section key={group.revision == null ? 'flat' : `v-${group.revision}`}>
                  {hasVersionedData && group.label ? (
                    <h4 className="section-label mb-2">
                      {group.label}
                    </h4>
                  ) : null}
                  <div className="overflow-x-auto rounded-md border border-border/70">
                    <table className="min-w-full text-sm">
                      <thead className="bg-muted/60 text-left text-muted-foreground">
                        <tr>
                          <th className="px-3 py-2 font-medium">Tool</th>
                          <th
                            className="px-3 py-2 font-medium"
                            title="Human approve and deny decisions"
                          >
                            Decisions
                          </th>
                          <th className="px-3 py-2 font-medium">Approved</th>
                          <th className="px-3 py-2 font-medium">Denied</th>
                          <th className="px-3 py-2 font-medium">Details</th>
                        </tr>
                      </thead>
                      <tbody>
                        {group.items.map((item) => (
                          <StatsRow
                            key={`${group.revision ?? 'none'}:${item.toolType}:${item.mcpServerId ?? ''}:${item.toolName}`}
                            item={item}
                            onViewDenied={() =>
                              setDeniedTool({
                                toolName: item.toolName,
                                skillId: item.skillId,
                                agentDefinitionRevision: group.revision,
                              })
                            }
                          />
                        ))}
                      </tbody>
                    </table>
                  </div>
                </section>
              ))}
            </div>
          )}
          {onConfigure ? (
            <div className="mt-4">
              <Button
                variant="outline"
                onClick={() => {
                  setOpen(false);
                  onConfigure();
                }}
              >
                Open Configure Agent
              </Button>
            </div>
          ) : null}
        </DialogContent>
      </Dialog>
      <HitlDeniedCallsDrawer
        open={deniedTool != null}
        query={{
          agentId,
          days: windowDays,
          skillId: deniedTool?.skillId,
          toolName: deniedTool?.skillId ? undefined : deniedTool?.toolName,
          ungrouped: deniedTool?.agentDefinitionRevision == null,
          agentDefinitionRevision:
            deniedTool?.agentDefinitionRevision == null
              ? undefined
              : deniedTool.agentDefinitionRevision,
        }}
        title={deniedTool?.toolName ?? 'Denied calls'}
        windowLabel={windowLabel}
        onClose={() => setDeniedTool(null)}
      />
    </>
  );
}

function StatsRow({
  item,
  onViewDenied,
}: Readonly<{ item: HitlApprovalStatItem; onViewDenied: () => void }>) {
  const stamp = rubberStampLabel(item);
  return (
    <tr className="border-t border-border/60 align-top">
      <td className="px-3 py-2">
        <div className="font-mono text-xs text-foreground">{item.toolName}</div>
        {stamp ? <div className="mt-0.5 text-[11px] text-muted-foreground">{stamp}</div> : null}
      </td>
      <td className="px-3 py-2 text-muted-foreground">{hitlDecisionTotal(item)}</td>
      <td className="px-3 py-2 text-muted-foreground">{item.approved}</td>
      <td className="px-3 py-2 text-muted-foreground">{item.denied}</td>
      <td className="px-3 py-2">
        {item.denied > 0 ? (
          <button
            type="button"
            className="text-xs underline-offset-2 hover:underline"
            onClick={onViewDenied}
          >
            Denied runs
          </button>
        ) : (
          <span className="text-xs text-muted-foreground">—</span>
        )}
      </td>
    </tr>
  );
}
