import type { AxiosError } from 'axios';
import type {
  AgentPersonaProfile,
  AgentRuntimeSettings,
  AgentToolExposure,
  ApprovalPolicyConfig,
  HitlExpiryAction,
  PersonaDefinitions,
  SystemRuntimeLimitsSettings,
} from '../types/settingsTypes';

export function apiError(err: unknown): string {
  const e = err as AxiosError<{ detail?: unknown; message?: unknown }>;

  if (e.response?.status === 429) {
    return 'This AI provider is rate-limiting you right now. Wait a minute and try again, or switch to another provider such as Groq.';
  }

  let candidate =
    e.response?.data?.detail ?? e.response?.data?.message ?? e.message ?? 'Unknown error';
  if (typeof candidate === 'object') {
    candidate = JSON.stringify(candidate);
  }
  return String(candidate);
}

export function normalizeToolExposure(input?: Record<string, unknown> | null): AgentToolExposure {
  return {
    exposedAsTool: Boolean(input?.exposedAsTool ?? false),
    description: typeof input?.description === 'string' ? input.description : '',
    invocationTimeoutSeconds:
      typeof input?.invocationTimeoutSeconds === 'number' ? input.invocationTimeoutSeconds : 120,
  };
}

export const MAX_PARALLEL_TOOLS_CAP = 64;

export const defaultSystemRuntimeLimits: SystemRuntimeLimitsSettings = {
  modelTimeoutSeconds: 300,
  maxRunDurationSeconds: 0,
  hitlExpirySeconds: 0,
  hitlExpiryAction: 'abort',
  allowMultipleToolCalls: true,
  allowConcurrentInvocation: false,
  maxParallelTools: 0,
};

export function normalizeHitlExpiryAction(value: unknown): HitlExpiryAction {
  return value === 'inform' || value === 'retry' ? value : 'abort';
}

export function normalizeSystemRuntimeLimits(
  input?: Partial<SystemRuntimeLimitsSettings> | null
): SystemRuntimeLimitsSettings {
  const maxParallel = Number(input?.maxParallelTools);
  return {
    ...defaultSystemRuntimeLimits,
    modelTimeoutSeconds: Math.max(1, Number(input?.modelTimeoutSeconds) || 300),
    maxRunDurationSeconds: Math.max(0, Number(input?.maxRunDurationSeconds) || 0),
    hitlExpirySeconds: Math.max(0, Number(input?.hitlExpirySeconds) || 0),
    hitlExpiryAction: normalizeHitlExpiryAction(input?.hitlExpiryAction),
    allowMultipleToolCalls: input?.allowMultipleToolCalls !== false,
    allowConcurrentInvocation: Boolean(input?.allowConcurrentInvocation),
    maxParallelTools: Number.isFinite(maxParallel)
      ? Math.min(MAX_PARALLEL_TOOLS_CAP, Math.max(0, Math.trunc(maxParallel)))
      : 0,
  };
}

function readNullableBool(value: unknown): boolean | null {
  return typeof value === 'boolean' ? value : null;
}

function readNullableInt(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? Math.trunc(value) : null;
}

export function normalizeAgentRuntime(input?: Record<string, unknown> | null): AgentRuntimeSettings {
  const action = typeof input?.hitlExpiryAction === 'string' ? input.hitlExpiryAction.trim().toLowerCase() : '';
  const hitlExpiryAction: HitlExpiryAction | null =
    action === 'inform' || action === 'retry' || action === 'abort' ? action : null;
  const maxParallel = readNullableInt(input?.maxParallelTools);
  return {
    maxRunDurationSeconds: readNullableInt(input?.maxRunDurationSeconds),
    hitlExpirySeconds: readNullableInt(input?.hitlExpirySeconds),
    hitlExpiryAction,
    allowMultipleToolCalls: readNullableBool(input?.allowMultipleToolCalls),
    allowConcurrentInvocation: readNullableBool(input?.allowConcurrentInvocation),
    maxParallelTools:
      maxParallel == null ? null : Math.min(MAX_PARALLEL_TOOLS_CAP, Math.max(0, maxParallel)),
  };
}

export function normalizeApprovalConfig(
  input?: Partial<ApprovalPolicyConfig> | null
): ApprovalPolicyConfig {
  return {
    requireMcpIds: input?.requireMcpIds ?? [],
    timeoutSeconds: Math.max(30, input?.timeoutSeconds ?? 300),
    requireMcpFunctionFilters: input?.requireMcpFunctionFilters ?? {},
    disabledMcpFunctions: input?.disabledMcpFunctions ?? {},
    agentToolIds: input?.agentToolIds ?? [],
    approvalRequiredAgentToolIds: input?.approvalRequiredAgentToolIds ?? [],
  };
}

export function normalizeCredentialMap(input: unknown): Record<string, string> {
  if (!input || typeof input !== 'object') {
    return {};
  }

  const root = input as Record<string, unknown>;

  // Scoped credential GET responses include metadata and a string[] of key names.
  // We do not expose secret values back to the UI, so treat this as an empty value map.
  if (Array.isArray(root.credentials)) {
    return {};
  }

  const candidate =
    root.credentials && typeof root.credentials === 'object' && !Array.isArray(root.credentials)
      ? (root.credentials as Record<string, unknown>)
      : root;

  const output = new Map<string, string>();
  for (const [key, value] of Object.entries(candidate)) {
    if (typeof value === 'string' && value.trim().length > 0) {
      output.set(key, value);
    }
  }

  return Object.fromEntries(output);
}

export function normalizePersonaDefinitions(input: unknown): PersonaDefinitions {
  const source = input && typeof input === 'object' ? (input as Record<string, unknown>) : {};

  return {
    styles: normalizeStringArray(source.styles, ['professional']),
    tones: normalizeStringArray(source.tones, ['neutral']),
    verbosity_levels: normalizeStringArray(source.verbosity_levels, ['concise']),
  };
}

export function normalizePersonaProfile(
  input: unknown,
  definitions?: PersonaDefinitions
): AgentPersonaProfile {
  const source = input && typeof input === 'object' ? (input as Record<string, unknown>) : {};
  const fallbackStyle = definitions?.styles[0] ?? 'professional';
  const fallbackTone = definitions?.tones[0] ?? 'neutral';
  const fallbackVerbosity = definitions?.verbosity_levels[0] ?? 'concise';
  const colors = ['#0F766E', '#2563EB', '#7C3AED', '#DB2777', '#EA580C', '#16A34A'];
  const candidateIcon = typeof source.icon === 'string' ? source.icon.trim() : '';
  const icon = candidateIcon.length > 0 && candidateIcon.length <= 16 && !/\s/.test(candidateIcon) ? candidateIcon : '🤖';
  const color = typeof source.color === 'string' && colors.includes(source.color) ? source.color : '#0F766E';

  return {
    temperature: readTemperature(source.temperature),
    style: readString(source.style, fallbackStyle),
    tone: readString(source.tone, fallbackTone),
    verbosity: readString(source.verbosity, fallbackVerbosity),
    icon,
    color,
  };
}

function normalizeStringArray(input: unknown, fallback: string[]): string[] {
  if (!Array.isArray(input)) {
    return [...fallback];
  }

  const values = input
    .filter((value): value is string => typeof value === 'string')
    .map((value) => value.trim())
    .filter((value) => value.length > 0);

  return values.length > 0 ? values : [...fallback];
}

function readString(input: unknown, fallback: string): string {
  return typeof input === 'string' && input.trim().length > 0 ? input.trim() : fallback;
}

function readOptionalString(input: unknown): string | undefined {
  return typeof input === 'string' && input.trim().length > 0 ? input.trim() : undefined;
}

function readTemperature(input: unknown): number {
  if (typeof input === 'number' && Number.isFinite(input)) {
    return Math.min(1, Math.max(0, input));
  }

  if (typeof input === 'string') {
    const parsed = Number.parseFloat(input);
    if (Number.isFinite(parsed)) {
      return Math.min(1, Math.max(0, parsed));
    }
  }

  return 0.7;
}
