import { transitionMyChannelLifecycle } from '@/features/auth/api/auth';
import { apiClient } from '@/lib/apiClient';
import { beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('@/lib/apiClient', () => ({
  apiClient: {
    post: vi.fn(),
  },
}));

describe('channel lifecycle API', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it.each(['suspend', 'recover'] as const)(
    'sends the authenticated self-service %s transition without accepting deletion',
    async (transition) => {
      vi.mocked(apiClient.post).mockResolvedValue({
        data: { accepted: true, status: transition === 'suspend' ? 'SUSPENDED' : 'INACTIVE' },
      });

      await expect(transitionMyChannelLifecycle('telegram', transition)).resolves.toMatchObject({
        accepted: true,
      });
      expect(apiClient.post).toHaveBeenCalledWith('/settings/me/channels/telegram/lifecycle', {
        transition,
        reason: 'user_requested',
      });
    }
  );
});
