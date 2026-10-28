import { Badge } from '@/components/ui/badge';
import React from 'react';
import { workflowStatusLabel, workflowStepStatusLabel } from '../model/workflowLabels';

const tone: Record<string, 'info' | 'attention' | 'success' | 'danger' | 'muted'> = {
  running: 'info',
  paused: 'attention',
  finished: 'success',
  error: 'danger',
  failed: 'danger',
  cancelled: 'muted',
  pending: 'muted',
};

interface WorkflowStatusBadgeProps {
  status: string;
  kind?: 'run' | 'step';
  label?: string;
}

export const WorkflowStatusBadge: React.FC<WorkflowStatusBadgeProps> = ({
  status,
  kind = 'run',
  label: labelOverride,
}) => {
  const label =
    labelOverride ??
    (kind === 'step'
      ? (workflowStepStatusLabel[status] ?? status)
      : (workflowStatusLabel[status] ?? status));
  return <Badge variant={tone[status] ?? 'muted'}>{label}</Badge>;
};
