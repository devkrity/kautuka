import { fetchExecutionReadiness } from '@/features/dashboard/api/dashboard';
import { apiClient } from '@/lib/apiClient';
import { beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('@/lib/apiClient', () => ({
  apiClient: {
    get: vi.fn(),
  },
}));

describe('fetchExecutionReadiness', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('reads the forward-only execution readiness contract', async () => {
    vi.mocked(apiClient.get).mockResolvedValue({
      data: {
        ready: false,
        code: 'CONTAINER_RUNTIME_UNAVAILABLE',
        message: 'The container runtime is unavailable.',
        dependencies: [
          {
            dependency: 'Manager',
            ready: false,
            code: 'CONTAINER_RUNTIME_UNAVAILABLE',
            message: 'Manager cannot reach the container runtime.',
            detail: 'Podman is not responding.',
          },
        ],
      },
    });

    await expect(fetchExecutionReadiness()).resolves.toEqual({
      ready: false,
      code: 'CONTAINER_RUNTIME_UNAVAILABLE',
      message: 'The container runtime is unavailable.',
      dependencies: [
        {
          dependency: 'Manager',
          ready: false,
          code: 'CONTAINER_RUNTIME_UNAVAILABLE',
          message: 'Manager cannot reach the container runtime.',
          detail: 'Podman is not responding.',
        },
      ],
    });
    expect(apiClient.get).toHaveBeenCalledWith('/runtime/execution-readiness');
  });

  it('fails closed when the readiness payload is malformed', async () => {
    vi.mocked(apiClient.get).mockResolvedValue({ data: {} });

    await expect(fetchExecutionReadiness()).resolves.toEqual({
      ready: false,
      code: 'EXECUTION_READINESS_INVALID',
      message: 'Agent execution readiness could not be verified.',
      dependencies: [],
    });
  });
});
