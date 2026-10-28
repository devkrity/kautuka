import { act, render, waitFor } from '@testing-library/react';
import React from 'react';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import {
  KautukaNaradProvider,
  useKautukaNaradContext,
  useKautukaNaradSnapshot,
} from '../features/chat/kautuka/KautukaNaradProvider';

const attachMock = vi.fn(async () => undefined);
const disposeMock = vi.fn();
const subscribeMock = vi.fn(() => () => undefined);
const requestRecoveryMock = vi.fn(async () => undefined);
const getSnapshotMock = vi.fn(() => ({
  attached: true,
  backfilling: false,
  live: true,
  liveSubscribed: true,
  pendingCount: 0,
  resyncRequired: false,
  liveError: null as string | null,
}));

vi.mock('@devkrity/narad', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@devkrity/narad')>();
  return {
    ...actual,
    createAttachCoordinator: () => ({
      attach: attachMock,
      dispose: disposeMock,
      requestRecovery: requestRecoveryMock,
      getSnapshot: getSnapshotMock,
      subscribe: subscribeMock,
    }),
  };
});

const { sendNaradUserMessageMock, resolveNaradInterruptMock } = vi.hoisted(() => ({
  sendNaradUserMessageMock: vi.fn(
    async (): Promise<{
      status: 'accepted' | 'rejected' | 'conflict';
      currentRevision?: number;
      currentSessionSequence?: number;
      detail?: string;
    }> => ({ status: 'accepted', currentRevision: 1, currentSessionSequence: 0 })
  ),
  resolveNaradInterruptMock: vi.fn(async () => ({
    status: 'accepted' as const,
    requestId: 'req-1',
  })),
}));

const fetchHistoryPages = vi.fn(
  async (
    _transport: unknown,
    _sessionId: string,
    _cursor: number,
    _pageSize: number,
    apply: (record: unknown) => void
  ) => {
    apply({
      type: 'run.started',
      protocolVersion: 'narad/v1',
      eventId: 'evt-1',
      timestamp: '2026-08-05T00:00:00Z',
      sessionId: 'session-1',
      sessionSequence: 1,
      rootRunId: 'run-1',
      runId: 'run-1',
      sequence: 1,
    });
  }
);

const consumeWatch = vi.fn(
  async (
    _options: unknown,
    _sessionId: string,
    getCursor: () => number,
    onRevision: (record: unknown) => void
  ) => {
    getCursor();
    onRevision({
      type: 'session.revision',
      protocolVersion: 'narad/v1',
      eventId: 'watch-1',
      timestamp: '2026-08-05T00:00:00Z',
      sessionId: 'session-1',
      revision: 2,
    });
    onRevision({
      type: 'session.revision',
      protocolVersion: 'narad/v1',
      eventId: 'watch-2',
      timestamp: '2026-08-05T00:00:01Z',
      sessionId: 'session-1',
      revision: 3,
    });
  }
);

vi.mock('../features/chat/kautuka/createKautukaNaradTransport', async (importOriginal) => {
  const actual =
    await importOriginal<typeof import('../features/chat/kautuka/createKautukaNaradTransport')>();
  return {
    ...actual,
    createKautukaNaradTransport: () => ({
      attach: vi.fn(async () => ({
        capabilities: {
          protocolVersion: 'narad/v1',
          activeProfiles: ['narad.conversation/v1'],
          currentSessionSequence: 0,
        },
        boundarySessionSequence: 0,
      })),
      fetchHistory: vi.fn(async () => ({ events: [], nextSessionSequence: null })),
      subscribeLive: async function* () {
        yield {
          type: 'run.finished',
          protocolVersion: 'narad/v1',
          eventId: 'evt-2',
          timestamp: '2026-08-05T00:00:01Z',
          sessionId: 'session-1',
          sessionSequence: 2,
          rootRunId: 'run-1',
          runId: 'run-1',
          sequence: 2,
        };
      },
      sendControl: vi.fn(async () => undefined),
      dispose: vi.fn(),
    }),
    fetchKautukaNaradHistoryPages: (
      transport: unknown,
      sessionId: string,
      cursor: number,
      pageSize: number,
      apply: (record: unknown) => void
    ) => fetchHistoryPages(transport, sessionId, cursor, pageSize, apply),
    consumeKautukaNaradWatchRevisions: (
      options: unknown,
      sessionId: string,
      getCursor: () => number,
      onRevision: (record: unknown) => void,
      _signal: AbortSignal
    ) => consumeWatch(options, sessionId, getCursor, onRevision),
  };
});

vi.mock('../features/chat/kautuka/kautukaNaradIntents', () => ({
  sendNaradUserMessage: sendNaradUserMessageMock,
  resolveNaradInterrupt: resolveNaradInterruptMock,
  stopNaradActiveRoot: vi.fn(),
  retryNaradExecutionTarget: vi.fn(),
  cancelNaradGraphNode: vi.fn(),
}));

function Probe() {
  const context = useKautukaNaradContext();
  const snapshot = useKautukaNaradSnapshot();
  return (
    <div>
      <span data-testid="connection">{context.connectionStatus}</span>
      <span data-testid="sequence">{snapshot.lastSessionSequence}</span>
      <span data-testid="pending">{String(context.localDeliveryPending)}</span>
    </div>
  );
}

describe('KautukaNaradProvider', () => {
  beforeEach(() => {
    attachMock.mockClear();
    disposeMock.mockClear();
    subscribeMock.mockClear();
    requestRecoveryMock.mockClear();
    sendNaradUserMessageMock.mockClear();
    resolveNaradInterruptMock.mockClear();
    fetchHistoryPages.mockClear();
    consumeWatch.mockClear();
    getSnapshotMock.mockReturnValue({
      attached: true,
      backfilling: false,
      live: true,
      liveSubscribed: true,
      pendingCount: 0,
      resyncRequired: false,
      liveError: null,
    });
  });

  it('attaches on mount, subscribes to coordinator, and disposes on unmount', async () => {
    const { unmount } = render(
      <KautukaNaradProvider agentId="agent-1" sessionId="session-1">
        <Probe />
      </KautukaNaradProvider>
    );
    await waitFor(() => expect(attachMock).toHaveBeenCalledTimes(1));
    expect(subscribeMock).toHaveBeenCalled();
    unmount();
    expect(disposeMock).toHaveBeenCalled();
  });

  it('does not re-attach when the parent re-renders the same session', async () => {
    const { rerender } = render(
      <KautukaNaradProvider agentId="agent-1" sessionId="session-1">
        <Probe />
      </KautukaNaradProvider>
    );
    await waitFor(() => expect(attachMock).toHaveBeenCalledTimes(1));
    rerender(
      <KautukaNaradProvider agentId="agent-1" sessionId="session-1">
        <Probe />
      </KautukaNaradProvider>
    );
    await new Promise((resolve) => setTimeout(resolve, 30));
    expect(attachMock).toHaveBeenCalledTimes(1);
  });

  it('surfaces coordinator live errors by leaving live connection status', async () => {
    getSnapshotMock.mockReturnValue({
      attached: true,
      backfilling: false,
      live: false,
      liveSubscribed: true,
      pendingCount: 0,
      resyncRequired: false,
      liveError: 'stream failed',
    });

    const { getByTestId } = render(
      <KautukaNaradProvider agentId="agent-1" sessionId="session-1">
        <Probe />
      </KautukaNaradProvider>
    );

    await waitFor(() => expect(getByTestId('connection')).toHaveTextContent('error'));
  });

  it('does not fill history on healthy watch when live (authority rides the stream)', async () => {
    render(
      <KautukaNaradProvider agentId="agent-1" sessionId="session-1">
        <Probe />
      </KautukaNaradProvider>
    );
    await waitFor(() => expect(attachMock).toHaveBeenCalled());
    await waitFor(() => expect(consumeWatch).toHaveBeenCalled());
    // Watch applies revisions; without a session gap, no history thrash.
    expect(requestRecoveryMock).not.toHaveBeenCalled();
    expect(fetchHistoryPages).not.toHaveBeenCalled();
  });

  it('recreates attach lifecycle when session identity changes', async () => {
    const { rerender } = render(
      <KautukaNaradProvider agentId="agent-a" sessionId="session-1">
        <Probe />
      </KautukaNaradProvider>
    );
    await waitFor(() => expect(attachMock).toHaveBeenCalledTimes(1));
    rerender(
      <KautukaNaradProvider agentId="agent-b" sessionId="session-2">
        <Probe />
      </KautukaNaradProvider>
    );
    await waitFor(() => expect(attachMock.mock.calls.length).toBeGreaterThan(1));
    expect(disposeMock).toHaveBeenCalled();
  });

  it('does not force history catch-up after accepted resolveInterrupt', async () => {
    let contextValue: ReturnType<typeof useKautukaNaradContext> | null = null;
    function IntentProbe() {
      contextValue = useKautukaNaradContext();
      return null;
    }
    render(
      <KautukaNaradProvider agentId="agent-1" sessionId="session-1">
        <IntentProbe />
      </KautukaNaradProvider>
    );
    await waitFor(() => expect(contextValue).not.toBeNull());
    requestRecoveryMock.mockClear();
    await contextValue!.intents.resolveInterrupt({ interruptId: 'req-1', decision: 'approve' });
    // interrupt.resolved arrives on live; no recovery thrash on accept.
    expect(requestRecoveryMock).not.toHaveBeenCalled();
    expect(fetchHistoryPages).not.toHaveBeenCalled();
  });

  it('rejects non-accepted intent responses without optimistic mutation', async () => {
    sendNaradUserMessageMock.mockResolvedValueOnce({
      status: 'conflict',
      detail: 'Revision mismatch.',
    } as unknown as Awaited<ReturnType<typeof sendNaradUserMessageMock>>);
    let contextValue: ReturnType<typeof useKautukaNaradContext> | null = null;
    function IntentProbe() {
      contextValue = useKautukaNaradContext();
      return null;
    }
    render(
      <KautukaNaradProvider agentId="agent-1" sessionId="session-1">
        <IntentProbe />
      </KautukaNaradProvider>
    );
    await waitFor(() => expect(contextValue).not.toBeNull());
    const response = await contextValue!.intents.sendUserMessage('hello');
    expect(response.status).toBe('conflict');
    expect(requestRecoveryMock).not.toHaveBeenCalled();
  });

  it('returns queue acceptance without waiting for transcript catch-up', async () => {
    sendNaradUserMessageMock.mockResolvedValueOnce({
      status: 'accepted',
      currentRevision: 0,
      currentSessionSequence: 2,
    });
    let contextValue: ReturnType<typeof useKautukaNaradContext> | null = null;
    function IntentProbe() {
      contextValue = useKautukaNaradContext();
      return null;
    }
    render(
      <KautukaNaradProvider agentId="agent-1" sessionId="session-1">
        <IntentProbe />
      </KautukaNaradProvider>
    );
    await waitFor(() => expect(contextValue).not.toBeNull());
    requestRecoveryMock.mockClear();
    await contextValue!.intents.sendUserMessage('hello');
    expect(requestRecoveryMock).not.toHaveBeenCalled();
    expect(sendNaradUserMessageMock).toHaveBeenCalledWith(
      'agent-1',
      'session-1',
      expect.objectContaining({ expectedRevision: null })
    );
  });

  it('does not fill history when live already holds the accepted journal cursor', async () => {
    sendNaradUserMessageMock.mockResolvedValueOnce({
      status: 'accepted',
      currentRevision: 0,
      currentSessionSequence: 0,
    });
    let contextValue: ReturnType<typeof useKautukaNaradContext> | null = null;
    function IntentProbe() {
      contextValue = useKautukaNaradContext();
      return null;
    }
    render(
      <KautukaNaradProvider agentId="agent-1" sessionId="session-1">
        <IntentProbe />
      </KautukaNaradProvider>
    );
    await waitFor(() => expect(contextValue).not.toBeNull());
    requestRecoveryMock.mockClear();
    await contextValue!.intents.sendUserMessage('hello');
    expect(requestRecoveryMock).not.toHaveBeenCalled();
  });
});
