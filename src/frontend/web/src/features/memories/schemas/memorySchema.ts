/**
 * Memories feature Zod schemas
 */
import { z } from 'zod';

export const memoryFactSchema = z.object({
  id: z.string(),
  agentId: z.string().optional(),
  title: z.string(),
  content: z.string(),
  category: z.string(),
  source: z.string().default('fact-store'),
  confidence: z.number().default(0),
  sessionId: z.string().nullable().optional(),
  createdAt: z.string(),
  updatedAt: z.string(),
  metadata: z.record(z.string(), z.unknown()).optional(),
});

export type MemoryFact = z.infer<typeof memoryFactSchema>;

export const sessionMessageSchema = z.object({
  id: z.string(),
  role: z.string(),
  content: z.string(),
  timestamp: z.string(),
});

export const memoriesSessionSchema = z.object({
  id: z.string(),
  targetType: z.literal('agent'),
  targetId: z.string(),
  title: z.string().optional().default(''),
  createdAt: z.string(),
  updatedAt: z.string(),
  messages: z.array(sessionMessageSchema).optional().default([]),
  compacted: z.boolean().optional().default(false),
  compactSummary: z.string().nullable().optional(),
});

export type MemoriesSession = z.infer<typeof memoriesSessionSchema>;

export const memoriesBundleSchema = z.object({
  sessions: z.array(memoriesSessionSchema).optional().default([]),
  facts: z.array(memoryFactSchema).optional().default([]),
});

export type MemoriesBundle = z.infer<typeof memoriesBundleSchema>;
