import React from 'react';
import { Input } from '@/components/ui/input';
import { normalizeApprovalConfig } from '../utils/settingsUtils';
import type { ApprovalPolicyConfig } from '../types/settingsTypes';

interface ApprovalPolicyFormProps {
  approvalConfig?: ApprovalPolicyConfig;
  onChange: (next: ApprovalPolicyConfig) => void;
}

export function ApprovalPolicyForm({
  approvalConfig,
  onChange,
}: Readonly<ApprovalPolicyFormProps>) {
  const current = normalizeApprovalConfig(approvalConfig);

  return (
    <div className="settings-subpanel-compact">
      <div className="mb-2 text-sm font-medium">Approval Policy</div>
      <div className="grid grid-cols-1 gap-3 md:grid-cols-3">
        <div>
          <label
            className="mb-1 block text-xs text-muted-foreground"
            htmlFor="approval-timeout-seconds"
          >
            Approval timeout (seconds)
          </label>
          <Input
            id="approval-timeout-seconds"
            type="number"
            min={30}
            max={3600}
            value={current.timeoutSeconds}
            onChange={(e) => {
              const timeout = Math.max(30, Math.min(3600, Number(e.target.value) || 300));
              onChange({ ...current, timeoutSeconds: timeout });
            }}
          />
        </div>
      </div>
    </div>
  );
}
