/**
 * Memories feature query key factory
 */

export const memoriesQueryKeys = {
  all: ['memories'] as const,
  forAgent: (agentId: string) => [...memoriesQueryKeys.all, 'agent', agentId] as const,
  fact: (factId: string) => [...memoriesQueryKeys.all, factId] as const,
};
