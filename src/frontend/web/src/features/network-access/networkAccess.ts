import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '@/lib/apiClient';

import type {
  NetworkAccessBrakeDto,
  NetworkRequestsResponseDto,
  NetworkAccessRuleDto,
  NetworkRulesResponseDto,
  RequestDecisionDto,
  RequestDecisionResponseDto,
  CreateOrUpdateRuleDto,
  ProviderNetworkTarget,
  NetworkActivityResponseDto,
  UserNetworkPrivilegesDto,
  UpdateUserNetworkPrivilegesDto,
  UpdateUserBrakeDto,
  UpdateSystemBrakeDto,
  ImageCompatProfileDto,
  ImageCompatResponseDto,
  CreateCompatProfileDto,
} from './networkAccessTypes';

export type * from './networkAccessTypes';

const DEFAULT_PROVIDER_URLS: Record<string, string> = {
  ollama: 'http://localhost:11434',
  openai: 'https://api.openai.com/v1',
  'openai-responses': 'https://api.openai.com/v1',
  anthropic: 'https://api.anthropic.com/v1',
  groq: 'https://api.groq.com/openai/v1',
  mistral: 'https://api.mistral.ai/v1',
  openrouter: 'https://openrouter.ai/api/v1',
  gemini: 'https://generativelanguage.googleapis.com/',
  google: 'https://generativelanguage.googleapis.com/',
};

function isIpv4Literal(host: string): boolean {
  const parts = host.split('.');
  if (parts.length !== 4) return false;
  return parts.every((p) => /^\d+$/.test(p) && Number(p) >= 0 && Number(p) <= 255);
}

function isLocalOrPrivateHost(host: string): boolean {
  const lower = host.toLowerCase();
  if (lower === 'localhost' || lower === '::1') return true;
  if (!isIpv4Literal(lower)) return false;
  const [a, b] = lower.split('.').map(Number);
  return a === 127 || a === 10 || (a === 192 && b === 168) || (a === 172 && b >= 16 && b <= 31);
}

export function resolveProviderNetworkTarget(
  providerType: string,
  baseUrl?: string | null,
  catalogDefaultBaseUrl?: string | null
): ProviderNetworkTarget | null {
  const raw = baseUrl?.trim() || catalogDefaultBaseUrl?.trim() || DEFAULT_PROVIDER_URLS[providerType.toLowerCase()];
  if (!raw) return null;

  try {
    const parsed = new URL(raw);
    const host = parsed.hostname.replace(/^\[|\]$/g, '');
    if (!host) return null;
    const port = parsed.port ? Number(parsed.port) : parsed.protocol === 'http:' ? 80 : 443;
    if (!Number.isInteger(port) || port < 1 || port > 65535) return null;
    return {
      host,
      port,
      destKind: isIpv4Literal(host) ? 'ip' : 'host',
      allowLan: isLocalOrPrivateHost(host),
    };
  } catch {
    return null;
  }
}

export async function checkProviderSystemwideRule(target: ProviderNetworkTarget): Promise<{
  alreadyAllowed: boolean;
  expectedRevision: number;
}> {
  const response = await apiClient.get<NetworkRulesResponseDto>('/network-access/rules');
  const expectedRevision = response.data.revision;
  const hostLower = target.host.toLowerCase();
  const alreadyAllowed = response.data.rules.some(
    (rule) =>
      rule.scopeKind === 'system' &&
      rule.allowed &&
      !rule.paused &&
      rule.destination.toLowerCase() === hostLower &&
      rule.port === target.port
  );
  return { alreadyAllowed, expectedRevision };
}

// ──────────────── Query Keys ────────────────
export const networkAccessKeys = {
  all: ['network-access'] as const,
  requests: (sinceRevision?: number) => ['network-access', 'requests', sinceRevision] as const,
  rules: (userId?: string) => ['network-access', 'rules', userId] as const,
  activity: ['network-access', 'activity'] as const,
  privileges: (userId: string) => ['network-access', 'privileges', userId] as const,
  brake: ['network-access', 'brake'] as const,
  compat: ['network-access', 'compat'] as const,
};

// ──────────────── Hooks ────────────────

/**
 * Polls network access requests, privileges and brake status every 3s while active.
 */
export function useNetworkRequests(sinceRevision?: number, enabled: boolean = true) {
  return useQuery({
    queryKey: networkAccessKeys.requests(sinceRevision),
    queryFn: async () => {
      const response = await apiClient.get<NetworkRequestsResponseDto>('/network-access/requests', {
        params: sinceRevision !== undefined ? { sinceRevision } : undefined,
      });
      return response.data;
    },
    enabled,
    refetchInterval: 3000,
    refetchIntervalInBackground: false,
  });
}

/**
 * Poll pending requests for the navigation badge and notifications.
 */
export function useNetworkPendingCount(enabled: boolean = true) {
  return useQuery({
    queryKey: networkAccessKeys.requests(),
    queryFn: async () => {
      const response = await apiClient.get<NetworkRequestsResponseDto>('/network-access/requests');
      return response.data;
    },
    select: (data: NetworkRequestsResponseDto) => {
      const pending = data?.requests?.filter((r) => r.status === 'pending') ?? [];
      return { count: pending.length, requests: pending, brake: data?.brake };
    },
    enabled,
    refetchInterval: 3000,
    refetchIntervalInBackground: false,
  });
}

/**
 * Queries rules list (optional userId filter) with 3s refetch interval.
 */
export function useNetworkRules(userId?: string, enabled: boolean = true) {
  return useQuery({
    queryKey: networkAccessKeys.rules(userId),
    queryFn: async () => {
      const response = await apiClient.get<NetworkRulesResponseDto>('/network-access/rules', {
        params: userId ? { userId } : undefined,
      });
      return response.data;
    },
    enabled,
    refetchInterval: 3000,
    refetchIntervalInBackground: false,
  });
}

/**
 * Queries connection activity audit records with 3s refetch interval.
 */
export function useNetworkActivity(enabled: boolean = true) {
  return useQuery({
    queryKey: networkAccessKeys.activity,
    queryFn: async () => {
      const response = await apiClient.get<NetworkActivityResponseDto>('/network-access/activity');
      return response.data;
    },
    enabled,
    refetchInterval: 3000,
    refetchIntervalInBackground: false,
  });
}

/**
 * Queries a user's network access privileges.
 */
export function useUserPrivileges(userId?: string) {
  return useQuery({
    queryKey: networkAccessKeys.privileges(userId ?? ''),
    queryFn: async () => {
      if (!userId) return null;
      const response = await apiClient.get<UserNetworkPrivilegesDto>(
        `/network-access/users/${encodeURIComponent(userId)}/privileges`
      );
      return response.data;
    },
    enabled: Boolean(userId),
  });
}

/**
 * Updates a user's network access privileges (admin only).
 */
export function useUpdateUserPrivileges() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({
      userId,
      privileges,
    }: {
      userId: string;
      privileges: UpdateUserNetworkPrivilegesDto;
    }) => {
      const response = await apiClient.put<UserNetworkPrivilegesDto>(
        `/network-access/users/${encodeURIComponent(userId)}/privileges`,
        privileges
      );
      return response.data;
    },
    onSuccess: (data) => {
      queryClient.setQueryData(networkAccessKeys.privileges(data.userId), data);
      void queryClient.invalidateQueries({ queryKey: networkAccessKeys.all });
    },
  });
}

/**
 * Queries current emergency brake status.
 */
export function useNetworkBrake(enabled: boolean = true) {
  return useQuery({
    queryKey: networkAccessKeys.brake,
    queryFn: async () => {
      const response = await apiClient.get<NetworkAccessBrakeDto>('/network-access/brake');
      return response.data;
    },
    enabled,
    refetchInterval: 3000,
    refetchIntervalInBackground: false,
  });
}

/**
 * Mutates user emergency brake: PUT /api/network-access/brake
 */
export function useUpdateUserBrake() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (payload: UpdateUserBrakeDto) => {
      const response = await apiClient.put<NetworkAccessBrakeDto>('/network-access/brake', payload);
      return response.data;
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: networkAccessKeys.all });
    },
  });
}

/**
 * Mutates system emergency brake (admin only): PUT /api/network-access/brake/system
 */
export function useUpdateSystemBrake() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (payload: UpdateSystemBrakeDto) => {
      const response = await apiClient.put<NetworkAccessBrakeDto>(
        '/network-access/brake/system',
        payload
      );
      return response.data;
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: networkAccessKeys.all });
    },
  });
}

/**
 * Queries compatibility mode pending requests and profiles (admin only).
 */
export function useImageCompat(enabled: boolean = true) {
  return useQuery({
    queryKey: networkAccessKeys.compat,
    queryFn: async () => {
      const response = await apiClient.get<ImageCompatResponseDto>('/network-access/compat');
      return response.data;
    },
    enabled,
    refetchInterval: 3000,
    refetchIntervalInBackground: false,
  });
}

/**
 * Submits a decision for a network access request: POST /api/network-access/requests/{id}/decision
 */
export function useDecideRequest() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({
      requestId,
      decision,
    }: {
      requestId: string;
      decision: RequestDecisionDto;
    }) => {
      const response = await apiClient.post<RequestDecisionResponseDto>(
        `/network-access/requests/${encodeURIComponent(requestId)}/decision`,
        decision
      );
      return response.data;
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: networkAccessKeys.all });
    },
  });
}

/**
 * Creates or updates a network rule: PUT /api/network-access/rules
 */
export function useCreateOrUpdateRule() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (payload: CreateOrUpdateRuleDto) => {
      const response = await apiClient.put<NetworkAccessRuleDto>(
        '/network-access/rules',
        payload
      );
      return response.data;
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: networkAccessKeys.all });
    },
  });
}

/**
 * Deletes a non-seed network rule: DELETE /api/network-access/rules/{id}?expectedRevision=
 */
export function useDeleteRule() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({
      ruleId,
      expectedRevision,
    }: {
      ruleId: string;
      expectedRevision: number;
    }) => {
      const response = await apiClient.delete(
        `/network-access/rules/${encodeURIComponent(ruleId)}`,
        {
          params: { expectedRevision },
        }
      );
      return response.data;
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: networkAccessKeys.all });
    },
  });
}

/**
 * Applies compatibility mode for an image digest (admin only): POST /api/network-access/compat/{imageDigest}
 */
export function useApplyCompatMode() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({
      imageDigest,
      profile,
    }: {
      imageDigest: string;
      profile: CreateCompatProfileDto;
    }) => {
      const response = await apiClient.post<ImageCompatProfileDto>(
        `/network-access/compat/${encodeURIComponent(imageDigest)}`,
        profile
      );
      return response.data;
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: networkAccessKeys.compat });
      void queryClient.invalidateQueries({ queryKey: networkAccessKeys.requests() });
    },
  });
}

/**
 * Removes compatibility mode for an image digest (admin only): DELETE /api/network-access/compat/{imageDigest}
 */
export function useRemoveCompatMode() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (imageDigest: string) => {
      const response = await apiClient.delete(
        `/network-access/compat/${encodeURIComponent(imageDigest)}`
      );
      return response.data;
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: networkAccessKeys.compat });
      void queryClient.invalidateQueries({ queryKey: networkAccessKeys.requests() });
    },
  });
}
