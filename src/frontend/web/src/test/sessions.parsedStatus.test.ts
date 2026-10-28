import { fetchParsedAssetStatus, fetchParsedAssetStatuses } from '@/features/chat';
import axios from 'axios';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const { getMock } = vi.hoisted(() => ({
  getMock: vi.fn(),
}));

vi.mock('@/lib/apiClient', () => ({
  apiClient: {
    get: getMock,
  },
}));

describe('parsed asset status API', () => {
  beforeEach(() => {
    vi.restoreAllMocks();
    vi.clearAllMocks();
  });

  it('parses single parsed asset status payload', async () => {
    getMock.mockResolvedValue({
      data: {
        assetId: 'asset-1',
        sessionId: 'session-1',
        status: 'success',
        parserName: 'internal-mcp-parser',
        parserVersion: '1.0.0',
        warning: null,
        error: null,
        updatedAt: '2026-04-26T00:00:00Z',
      },
    });

    const result = await fetchParsedAssetStatus('session-1', 'asset-1');

    expect(result).not.toBeNull();
    expect(result?.assetId).toBe('asset-1');
    expect(result?.status).toBe('success');
  });

  it('returns null on 404 for single parsed asset status', async () => {
    getMock.mockRejectedValue({
      isAxiosError: true,
      response: { status: 404 },
    });

    vi.spyOn(axios, 'isAxiosError').mockReturnValue(true);

    const result = await fetchParsedAssetStatus('session-1', 'missing');

    expect(result).toBeNull();
  });

  it('parses parsed asset statuses collection payload', async () => {
    getMock.mockResolvedValue({
      data: {
        assets: [
          {
            assetId: 'asset-1',
            sessionId: 'session-1',
            status: 'pending',
            parserName: 'internal-mcp-parser',
            parserVersion: '1.0.0',
            warning: null,
            error: null,
            updatedAt: '2026-04-26T00:00:00Z',
          },
        ],
      },
    });

    const result = await fetchParsedAssetStatuses('session-1');

    expect(result).toHaveLength(1);
    expect(result[0].status).toBe('pending');
  });
});
