import { z } from 'zod';

export const updateOperationSchema = z.object({
  operationType: z.string(),
  completedAtUtc: z.string(),
  fromVersion: z.string().nullable(),
  toVersion: z.string(),
  succeeded: z.boolean(),
  errorMessage: z.string().nullable(),
});

export type UpdateOperation = z.infer<typeof updateOperationSchema>;

export const updateHistorySchema = z.array(updateOperationSchema);
