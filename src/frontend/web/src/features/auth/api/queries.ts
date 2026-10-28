/**
 * Auth feature TanStack Query hooks
 */
import { useQuery } from '@tanstack/react-query';
import { getProfile } from './auth';

export const authQueryKeys = {
  profile: () => ['auth', 'me'] as const,
};

/** Returns the current authenticated user's profile. Stale after 5 min. */
export function useCurrentUser(options: { enabled?: boolean } = {}) {
  return useQuery({
    queryKey: authQueryKeys.profile(),
    queryFn: getProfile,
    staleTime: 5 * 60_000,
    retry: false,
    enabled: options.enabled ?? true,
  });
}
