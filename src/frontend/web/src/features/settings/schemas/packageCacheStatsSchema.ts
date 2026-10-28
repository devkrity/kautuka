import { z } from 'zod';

const popularPackageSchema = z.object({
  ecosystem: z.string(),
  name: z.string(),
  hits: z.number(),
  sizeBytes: z.number(),
});

const recentPackageSchema = z.object({
  ecosystem: z.string(),
  name: z.string(),
  version: z.string(),
  cachedAt: z.string(),
  sizeBytes: z.number(),
});

export const pkgsProxyCacheStatsSchema = z.object({
  ok: z.boolean(),
  message: z.string(),
  packages: z.number(),
  versions: z.number(),
  artifacts: z.number(),
  totalSizeBytes: z.number(),
  totalHits: z.number(),
  ecosystems: z.record(z.string(), z.number()),
  popular: z.array(popularPackageSchema),
  recent: z.array(recentPackageSchema),
});

export type PkgsProxyCacheStats = z.infer<typeof pkgsProxyCacheStatsSchema>;
