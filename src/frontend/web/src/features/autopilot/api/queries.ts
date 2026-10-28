import { useMutation, useQuery } from '@tanstack/react-query';
import { queryClient } from '@/lib/queryClient';
import {
  createAutopilotSchedule,
  deleteAutopilotSchedule,
  listAutopilotInbox,
  listAutopilotRuns,
  listAutopilotSchedules,
  markAutopilotInboxRead,
  pauseAutopilotSchedule,
  resumeAutopilotSchedule,
  runAutopilotScheduleNow,
  updateAutopilotSchedule,
  type SaveAutopilotScheduleRequest,
} from './autopilot';
import { autopilotQueryKeys } from './queryKeys';

function hasActiveRuns(data: Awaited<ReturnType<typeof listAutopilotRuns>> | undefined): boolean {
  if (!data || data.length === 0) {
    return false;
  }

  return data.some((run) => run.stateCategory !== 'terminal');
}

export function useAutopilotSchedules() {
  return useQuery({
    queryKey: autopilotQueryKeys.schedules(),
    queryFn: () => listAutopilotSchedules(),
    refetchInterval: 10_000,
  });
}

export function useAutopilotRuns(scheduleId?: string) {
  return useQuery({
    queryKey: autopilotQueryKeys.runs(scheduleId),
    queryFn: () => listAutopilotRuns(scheduleId),
    // Poll faster while a run is active so approval/result state appears quickly in UI.
    refetchInterval: (query) => (hasActiveRuns(query.state.data) ? 2_500 : 10_000),
  });
}

export function useAutopilotInbox() {
  return useQuery({
    queryKey: autopilotQueryKeys.inbox(),
    queryFn: () => listAutopilotInbox(false),
    refetchInterval: 10_000,
  });
}

export function useCreateAutopilotSchedule() {
  return useMutation({
    mutationFn: (request: SaveAutopilotScheduleRequest) => createAutopilotSchedule(request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: autopilotQueryKeys.schedules() });
    },
  });
}

export function useUpdateAutopilotSchedule() {
  return useMutation({
    mutationFn: ({
      scheduleId,
      request,
    }: {
      scheduleId: string;
      request: SaveAutopilotScheduleRequest;
    }) => updateAutopilotSchedule(scheduleId, request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: autopilotQueryKeys.schedules() });
    },
  });
}

export function usePauseAutopilotSchedule() {
  return useMutation({
    mutationFn: (scheduleId: string) => pauseAutopilotSchedule(scheduleId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: autopilotQueryKeys.schedules() });
    },
  });
}

export function useResumeAutopilotSchedule() {
  return useMutation({
    mutationFn: (scheduleId: string) => resumeAutopilotSchedule(scheduleId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: autopilotQueryKeys.schedules() });
    },
  });
}

export function useRunAutopilotNow() {
  return useMutation({
    mutationFn: (scheduleId: string) => runAutopilotScheduleNow(scheduleId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: autopilotQueryKeys.schedules() });
      queryClient.invalidateQueries({ queryKey: autopilotQueryKeys.runs() });
    },
  });
}

export function useDeleteAutopilotSchedule() {
  return useMutation({
    mutationFn: (scheduleId: string) => deleteAutopilotSchedule(scheduleId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: autopilotQueryKeys.schedules() });
      queryClient.invalidateQueries({ queryKey: autopilotQueryKeys.runs() });
    },
  });
}

export function useMarkAutopilotInboxRead() {
  return useMutation({
    mutationFn: (notificationId: string) => markAutopilotInboxRead(notificationId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: autopilotQueryKeys.inbox() });
    },
  });
}
