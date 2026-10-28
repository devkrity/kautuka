import { apiClient } from '@/lib/apiClient';
import { pkgsProxyCacheStatsSchema, type PkgsProxyCacheStats } from '../schemas/packageCacheStatsSchema';

export async function fetchPkgsProxyCacheStats(): Promise<PkgsProxyCacheStats> {
  const response = await apiClient.get('/runtime/diagnostics/pkgs-proxy/stats');
  return pkgsProxyCacheStatsSchema.parse(response.data);
}
