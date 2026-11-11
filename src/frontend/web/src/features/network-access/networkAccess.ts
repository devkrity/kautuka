import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '@/lib/apiClient';

export interface NetworkAccessRequestDto {
  id: string;
  userId?: string;
  userDisplayName?: string;
  userPronoun?: string;
  agentId?: string;
  agentDisplayName?: string;
  connectorId?: string;
  connectorDisplayName?: string;
  systemwide: boolean;
  attribution: 'user-agent' | 'user' | 'none';
  destination: string;
  destKind: 'host' | 'ip';
  port: number;
  actorKind: 'agent' | 'connector' | 'builder' | 'first-party';
  firstSeenUtc: string;
  lastSeenUtc: string;
  attempts: number;
  status: 'pending' | 'allowed' | 'blocked';
  origin: 'container' | 'gateway' | 'channelhost';
  purpose?: string;
  compatibilityMode?: boolean;
  waitingForAdmin?: boolean;
}

export interface UserPrivilegesSummaryDto {
  canApproveOwn: boolean;
  canAllowAllAgents: boolean;
}

export interface NetworkAccessBrakeDto {
  userPaused: boolean;
  userPausedBy?: string;
  userLockedByAdmin: boolean;
  systemPaused: boolean;
  systemPausedBy?: string;
}

export interface NetworkRequestsResponseDto {
  revision: number;
  requests: NetworkAccessRequestDto[];
  privileges: UserPrivilegesSummaryDto;
  brake: NetworkAccessBrakeDto;
}

export interface NetworkAccessRuleDto {
  id: string;
  scopeKind: 'agent' | 'user' | 'connector-system';
  userId?: string;
  userDisplayName?: string;
  agentId?: string;
  agentDisplayName?: string;
  connectorId?: string;
  connectorDisplayName?: string;
  destination: string;
  destKind: 'host' | 'ip' | 'any-public';
  port: number;
  allowed: boolean;
  allowLan: boolean;
  source: 'user' | 'seed';
  paused: boolean;
  summaryText: string;
  decidedBy: string;
  decidedAtUtc: string;
  openInternetBadge: boolean;
}

export interface NetworkRulesResponseDto {
  revision: number;
  rules: NetworkAccessRuleDto[];
}

export interface RequestDecisionDto {
  allow: boolean;
  scope?: 'agent' | 'user' | 'connector-system';
  allowLan?: boolean;
  expectedRevision: number;
}

export interface RequestDecisionResponseDto {
  revision: number;
  rule?: NetworkAccessRuleDto;
  request?: NetworkAccessRequestDto;
}

export interface CreateOrUpdateRuleDto {
  scopeKind: 'agent' | 'user' | 'connector-system';
  userId?: string;
  agentId?: string;
  connectorId?: string;
  destination: string;
  destKind: 'host' | 'ip' | 'any-public';
  port: number;
  allowed: boolean;
  allowLan?: boolean;
  expectedRevision: number;
}

export interface NetworkAccessActivityDto {
  timestampUtc: string;
  userId?: string;
  userDisplayName?: string;
  agentId?: string;
  agentDisplayName?: string;
  connectorId?: string;
  connectorDisplayName?: string;
  destination: string;
  port: number;
  decision: 'allow' | 'deny';
  reason: string;
  openInternetBadge: boolean;
  compatibilityMode: boolean;
}

export interface NetworkActivityResponseDto {
  entries: NetworkAccessActivityDto[];
}

export interface UserNetworkPrivilegesDto {
  userId: string;
  canApproveOwn: boolean;
  canAllowAllAgents: boolean;
  setBy?: string;
  setAtUtc?: string;
}

export interface UpdateUserNetworkPrivilegesDto {
  canApproveOwn: boolean;
  canAllowAllAgents: boolean;
}

export interface UpdateUserBrakeDto {
  userId?: string;
  on: boolean;
}

export interface UpdateSystemBrakeDto {
  on: boolean;
}

export interface ImageCompatRequestDto {
  imageDigest: string;
  connectorId?: string;
  connectorDisplayName?: string;
  firstSeenUtc: string;
  reason?: string;
}

export interface ImageCompatProfileDto {
  imageDigest: string;
  connectorId?: string;
  connectorDisplayName?: string;
  relaxations: ('writable-rootfs' | 'root-user' | 'file-ownership-caps')[];
  grantedBy: string;
  grantedAtUtc: string;
  reason?: string;
}

export interface ImageCompatResponseDto {
  pendingRequests: ImageCompatRequestDto[];
  profiles: ImageCompatProfileDto[];
}

export interface CreateCompatProfileDto {
  connectorId?: string;
  connectorDisplayName?: string;
  relaxations: ('writable-rootfs' | 'root-user' | 'file-ownership-caps')[];
  reason?: string;
}

// ──────────────── Query Keys ────────────────
export const networkAccessKeys = {
  all: ['network-access'] as const,
  requests: (sinceRevision?: number) => ['network-access', 'requests', sinceRevision] as const,
  pendingCount: ['network-access', 'pending-count'] as const,
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
 * Lightweight hook to poll pending request count for the navigation badge every 15s.
 */
export function useNetworkPendingCount(enabled: boolean = true) {
  return useQuery({
    queryKey: networkAccessKeys.pendingCount,
    queryFn: async () => {
      const response = await apiClient.get<NetworkRequestsResponseDto>('/network-access/requests');
      const pending = response.data?.requests?.filter((r) => r.status === 'pending') ?? [];
      return {
        count: pending.length,
        requests: pending,
        brake: response.data?.brake,
      };
    },
    enabled,
    refetchInterval: 15000,
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
