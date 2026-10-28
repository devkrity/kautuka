/**
 * Settings feature API operations
 *
 * Covers the main topology endpoint list calls used by Dashboard, ChatPage, MemoriesPage, etc.
 * Full CRUD for settings management lives in settingsAdmin.ts.
 */
import { z } from 'zod';
import { apiClient } from '@/lib/apiClient';
import { readApiArray } from '@/lib/apiResponse';
import type { AgentToolPolicyProjection } from '../types/settingsTypes';

// ── summary schemas ───────────────────────────────────────────────────────────
// Intentionally minimal: only the fields used across the app.
// Full detail schemas live in settingsSchema.ts.

export const agentSummarySchema = z
  .object({
    id: z.string(),
    name: z.string(),
    persona: z.object({ icon: z.string().optional(), color: z.string().optional() }).optional(),
    scope: z.enum(['system', 'user']),
    ownerUserId: z.string().nullable(),
    modelId: z.string().optional(),
    sandbox: z
      .object({
        enabled: z.boolean().optional(),
      })
      .nullable()
      .optional(),
  })
  .passthrough();

export const modelSummarySchema = z
  .object({ id: z.string(), name: z.string(), providerId: z.string() })
  .passthrough();

export const providerSummarySchema = z
  .object({
    id: z.string(),
    type: z.string(),
    settings: z.record(z.string(), z.unknown()).optional(),
    createdAt: z.string().optional(),
    hasApiKey: z.boolean().optional(),
  })
  .passthrough();

export type AgentSummary = z.infer<typeof agentSummarySchema>;
export type ModelSummary = z.infer<typeof modelSummarySchema>;
export type ProviderSummary = z.infer<typeof providerSummarySchema>;

const toolPolicyReasonCodeSchema = z.union([
  z.literal('allowed'),
  z.literal('sandbox_required'),
  z.literal('superexec_disabled'),
  z.literal('uncategorized_category_denied'),
  z.literal('unknown_category_denied'),
]);

const toolPolicyDecisionSchema = z.object({
  allowed: z.boolean(),
  reasonCode: toolPolicyReasonCodeSchema,
  message: z.string(),
});

const toolPolicyCategorySchema = z.object({
  category: z.union([
    z.literal('connector'),
    z.literal('skill'),
    z.literal('superexec'),
    z.literal('other'),
  ]),
  exposure: toolPolicyDecisionSchema,
  execution: toolPolicyDecisionSchema,
});

const agentToolPolicyProjectionSchema = z.object({
  agentId: z.string(),
  categories: z.array(toolPolicyCategorySchema),
});

// ── fetchers ──────────────────────────────────────────────────────────────────

export async function fetchAgents(): Promise<AgentSummary[]> {
  const response = await apiClient.get('/settings/agents');
  const data = readApiArray(response.data, 'agents');
  return (data as unknown[]).map((a) => agentSummarySchema.parse(a));
}

export async function fetchModels(): Promise<ModelSummary[]> {
  const response = await apiClient.get('/settings/models');
  const data = readApiArray(response.data, 'models');
  return (data as unknown[]).map((m) => {
    if (!m || typeof m !== 'object' || Array.isArray(m)) {
      return modelSummarySchema.parse(m);
    }

    const record = m as Record<string, unknown>;
    const normalized = {
      ...record,
      name:
        typeof record.name === 'string'
          ? record.name
          : typeof record.modelName === 'string'
            ? record.modelName
            : record.name,
    };

    return modelSummarySchema.parse(normalized);
  });
}

export async function fetchProviders(): Promise<ProviderSummary[]> {
  const response = await apiClient.get('/settings/providers');
  const data = readApiArray(response.data, 'providers');
  return (data as unknown[]).map((p) => providerSummarySchema.parse(p));
}

export async function fetchAgentToolPolicyProjection(
  agentId: string
): Promise<AgentToolPolicyProjection> {
  const response = await apiClient.get(
    `/settings/agents/${encodeURIComponent(agentId)}/tool-policy`
  );
  return agentToolPolicyProjectionSchema.parse(response.data) as AgentToolPolicyProjection;
}
