import { beforeEach, describe, expect, it, vi } from 'vitest';
import { apiClient } from '@/lib/apiClient';
import {
  createRemoteMcpDraft,
  testRemoteMcpDraft,
  saveRemoteMcpDraft,
  cancelRemoteMcpDraft,
} from '@/features/connectors/api/connectors';
import { blankDraft, buildSavePayload } from '@/features/connectors/types/connectorDraft';

vi.mock('@/lib/apiClient', () => ({ apiClient: { get: vi.fn(), post: vi.fn(), delete: vi.fn() } }));

describe('remote MCP draft API boundary', () => {
  beforeEach(() => vi.resetAllMocks());
  it('sends typed values only to credential administration and returns only the opaque handle', async () => {
    const request = {
      connectorId: 'search',
      candidate: buildSavePayload({
        ...blankDraft(),
        name: 'Search',
        transport: 'http',
        httpUrl: 'https://search.example/mcp',
      }),
      credentialScope: 'user' as const,
      entries: { key: 'synthetic-input' },
      removeKeys: [],
    };
    vi.mocked(apiClient.post).mockResolvedValue({
      data: { draftId: 'opaque/id', expiresAt: 'later', entries: { key: 'unexpected-echo' } },
    });
    expect(await createRemoteMcpDraft(request)).toEqual({
      draftId: 'opaque/id',
      expiresAt: 'later',
    });
    expect(apiClient.post).toHaveBeenCalledWith('/credentials/connector-drafts', request);
  });
  it('encodes the reference and sends no body for test, save or cancellation', async () => {
    vi.mocked(apiClient.post)
      .mockResolvedValueOnce({ data: { success: true, message: 'Connected.' } })
      .mockResolvedValueOnce({ data: { id: 'search', name: 'Search', transport: 'http' } });
    await testRemoteMcpDraft('opaque/id');
    await saveRemoteMcpDraft('opaque/id');
    await cancelRemoteMcpDraft('opaque/id');
    expect(vi.mocked(apiClient.post).mock.calls).toEqual([
      ['/connectors/drafts/opaque%2Fid/test'],
      ['/connectors/drafts/opaque%2Fid/save'],
    ]);
    expect(apiClient.delete).toHaveBeenCalledWith('/credentials/connector-drafts/opaque%2Fid');
  });
  it('rejects missing save and draft identities rather than treating the operation as successful', async () => {
    vi.mocked(apiClient.post).mockResolvedValue({ data: {} });
    await expect(
      createRemoteMcpDraft({
        connectorId: 'search',
        candidate: buildSavePayload(blankDraft()),
        credentialScope: 'user',
        entries: {},
        removeKeys: [],
      })
    ).rejects.toThrow('draft reference');
    await expect(saveRemoteMcpDraft('opaque')).rejects.toThrow('saved connector');
  });
});
