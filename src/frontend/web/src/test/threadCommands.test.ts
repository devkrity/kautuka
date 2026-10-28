import { describe, expect, it, vi, beforeEach } from 'vitest';

import {
  resolveThreadInterrupt,
  stopActiveRootCommand,
} from '../features/chat/api/threadCommands';

const authenticatedFetch = vi.fn();

vi.mock('@/lib/authenticatedFetch', () => ({
  authenticatedFetch: (...args: unknown[]) => authenticatedFetch(...args),
}));

describe('threadCommands', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('posts atomic resolve-interrupt payloads', async () => {
    authenticatedFetch.mockResolvedValue(
      new Response(JSON.stringify({ status: 'accepted', decision: 'approved', requestId: 'req-1' }), {
        status: 202,
      })
    );

    const result = await resolveThreadInterrupt({
      interruptId: 'req-1',
      decision: 'approve',
    });

    expect(result.status).toBe('accepted');
    expect(authenticatedFetch).toHaveBeenCalledWith(
      '/api/narad/v1/commands/resolve-interrupt',
      expect.objectContaining({
        method: 'POST',
        body: JSON.stringify({
          interruptId: 'req-1',
          decision: 'approve',
        }),
      })
    );
  });

  it('surfaces unsupported stop-active-root conflicts without throwing', async () => {
    authenticatedFetch.mockResolvedValue(
      new Response(JSON.stringify({ status: 'conflict', detail: 'Root run is already terminal.' }), {
        status: 409,
      })
    );

    const result = await stopActiveRootCommand('agent-1', 'thread-1', { runId: 'run-1' });
    expect(result.status).toBe('conflict');
    expect(result.detail).toContain('terminal');
  });
});
