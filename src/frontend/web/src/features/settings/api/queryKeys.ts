/**
 * Settings feature query key factory
 */
export const settingsQueryKeys = {
  all: ['settings'] as const,
  agents: () => ['settings', 'agents'] as const,
  agentToolPolicy: (agentId: string) => ['settings', 'agents', agentId, 'tool-policy'] as const,
  agentRevisions: (agentId: string) => ['settings', 'agents', agentId, 'revisions'] as const,
  agentRevisionDiff: (agentId: string, revision: number, from?: number | null) =>
    ['settings', 'agents', agentId, 'revisions', revision, 'diff', from ?? 'previous'] as const,
  models: () => ['settings', 'models'] as const,
  providers: () => ['settings', 'providers'] as const,
  providerTypes: () => ['settings', 'provider-types'] as const,
  packageCacheStats: () => ['settings', 'package-cache-stats'] as const,
};
