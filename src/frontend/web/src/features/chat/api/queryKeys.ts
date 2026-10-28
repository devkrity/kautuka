/**
 * Chat feature query key factory
 */

export const chatQueryKeys = {
  all: ['chat'] as const,
  sessions: () => [...chatQueryKeys.all, 'sessions'] as const,
  session: (sessionId: string) => [...chatQueryKeys.sessions(), sessionId] as const,
  sessionMessages: (sessionId: string) =>
    [...chatQueryKeys.session(sessionId), 'messages'] as const,
  sessionAssets: (sessionId: string, assetIds?: readonly string[]) =>
    [...chatQueryKeys.session(sessionId), 'assets', ...(assetIds ?? [])] as const,
};
