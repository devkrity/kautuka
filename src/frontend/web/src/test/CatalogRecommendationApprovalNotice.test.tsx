import { renderHook } from '@testing-library/react';
import { expect, it, vi } from 'vitest';
import { useInstallCatalogRecommendation } from '@/features/settings/hooks/useCatalogRecommendations';

const mocks = vi.hoisted(() => ({ info: vi.fn(), success: vi.fn(), invalidate: vi.fn().mockResolvedValue(undefined) }));
vi.mock('sonner', () => ({ toast: { info: mocks.info, success: mocks.success, error: vi.fn() } }));
vi.mock('@tanstack/react-query', () => ({
  useQueryClient: () => ({ invalidateQueries: mocks.invalidate }),
  useMutation: (options: unknown) => options,
  useQuery: vi.fn(),
}));

it('tells the user to approve and test when catalogue discovery needs network access', async () => {
  const { result } = renderHook(() => useInstallCatalogRecommendation('connectors'));
  const mutation = result.current as unknown as { onSuccess: (response: unknown, recommendation: unknown) => Promise<void> };
  await mutation.onSuccess({ data: { networkApprovalRequired: true } }, { title: 'Web Tools' });
  expect(mocks.info).toHaveBeenCalledWith(expect.stringMatching(/Approve.*test the connection/i));
  expect(mocks.success).not.toHaveBeenCalled();
});
