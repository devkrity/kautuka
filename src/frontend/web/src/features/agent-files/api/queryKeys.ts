export const agentFilesQueryKeys = {
  all: ['agentFiles'] as const,
  agent: (agentId: string) => [...agentFilesQueryKeys.all, agentId] as const,
  directory: (agentId: string, path: string) =>
    [...agentFilesQueryKeys.agent(agentId), 'directory', path] as const,
  search: (agentId: string, query: string, limit: number) =>
    [...agentFilesQueryKeys.agent(agentId), 'search', query, limit] as const,
};