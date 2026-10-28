import { z } from 'zod';

export const applianceHealthSchema = z.object({
  installedVersion: z.string().nullable(),
  lastUpdateAppliedAtUtc: z.string().nullable(),
  sidecarsStagedAtUtc: z.string().nullable(),
  sidecarCount: z.number().nullable(),
});

export type ApplianceHealth = z.infer<typeof applianceHealthSchema>;
