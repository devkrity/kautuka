import { AgentsTab } from '@/features/settings/components/AgentsTab';
import { render, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const { startCreateMock, startEditMock, useAgentsTabMock } = vi.hoisted(() => ({
  startCreateMock: vi.fn(),
  startEditMock: vi.fn(),
  useAgentsTabMock: vi.fn(),
}));

vi.mock('@/features/settings/hooks/useAgentsTab', () => ({
  useAgentsTab: useAgentsTabMock,
}));

vi.mock('@/features/settings/api/queries', () => ({
  useAgentToolPolicy: () => ({ data: null }),
  useAgentRevisions: () => ({ data: undefined, isLoading: false }),
  useAgentRevisionDiff: () => ({ data: undefined, isLoading: false }),
}));

describe('AgentsTab requested actions', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    useAgentsTabMock.mockReturnValue({
      agents: [
        {
          id: 'agent-1',
          name: 'Agent One',
          modelId: 'model-1',
          scope: 'user',
          superExecMode: 'disabled',
          mcpBindings: [],
          skillAttachments: [],
          session: { maxHistoryRuns: 5 },
          sandbox: { enabled: false },
        },
      ],
      models: [],
      mcpServers: [],
      skills: [],
      loading: false,
      editingId: null,
      savingId: null,
      draftAgent: null,
      personaDefinitions: { styles: [], tones: [], verbosity_levels: [] },
      organizationDefaults: null,
      patchAgent: vi.fn(),
      getConnectorMode: vi.fn(() => 'off'),
      setConnectorMode: vi.fn(),
      getToolMode: vi.fn(() => 'off'),
      setToolMode: vi.fn(),
      toggleToolPanel: vi.fn(),
      isToolPanelOpen: vi.fn(() => false),
      saveAgent: vi.fn(),
      removeAgent: vi.fn(),
      startCreate: startCreateMock,
      startEdit: startEditMock,
      cancelEdit: vi.fn(),
      statusItems: [],
      getAgentToolMode: vi.fn(() => 'off'),
      setAgentToolMode: vi.fn(),
    });
  });

  it('starts a new draft after the agent catalog has loaded', async () => {
    const handled = vi.fn();
    render(<AgentsTab createRequested onRequestedActionHandled={handled} />);

    await waitFor(() => expect(startCreateMock).toHaveBeenCalledTimes(1));
    expect(startEditMock).not.toHaveBeenCalled();
    expect(handled).toHaveBeenCalledTimes(1);
  });

  it('opens the requested existing agent for editing', async () => {
    const handled = vi.fn();
    render(<AgentsTab requestedAgentId="agent-1" onRequestedActionHandled={handled} />);

    await waitFor(() => expect(startEditMock).toHaveBeenCalledWith('agent-1'));
    expect(startCreateMock).not.toHaveBeenCalled();
    expect(handled).toHaveBeenCalledTimes(1);
  });
});
