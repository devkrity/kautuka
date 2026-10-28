/**
 * Chat feature TanStack Query hooks
 */
import { useQuery, useMutation } from '@tanstack/react-query';
import { queryClient } from '@/lib/queryClient';
import {
  fetchSession,
  deleteSession,
  compactSession,
  listConversationAssets,
  listRecentSessions,
  listSessionsForAgent,
} from './sessions';
import { chatQueryKeys } from './queryKeys';

export function useSession(sessionId: string | null) {
  return useQuery({
    queryKey: sessionId ? chatQueryKeys.session(sessionId) : ['chat', 'no-session'],
    queryFn: () =>
      sessionId ? fetchSession(sessionId) : Promise.reject(new Error('No session ID')),
    enabled: !!sessionId,
  });
}

export function useDeleteSession() {
  return useMutation({
    mutationFn: (sessionId: string) => deleteSession(sessionId),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: chatQueryKeys.sessions() });
    },
  });
}

export function useCompactSession() {
  return useMutation({
    mutationFn: (sessionId: string) => compactSession(sessionId),
    onSuccess: (session, sessionId) => {
      queryClient.setQueryData(chatQueryKeys.session(sessionId), session);
      void queryClient.invalidateQueries({ queryKey: chatQueryKeys.session(sessionId) });
      void queryClient.invalidateQueries({ queryKey: chatQueryKeys.sessions() });
    },
  });
}

export function useRecentSessions(limit = 20) {
  return useQuery({
    queryKey: chatQueryKeys.sessions(),
    queryFn: () => listRecentSessions(limit),
  });
}

export function useSessionAssets(sessionId: string | null, assetIds?: readonly string[]) {
  const normalizedIds = assetIds ? [...assetIds].sort() : undefined;
  return useQuery({
    queryKey: sessionId
      ? chatQueryKeys.sessionAssets(sessionId, normalizedIds)
      : ['chat', 'no-session-assets'],
    queryFn: () =>
      sessionId
        ? listConversationAssets(sessionId, normalizedIds)
        : Promise.reject(new Error('No session ID')),
    enabled: Boolean(sessionId) && (normalizedIds === undefined || normalizedIds.length > 0),
  });
}

export function useSessionsForAgent(agentId: string | null) {
  return useQuery({
    queryKey: agentId ? [...chatQueryKeys.all, 'agent', agentId] : ['chat', 'no-agent'],
    queryFn: () =>
      agentId ? listSessionsForAgent(agentId) : Promise.reject(new Error('No agent ID')),
    enabled: !!agentId,
  });
}
