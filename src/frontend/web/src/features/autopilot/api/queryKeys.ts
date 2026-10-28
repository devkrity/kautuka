export const autopilotQueryKeys = {
  schedules: () => ['autopilot', 'schedules'] as const,
  runs: (scheduleId?: string) => ['autopilot', 'runs', scheduleId ?? 'all'] as const,
  inbox: () => ['autopilot', 'inbox'] as const,
};
