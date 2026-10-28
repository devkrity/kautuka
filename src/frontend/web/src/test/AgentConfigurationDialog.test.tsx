import { AgentConfigurationDialog } from '@/features/settings/components/AgentConfigurationDialog';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const {
  cancelEditMock,
  removeAgentMock,
  saveAgentMock,
  startCreateMock,
  startEditMock,
  useAgentsTabMock,
} = vi.hoisted(() => ({
  cancelEditMock: vi.fn(),
  removeAgentMock: vi.fn(),
  saveAgentMock: vi.fn(),
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

vi.mock('@/features/settings/components/AgentForm', () => ({
  AgentForm: ({ agent }: { agent: { id: string; name: string } }) => (
    <div data-testid="agent-form">{agent.name || 'Unnamed agent'}</div>
  ),
}));

const userAgent = {
  id: 'agent-1',
  name: 'Agent One',
  modelId: 'model-1',
  scope: 'user' as const,
  enabled: true,
  ownerUserId: 'user-1',
  systemPrompt: '',
  superExecMode: 'disabled' as const,
  mcpBindings: [],
  skillAttachments: [],
  toolExposure: { exposedAsTool: false, description: '', invocationTimeoutSeconds: 120 },
  approvalConfig: {},
  persona: {},
  session: {
    historyEnabled: true,
    maxHistoryRuns: 5,
    summariesEnabled: true,
    memoryFactExtractionEnabled: true,
    summaryModelId: null,
  },
  sandbox: { enabled: false, allowedLanguages: [], maxMemory: '512m', maxTimeout: 30 },
};

const createDraft = { ...userAgent, id: '', name: '', ownerUserId: '' };

function hookValue(overrides: Record<string, unknown> = {}) {
  return {
    agents: [userAgent],
    models: [],
    mcpServers: [],
    skills: [],
    loading: false,
    editingId: userAgent.id,
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
    saveAgent: saveAgentMock,
    removeAgent: removeAgentMock,
    startCreate: startCreateMock,
    startEdit: startEditMock,
    cancelEdit: cancelEditMock,
    getAgentToolMode: vi.fn(() => 'off'),
    setAgentToolMode: vi.fn(),
    ...overrides,
  };
}

describe('AgentConfigurationDialog', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    saveAgentMock.mockResolvedValue(userAgent.id);
    removeAgentMock.mockResolvedValue(true);
    useAgentsTabMock.mockReturnValue(hookValue());
  });

  it('initializes create mode and reports the saved agent', async () => {
    useAgentsTabMock.mockReturnValue(
      hookValue({ agents: [], editingId: '', draftAgent: createDraft })
    );
    const onSaved = vi.fn();
    const onClose = vi.fn();

    render(
      <AgentConfigurationDialog
        open
        mode="create"
        actor={{ id: 'user-1', role: 'member' }}
        onSaved={onSaved}
        onDeleted={vi.fn()}
        onClose={onClose}
      />
    );

    await waitFor(() => expect(startCreateMock).toHaveBeenCalledTimes(1));
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(saveAgentMock).toHaveBeenCalledWith(createDraft));
    expect(onSaved).toHaveBeenCalledWith(userAgent.id);
    expect(onClose).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole('button', { name: 'Delete' })).not.toBeInTheDocument();
  });

  it('initializes edit mode for the requested agent', async () => {
    render(
      <AgentConfigurationDialog
        open
        mode="edit"
        agentId={userAgent.id}
        actor={{ id: 'user-1', role: 'member' }}
        onSaved={vi.fn()}
        onDeleted={vi.fn()}
        onClose={vi.fn()}
      />
    );

    await waitFor(() => expect(startEditMock).toHaveBeenCalledWith(userAgent.id));
    expect(screen.getByTestId('agent-form')).toHaveTextContent('Agent One');
    expect(screen.getByRole('button', { name: 'Delete' })).toBeInTheDocument();
  });

  it('hides delete when the actor does not own a user agent', () => {
    render(
      <AgentConfigurationDialog
        open
        mode="edit"
        agentId={userAgent.id}
        actor={{ id: 'user-2', role: 'member' }}
        onSaved={vi.fn()}
        onDeleted={vi.fn()}
        onClose={vi.fn()}
      />
    );

    expect(screen.queryByRole('button', { name: 'Delete' })).not.toBeInTheDocument();
  });

  it('reports deletion only after the canonical delete succeeds', async () => {
    const onDeleted = vi.fn();
    const onClose = vi.fn();
    render(
      <AgentConfigurationDialog
        open
        mode="edit"
        agentId={userAgent.id}
        actor={{ id: 'user-1', role: 'member' }}
        onSaved={vi.fn()}
        onDeleted={onDeleted}
        onClose={onClose}
      />
    );

    fireEvent.click(screen.getByRole('button', { name: 'Delete' }));
    const deleteButtons = await screen.findAllByRole('button', { name: 'Delete' });
    fireEvent.click(deleteButtons[deleteButtons.length - 1]);

    await waitFor(() => expect(removeAgentMock).toHaveBeenCalledWith(userAgent.id));
    expect(onDeleted).toHaveBeenCalledWith(userAgent.id);
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('does not report a failed deletion', async () => {
    removeAgentMock.mockResolvedValue(false);
    const onDeleted = vi.fn();
    const onClose = vi.fn();
    render(
      <AgentConfigurationDialog
        open
        mode="edit"
        agentId={userAgent.id}
        actor={{ id: 'user-1', role: 'member' }}
        onSaved={vi.fn()}
        onDeleted={onDeleted}
        onClose={onClose}
      />
    );

    fireEvent.click(screen.getByRole('button', { name: 'Delete' }));
    const deleteButtons = await screen.findAllByRole('button', { name: 'Delete' });
    fireEvent.click(deleteButtons[deleteButtons.length - 1]);

    await waitFor(() => expect(removeAgentMock).toHaveBeenCalledWith(userAgent.id));
    expect(onDeleted).not.toHaveBeenCalled();
    expect(onClose).not.toHaveBeenCalled();
  });
});
