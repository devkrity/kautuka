import { apiClient } from '@/lib/apiClient';
import {
  autopilotInboxItemSchema,
  autopilotRunSchema,
  autopilotScheduleSchema,
  type AutopilotInboxItem,
  type AutopilotRun,
  type AutopilotSchedule,
} from '../schemas/autopilotSchema';

export interface SaveAutopilotScheduleRequest {
  targetType: 'agent' | 'workflow';
  targetId: string;
  cronExpression: string;
  mode: 'off' | 'on' | 'needs_approval';
  schedulePromptContext: string;
  workspaceFilePaths: string[];
  notifyUiInbox: boolean;
  notifyChannels: boolean;
  preRunApprovalExpiryPolicy: 'next_occurrence' | 'duration' | 'never';
  preRunApprovalTimeoutSeconds?: number | null;
  overlapPolicy: 'skip_and_notify' | 'skip_silently';
  isPaused?: boolean;
}

export async function listAutopilotSchedules(): Promise<AutopilotSchedule[]> {
  const response = await apiClient.get('/autopilot/schedules');
  return (Array.isArray(response.data) ? response.data : []).map((item: unknown) =>
    autopilotScheduleSchema.parse(item)
  );
}

export async function createAutopilotSchedule(
  request: SaveAutopilotScheduleRequest
): Promise<AutopilotSchedule> {
  const response = await apiClient.post('/autopilot/schedules', request);
  return autopilotScheduleSchema.parse(response.data);
}

export async function updateAutopilotSchedule(
  scheduleId: string,
  request: SaveAutopilotScheduleRequest
): Promise<AutopilotSchedule> {
  const response = await apiClient.put(`/autopilot/schedules/${scheduleId}`, request);
  return autopilotScheduleSchema.parse(response.data);
}

export async function pauseAutopilotSchedule(scheduleId: string): Promise<AutopilotSchedule> {
  const response = await apiClient.post(`/autopilot/schedules/${scheduleId}/pause`, {});
  return autopilotScheduleSchema.parse(response.data);
}

export async function resumeAutopilotSchedule(scheduleId: string): Promise<AutopilotSchedule> {
  const response = await apiClient.post(`/autopilot/schedules/${scheduleId}/resume`, {});
  return autopilotScheduleSchema.parse(response.data);
}

export async function runAutopilotScheduleNow(scheduleId: string): Promise<void> {
  await apiClient.post(`/autopilot/schedules/${scheduleId}/run-now`, {});
}

export async function deleteAutopilotSchedule(scheduleId: string): Promise<void> {
  await apiClient.delete(`/autopilot/schedules/${scheduleId}`);
}

export async function listAutopilotRuns(scheduleId?: string): Promise<AutopilotRun[]> {
  const response = await apiClient.get('/autopilot/runs', {
    params: scheduleId ? { scheduleId } : undefined,
  });
  return (Array.isArray(response.data) ? response.data : []).map((item: unknown) =>
    autopilotRunSchema.parse(item)
  );
}

export async function listAutopilotInbox(unreadOnly = false): Promise<AutopilotInboxItem[]> {
  const response = await apiClient.get('/autopilot/inbox', {
    params: unreadOnly ? { unreadOnly: true } : undefined,
  });
  return (Array.isArray(response.data) ? response.data : []).map((item: unknown) =>
    autopilotInboxItemSchema.parse(item)
  );
}

export async function markAutopilotInboxRead(notificationId: string): Promise<void> {
  await apiClient.post(`/autopilot/inbox/${notificationId}/read`, {});
}
