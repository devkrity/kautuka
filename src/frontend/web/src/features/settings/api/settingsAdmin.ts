/**
 * Settings admin API — wraps all /settings/* and /credentials/* endpoints.
 * Pages that manage configuration (SettingsPage) should import from here.
 * Strongly typed admin Settings API facade.
 */
import { apiClient } from '@/lib/apiClient';
import type {
  AdvancedSettingsEntry,
    AgentPersonaProfile,
    AgentSettingsEntry,
    KautukaInfrastructureSettingsEntry,
    DiscoveredModelEntry,
    MemorySettingsEntry,
    ModelEntry,
    OrganizationSettingsEntry,
    PersonaDefinitions,
    AgentToolPolicyProjection,
    ProviderEntry,
    ProviderPathPolicySettingsEntry,
    SystemRuntimeLimitsSettings,
    SystemSettingsEntry,
    UserSettingsEntry,
} from '../types/settingsTypes';

interface SystemSettingsResponse {
  system: SystemSettingsEntry;
  kautuka: KautukaInfrastructureSettingsEntry;
}

interface ProvidersResponse {
  providers: ProviderEntry[];
}

interface ModelsResponse {
  models: ModelEntry[];
}

interface AgentsResponse {
  agents: AgentSettingsEntry[];
}

export interface AgentRevisionListItem {
  revision: number;
  createdAt: string;
  changedSettingIds: string[];
  changedSettingNames: string[];
}

export interface AgentRevisionsResponse {
  agentId: string;
  currentRevision: number;
  revisions: AgentRevisionListItem[];
}

export interface AgentRevisionChange {
  settingId: string;
  settingName: string;
  before: unknown;
  after: unknown;
}

export interface AgentRevisionDiffResponse {
  agentId: string;
  fromRevision: number | null;
  toRevision: number;
  createdAt: string;
  changes: AgentRevisionChange[];
}

interface UsersResponse {
  users: UserSettingsEntry[];
}

interface DiscoverModelsResponse {
  models: DiscoveredModelEntry[];
}

interface ProbeModelResponse {
  capabilities: ModelEntry['capabilities'];
}

interface ProbeModelCandidateResponse {
  capabilities: ModelEntry['capabilities'];
  warningMessage?: string | null;
}

interface ProviderTestResponse {
  success?: boolean;
  message?: string;
}

interface PersonaDefinitionsResponse {
  definitions: PersonaDefinitions;
}

interface PersonaProfilesResponse {
  profiles: Record<string, AgentPersonaProfile>;
}

export interface CatalogRecommendationItem {
  id: string;
  kind: string;
  title: string;
  summary: string;
  publisher: string;
  version: string;
  isAvailable: boolean;
  enabled: boolean;
  canInstall: boolean;
  trustKind: string;
  installDisabledReason?: string | null;
}

export interface CatalogRecommendationsResponse {
  connectors: CatalogRecommendationItem[];
  skills: CatalogRecommendationItem[];
  message?: string | null;
}

export const settingsAdmin = {
  // System
  getSystemSettings: () => apiClient.get<SystemSettingsResponse>('/settings/system'),
  getRuntimeLimits: () =>
    apiClient.get<SystemRuntimeLimitsSettings>('/settings/runtime-limits'),
  saveSystemSettings: (body: Record<string, unknown>) =>
    apiClient.put<SystemSettingsResponse>('/settings/system', body),
  getAdvancedSettings: () => apiClient.get<AdvancedSettingsEntry>('/settings/advanced'),
  saveAdvancedSettings: (body: AdvancedSettingsEntry) =>
    apiClient.put<AdvancedSettingsEntry>('/settings/advanced', body),
  // Providers
  listProviderTypes: () =>
    apiClient.get<{ providerTypes: import('../types/settingsTypes').ProviderTypeInfo[] }>(
      '/settings/provider-types'
    ),
  listProviders: () => apiClient.get<ProvidersResponse>('/settings/providers'),
  testProvider: (body: Record<string, unknown>) =>
    apiClient.post<ProviderTestResponse>('/settings/providers/test', body),
  saveProvider: (body: Record<string, unknown>) => apiClient.post('/settings/providers/save', body),
  deleteProvider: (id: string) => apiClient.delete(`/settings/providers/${id}`),
  // Models
  listModels: () => apiClient.get<ModelsResponse>('/settings/models'),
  discoverModels: (body: Record<string, unknown>) =>
    apiClient.post<DiscoverModelsResponse>('/settings/models/discover', body),
  probeModelCandidate: (body: Record<string, unknown>) =>
    apiClient.post<ProbeModelCandidateResponse>('/settings/models/probe-candidate', body),
  saveModel: (body: Record<string, unknown>) => apiClient.post('/settings/models/save', body),
  deleteModel: (id: string) => apiClient.delete(`/settings/models/${id}`),
  probeModel: (id: string) =>
    apiClient.post<ProbeModelResponse>(`/settings/models/${id}/probe`, {}),
  // Agents
  listAgents: () => apiClient.get<AgentsResponse>('/settings/agents'),
  saveAgent: (id: string, body: Record<string, unknown>) =>
    apiClient.put(`/settings/agents/${id}`, body),
  deleteAgent: (id: string) => apiClient.delete(`/settings/agents/${id}`),
  getAgentToolPolicy: (id: string) =>
    apiClient.get<AgentToolPolicyProjection>(`/settings/agents/${id}/tool-policy`),
  listAgentRevisions: (id: string) =>
    apiClient.get<AgentRevisionsResponse>(`/settings/agents/${id}/revisions`),
  getAgentRevisionDiff: (id: string, revision: number, from?: number | null) =>
    apiClient.get<AgentRevisionDiffResponse>(
      `/settings/agents/${id}/revisions/${revision}/diff`,
      from == null ? undefined : { params: { from } }
    ),
  // Persona
  getPersonaDefinitions: () =>
    apiClient.get<PersonaDefinitionsResponse>('/settings/persona/definitions'),
  savePersonaDefinitions: (body: Record<string, unknown>) =>
    apiClient.put<PersonaDefinitionsResponse>('/settings/persona/definitions', body),
  listPersonaProfiles: () => apiClient.get<PersonaProfilesResponse>('/settings/persona/profiles'),
  saveAgentPersonaProfile: (agentId: string, body: Record<string, unknown>) =>
    apiClient.put<PersonaProfilesResponse>(`/settings/persona/profiles/${agentId}`, body),
  // Users / Access
  listUsers: () => apiClient.get<UsersResponse>('/settings/users'),
  clearExternalLogin: (userId: string, provider: string) =>
    apiClient.delete(`/settings/users/${userId}/external-logins/${provider}`),
  inviteUser: (body: Record<string, unknown>) => apiClient.post('/settings/users/invite', body),
  updateUserProfile: (userId: string, body: Record<string, unknown>) =>
    apiClient.put(`/settings/users/${userId}/profile`, body),
  setUserRole: (userId: string, body: Record<string, unknown>) =>
    apiClient.put(`/settings/users/${userId}/role`, body),
  setUserActive: (userId: string, body: Record<string, unknown>) =>
    apiClient.put(`/settings/users/${userId}/active`, body),
  setUserImportPermissions: (userId: string, body: Record<string, unknown>) =>
    apiClient.put(`/settings/users/${userId}/import-permissions`, body),
  saveUserChannelSettings: (userId: string, channelType: string, body: Record<string, unknown>) =>
    apiClient.put(`/settings/users/${userId}/channels/${channelType}`, body),
  dispatchUserChannelHitlTest: (
    userId: string,
    channelType: string,
    body: Record<string, unknown>
  ) => apiClient.post(`/settings/users/${userId}/channels/${channelType}/hitl-test`, body),
  // Agent Credentials (admin-scoped)
  getScopedCredentials: (scopeType: string, scopeId: string) =>
    apiClient.get(`/credentials/${scopeType}/${scopeId}`),
  setScopedCredentials: (scopeType: string, scopeId: string, body: Record<string, unknown>) =>
    apiClient.put(`/credentials/${scopeType}/${scopeId}`, body),
  // Provider Path Policy
  getProviderPathPolicySettings: () =>
    apiClient.get<ProviderPathPolicySettingsEntry>('/settings/provider-path-policy'),
  saveProviderPathPolicySettings: (body: Record<string, unknown>) =>
    apiClient.put<ProviderPathPolicySettingsEntry>('/settings/provider-path-policy', body),
  // Memory
  getMemorySettings: () => apiClient.get<MemorySettingsEntry>('/settings/memory'),
  saveMemorySettings: (body: Record<string, unknown>) =>
    apiClient.put<MemorySettingsEntry>('/settings/memory', body),
  // Organization
  getOrganization: () =>
    apiClient.get<{ organization: OrganizationSettingsEntry }>('/settings/organization'),
  saveOrganization: (body: Partial<OrganizationSettingsEntry>) =>
    apiClient.put<{ organization: OrganizationSettingsEntry }>('/settings/organization', body),
  getCatalogRecommendations: () =>
    apiClient.get<CatalogRecommendationsResponse>('/catalog/recommendations'),
  installCatalogRecommendation: (kind: string, id: string) =>
    apiClient.post('/catalog/recommendations/' + kind + '/' + id + '/install', {}),
};
