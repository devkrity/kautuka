/**
 * Dashboard feature Zod schemas
 */
import { z } from 'zod';

export const recentSessionSchema = z.object({
  id: z.string(),
  agentId: z.string().optional(),
  agentName: z.string().optional(),
  messageCount: z.number().optional(),
  lastMessageAt: z.string().datetime().optional(),
  createdAt: z.string().datetime(),
});

export type RecentSession = z.infer<typeof recentSessionSchema>;
