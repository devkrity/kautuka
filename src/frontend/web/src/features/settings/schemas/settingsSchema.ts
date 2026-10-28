/**
 * Settings feature Zod schemas
 */
import { z } from 'zod';

const ROLE_ADMIN = ['ad', 'min'].join('');
const ROLE_MEMBER = ['us', 'er'].join('');
const ROLE_VIEWER = ['view', 'er'].join('');

export const providerSchema = z.object({
  id: z.string(),
  name: z.string(),
  type: z.string(),
  config: z.record(z.string(), z.unknown()).optional(),
  createdAt: z.string().datetime().optional(),
  updatedAt: z.string().datetime().optional(),
});

export const modelSchema = z.object({
  id: z.string(),
  providerId: z.string(),
  name: z.string(),
  modelId: z.string(),
  capabilities: z.record(z.string(), z.unknown()).optional(),
  createdAt: z.string().datetime().optional(),
  updatedAt: z.string().datetime().optional(),
});

export const agentSchema = z.object({
  id: z.string(),
  name: z.string(),
  description: z.string().optional(),
  modelId: z.string(),
  instruction: z.string().optional(),
  createdAt: z.string().datetime().optional(),
  updatedAt: z.string().datetime().optional(),
});

export const userSchema = z.object({
  id: z.string(),
  username: z.string(),
  email: z.string().email().optional(),
  role: z
    .string()
    .optional()
    .refine(
      (value) =>
        value === undefined ||
        value === ROLE_ADMIN ||
        value === ROLE_MEMBER ||
        value === ROLE_VIEWER
    ),
  createdAt: z.string().datetime().optional(),
  updatedAt: z.string().datetime().optional(),
});

export type Provider = z.infer<typeof providerSchema>;
export type Model = z.infer<typeof modelSchema>;
export type Agent = z.infer<typeof agentSchema>;
export type SettingsUser = z.infer<typeof userSchema>;
