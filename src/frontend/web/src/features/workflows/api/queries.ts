import { useMutation, useQuery } from '@tanstack/react-query';
import { queryClient } from '@/lib/queryClient';
import { workflowQueryKeys } from './queryKeys';
import {
  createWorkflow,
  archiveWorkflow,
  deleteWorkflow,
  getWorkflow,
  getWorkflowInstance,
  listWorkflowInstances,
  listWorkflowJournal,
  listWorkflows,
  startWorkflowInstance,
  stopWorkflowInstance,
  restoreWorkflow,
  updateWorkflow,
  type SaveWorkflowRequest,
} from './workflows';

export function useWorkflows(archived = false) {
  return useQuery({
    queryKey: workflowQueryKeys.definitions(archived),
    queryFn: () => listWorkflows(archived),
  });
}

function useWorkflowLifecycleMutation(mutationFn: (workflowId: string) => Promise<void>) {
  return useMutation({
    mutationFn,
    retry: false,
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: workflowQueryKeys.definitions() });
      queryClient.invalidateQueries({ queryKey: workflowQueryKeys.definitions(true) });
    },
  });
}

export const useArchiveWorkflow = () => useWorkflowLifecycleMutation(archiveWorkflow);
export const useRestoreWorkflow = () => useWorkflowLifecycleMutation(restoreWorkflow);
export const useDeleteWorkflow = () => useWorkflowLifecycleMutation(deleteWorkflow);

export function useWorkflow(workflowId: string | undefined) {
  return useQuery({
    queryKey: workflowQueryKeys.definition(workflowId ?? 'none'),
    queryFn: () => getWorkflow(workflowId!),
    enabled: Boolean(workflowId),
  });
}

export function useWorkflowInstances(workflowId?: string) {
  return useQuery({
    queryKey: workflowQueryKeys.instances(workflowId),
    queryFn: () => listWorkflowInstances(workflowId),
    staleTime: 0,
  });
}

export function useWorkflowInstance(
  workflowId: string | undefined,
  instanceId: string | undefined
) {
  return useQuery({
    queryKey: workflowQueryKeys.instance(workflowId ?? 'none', instanceId ?? 'none'),
    queryFn: () => getWorkflowInstance(workflowId!, instanceId!),
    enabled: Boolean(workflowId && instanceId),
    staleTime: 0,
  });
}

export function useWorkflowJournal(workflowId: string | undefined, instanceId: string | undefined) {
  return useQuery({
    queryKey: workflowQueryKeys.journal(workflowId ?? 'none', instanceId ?? 'none'),
    queryFn: () => listWorkflowJournal(workflowId!, instanceId!),
    enabled: Boolean(workflowId && instanceId),
    staleTime: 0,
  });
}

export function useCreateWorkflow() {
  return useMutation({
    mutationFn: (request: SaveWorkflowRequest) => createWorkflow(request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: workflowQueryKeys.definitions() });
    },
  });
}

export function useUpdateWorkflow(workflowId: string) {
  return useMutation({
    mutationFn: (request: SaveWorkflowRequest) => updateWorkflow(workflowId, request),
    onSuccess: (saved) => {
      queryClient.setQueryData(workflowQueryKeys.definition(workflowId), saved);
      queryClient.invalidateQueries({ queryKey: workflowQueryKeys.definitions() });
      queryClient.invalidateQueries({ queryKey: workflowQueryKeys.definition(workflowId) });
    },
  });
}

export function useStartWorkflowInstance(workflowId: string) {
  return useMutation({
    mutationFn: ({
      input,
      correlationKey,
      files,
    }: {
      input: Record<string, unknown>;
      correlationKey?: string;
      files?: string[];
    }) => startWorkflowInstance(workflowId, input, correlationKey, files),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: workflowQueryKeys.instanceLists() });
      queryClient.invalidateQueries({ queryKey: workflowQueryKeys.definitions() });
      queryClient.invalidateQueries({ queryKey: workflowQueryKeys.definitions(true) });
    },
  });
}

export function useStopWorkflowInstance(workflowId: string, instanceId: string) {
  return useMutation({
    mutationFn: () => stopWorkflowInstance(workflowId, instanceId),
    onSuccess: () => {
      queryClient.invalidateQueries({
        queryKey: workflowQueryKeys.instance(workflowId, instanceId),
      });
      queryClient.invalidateQueries({
        queryKey: workflowQueryKeys.journal(workflowId, instanceId),
      });
      queryClient.invalidateQueries({ queryKey: workflowQueryKeys.instanceLists() });
    },
  });
}
