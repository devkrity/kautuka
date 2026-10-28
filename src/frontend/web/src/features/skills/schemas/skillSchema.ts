import { z } from 'zod';
import { ownershipScopeSchema } from '@/features/connectors';

export const skillAttachmentModeSchema = z.enum(['disabled', 'trusted', 'require_approval']);

export const agentSkillAttachmentSchema = z.object({
  skillId: z.string(),
  mode: skillAttachmentModeSchema,
});

export const skillSchema = z.object({
  id: z.string(),
  name: z.string(),
  description: z.string().default(''),
  packageHash: z.string().default(''),
  status: z.string().default('ready'),
  packagePath: z.string().default(''),
  hasResources: z.boolean().default(false),
  hasScripts: z.boolean().default(false),
  enabled: z.boolean().default(true),
  envKeys: z.array(z.string()).default([]),
  importTimestamp: z.string().default(''),
  sourceMetadata: z.record(z.string(), z.string()).default({}),
  scope: ownershipScopeSchema.default('system'),
  ownerUserId: z.string().nullable().default(null),
});

export const skillInspectionSchema = z.object({
  skill: skillSchema,
  instructionsMarkdown: z.string().default(''),
  resourcePaths: z.array(z.string()).default([]),
  scriptPaths: z.array(z.string()).default([]),
});

export const bundleImportResponseSchema = z.object({
  skills: z.array(skillInspectionSchema).default([]),
});

export const skillImportResultSchema = z.discriminatedUnion('type', [
  z.object({
    type: z.literal('single'),
    single: skillInspectionSchema,
    bundle: z.null().optional(),
  }),
  z.object({
    type: z.literal('bundle'),
    single: z.null().optional(),
    bundle: bundleImportResponseSchema,
  }),
]);

export type SkillAttachmentMode = z.infer<typeof skillAttachmentModeSchema>;
export type AgentSkillAttachment = z.infer<typeof agentSkillAttachmentSchema>;
export type Skill = z.infer<typeof skillSchema>;
export type SkillInspection = z.infer<typeof skillInspectionSchema>;
export type SkillImportResult = z.infer<typeof skillImportResultSchema>;

const asRecord = (value: unknown): Record<string, unknown> =>
  value && typeof value === 'object' ? (value as Record<string, unknown>) : {};

export const normalizeSkillAttachmentMode = (value: unknown): SkillAttachmentMode => {
  if (typeof value === 'number') {
    if (value === 1) return 'trusted';
    if (value === 2) return 'require_approval';
    return 'disabled';
  }

  if (typeof value === 'string') {
    const normalized = value.trim().toLowerCase().split('-').join('_');
    if (normalized === 'trusted') return 'trusted';
    if (normalized === 'requireapproval' || normalized === 'require_approval')
      return 'require_approval';
  }

  return 'disabled';
};

export const normalizeAgentSkillAttachments = (value: unknown): AgentSkillAttachment[] => {
  if (!Array.isArray(value)) {
    return [];
  }

  return value
    .map((entry) => {
      const record = asRecord(entry);
      const skillId = typeof record.skillId === 'string' ? record.skillId : '';
      if (!skillId.trim()) {
        return null;
      }

      return {
        skillId,
        mode: normalizeSkillAttachmentMode(record.mode),
      };
    })
    .filter((entry): entry is AgentSkillAttachment => entry !== null);
};
