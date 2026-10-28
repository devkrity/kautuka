export const skillsQueryKeys = {
  all: ['skills'] as const,
  list: () => ['skills'] as const,
  detail: (skillId: string) => ['skills', skillId] as const,
  userEnvironmentKeys: () => ['skills', 'user-environment-keys'] as const,
};
