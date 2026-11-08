import { beforeEach, describe, expect, it, vi } from 'vitest';
const { get, put } = vi.hoisted(() => ({ get: vi.fn(), put: vi.fn() }));
vi.mock('@/lib/apiClient', () => ({ apiClient: { get, put } }));
import { allowProviderNetworkAccess } from '@/features/onboarding/utils/providerNetworkAccess';

const policy = (revision: number, activeRevision = revision, activationState = 'active') => ({
  enabled: true,
  revision,
  activeRevision,
  activationState,
  activeVersion: 'test',
  lastError: null,
  rules: [],
  defaults: [],
  sources: [],
  requests: [],
});

describe('provider network approval', () => {
  beforeEach(() => {
    vi.resetAllMocks();
    vi.useRealTimers();
  });

  it('allows only the displayed endpoint for Gateway using a fresh policy revision', async () => {
    get.mockResolvedValue({ data: policy(7) });
    put.mockResolvedValue({ data: policy(8) });
    await allowProviderNetworkAccess({ destination: '192.168.1.4', port: 11434 });
    expect(put).toHaveBeenCalledWith('/network-access/rules', {
      identity: {
        scopeType: 'core',
        userId: 'system',
        agentId: 'gateway',
        role: 'core',
        componentId: 'gateway',
      },
      destination: '192.168.1.4',
      port: 11434,
      allowed: true,
      expectedRevision: 7,
    });
  });

  it('waits for activation instead of treating a saved permission as active', async () => {
    vi.useFakeTimers();
    get.mockResolvedValueOnce({ data: policy(7) }).mockResolvedValueOnce({ data: policy(8) });
    put.mockResolvedValue({ data: policy(8, 7, 'pending') });
    const finished = vi.fn();
    const pending = allowProviderNetworkAccess({ destination: 'api.example.com', port: 443 }).then(
      finished
    );
    await vi.advanceTimersByTimeAsync(0);
    expect(finished).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(500);
    await pending;
    expect(finished).toHaveBeenCalledOnce();
    vi.useRealTimers();
  });

  it('does not continue when activation fails or the policy revision conflicts', async () => {
    get.mockResolvedValue({ data: policy(7) });
    put.mockResolvedValue({ data: { ...policy(8, 7, 'failed'), lastError: 'Envoy unavailable' } });
    await expect(
      allowProviderNetworkAccess({ destination: '192.168.1.4', port: 11434 })
    ).rejects.toThrow('Envoy unavailable');
    put.mockRejectedValue(new Error('Policy changed; refresh and retry'));
    await expect(
      allowProviderNetworkAccess({ destination: '192.168.1.4', port: 11434 })
    ).rejects.toThrow('Policy changed');
    expect(get).toHaveBeenCalledTimes(2);
  });

  it('does not treat a newer policy as confirmation of the approved permission', async () => {
    vi.useFakeTimers();
    get.mockResolvedValueOnce({ data: policy(7) }).mockResolvedValueOnce({ data: policy(9) });
    put.mockResolvedValue({ data: policy(8, 7, 'pending') });
    const assertion = expect(
      allowProviderNetworkAccess({ destination: '192.168.1.4', port: 11434 })
    ).rejects.toThrow('Network policy changed');
    await vi.advanceTimersByTimeAsync(500);
    await assertion;
    vi.useRealTimers();
  });
});
