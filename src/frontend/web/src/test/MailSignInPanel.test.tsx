import { ConnectorSignInPanel } from '@/features/connectors/components/MailSignInPanel';
import { fireEvent, screen } from '@testing-library/dom';
import { render, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const beginMutation = vi.fn();
const disconnectMutation = vi.fn();
const pendingState = vi.hoisted(() => ({ pending: false, boundFlow: false }));
const network = vi.hoisted(() => ({ post: vi.fn(), get: vi.fn(), refetch: vi.fn() }));
vi.mock('@/lib/apiClient', () => ({ apiClient: { post: network.post, get: network.get } }));
vi.mock('@/features/auth', () => ({ runBoundMailLogin: async (_id: string, _signal: AbortSignal, before: () => Promise<void>) => before() }));

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
    refetch: network.refetch,
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
    network.post.mockReset(); network.get.mockReset(); network.refetch.mockReset();
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

  it('asks before allowing the broker and the mail service for this connector', async () => {
    pendingState.boundFlow = true;
    network.get.mockResolvedValue({ data: { revision: 7 } });
    const approved = new Set<string>();
    network.post.mockImplementation(async (path: string) => {
      if (path.endsWith('/auth')) return { data: { allowed: approved.has('auth'), requestId: 'auth', destination: 'auth.kautuka.io' } };
      if (path.endsWith('/mail')) return { data: { allowed: approved.has('mail'), requestId: 'mail', destination: 'gmail.googleapis.com' } };
      if (path.includes('/auth/decision')) approved.add('auth');
      if (path.includes('/mail/decision')) approved.add('mail');
      return { data: {} };
    });
    render(<ConnectorSignInPanel connectorId="kautuka-google-mail" />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in with Google' }));
    expect(await screen.findByText('Allow this connector to reach auth.kautuka.io?')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Allow' }));
    expect(await screen.findByText('Allow this connector to reach gmail.googleapis.com?')).toBeTruthy();
    expect(screen.getByText('Signed in. Checking mailbox network access…')).toBeTruthy();
    expect(network.refetch).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Allow' }));
    await waitFor(() => expect(network.post).toHaveBeenCalledWith('/network-access/requests/mail/decision',
      { allow: true, scope: 'connector', expectedRevision: 7 }, expect.any(Object)));
    await waitFor(() => expect(network.refetch).toHaveBeenCalledOnce());
    expect(network.post).toHaveBeenCalledWith('/network-access/requests/auth/decision',
      { allow: true, scope: 'connector', expectedRevision: 7 }, expect.any(Object));
  });

  it('keeps sign-in cancelled when the user declines broker access', async () => {
    pendingState.boundFlow = true;
    network.post.mockResolvedValue({ data: { allowed: false, requestId: 'auth', destination: 'auth.kautuka.io' } });
    render(<ConnectorSignInPanel connectorId="kautuka-google-mail" />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in with Google' }));
    expect(await screen.findByText('Allow this connector to reach auth.kautuka.io?')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Not now' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(network.get).not.toHaveBeenCalled();
    expect(network.refetch).not.toHaveBeenCalled();
  });

  it('keeps the sign-in when the user declines mail service access', async () => {
    pendingState.boundFlow = true;
    network.post.mockImplementation(async (path: string) => path.endsWith('/auth')
      ? { data: { allowed: true } }
      : { data: { allowed: false, requestId: 'mail', destination: 'gmail.googleapis.com' } });
    render(<ConnectorSignInPanel connectorId="kautuka-google-mail" />);
    fireEvent.click(screen.getByRole('button', { name: 'Sign in with Google' }));
    expect(await screen.findByText('Allow this connector to reach gmail.googleapis.com?')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Not now' }));
    expect(await screen.findByText('Signed in. Access to gmail.googleapis.com was not allowed.')).toBeTruthy();
    expect(network.refetch).toHaveBeenCalled();
    expect(network.get).not.toHaveBeenCalled();
  });
});
