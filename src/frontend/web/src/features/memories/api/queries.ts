/**
 * Memories feature TanStack Query hooks
 */
import { useQuery, useMutation } from '@tanstack/react-query';
import { queryClient } from '@/lib/queryClient';
import { fetchMemoriesForAgent, deleteFact, updateFact } from './memories';
import { memoriesQueryKeys } from './queryKeys';

export function useMemoriesForAgent(agentId: string | null) {
  return useQuery({
    queryKey: agentId ? memoriesQueryKeys.forAgent(agentId) : ['memories', 'no-agent'],
    queryFn: () =>
      agentId ? fetchMemoriesForAgent(agentId) : Promise.reject(new Error('No agent ID')),
    enabled: !!agentId,
  });
}

export function useDeleteFact() {
  return useMutation({
    mutationFn: (factId: string) => deleteFact(factId),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: memoriesQueryKeys.all });
    },
  });
}

export function useUpdateFact() {
  return useMutation({
    mutationFn: ({
      factId,
      patch,
    }: {
      factId: string;
      patch: { title: string; content: string; category: string };
    }) => updateFact(factId, patch),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: memoriesQueryKeys.all });
    },
  });
}
