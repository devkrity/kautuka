export interface NetworkAccessRequestDto {
  id: string;
  userId?: string;
  userDisplayName?: string;
  userPronoun?: string;
  agentId?: string;
  agentDisplayName?: string;
  connectorId?: string;
  connectorDisplayName?: string;
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
  ownerCanApproveOwn?: boolean;
  baseDomain?: string | null;
}

export interface UserPrivilegesSummaryDto {
  canApproveOwn: boolean;
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
  scopeKind: 'agent' | 'user' | 'system';
  userId?: string;
  userDisplayName?: string;
  agentId?: string;
  agentDisplayName?: string;
  connectorId?: string;
  connectorDisplayName?: string;
  destination: string;
  destKind: 'host' | 'ip' | 'domain' | 'any-public';
  port: number;
  allowed: boolean;
  allowLan: boolean;
  source: 'user' | 'seed';
  paused: boolean;
  summaryText: string;
  decidedBy: string;
  decidedAtUtc: string;
  openInternetBadge: boolean;
  protectedFromUser?: boolean;
}

export interface NetworkRulesResponseDto {
  revision: number;
  rules: NetworkAccessRuleDto[];
}

export interface RequestDecisionDto {
  allow: boolean;
  scope?: 'agent' | 'user' | 'connector' | 'system';
  allowLan?: boolean;
  expectedRevision: number;
  includeSubdomains?: boolean;
}

export interface RequestDecisionResponseDto {
  revision: number;
  rule?: NetworkAccessRuleDto;
  request?: NetworkAccessRequestDto;
}

export interface CreateOrUpdateRuleDto {
  scopeKind: 'agent' | 'user' | 'system';
  userId?: string;
  agentId?: string;
  connectorId?: string;
  destination: string;
  destKind: 'host' | 'ip' | 'domain' | 'any-public';
  port: number;
  allowed: boolean;
  allowLan?: boolean;
  expectedRevision: number;
  replaceRuleId?: string;
}

export interface ProviderNetworkTarget {
  host: string;
  port: number;
  destKind: 'host' | 'ip';
  allowLan: boolean;
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
  setBy?: string;
  setAtUtc?: string;
}

export interface UpdateUserNetworkPrivilegesDto {
  canApproveOwn: boolean;
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

