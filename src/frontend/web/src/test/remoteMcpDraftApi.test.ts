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

function enforceRemoteCandidate(candidate: ReturnType<typeof buildSavePayload>) {
  const forbidden = [
    candidate.localCommand,
    candidate.containerImage,
    candidate.containerCommand,
    candidate.containerSidecarName,
    candidate.bundlePath,
    candidate.serverType,
    candidate.readinessTimeoutSeconds,
    candidate.jitCooldownSeconds,
    candidate.healthcheckUrl,
    candidate.healthcheckIntervalMilliseconds,
  ];
  if (
    forbidden.some((value) => value !== null) ||
    candidate.localArgs.length ||
    candidate.containerArgs.length ||
    candidate.envKeys.length
  )
    throw new Error('Draft requires a complete remote connector configuration.');
}

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
  it.each(['blank', 'contaminated'] as const)(
    'sends a backend-valid remote candidate from a %s draft',
    async (kind) => {
      const draft = {
        ...blankDraft(),
        name: 'Search',
        transport: 'http',
        httpUrl: 'https://search.example/mcp',
        mcpProtocolVersion: '2025-11-25',
        credentials: [{ key: 'key', label: 'Key', required: true }],
        httpHeaders: [{ name: 'Authorization', credentialKey: 'key', prefix: 'Bearer ' }],
        ...(kind === 'contaminated'
          ? {
              localCommand: 'npx',
              localArgs: '-y\npackage',
              containerImage: 'image',
              containerCommand: 'command',
              containerArgs: 'arg',
              containerSidecarName: 'sidecar',
              bundlePath: '/bundle',
              serverType: 'node',
              readinessTimeoutSeconds: '8',
              jitCooldownSeconds: '9',
              healthcheckUrl: 'https://health.example',
              healthcheckIntervalMilliseconds: '500',
              envKeys: 'TOKEN',
            }
          : {}),
      };
      vi.mocked(apiClient.post).mockImplementation(async (_path, body) => {
        const candidate = (body as { candidate: ReturnType<typeof buildSavePayload> }).candidate;
        enforceRemoteCandidate(candidate);
        return { data: { draftId: 'opaque', expiresAt: 'later' } };
      });
      await createRemoteMcpDraft({
        connectorId: 'search',
        candidate: buildSavePayload(draft),
        credentialScope: 'user',
        entries: { key: 'synthetic-input' },
        removeKeys: [],
      });
      const sent = vi.mocked(apiClient.post).mock.calls[0][1] as {
        candidate: ReturnType<typeof buildSavePayload>;
        entries: Record<string, string>;
      };
      expect(sent.candidate).toMatchObject({
        transport: 'http',
        httpUrl: 'https://search.example/mcp',
        mcpProtocolVersion: '2025-11-25',
        credentials: draft.credentials,
        httpHeaders: draft.httpHeaders,
      });
      expect(JSON.stringify(sent.candidate)).not.toContain('synthetic-input');
      expect(sent.entries.key).toBe('synthetic-input');
    }
  );
  it('rejects the inherited local execution defaults if a caller submits them directly', async () => {
    vi.mocked(apiClient.post).mockImplementation(async (_path, body) => {
      enforceRemoteCandidate(
        (body as { candidate: ReturnType<typeof buildSavePayload> }).candidate
      );
      return { data: { draftId: 'opaque', expiresAt: 'later' } };
    });
    const candidate = {
      ...buildSavePayload({ ...blankDraft(), transport: 'http' }),
      localCommand: 'npx',
      localArgs: ['-y'],
    };
    await expect(
      createRemoteMcpDraft({
        connectorId: 'search',
        candidate,
        credentialScope: 'user',
        entries: {},
        removeKeys: [],
      })
    ).rejects.toThrow('complete remote connector configuration');
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
