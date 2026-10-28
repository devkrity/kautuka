import { useQuery } from '@tanstack/react-query';
import { fetchPkgsProxyCacheStats } from '../api/packageCacheStats';
import { settingsQueryKeys } from '../api/queryKeys';

export function usePackageCacheStats() {
  return useQuery({
    queryKey: settingsQueryKeys.packageCacheStats(),
    queryFn: fetchPkgsProxyCacheStats,
    staleTime: 0,
    refetchOnWindowFocus: false,
    retry: false,
  });
}
