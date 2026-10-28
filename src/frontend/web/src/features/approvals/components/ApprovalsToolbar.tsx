import { SegmentedControl } from '@/components/ui/segmented-control';
import React from 'react';
import { History, ShieldCheck } from 'lucide-react';

interface ApprovalsToolbarProps {
  tab: 'pending' | 'history';
  pendingCount: number;
  isAdmin: boolean;
  scope: 'mine' | 'all';
  onTabChange: (tab: 'pending' | 'history') => void;
  onScopeChange: (scope: 'mine' | 'all') => void;
}

export function ApprovalsToolbar({
  tab,
  pendingCount,
  isAdmin,
  scope,
  onTabChange,
  onScopeChange,
}: ApprovalsToolbarProps) {
  return (
    <div className="mb-4 flex flex-wrap items-center justify-between gap-3 border-b border-border pb-3">
      <SegmentedControl
        value={tab}
        onChange={onTabChange}
        size="md"
        options={[
          {
            value: 'pending',
            label: (
              <span className="inline-flex items-center gap-1.5">
                <ShieldCheck className="h-3.5 w-3.5" />
                Pending
                {pendingCount > 0 && (
                  <span className="ml-0.5 rounded-full bg-attention px-1.5 py-0.5 text-[10px] font-bold leading-none text-attention-foreground">
                    {pendingCount}
                  </span>
                )}
              </span>
            ),
          },
          {
            value: 'history',
            label: (
              <span className="inline-flex items-center gap-1.5">
                <History className="h-3.5 w-3.5" />
                History
              </span>
            ),
          },
        ]}
      />

      {isAdmin && (
        <SegmentedControl
          value={scope}
          onChange={onScopeChange}
          size="md"
          options={[
            { value: 'mine', label: 'My approvals' },
            { value: 'all', label: 'All approvals' },
          ]}
        />
      )}
    </div>
  );
}
