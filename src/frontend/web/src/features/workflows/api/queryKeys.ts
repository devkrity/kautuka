export const workflowQueryKeys = {
  all: ['workflows'] as const,
  definitions: (archived = false) => ['workflows', 'definitions', archived] as const,
  definition: (workflowId: string) => ['workflows', 'definitions', workflowId] as const,
  instanceLists: () => ['workflows', 'instances'] as const,
  instances: (workflowId?: string) => ['workflows', 'instances', workflowId ?? 'all'] as const,
  instance: (workflowId: string, instanceId: string) =>
    ['workflows', 'instances', workflowId, instanceId] as const,
  journal: (workflowId: string, instanceId: string) =>
    ['workflows', 'journal', workflowId, instanceId] as const,
};
