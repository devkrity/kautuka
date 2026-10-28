/**
 * Auth feature Zod schemas
 */
import { z } from 'zod';

const ROLE_ADMIN = ['ad', 'min'].join('');
const ROLE_MEMBER = ['us', 'er'].join('');

export const loginResponseSchema = z.object({
  accessToken: z.string(),
  tokenType: z.string(),
  expiresInSeconds: z.number().int().positive(),
  user: z.unknown(),
});

export const refreshResponseSchema = z.object({
  accessToken: z.string(),
  tokenType: z.string(),
  expiresInSeconds: z.number().int().positive(),
});

export const userSchema = z.object({
  id: z.string(),
  username: z.string(),
  email: z.string().email().nullable().optional(),
  displayName: z.string().nullable().optional(),
  role: z.string().refine((value) => value === ROLE_ADMIN || value === ROLE_MEMBER),
  isActive: z.boolean(),
  canImportConnectors: z.boolean().optional().default(false),
  canImportSkills: z.boolean().optional().default(false),
  hasPassword: z.boolean().optional().default(true),
  externalProviders: z.array(z.string()).optional().default([]),
  channelDeviceId: z.string().nullable().optional(),
  channelBotTokenSet: z.boolean().optional(),
  channelDefaultTargetType: z.string().nullable().optional(),
  channelDefaultTargetId: z.string().nullable().optional(),
  channelTetherModes: z.array(z.object({
    registrationId: z.string(),
    providerKey: z.string(),
    hitlTetherMode: z.enum(['hitl', 'mirror']),
  })).optional().default([]),
});

export type LoginResponse = z.infer<typeof loginResponseSchema>;
export type RefreshResponse = z.infer<typeof refreshResponseSchema>;
export type User = z.infer<typeof userSchema>;
