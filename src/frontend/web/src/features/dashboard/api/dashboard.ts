/**
 * Dashboard feature API operations
 */
import { apiClient } from '@/lib/apiClient';
import {
  readApiArray,
  readApiBoolean,
  readApiNullableString,
  readApiString,
  readApiStringArray,
} from '@/lib/apiResponse';
import { recentSessionSchema, type RecentSession } from '../schemas/dashboardSchema';
import { updateStatusSchema, type UpdateStatusSummary } from '../schemas/updateStatusSchema';
import { updateHistorySchema, type UpdateOperation } from '../schemas/updateHistorySchema';
import { applianceHealthSchema, type ApplianceHealth } from '../schemas/applianceHealthSchema';

export interface ServiceHealthSummary {
  status: string;
  service: string;
}

export interface SystemHealthSummary {
  runtime: ServiceHealthSummary;
  channels: ServiceHealthSummary;
  workspace: ServiceHealthSummary;
  lifecycle: {
    status: string;
    coreReady: boolean;
    agentsPending: boolean;
    agentsReady: boolean;
  };
  actionHints: string[];
  driftActions: string[];
}

export interface ExecutionDependencyReadiness {
  dependency: string;
  ready: boolean;
  code: string;
  message: string;
  detail: string | null;
}

export interface ExecutionReadinessSummary {
  ready: boolean;
  code: string;
  message: string;
  dependencies: ExecutionDependencyReadiness[];
}

export async function fetchRecentSessions(limit = 20): Promise<RecentSession[]> {
  const response = await apiClient.get('/sessions/recent', { params: { limit } });
  const data = readApiArray(response.data, 'sessions');
  return data.map((s: unknown) => recentSessionSchema.parse(s));
}

function readServiceHealth(source: unknown, key: string): ServiceHealthSummary {
  const value =
    source && typeof source === 'object'
      ? (Object.entries(source as Record<string, unknown>).find(
          ([entryKey]) => entryKey === key
        )?.[1] ?? null)
      : null;

  return {
    status: readApiString(value, 'status') ?? 'unknown',
    service: readApiString(value, 'service') ?? key,
  };
}

export async function fetchSystemHealth(): Promise<SystemHealthSummary> {
  const response = await apiClient.get('/runtime/system-health');

  const lifecycleSource =
    response.data && typeof response.data === 'object'
      ? (Object.entries(response.data as Record<string, unknown>).find(
          ([entryKey]) => entryKey === 'lifecycle'
        )?.[1] ?? null)
      : null;

  return {
    runtime: readServiceHealth(response.data, 'runtime'),
    channels: readServiceHealth(response.data, 'channels'),
    workspace: readServiceHealth(response.data, 'workspace'),
    lifecycle: {
      status: readApiString(lifecycleSource, 'status') ?? 'core_not_ready',
      coreReady: readApiBoolean(lifecycleSource, 'coreReady') ?? false,
      agentsPending: readApiBoolean(lifecycleSource, 'agentsPending') ?? false,
      agentsReady: readApiBoolean(lifecycleSource, 'agentsReady') ?? false,
    },
    actionHints: readApiStringArray(response.data, 'actionHints') ?? [],
    driftActions: readApiStringArray(response.data, 'driftActions') ?? [],
  };
}

export async function fetchExecutionReadiness(): Promise<ExecutionReadinessSummary> {
  const response = await apiClient.get('/runtime/execution-readiness');
  const dependencies = readApiArray(response.data, 'dependencies').map((dependency) => ({
    dependency: readApiString(dependency, 'dependency') ?? 'Unknown dependency',
    ready: readApiBoolean(dependency, 'ready') ?? false,
    code: readApiString(dependency, 'code') ?? 'DEPENDENCY_STATUS_INVALID',
    message: readApiString(dependency, 'message') ?? 'Dependency readiness is unknown.',
    detail: readApiNullableString(dependency, 'detail') ?? null,
  }));

  return {
    ready: readApiBoolean(response.data, 'ready') ?? false,
    code: readApiString(response.data, 'code') ?? 'EXECUTION_READINESS_INVALID',
    message:
      readApiString(response.data, 'message') ?? 'Agent execution readiness could not be verified.',
    dependencies,
  };
}

export async function fetchUpdateStatus(): Promise<UpdateStatusSummary> {
  const response = await apiClient.get('/runtime/update-status');
  return updateStatusSchema.parse(response.data);
}

export async function refreshUpdateDiscovery(): Promise<UpdateStatusSummary> {
  const response = await apiClient.post('/runtime/update-status/refresh');
  return updateStatusSchema.parse(response.data);
}

export async function applyUpdate(): Promise<UpdateStatusSummary> {
  const response = await apiClient.post('/runtime/update-status/apply');
  return updateStatusSchema.parse(response.data);
}

export async function revertUpdate(): Promise<UpdateStatusSummary> {
  const response = await apiClient.post('/runtime/update-status/revert');
  return updateStatusSchema.parse(response.data);
}

export async function fetchUpdateHistory(): Promise<UpdateOperation[]> {
  const response = await apiClient.get('/runtime/update-history');
  return updateHistorySchema.parse(response.data);
}

export async function fetchApplianceHealth(): Promise<ApplianceHealth> {
  const response = await apiClient.get('/runtime/appliance-health');
  return applianceHealthSchema.parse(response.data);
}
