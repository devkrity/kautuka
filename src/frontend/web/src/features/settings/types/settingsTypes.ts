import type { AgentSkillAttachment, Skill, SkillAttachmentMode } from '@/features/skills';

export type Tab =
  | 'overview'
  | 'providers'
  | 'models'
  | 'agents'
  | 'users'
  | 'advanced'
  | 'organization';

export const ROLE_ADMIN = ['ad', 'min'].join('');
export const ROLE_MEMBER = ['us', 'er'].join('');

export interface TargetTelegramSettings {
  enabled: boolean;
  bot_id: string;
  bot_token_set: boolean;
  poll_timeout_seconds: number;
}

/** Shape returned by GET /settings/providers. Encrypted settings values are stripped; hasApiKey signals their presence. */
export interface ProviderEntry {
  id: string;
  type: string;
  settings: Record<string, unknown>;
  hasApiKey: boolean;
  createdAt?: string;
}

export interface ModelCapabilities {
  probedAt: string;
  toolCalling: boolean | null;
  streaming: boolean | null;
  reasoningOnlyOutput: boolean | null;
  structuredJson: boolean | null;
  agentConversations: boolean | null;
  probeError?: string | null;
}

export interface ModelEntry {
  id: string;
  providerId: string;
  modelName: string;
  settings: Record<string, unknown>;
  capabilities?: ModelCapabilities | null;
}

export interface DiscoveredModelEntry {
  id: string;
  displayName: string;
  description?: string | null;
  supportsChat?: boolean | null;
  supportsEmbeddings?: boolean | null;
  capabilityTags?: string[] | null;
}

/** Approval policy uses the backend's camelCase contract throughout the settings UI. */
export interface ApprovalPolicyConfig {
  requireMcpIds: string[];
  timeoutSeconds: number;
  requireMcpFunctionFilters?: Record<string, string[]>;
  disabledMcpFunctions?: Record<string, string[]>;
  /** Agent IDs this agent (as caller) is permitted to invoke as tools. */
  agentToolIds: string[];
  /** Subset of agentToolIds that require human approval before invocation. */
  approvalRequiredAgentToolIds: string[];
}

export interface PersonaDefinitions {
  styles: string[];
  tones: string[];
  verbosity_levels: string[];
}

export interface AgentPersonaProfile {
  temperature: number;
  style: string;
  tone: string;
  verbosity: string;
  icon?: string;
  color?: string;
}

export interface AgentToolExposure {
  exposedAsTool: boolean;
  description: string;
  invocationTimeoutSeconds: number;
}

export interface AgentRuntimeSettings {
  maxRunDurationSeconds?: number | null;
  hitlExpirySeconds?: number | null;
  hitlExpiryAction?: HitlExpiryAction | null;
  allowMultipleToolCalls?: boolean | null;
  allowConcurrentInvocation?: boolean | null;
  maxParallelTools?: number | null;
}

export interface ProviderPathPolicySettingsEntry {
  toolNativePromotionThreshold: number;
  toolMinObservations: number;
  toolNativeDemotionThreshold: number;
  structuredJsonStrictPromotionThreshold: number;
  structuredJsonMinObservations: number;
  visionEnableThreshold: number;
  observationDecayWindowDays: number;
  providerPathFallbackModeEnabled: boolean;
}

export interface MemorySettingsEntry {
  embeddingsEnabled: boolean;
  factPersistThreshold: number;
  factInjectThreshold: number;
  maxInjectedFacts: number;
  injectionTokenBudget: number;
  sessionCompactionModelId?: string | null;
}

export type ToolPolicyCategoryCode = 'connector' | 'skill' | 'superexec' | 'other';

export type ToolPolicyReasonCode =
  | 'allowed'
  | 'sandbox_required'
  | 'superexec_disabled'
  | 'uncategorized_category_denied'
  | 'unknown_category_denied';

export interface ToolPolicyDecisionProjection {
  allowed: boolean;
  reasonCode: ToolPolicyReasonCode;
  message: string;
}

export interface ToolPolicyCategoryProjection {
  category: ToolPolicyCategoryCode;
  exposure: ToolPolicyDecisionProjection;
  execution: ToolPolicyDecisionProjection;
}

export interface AgentToolPolicyProjection {
  agentId: string;
  categories: ToolPolicyCategoryProjection[];
}

export interface AgentMcpBindingEntry {
  connectorId: string;
  context: string;
}

export interface AgentSettingsEntry {
  id: string;
  scope?: 'system' | 'user';
  enabled?: boolean;
  name: string;
  modelId: string;
  ownerUserId: string;
  systemPrompt: string;
  superExecMode: 'disabled' | 'trusted' | 'ask';
  mcpBindings: AgentMcpBindingEntry[];
  skillAttachments: AgentSkillAttachment[];
  approvalConfig?: ApprovalPolicyConfig;
  persona: AgentPersonaProfile;
  toolExposure: AgentToolExposure;
  runtime?: AgentRuntimeSettings;
  revision?: number;
  session: {
    historyEnabled: boolean;
    maxHistoryRuns: number;
    summariesEnabled: boolean;
    summaryModelId?: string | null;
    memoryFactExtractionEnabled: boolean;
  };
  sandbox: {
    enabled: boolean;
    allowedLanguages: string[];
    maxMemory: string;
    maxTimeout: number;
  };
  telegram?: TargetTelegramSettings;
}

export interface AgentSkillOptionSummary {
  agentId: string;
  agentName: string;
  mode: SkillAttachmentMode;
}

export type PlatformSkillEntry = Skill;

export interface UserSettingsEntry {
  id: string;
  username: string;
  email?: string | null;
  displayName?: string | null;
  role: string;
  isActive: boolean;
  canImportConnectors?: boolean;
  canImportSkills?: boolean;
  channelDeviceId?: string | null;
  channelBotTokenSet?: boolean;
  channelDefaultTargetType?: 'agent' | null;
  channelDefaultTargetId?: string | null;
  channelTetherModes?: { registrationId: string; providerKey: string; hitlTetherMode: 'hitl' | 'mirror' }[];
  externalProviders?: string[];
}

export interface SystemObservabilitySettings {
  includeBinaryContent: boolean;
  captureMessageContent: boolean;
}

export interface SystemUpdateSettings {
  releaseChannel: 'stable' | 'preview';
}

export interface SystemRuntimeGroundingSettings {
  locale: string;
  timeZone: string;
}

export type HitlExpiryAction = 'inform' | 'retry' | 'abort';

export interface SystemRuntimeLimitsSettings {
  modelTimeoutSeconds: number;
  maxRunDurationSeconds: number;
  hitlExpirySeconds: number;
  hitlExpiryAction: HitlExpiryAction;
  allowMultipleToolCalls: boolean;
  allowConcurrentInvocation: boolean;
  maxParallelTools: number;
}

export interface SystemSettingsEntry {
  logLevel?: string | null;
  observability: SystemObservabilitySettings;
  updates: SystemUpdateSettings;
  runtimeGrounding: SystemRuntimeGroundingSettings;
  runtimeLimits?: SystemRuntimeLimitsSettings;
}

export interface KautukaWorkspaceSettingsEntry {
  baseRoot: string;
}

export interface KautukaSandboxSettingsEntry {
  image: string;
  defaultCpus: number;
  defaultMemory: string;
  defaultTimeout: number;
}

export interface KautukaDocumentIngestionSettingsEntry {
  enabled: boolean;
  internalParserConnectorId: string;
  internalParserToolName: string;
  parserTimeoutSeconds: number;
  parseRequiredExtensions: string;
  pdfImageFallbackEnabled: boolean;
  visionRouteAllowed: boolean;
}

export interface KautukaInfrastructureSettingsEntry {
  workspace: KautukaWorkspaceSettingsEntry;
  sandbox: KautukaSandboxSettingsEntry;
  documentIngestion: KautukaDocumentIngestionSettingsEntry;
}

export interface AdvancedSettingsEntry {
  system: SystemSettingsEntry;
  kautuka: KautukaInfrastructureSettingsEntry;
  personaDefinitions: PersonaDefinitions;
  providerPathPolicy: ProviderPathPolicySettingsEntry;
  memory: MemorySettingsEntry;
  providerTypeCatalog: ProviderCatalogEntry[];
}

export interface ProviderCatalogEntry {
  type: string;
  friendlyName: string;
  defaultBaseUrl?: string | null;
  defaultHttpTimeoutSeconds?: number | null;
  requiresApiKey: boolean;
  requiresBaseUrl: boolean;
  supportsHttpTimeoutSeconds: boolean;
  isBuiltIn: boolean;
  seedVersion: number;
}

export interface ProviderTypeInfo {
  type: string;
  friendlyName: string;
  requiresApiKey: boolean;
  requiresBaseUrl: boolean;
  supportsHttpTimeoutSeconds: boolean;
}

export interface OrganizationSettingsEntry {
  name: string | null;
  industry: string | null;
}
