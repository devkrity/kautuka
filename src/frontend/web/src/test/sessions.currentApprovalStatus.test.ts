import { fetchSession } from '@/features/chat';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const { getMock } = vi.hoisted(() => ({
  getMock: vi.fn(),
}));

vi.mock('@/lib/apiClient', () => ({
  apiClient: {
    get: getMock,
  },
}));

describe('session API current approval statuses', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('ignores legacy mirrored approval fields on session messages', async () => {
    getMock.mockResolvedValue({
      data: {
        id: 'session-1',
        targetType: 'agent',
        targetId: 'agent-1',
        title: 'Session',
        createdAt: '2026-05-16T00:00:00Z',
        updatedAt: '2026-05-16T00:00:10Z',
        compacted: false,
        messages: [
          {
            id: 'msg-1',
            role: 'assistant',
            content: '',
            timestamp: '2026-05-16T00:00:10Z',
            pendingApproval: {
              requestId: 'apr-1',
              toolCallId: 'call-1',
              toolName: 'run_skill_script',
              argumentsJson: '{"namedArguments":{"sides":100},"skillId":"roll-dice"}',
              status: 'completed',
              checkpointId: 'cp-1',
              runId: 'run-1',
            },
          },
        ],
      },
    });

    const session = await fetchSession('session-1');

    expect(session.messages).toHaveLength(1);
    expect(Object.prototype.hasOwnProperty.call(session.messages[0] ?? {}, 'pendingApproval')).toBe(false);
  });
});