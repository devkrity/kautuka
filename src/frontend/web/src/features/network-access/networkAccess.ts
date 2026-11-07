import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { z } from 'zod';
import { apiClient } from '@/lib/apiClient';

const identitySchema = z.object({
  scopeType: z.string(),
  userId: z.string(),
  agentId: z.string(),
  role: z.string(),
  componentId: z.string(),
});
const ruleSchema = z.object({
  id: z.string(),
  identity: identitySchema,
  destination: z.string(),
  port: z.number().int(),
  allowed: z.boolean(),
  updatedAtUtc: z.string().nullable(),
});
export const networkAccessSchema = z.object({
  enabled: z.boolean(),
  revision: z.number().int(),
  activeRevision: z.number().int(),
  activeVersion: z.string().nullable(),
  activationState: z.string(),
  lastError: z.string().nullable(),
  rules: z.array(ruleSchema),
  defaults: z.array(ruleSchema),
  nextOffset: z.number().int().default(0),
  ruleCount: z.number().int().default(0),
  requestCount: z.number().int().default(0),
  sourceCount: z.number().int().default(0),
  defaultCount: z.number().int().default(0),
  pendingRequestCount: z.number().int().default(0),
  requestCapacityReached: z.boolean().default(false),
  sources: z.array(identitySchema),
  requests: z.array(
    z.object({
      id: z.string(),
      identity: identitySchema,
      destination: z.string(),
      port: z.number().int(),
      createdAtUtc: z.string(),
      lastSeenAtUtc: z.string(),
      attempts: z.number().int(),
      status: z.enum(['pending', 'allowed', 'blocked']),
    })
  ),
});
export type NetworkIdentity = z.infer<typeof identitySchema>;
export type NetworkAccessView = z.input<typeof networkAccessSchema>;
export type NetworkRule = z.infer<typeof ruleSchema>;
const auditSchema = z.object({
  events: z.array(
    z.object({
      sequence: z.number().int(),
      id: z.string(),
      startedAtUtc: z.string(),
      observedAtUtc: z.string(),
      identity: identitySchema.nullable(),
      containerId: z.string().nullable(),
      sourceAddress: z.string(),
      destination: z.string(),
      port: z.number().int(),
      outcome: z.string(),
      connected: z.boolean(),
      closed: z.boolean(),
      bytesSent: z.number(),
      bytesReceived: z.number(),
      durationMilliseconds: z.number(),
    })
  ),
  nextBefore: z.number().int(),
  coverageGaps: z.number().int(),
  unattributedConnections: z.number().int(),
  lastError: z.string().nullable(),
});

export function useNetworkAudit() {
  return useInfiniteQuery({
    queryKey: ['network-access-audit'],
    initialPageParam: 0,
    queryFn: async ({ pageParam }) =>
      auditSchema.parse(
        (
          await apiClient.get<unknown>('/network-access/audit', {
            params: { before: pageParam, limit: 50 },
          })
        ).data
      ),
    getNextPageParam: (page) => (page.nextBefore > 0 ? page.nextBefore : undefined),
    refetchInterval: 3000,
    refetchIntervalInBackground: false,
  });
}
type Change =
  | { kind: 'decision'; requestId: string; allowed: boolean; expectedRevision: number }
  | {
      kind: 'rule';
      identity: NetworkIdentity;
      destination: string;
      port: number;
      allowed: boolean;
      expectedRevision: number;
    };
const queryKey = ['network-access'] as const;

export function useNetworkAccessSummary(enabled: boolean) {
  return useQuery({
    queryKey: ['network-access-summary'],
    enabled,
    queryFn: async () =>
      networkAccessSchema.parse(
        (await apiClient.get<unknown>('/network-access', { params: { summary: true } })).data
      ),
    refetchInterval: 3000,
    refetchIntervalInBackground: false,
  });
}

type PolicyPage = z.output<typeof networkAccessSchema>;
const unique = <T>(items: T[], key: (item: T) => string): T[] => {
  const seen = new Set<string>();
  return items.filter((item) => {
    const id = key(item);
    if (seen.has(id)) return false;
    seen.add(id);
    return true;
  });
};

export function useNetworkAccess() {
  return useInfiniteQuery({
    queryKey,
    initialPageParam: { offset: 0, revision: undefined as number | undefined },
    queryFn: async ({ pageParam }) =>
      networkAccessSchema.parse(
        (await apiClient.get<unknown>('/network-access', { params: pageParam })).data
      ),
    getNextPageParam: (page) =>
      page.nextOffset > 0 ? { offset: page.nextOffset, revision: page.revision } : undefined,
    select: (data): PolicyPage => ({
      ...data.pages[0]!,
      rules: unique(
        data.pages.flatMap((page) => page.rules),
        (item) => item.id
      ),
      requests: unique(
        data.pages.flatMap((page) => page.requests),
        (item) => item.id
      ),
      sources: unique(
        data.pages.flatMap((page) => page.sources),
        identityKey
      ),
      defaults: unique(
        data.pages.flatMap((page) => page.defaults),
        (item) => `${identityKey(item.identity)}:${item.destination}:${item.port}`
      ),
    }),
    refetchInterval: 3000,
    refetchIntervalInBackground: false,
  });
}

export function useNetworkAccessChange() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: async (change: Change) => {
      const response =
        change.kind === 'decision'
          ? await apiClient.post<unknown>(
              `/network-access/requests/${encodeURIComponent(change.requestId)}/decision`,
              {
                allowed: change.allowed,
                expectedRevision: change.expectedRevision,
              }
            )
          : await apiClient.put<unknown>('/network-access/rules', {
              identity: change.identity,
              destination: change.destination,
              port: change.port,
              allowed: change.allowed,
              expectedRevision: change.expectedRevision,
            });
      return networkAccessSchema.parse(response.data);
    },
    onSuccess: (data) => {
      client.setQueryData(queryKey, {
        pages: [data],
        pageParams: [{ offset: 0, revision: undefined }],
      });
      void client.invalidateQueries({ queryKey: ['network-access-summary'] });
    },
    onError: () => {
      void client.invalidateQueries({ queryKey });
    },
  });
}

export const identityKey = (identity: NetworkIdentity): string =>
  JSON.stringify([
    identity.scopeType,
    identity.userId,
    identity.agentId,
    identity.role,
    identity.componentId,
  ]);
export const identityLabel = (identity: NetworkIdentity): string =>
  `${identity.role}: ${identity.componentId}${identity.agentId ? ` · agent ${identity.agentId}` : ''}`;
