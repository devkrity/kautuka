/**
 * @vitest-environment jsdom
 */
import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import React from 'react';
import { createMemoryRouter, RouterProvider, useSearchParams } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const listSessionsForAgentMock = vi.fn();
const useAgentsMock = vi.fn();
const useCurrentUserMock = vi.fn();
const useAgentsTabMock = vi.fn();
const naradProviderPropsMock = vi.fn();
const sendUserMessageMock = vi.fn();
const createSessionMock = vi.fn();

vi.mock('@/features/approvals', () => ({
  HitlWorkspaceSummary: () => null,
}));

vi.mock('@/features/settings', () => ({
  ROLE_ADMIN: 'admin',
  useAgents: () => useAgentsMock(),
  useAgentsTab: () => useAgentsTabMock(),
  useAgentToolPolicy: () => ({ data: null }),
  AgentForm: () => null,
  AgentConfigurationDialog: ({ open, mode }: { open: boolean; mode: string }) =>
    open ? <div data-testid="agent-dialog">{mode}</div> : null,
  ConfirmDeleteButton: () => null,
}));

vi.mock('@/features/auth', () => ({
  useCurrentUser: () => useCurrentUserMock(),
}));

vi.mock('@/features/chat', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/features/chat')>();
  return {
    ...actual,
    listSessionsForAgent: (...args: unknown[]) => listSessionsForAgentMock(...args),
    createSession: (...args: unknown[]) => createSessionMock(...args),
    KautukaNaradProvider: ({
      children,
      sessionId,
      agentId,
    }: {
      children: React.ReactNode;
      sessionId: string;
      agentId: string;
    }) => {
      naradProviderPropsMock({ sessionId, agentId });
      return <>{children}</>;
    },
    useKautukaNaradContext: () => ({
      connectionStatus: 'live',
      attachSnapshot: { live: true, attached: true },
      localDeliveryPending: false,
      intents: {
        sendUserMessage: vi.fn(),
        resolveInterrupt: vi.fn(),
        stopActiveRoot: vi.fn(),
        retryTarget: vi.fn(),
        cancelGraphNode: vi.fn(),
        abortLocalDelivery: vi.fn(),
      },
    }),
    useKautukaNaradConnectionStatus: () => 'live',
    useKautukaNaradIntents: () => ({
      sendUserMessage: sendUserMessageMock,
      resolveInterrupt: vi.fn(),
      stopActiveRoot: vi.fn(),
      retryTarget: vi.fn(),
      cancelGraphNode: vi.fn(),
      abortLocalDelivery: vi.fn(),
    }),
    useKautukaTranscriptTurns: () => [],
    useKautukaPartialAssistantText: () => '',
    useKautukaRunSummary: () => ({ activeRootRunId: null, isRunning: false, nestedRuns: [] }),
    useKautukaOpenInterrupts: () => [],
    useKautukaMergedOpenInterrupts: () => [],
    useKautukaActiveRootGraph: () => ({ graph: null, resyncRequired: false }),
    useKautukaConnectionView: () => ({ lastError: null, resyncRequired: false, sessionGap: false }),
    useKautukaComposerLocked: () => false,
    useSessionAssets: () => ({ data: [] }),
    ExecutionGraphPanel: () => null,
    ActivityLedger: () => null,
    ApprovalCard: () => null,
    RunTraceInspector: () => null,
    SessionQueue: () => null,
  };
});

function SessionProbe() {
  const [searchParams] = useSearchParams();
  return <div data-testid="session-id">{searchParams.get('sessionId') ?? 'none'}</div>;
}

function createAgentsTabMock() {
  return {
    agents: [
      { id: 'agent-1', name: 'Agent One', scope: 'user', ownerUserId: 'user-1' },
      { id: 'agent-2', name: 'Agent Two', scope: 'user', ownerUserId: 'user-1' },
    ],
    models: [],
    mcpServers: [],
    skills: [],
    personaDefinitions: { styles: [], tones: [], verbosity_levels: [] },
    organizationDefaults: null,
    getAgentToolMode: vi.fn(),
    setAgentToolMode: vi.fn(),
    patchAgent: vi.fn(),
    getConnectorMode: vi.fn(() => 'off'),
    setConnectorMode: vi.fn(),
    getToolMode: vi.fn(() => 'off'),
    setToolMode: vi.fn(),
    toggleToolPanel: vi.fn(),
    isToolPanelOpen: vi.fn(() => false),
    saveAgent: vi.fn(),
    removeAgent: vi.fn(),
    savingId: null,
    startCreate: vi.fn(),
    draftAgent: null,
  };
}

describe('AgentPageV2 deep-link and navigation', () => {
  it.each(['rejected', 'network'])(
    'preserves the draft on %s admission failure',
    async (failure) => {
      if (failure === 'network')
        sendUserMessageMock.mockRejectedValueOnce(new Error('Network unavailable'));
      else
        sendUserMessageMock.mockResolvedValueOnce({
          status: 'conflict',
          detail: 'Thread projection revision is stale.',
        });
      const { AgentPageV2 } = await import('@/pages/AgentPageV2');
      const router = createMemoryRouter(
        [{ path: '/workspace/:agentId', element: <AgentPageV2 /> }],
        { initialEntries: ['/workspace/agent-1?sessionId=session-1'] }
      );
      render(<RouterProvider router={router} />);
      const composer = await screen.findByRole('textbox', { name: '' });
      await userEvent.type(composer, 'keep this prompt{Enter}');
      await waitFor(() => expect(sendUserMessageMock).toHaveBeenCalledTimes(1));
      expect(composer).toHaveValue('keep this prompt');
      expect(await screen.findByRole('alert')).toHaveTextContent(
        failure === 'network' ? 'Network unavailable' : 'Thread projection revision is stale.'
      );
    }
  );

  it('keeps the draft pending and clears it only after queue acceptance', async () => {
    let accept!: (value: { status: 'accepted' }) => void;
    sendUserMessageMock.mockReturnValueOnce(
      new Promise((resolve) => {
        accept = resolve;
      })
    );
    const { AgentPageV2 } = await import('@/pages/AgentPageV2');
    const router = createMemoryRouter([{ path: '/workspace/:agentId', element: <AgentPageV2 /> }], {
      initialEntries: ['/workspace/agent-1?sessionId=session-1'],
    });
    render(<RouterProvider router={router} />);
    const composer = await screen.findByRole('textbox', { name: '' });
    await userEvent.type(composer, 'queue this prompt{Enter}{Enter}');
    expect(composer).toHaveValue('queue this prompt');
    expect(sendUserMessageMock).toHaveBeenCalledTimes(1);
    await act(async () => {
      accept({ status: 'accepted' });
    });
    await waitFor(() => expect(composer).toHaveValue(''));
  });

  beforeEach(() => {
    listSessionsForAgentMock.mockReset();
    naradProviderPropsMock.mockReset();
    sendUserMessageMock.mockReset();
    createSessionMock.mockReset();
    useAgentsMock.mockReturnValue({
      data: [
        { id: 'agent-1', name: 'Agent One', scope: 'user', ownerUserId: 'user-1' },
        { id: 'agent-2', name: 'Agent Two', scope: 'user', ownerUserId: 'user-1' },
      ],
      isLoading: false,
    });
    useCurrentUserMock.mockReturnValue({ data: { role: 'admin', id: 'user-1' } });
    useAgentsTabMock.mockReturnValue(createAgentsTabMock());
    listSessionsForAgentMock.mockImplementation(async (agentId: string) => [
      {
        id: agentId === 'agent-1' ? 'session-1' : 'session-2',
        targetType: 'agent',
        targetId: agentId,
        title: 'Session',
        updatedAt: '2026-08-04T00:00:00.000Z',
        createdAt: '2026-08-04T00:00:00.000Z',
        messages: [],
        compacted: false,
      },
    ]);
  });

  it('opens the create dialog for a non-admin instead of leaving the workspace', async () => {
    useCurrentUserMock.mockReturnValue({ data: { role: 'user', id: 'user-2' } });
    const { AgentPageV2 } = await import('@/pages/AgentPageV2');
    const router = createMemoryRouter(
      [
        { path: '/workspace/:agentId', element: <AgentPageV2 /> },
        { path: '/dashboard', element: <div>Dashboard</div> },
        { path: '/settings', element: <div>Settings</div> },
      ],
      { initialEntries: ['/workspace/agent-1?sessionId=session-1'] }
    );

    render(<RouterProvider router={router} />);
    await screen.findByRole('button', { name: 'Files' });
    expect(screen.queryByRole('button', { name: 'Configure agent' })).not.toBeInTheDocument();
    await userEvent.click(await screen.findByRole('button', { name: 'New Agent' }));

    expect(screen.getByTestId('agent-dialog')).toHaveTextContent('create');
    expect(router.state.location.pathname).toBe('/workspace/agent-1');
  });

  it('preserves requested sessionId on initial /workspace/:agentId deep link', async () => {
    const { AgentPageV2 } = await import('@/pages/AgentPageV2');

    const router = createMemoryRouter([{ path: '/workspace/:agentId', element: <AgentPageV2 /> }], {
      initialEntries: ['/workspace/agent-1?sessionId=session-1'],
    });

    render(<RouterProvider router={router} />);

    await waitFor(() => expect(listSessionsForAgentMock).toHaveBeenCalledWith('agent-1'));
    await waitFor(() =>
      expect(naradProviderPropsMock).toHaveBeenCalledWith(
        expect.objectContaining({ sessionId: 'session-1', agentId: 'agent-1' })
      )
    );
    // First test in the file pays the cold import of the AgentPageV2 module graph,
    // which exceeds the default timeout when the full suite runs in parallel.
  }, 15000);

  it('follows browser back/forward sessionId query updates', async () => {
    const { AgentPageV2 } = await import('@/pages/AgentPageV2');

    const router = createMemoryRouter(
      [
        {
          path: '/workspace/:agentId',
          element: (
            <>
              <AgentPageV2 />
              <SessionProbe />
            </>
          ),
        },
      ],
      {
        initialEntries: [
          '/workspace/agent-1?sessionId=session-1',
          '/workspace/agent-1?sessionId=session-2',
        ],
        initialIndex: 1,
      }
    );

    render(<RouterProvider router={router} />);

    await waitFor(() => expect(screen.getByTestId('session-id')).toHaveTextContent('session-2'));
    await waitFor(() =>
      expect(naradProviderPropsMock).toHaveBeenCalledWith(
        expect.objectContaining({ sessionId: 'session-2', agentId: 'agent-1' })
      )
    );
  });

  it('auto-selects the target agent session when switching agents in the tree', async () => {
    const { AgentPageV2 } = await import('@/pages/AgentPageV2');

    const router = createMemoryRouter([{ path: '/workspace/:agentId', element: <AgentPageV2 /> }], {
      initialEntries: ['/workspace/agent-1?sessionId=session-1'],
    });

    render(<RouterProvider router={router} />);

    await waitFor(() => expect(listSessionsForAgentMock).toHaveBeenCalledWith('agent-1'));

    await userEvent.click(await screen.findByRole('button', { name: 'Agent Two' }));

    await waitFor(() => expect(listSessionsForAgentMock).toHaveBeenCalledWith('agent-2'));
    await waitFor(() =>
      expect(naradProviderPropsMock).toHaveBeenCalledWith(
        expect.objectContaining({ sessionId: 'session-2', agentId: 'agent-2' })
      )
    );
    expect(
      screen.queryByText(/Choose a session from the sidebar|Create a new session from the sidebar/i)
    ).not.toBeInTheDocument();
  });

  it('creates a new session from the agent tree', async () => {
    const { AgentPageV2 } = await import('@/pages/AgentPageV2');

    const router = createMemoryRouter([{ path: '/workspace/:agentId', element: <AgentPageV2 /> }], {
      initialEntries: ['/workspace/agent-1?sessionId=session-1'],
    });

    render(<RouterProvider router={router} />);

    await waitFor(() =>
      expect(naradProviderPropsMock).toHaveBeenCalledWith(
        expect.objectContaining({ sessionId: 'session-1', agentId: 'agent-1' })
      )
    );

    createSessionMock.mockResolvedValueOnce({
      id: 'session-new',
      targetType: 'agent',
      targetId: 'agent-1',
      title: '[Chat] New Chat',
      updatedAt: '2026-08-04T00:00:00.000Z',
      createdAt: '2026-08-04T00:00:00.000Z',
      messages: [],
      compacted: false,
    });
    await userEvent.click(await screen.findByRole('button', { name: 'New session' }));

    await waitFor(() =>
      expect(naradProviderPropsMock).toHaveBeenCalledWith(
        expect.objectContaining({ sessionId: 'session-new', agentId: 'agent-1' })
      )
    );
  });

  it('auto-selects first session when soft-navigating to /workspace from another page', async () => {
    const { AgentPageV2 } = await import('@/pages/AgentPageV2');

    const router = createMemoryRouter(
      [
        { path: '/dashboard', element: <div>Dashboard</div> },
        { path: '/workspace', element: <AgentPageV2 /> },
        { path: '/workspace/:agentId', element: <AgentPageV2 /> },
      ],
      { initialEntries: ['/dashboard'] }
    );

    render(<RouterProvider router={router} />);

    await router.navigate('/workspace');

    await waitFor(() => expect(listSessionsForAgentMock).toHaveBeenCalledWith('agent-1'));
    await waitFor(() =>
      expect(naradProviderPropsMock).toHaveBeenCalledWith(
        expect.objectContaining({ sessionId: 'session-1', agentId: 'agent-1' })
      )
    );
  });

  it('auto-selects first session when soft-navigating to /workspace/:agentId without sessionId', async () => {
    const { AgentPageV2 } = await import('@/pages/AgentPageV2');

    const router = createMemoryRouter(
      [
        { path: '/dashboard', element: <div>Dashboard</div> },
        { path: '/workspace/:agentId', element: <AgentPageV2 /> },
      ],
      { initialEntries: ['/dashboard'] }
    );

    render(<RouterProvider router={router} />);

    await router.navigate('/workspace/agent-2');

    await waitFor(() => expect(listSessionsForAgentMock).toHaveBeenCalledWith('agent-2'));
    await waitFor(() =>
      expect(naradProviderPropsMock).toHaveBeenCalledWith(
        expect.objectContaining({ sessionId: 'session-2', agentId: 'agent-2' })
      )
    );
    expect(
      screen.queryByText(
        /Choose a session from the sidebar|Create a new session from the sidebar/i
      )
    ).not.toBeInTheDocument();
  });

  it('auto-selects session when client-navigating between agent URLs without the dropdown', async () => {
    const { AgentPageV2 } = await import('@/pages/AgentPageV2');

    const router = createMemoryRouter([{ path: '/workspace/:agentId', element: <AgentPageV2 /> }], {
      initialEntries: ['/workspace/agent-1?sessionId=session-1'],
    });

    render(<RouterProvider router={router} />);

    await waitFor(() =>
      expect(naradProviderPropsMock).toHaveBeenCalledWith(
        expect.objectContaining({ sessionId: 'session-1', agentId: 'agent-1' })
      )
    );

    naradProviderPropsMock.mockClear();
    await router.navigate('/workspace/agent-2');

    await waitFor(() => expect(listSessionsForAgentMock).toHaveBeenCalledWith('agent-2'));
    await waitFor(() =>
      expect(naradProviderPropsMock).toHaveBeenCalledWith(
        expect.objectContaining({ sessionId: 'session-2', agentId: 'agent-2' })
      )
    );
    expect(
      screen.queryByText(
        /Choose a session from the sidebar|Create a new session from the sidebar/i
      )
    ).not.toBeInTheDocument();
  });

  it('canonicalizes /workspace onto the first agent without dropping sessionId', async () => {
    const { AgentPageV2 } = await import('@/pages/AgentPageV2');

    const router = createMemoryRouter(
      [
        { path: '/workspace', element: <AgentPageV2 /> },
        { path: '/workspace/:agentId', element: <AgentPageV2 /> },
      ],
      { initialEntries: ['/workspace?sessionId=session-1'] }
    );

    render(<RouterProvider router={router} />);

    await waitFor(() => expect(router.state.location.pathname).toBe('/workspace/agent-1'));
    expect(router.state.location.search).toBe('?sessionId=session-1');
  });

  it('does not snap back to the first agent after an agents-list refetch', async () => {
    const { AgentPageV2 } = await import('@/pages/AgentPageV2');

    function WorkspaceHarness() {
      const [generation, setGeneration] = React.useState(0);
      useAgentsMock.mockReturnValue({
        data: [
          { id: 'agent-1', name: `Agent One ${generation}` },
          { id: 'agent-2', name: `Agent Two ${generation}` },
        ],
        isLoading: false,
      });
      return (
        <>
          <button type="button" onClick={() => setGeneration((current) => current + 1)}>
            refetch-agents
          </button>
          <AgentPageV2 />
        </>
      );
    }

    const router = createMemoryRouter(
      [
        { path: '/workspace', element: <WorkspaceHarness /> },
        { path: '/workspace/:agentId', element: <WorkspaceHarness /> },
      ],
      { initialEntries: ['/workspace'] }
    );
    const user = userEvent.setup();

    render(<RouterProvider router={router} />);

    await waitFor(() => expect(router.state.location.pathname).toBe('/workspace/agent-1'));
    await user.click(await screen.findByRole('button', { name: 'Agent Two 0' }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/workspace/agent-2'));
    expect(await screen.findByRole('button', { name: 'Agent Two 0' })).toHaveAttribute('aria-current', 'page');

    await user.click(screen.getByRole('button', { name: 'refetch-agents' }));

    expect(await screen.findByRole('button', { name: 'Agent Two 1' })).toHaveAttribute('aria-current', 'page');
    expect(router.state.location.pathname).toBe('/workspace/agent-2');
  });
});
