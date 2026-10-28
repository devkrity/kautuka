/**
 * Settings feature TanStack Query hooks
 */
import { useQuery } from '@tanstack/react-query';
import { fetchAgentToolPolicyProjection, fetchAgents, fetchModels, fetchProviders } from './settings';
import { settingsAdmin } from './settingsAdmin';
import { settingsQueryKeys } from './queryKeys';

/** Returns the full agent list. 30 s stale time — entity lists rarely change mid-session. */
export function useAgents() {
  return useQuery({
    queryKey: settingsQueryKeys.agents(),
    queryFn: fetchAgents,
    staleTime: 30_000,
  });
}

/** Returns the full model list. */
export function useModels() {
  return useQuery({
    queryKey: settingsQueryKeys.models(),
    queryFn: fetchModels,
    staleTime: 30_000,
  });
}

/** Returns the full provider list. */
export function useProviders() {
  return useQuery({
    queryKey: settingsQueryKeys.providers(),
    queryFn: fetchProviders,
    staleTime: 30_000,
  });
}

export function useAgentToolPolicy(agentId: string | null) {
  return useQuery({
    queryKey: settingsQueryKeys.agentToolPolicy(agentId ?? 'none'),
    queryFn: async () => {
      if (!agentId) {
        return null;
      }

      return fetchAgentToolPolicyProjection(agentId);
    },
    enabled: Boolean(agentId),
    staleTime: 30_000,
  });
}

export function useAgentRevisions(agentId: string | null) {
  return useQuery({
    queryKey: settingsQueryKeys.agentRevisions(agentId ?? 'none'),
    queryFn: async () => {
      if (!agentId) {
        return null;
      }

      const response = await settingsAdmin.listAgentRevisions(agentId);
      return response.data;
    },
    enabled: Boolean(agentId),
    staleTime: 15_000,
  });
}

export function useAgentRevisionDiff(
  agentId: string | null,
  revision: number | null,
  from?: number | null
) {
  return useQuery({
    queryKey: settingsQueryKeys.agentRevisionDiff(agentId ?? 'none', revision ?? 0, from),
    queryFn: async () => {
      if (!agentId || revision == null) {
        return null;
      }

      const response = await settingsAdmin.getAgentRevisionDiff(agentId, revision, from);
      return response.data;
    },
    enabled: Boolean(agentId) && revision != null,
    staleTime: 30_000,
  });
}
