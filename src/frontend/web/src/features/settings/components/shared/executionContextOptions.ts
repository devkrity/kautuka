export const executionContextOptions = [
  { value: 'OwnContext', label: 'Isolated' },
  { value: 'OwnContextWithAgentFiles', label: 'Isolated with access to Agent Files' },
  { value: 'AgentContext', label: 'Within Agent Sandbox' },
] as const;
