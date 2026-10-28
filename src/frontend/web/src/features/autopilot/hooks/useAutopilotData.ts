import React from 'react';
import {
  useAutopilotInbox,
  useAutopilotRuns,
  useAutopilotSchedules,
  useCreateAutopilotSchedule,
  useDeleteAutopilotSchedule,
  useMarkAutopilotInboxRead,
  usePauseAutopilotSchedule,
  useResumeAutopilotSchedule,
  useRunAutopilotNow,
  useUpdateAutopilotSchedule,
} from '../api/queries';
import type { SaveAutopilotScheduleRequest } from '../api/autopilot';
import { resolveWorkspaceFilesForAgent } from '@/features/chat';
import { useWorkflow } from '@/features/workflows';

export function useAutopilotData() {
  const schedulesQuery = useAutopilotSchedules();
  const runsQuery = useAutopilotRuns();
  const inboxQuery = useAutopilotInbox();

  const createMutation = useCreateAutopilotSchedule();
  const updateMutation = useUpdateAutopilotSchedule();
  const pauseMutation = usePauseAutopilotSchedule();
  const resumeMutation = useResumeAutopilotSchedule();
  const runNowMutation = useRunAutopilotNow();
  const deleteMutation = useDeleteAutopilotSchedule();
  const markReadMutation = useMarkAutopilotInboxRead();
  const [validationError, setValidationError] = React.useState<string | null>(null);

  const [draft, setDraft] = React.useState<SaveAutopilotScheduleRequest>({
    targetType: 'agent',
    targetId: '',
    cronExpression: '*/5 * * * *',
    mode: 'on',
    schedulePromptContext: '',
    workspaceFilePaths: [],
    notifyUiInbox: true,
    notifyChannels: false,
    preRunApprovalExpiryPolicy: 'next_occurrence',
    preRunApprovalTimeoutSeconds: null,
    overlapPolicy: 'skip_and_notify',
  });
  const [editingScheduleId, setEditingScheduleId] = React.useState<string | null>(null);
  const workflow = useWorkflow(draft.targetType === 'workflow' ? draft.targetId : undefined);

  const setDraftAndClearValidation: React.Dispatch<React.SetStateAction<SaveAutopilotScheduleRequest>> = React.useCallback(
    (value) => {
      setValidationError(null);
      setDraft(value);
    },
    []
  );

  const mutationError =
    createMutation.error
    ?? updateMutation.error
    ?? pauseMutation.error
    ?? resumeMutation.error
    ?? runNowMutation.error
    ?? deleteMutation.error
    ?? markReadMutation.error
    ?? validationError;

  const save = async () => {
    const workspaceAgentId = draft.targetType === 'workflow'
      ? workflow.data?.nodes[0]?.agentId ?? ''
      : draft.targetId;
    const { workspaceFiles, validationError: nextValidationError } = await resolveWorkspaceFilesForAgent(
      workspaceAgentId,
      draft.schedulePromptContext
    );

    if (nextValidationError) {
      setValidationError(nextValidationError);
      return false;
    }

    const request: SaveAutopilotScheduleRequest = {
      ...draft,
      workspaceFilePaths: [
        ...new Set([
          ...workspaceFiles,
          ...(draft.workspaceFilePaths ?? []).map((path) => path.replace(/\\/g, '/').replace(/^\/+/, '')),
        ]),
      ],
    };

    if (editingScheduleId) {
      await updateMutation.mutateAsync({ scheduleId: editingScheduleId, request });
      setEditingScheduleId(null);
      return true;
    }

    await createMutation.mutateAsync(request);
    return true;
  };

  return {
    schedules: schedulesQuery.data ?? [],
    runs: runsQuery.data ?? [],
    inbox: inboxQuery.data ?? [],
    loading: schedulesQuery.isLoading || runsQuery.isLoading,
    error: schedulesQuery.error ?? runsQuery.error ?? inboxQuery.error ?? mutationError,
    draft,
    setDraft: setDraftAndClearValidation,
    editingScheduleId,
    setEditingScheduleId,
    setDraftFromSchedule: (scheduleId: string) => {
      const schedule = (schedulesQuery.data ?? []).find((item) => item.id === scheduleId);
      if (!schedule) {
        return;
      }

      setValidationError(null);
      setDraft({
        targetType: schedule.targetType === 'workflow' ? 'workflow' : 'agent',
        targetId: schedule.targetId,
        cronExpression: schedule.cronExpression,
        mode: schedule.mode as 'off' | 'on' | 'needs_approval',
        schedulePromptContext: schedule.schedulePromptContext,
        workspaceFilePaths: schedule.workspaceFilePaths ?? [],
        notifyUiInbox: schedule.notifyUiInbox,
        notifyChannels: schedule.notifyChannels,
        preRunApprovalExpiryPolicy: schedule.preRunApprovalExpiryPolicy,
        preRunApprovalTimeoutSeconds: schedule.preRunApprovalTimeoutSeconds,
        overlapPolicy: schedule.overlapPolicy,
        isPaused: schedule.isPaused,
      });
      setEditingScheduleId(schedule.id);
    },
    save,
    pauseSchedule: (scheduleId: string) => pauseMutation.mutateAsync(scheduleId),
    resumeSchedule: (scheduleId: string) => resumeMutation.mutateAsync(scheduleId),
    runNow: (scheduleId: string) => runNowMutation.mutateAsync(scheduleId),
    deleteSchedule: (scheduleId: string) => deleteMutation.mutateAsync(scheduleId),
    markInboxRead: (notificationId: string) => markReadMutation.mutateAsync(notificationId),
    saving: createMutation.isPending || updateMutation.isPending,
    mutating:
      pauseMutation.isPending ||
      resumeMutation.isPending ||
      runNowMutation.isPending ||
      deleteMutation.isPending ||
      markReadMutation.isPending,
  };
}
