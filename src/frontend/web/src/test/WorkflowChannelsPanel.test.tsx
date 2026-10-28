import { WorkflowChannelsPanel } from '@/features/workflows/components/WorkflowChannelsPanel';
import { WorkflowSettingsDialog } from '@/features/workflows/components/WorkflowSettingsDialog';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import type { ReactNode } from 'react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';

const { apiClientGetMock, apiClientPutMock } = vi.hoisted(() => ({
  apiClientGetMock: vi.fn(),
  apiClientPutMock: vi.fn(),
}));

vi.mock('@/lib/apiClient', () => ({
  apiClient: {
    get: apiClientGetMock,
    put: apiClientPutMock,
  },
}));

const accountChannel = {
  providerKey: 'telegram',
  registrationId: 'telegram:user-1:default',
  externalUserId: '998877',
  status: 'ACTIVE',
  settings: { channel_device_id: '998877' },
  configuredSecretFieldKeys: ['bot_token'],
};

function renderWithRouter(ui: ReactNode) {
  return render(<MemoryRouter>{ui}</MemoryRouter>);
}

describe('workflow channels panel', () => {
  it('sends reviews to the account bot until a workflow bot is chosen', async () => {
    apiClientGetMock.mockResolvedValue({ data: { channels: [accountChannel] } });
    renderWithRouter(<WorkflowChannelsPanel scope="shared" />);

    expect(await screen.findByRole('button', { name: 'Account bot' })).toBeInTheDocument();
    expect(
      screen.getByText('Review cards go through the Telegram bot on your account.')
    ).toBeInTheDocument();
    expect(screen.queryByLabelText('Bot token')).not.toBeInTheDocument();
  });

  it('links to the Users settings tab for the device ID', async () => {
    apiClientGetMock.mockResolvedValue({ data: { channels: [] } });
    renderWithRouter(<WorkflowChannelsPanel scope="shared" />);

    const settingsLink = await screen.findByRole('link', { name: 'Settings' });
    expect(settingsLink).toHaveAttribute('href', '/settings?tab=users');
  });

  it('saves a shared workflow bot with the account chat id', async () => {
    apiClientGetMock.mockResolvedValue({ data: { channels: [accountChannel] } });
    apiClientPutMock.mockResolvedValue({ data: { channels: [accountChannel] } });
    renderWithRouter(<WorkflowChannelsPanel scope="shared" />);

    await userEvent.click(await screen.findByRole('button', { name: 'Separate bot' }));
    expect(
      screen.getByText(
        'This replaces the account bot for workflow reviews. It uses the Telegram chat already on your account.'
      )
    ).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('Bot token'), { target: { value: 'workflow-token' } });
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() =>
      expect(apiClientPutMock).toHaveBeenCalledWith('/settings/me/channels/telegram', {
        settings: {
          channel_device_id: '998877',
          bot_token: 'workflow-token',
        },
        attachmentRole: 'workflows',
        attachmentSubject: undefined,
        clear: false,
      })
    );
  });

  it('uses a separate bot for one workflow', async () => {
    apiClientGetMock.mockResolvedValue({ data: { channels: [accountChannel] } });
    apiClientPutMock.mockResolvedValue({ data: { channels: [accountChannel] } });
    renderWithRouter(<WorkflowChannelsPanel scope="workflow" workflowId="wf-1" />);

    expect(await screen.findByRole('button', { name: 'Account bot' })).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Separate bot' }));
    fireEvent.change(screen.getByLabelText('Bot token'), { target: { value: 'override-token' } });
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() =>
      expect(apiClientPutMock).toHaveBeenCalledWith('/settings/me/channels/telegram', {
        settings: {
          channel_device_id: '998877',
          bot_token: 'override-token',
        },
        attachmentRole: 'workflow',
        attachmentSubject: 'wf-1',
        clear: false,
      })
    );
  });

  it('puts Channels inside workflow settings', async () => {
    apiClientGetMock.mockResolvedValue({ data: { channels: [accountChannel] } });
    renderWithRouter(<WorkflowSettingsDialog open onClose={() => undefined} />);

    expect(screen.getByRole('heading', { name: 'Workflow settings' })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Channels' })).toBeInTheDocument();
    expect(
      await screen.findByText('Review cards go through the Telegram bot on your account.')
    ).toBeInTheDocument();
  });
});
