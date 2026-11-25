import { useQuery } from '@tanstack/react-query';
import { fetchProviders, settingsAdmin } from '@/features/settings';
import { apiClient } from '@/lib/apiClient';
import { readApiBoolean, readApiString, readApiStringArray } from '@/lib/apiResponse';

type OnboardingHealthStatus = {
  lifecycleStatus: string;
  coreReady: boolean;
  agentsPending: boolean;
  agentsReady: boolean;
  actionHints: string[];
};

export function useSettingsStatus(enabled: boolean) {
  const query = useQuery({
    queryKey: ['system-setup-status'],
    queryFn: async () => {
      const [providers, agents, systemHealthResponse] = await Promise.all([
        fetchProviders(),
        settingsAdmin.listAgents(),
        apiClient.get('/runtime/system-health').catch(() => null),
      ]);

      const lifecycleSource =
        systemHealthResponse?.data && typeof systemHealthResponse.data === 'object'
          ? (Object.entries(systemHealthResponse.data as Record<string, unknown>).find(
              ([entryKey]) => entryKey === 'lifecycle'
            )?.[1] ?? null)
          : null;

      const health: OnboardingHealthStatus = {
        lifecycleStatus: readApiString(lifecycleSource, 'status') ?? 'core_not_ready',
        coreReady: readApiBoolean(lifecycleSource, 'coreReady') ?? false,
        agentsPending: readApiBoolean(lifecycleSource, 'agentsPending') ?? false,
        agentsReady: readApiBoolean(lifecycleSource, 'agentsReady') ?? false,
        actionHints: readApiStringArray(systemHealthResponse?.data, 'actionHints') ?? [],
      };

      return {
        providersCount: providers.length,
        hasProviders: providers.length > 0,
        hasConfiguredHelper: agents.data.agents.some((agent) => agent.modelId.trim().length > 0),
        health,
      };
    },
    enabled,
    staleTime: 300_000,
  });
  return { ...query, isFetching: query.isFetching };
}
