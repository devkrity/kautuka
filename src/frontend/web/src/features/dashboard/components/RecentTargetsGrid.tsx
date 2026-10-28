import React from 'react';
import { Card, CardContent } from '@/components/ui/card';
import { Bot, Sparkles } from 'lucide-react';
import type { DashboardRecentTarget } from '../hooks/useDashboardData';
import { fmtRelative } from '../hooks/useDashboardData';

interface RecentTargetsGridProps {
  targets: DashboardRecentTarget[];
  onOpenTarget: (target: DashboardRecentTarget) => void;
}

export function RecentTargetsGrid({ targets, onOpenTarget }: Readonly<RecentTargetsGridProps>) {
  return (
    <>
      <div className="mb-3 flex items-center justify-between">
        <h2 className="flex items-center gap-2 text-xl font-bold">
          <Sparkles className="h-4 w-4 text-primary" />
          Recently Used Agents
        </h2>
      </div>

      {targets.length === 0 ? (
        <Card>
          <CardContent className="p-5 text-sm text-muted-foreground">
            No recent targets yet. Start a conversation in Chat to populate this list.
          </CardContent>
        </Card>
      ) : (
        <div className="mb-8 grid grid-cols-1 gap-4 md:grid-cols-2">
          {targets.map((target) => {
            const Icon = Bot;
            return (
              <Card
                key={`${target.type}:${target.id}`}
                className="hover-lift cursor-pointer"
                onClick={() => onOpenTarget(target)}
              >
                <CardContent className="p-5">
                  <div className="mb-4 flex items-start justify-between">
                    <div className="rounded-xl bg-primary/10 p-2 text-primary">
                      <Icon className="h-4 w-4" />
                    </div>
                    <span className="rounded-full bg-muted px-2 py-0.5 text-[11px] font-semibold uppercase text-muted-foreground">
                      {target.type}
                    </span>
                  </div>
                  <p className="text-lg font-bold leading-tight text-foreground">{target.name}</p>
                  <p className="mt-2 text-sm text-muted-foreground">
                    {target.sessions} sessions · last used {fmtRelative(target.lastUsedAt)}
                  </p>
                </CardContent>
              </Card>
            );
          })}
        </div>
      )}
    </>
  );
}
