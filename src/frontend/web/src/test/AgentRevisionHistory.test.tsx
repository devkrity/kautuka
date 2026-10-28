import { AgentRevisionHistory } from '@/features/settings/components/AgentRevisionHistory';
import { fireEvent, render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const { useAgentRevisionDiffMock, useAgentRevisionsMock } = vi.hoisted(() => ({
  useAgentRevisionDiffMock: vi.fn(),
  useAgentRevisionsMock: vi.fn(),
}));

vi.mock('@/features/settings/api/queries', () => ({
  useAgentRevisions: useAgentRevisionsMock,
  useAgentRevisionDiff: useAgentRevisionDiffMock,
}));

describe('AgentRevisionHistory', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    useAgentRevisionsMock.mockReturnValue({
      isLoading: false,
      data: {
        agentId: 'agent-1',
        currentRevision: 2,
        revisions: [
          {
            revision: 2,
            createdAt: '2026-08-22T00:00:00.000Z',
            changedSettingIds: ['name'],
            changedSettingNames: ['Name'],
          },
          {
            revision: 1,
            createdAt: '2026-08-21T00:00:00.000Z',
            changedSettingIds: [],
            changedSettingNames: [],
          },
        ],
      },
    });
    useAgentRevisionDiffMock.mockReturnValue({
      isLoading: false,
      data: {
        agentId: 'agent-1',
        fromRevision: 1,
        toRevision: 2,
        createdAt: '2026-08-22T00:00:00.000Z',
        changes: [
          {
            settingId: 'name',
            settingName: 'Name',
            before: 'Alpha Agent',
            after: 'Alpha Agent v2',
          },
        ],
      },
    });
  });

  it('renders revision summaries and expanded before/after values', () => {
    render(<AgentRevisionHistory agentId="agent-1" />);

    expect(screen.getByText('Configuration history')).toBeInTheDocument();
    expect(screen.getAllByText(/Revision 2/).length).toBeGreaterThan(0);
    expect(screen.getByText('Name')).toBeInTheDocument();
    expect(screen.getByText('Initial configuration')).toBeInTheDocument();

    fireEvent.click(screen.getAllByRole('button', { name: 'Inspect' })[0]);

    expect(screen.getByText('Before')).toBeInTheDocument();
    expect(screen.getByText('After')).toBeInTheDocument();
    expect(screen.getByText('Alpha Agent')).toBeInTheDocument();
    expect(screen.getByText('Alpha Agent v2')).toBeInTheDocument();
  });
});
