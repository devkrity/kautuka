import React from 'react';
import { useLocation } from 'react-router-dom';
import { useWorkflow, useWorkflows } from '@/features/workflows';
import type { SaveAutopilotScheduleRequest } from '../api/autopilot';

export function useAutopilotWorkflowTargets(
  draft: SaveAutopilotScheduleRequest,
  setDraft: React.Dispatch<React.SetStateAction<SaveAutopilotScheduleRequest>>,
  setEditingScheduleId: (id: string | null) => void,
  setShowScheduleForm: (show: boolean) => void
) {
  const location = useLocation();
  const { data: workflows = [] } = useWorkflows();
  const workflow = useWorkflow(draft.targetType === 'workflow' ? draft.targetId : undefined);
  const initializedKey = React.useRef<string | null>(null);
  const navigation = location.state as { targetType?: string; targetId?: string } | null;

  React.useEffect(() => {
    if (initializedKey.current === location.key) return;
    initializedKey.current = location.key;
    if (navigation?.targetType !== 'workflow' || !navigation.targetId) return;
    setEditingScheduleId(null);
    setDraft((previous) => ({
      ...previous,
      targetType: 'workflow',
      targetId: navigation.targetId!,
      cronExpression: '0 9 * * *',
      schedulePromptContext: '{}',
      workspaceFilePaths: [],
    }));
    setShowScheduleForm(true);
  }, [
    location.key,
    navigation?.targetId,
    navigation?.targetType,
    setDraft,
    setEditingScheduleId,
    setShowScheduleForm,
  ]);

  return {
    workflows,
    workflow,
    targetAgentId:
      draft.targetType === 'workflow' ? (workflow.data?.nodes[0]?.agentId ?? '') : draft.targetId,
  };
}
