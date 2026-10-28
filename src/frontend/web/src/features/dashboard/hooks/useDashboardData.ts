import { useMemo } from 'react';
import { useAgents, useModels } from '@/features/settings';
import { useRecentSessions } from '@/features/dashboard';
import { parseApiTimestamp } from '@/lib/utils';

export interface DashboardRecentTarget {
  id: string;
  name: string;
  type: 'agent';
  lastUsedAt: string;
  sessions: number;
}

export interface DashboardRecentSession {
  id: string;
  title: string;
  updatedAt: string;
  messages: number;
  targetType: 'agent';
  targetId: string;
  targetName: string;
}

export const fmtRelative = (iso: string): string => {
  const then = parseApiTimestamp(iso).getTime();
  const now = Date.now();
  const diff = Math.max(0, now - then);
  const min = Math.floor(diff / 60000);
  if (min < 1) return 'just now';
  if (min < 60) return `${min}m ago`;
  const hr = Math.floor(min / 60);
  if (hr < 24) return `${hr}h ago`;
  const day = Math.floor(hr / 24);
  if (day < 7) return `${day}d ago`;
  return parseApiTimestamp(iso).toLocaleDateString();
};

export function useDashboardData() {
  const agentsQuery = useAgents();
  const modelsQuery = useModels();
  const sessionsQuery = useRecentSessions(30);

  const agents = agentsQuery.data ?? [];
  const models = modelsQuery.data ?? [];
  const sessionsRaw = sessionsQuery.data ?? [];

  const loading = agentsQuery.isLoading || modelsQuery.isLoading || sessionsQuery.isLoading;

  const { recentTargets, recentSessions } = useMemo(() => {
    const agentNameById = new Map(agents.map((a) => [a.id, a.name]));

    const sortedSessions = [...sessionsRaw].sort(
      (a, b) =>
        parseApiTimestamp(b.lastMessageAt ?? b.createdAt).getTime() -
        parseApiTimestamp(a.lastMessageAt ?? a.createdAt).getTime()
    );

    const sessions: DashboardRecentSession[] = sortedSessions
      .filter((s) => Boolean(s.agentId))
      .map(
        (s): DashboardRecentSession => ({
          id: s.id,
          title: s.id,
          updatedAt: s.lastMessageAt ?? s.createdAt,
          messages: s.messageCount ?? 0,
          targetType: 'agent',
          targetId: s.agentId ?? '',
          targetName: agentNameById.get(s.agentId ?? '') ?? s.agentName ?? s.agentId ?? '',
        })
      )
      .slice(0, 8);

    const sessionsByTarget = new Map<string, typeof sessionsRaw>();
    for (const s of sessionsRaw) {
      if (!s.agentId) {
        continue;
      }

      const key = `agent:${s.agentId}`;
      if (!sessionsByTarget.has(key)) sessionsByTarget.set(key, []);
      sessionsByTarget.get(key)!.push(s);
    }

    const targetAgg = new Map<string, DashboardRecentTarget>();
    for (const [key, targetSessions] of sessionsByTarget) {
      const first = targetSessions[0]!;
      const targetId = first.agentId ?? '';
      const resolvedName = agentNameById.get(targetId) ?? first.agentName ?? targetId;

      const maxLastUsedAt = targetSessions.reduce((max, s) => {
        const t = parseApiTimestamp(s.lastMessageAt ?? s.createdAt).getTime();
        return t > parseApiTimestamp(max).getTime() ? (s.lastMessageAt ?? s.createdAt) : max;
      }, first.lastMessageAt ?? first.createdAt);

      targetAgg.set(key, {
        id: targetId,
        name: resolvedName,
        type: 'agent',
        sessions: targetSessions.length,
        lastUsedAt: maxLastUsedAt,
      });
    }

    const recentTargets = [...targetAgg.values()]
      .sort(
        (a, b) =>
          parseApiTimestamp(b.lastUsedAt).getTime() - parseApiTimestamp(a.lastUsedAt).getTime()
      )
      .slice(0, 6);

    return { recentTargets, recentSessions: sessions };
  }, [agents, sessionsRaw]);

  return {
    agents,
    models,
    loading,
    recentTargets,
    recentSessions,
  };
}
