import { SettingsPage } from '@/pages/SettingsPage';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';

const { agentsTabMock } = vi.hoisted(() => ({
  agentsTabMock: vi.fn(),
}));

vi.mock('@/features/settings', () => ({
  ProvidersTab: () => <div>Providers</div>,
  ModelsTab: () => <div>Models</div>,
  AgentsTab: (props: {
    requestedAgentId?: string | null;
    createRequested?: boolean;
    onRequestedActionHandled?: () => void;
  }) => {
    agentsTabMock(props);
    return (
      <button type="button" onClick={props.onRequestedActionHandled}>
        Agent settings
      </button>
    );
  },
  UsersTab: () => <div>Users</div>,
  AdvancedTab: () => <div>Advanced</div>,
  OrganizationTab: () => <div>Organization</div>,
}));

function LocationProbe() {
  const location = useLocation();
  return <span data-testid="location-probe">{`${location.pathname}${location.search}`}</span>;
}

describe('SettingsPage agent deep links', () => {
  it('opens new-agent creation on the canonical Agents tab', async () => {
    const user = userEvent.setup();
    render(
      <MemoryRouter initialEntries={['/settings?tab=agents&action=create']}>
        <SettingsPage />
        <LocationProbe />
      </MemoryRouter>
    );

    await waitFor(() =>
      expect(agentsTabMock).toHaveBeenCalledWith(
        expect.objectContaining({ createRequested: true, requestedAgentId: null })
      )
    );

    await user.click(screen.getByRole('button', { name: 'Agent settings' }));
    await waitFor(() =>
      expect(screen.getByTestId('location-probe')).toHaveTextContent('/settings?tab=agents')
    );
  });

  it('opens a requested existing agent on the canonical Agents tab', async () => {
    render(
      <MemoryRouter initialEntries={['/settings?tab=agents&agentId=agent-1']}>
        <SettingsPage />
      </MemoryRouter>
    );

    await waitFor(() =>
      expect(agentsTabMock).toHaveBeenCalledWith(
        expect.objectContaining({ createRequested: false, requestedAgentId: 'agent-1' })
      )
    );
  });
});
