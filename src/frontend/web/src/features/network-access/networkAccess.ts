import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
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
export type NetworkAccessView = z.infer<typeof networkAccessSchema>;
export type NetworkRule = z.infer<typeof ruleSchema>;
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

export function useNetworkAccess() {
  return useQuery({
    queryKey,
    queryFn: async () =>
      networkAccessSchema.parse((await apiClient.get<unknown>('/network-access')).data),
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
    onSuccess: (data) => client.setQueryData(queryKey, data),
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
