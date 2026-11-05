import { ConnectorSignInPanel } from '@/features/connectors/components/MailSignInPanel';
import { fireEvent, screen } from '@testing-library/dom';
import { render } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const beginMutation = vi.fn();
const disconnectMutation = vi.fn();
const pendingState = vi.hoisted(() => ({ pending: false, boundFlow: false }));

vi.mock('@/features/connectors/api/queries', () => ({
  useConnectorOAuth: () => ({
    data: [
      {
        connectorId: 'kautuka-google-mail',
        connected: false,
        hasSavedCredential: true,
        configured: true,
        message: 'Reconnect this mailbox to continue using it.',
        signInLabel: 'Sign in with Google',
        revokeUrl: 'https://myaccount.google.com/permissions',
        pending: pendingState.pending,
        boundFlow: pendingState.boundFlow,
      },
    ],
  }),
  useBeginConnectorOAuth: () => ({ isPending: false, mutate: beginMutation }),
  useDisconnectConnectorOAuth: () => ({ isPending: false, mutate: disconnectMutation }),
}));

describe('connector sign-in migration state', () => {
  beforeEach(() => {
    beginMutation.mockReset();
    disconnectMutation.mockReset();
    pendingState.pending = false;
    pendingState.boundFlow = false;
  });

  it('offers removal and prevents a new sign-in while an activation is pending', () => {
    pendingState.pending = true;
    pendingState.boundFlow = true;
    render(<ConnectorSignInPanel connectorId="kautuka-google-mail" />);
    expect(screen.getByRole('button', { name: 'Sign in with Google' })).toBeDisabled();
    fireEvent.click(screen.getByRole('button', { name: 'Remove saved sign-in' }));
    expect(disconnectMutation).toHaveBeenCalledWith('kautuka-google-mail', expect.any(Object));
  });

  it('offers reconnect and local removal for a legacy hosted credential', () => {
    render(<ConnectorSignInPanel connectorId="kautuka-google-mail" />);

    expect(screen.getByText('Reconnect this mailbox to continue using it.')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Sign in with Google' })).toBeTruthy();
    expect(screen.getByRole('link', { name: 'Manage provider permissions' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Remove saved sign-in' }));
    expect(disconnectMutation).toHaveBeenCalledWith('kautuka-google-mail', expect.any(Object));
  });
});
