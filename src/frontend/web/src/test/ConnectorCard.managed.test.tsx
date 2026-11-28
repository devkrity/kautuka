import { ConnectorCard } from '@/features/connectors/components/ConnectorCard';
import { connectorSchema, type Connector } from '@/features/connectors/schemas/connectorSchema';
import { screen } from '@testing-library/dom';
import { fireEvent, render } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

const { testConnector } = vi.hoisted(() => ({ testConnector: vi.fn() }));

vi.mock('@/features/connectors/api/queries', () => ({
  useTestConnector: () => ({ mutate: testConnector, isPending: false }),
  useConnectorOAuth: () => ({
    data: [
      {
        connectorId: 'kautuka-m365-mail',
        connected: true,
        configured: true,
        message: 'Signed in.',
        signInLabel: 'Sign in with Microsoft',
        revokeUrl: 'https://myaccount.microsoft.com/permissions',
      },
    ],
  }),
  useConnectorCredentialStatus: () => ({ data: undefined, isLoading: false }),
  useBeginConnectorOAuth: () => ({ mutate: vi.fn(), isPending: false }),
  useDisconnectConnectorOAuth: () => ({ mutate: vi.fn(), isPending: false, data: null }),
}));

vi.mock('@/features/approvals', () => ({
  HitlFunctionStat: () => null,
  useHitlStats: () => ({ data: { items: [] } }),
}));

const mail = {
  id: 'kautuka-m365-mail',
  name: 'Microsoft 365 mail',
  description: 'Read, draft, and send mail.',
  transport: 'container',
  containerCommand: 'kautuka-m365-mail',
  containerImage: 'mcp/markitdown',
  envKeys: ['KAUTUKA_M365_REFRESH_TOKEN'],
  enabled: true,
  scope: 'system',
  isInternalSystemConnector: true,
} as Connector;

it('shows refused-setting guidance where the connector owner manages it', () => {
  const connector = connectorSchema.parse({
    id: 'personal', name: 'Family calendar', transport: 'container', scope: 'user',
    ownerUserId: 'owner', enabled: true,
    notices: ["Setting 'HTTPS_PROXY' is managed by Kautuka. Edit this connector to choose a supported setting."],
  });
  render(<ConnectorCard connector={connector} canManage onToggle={vi.fn()} onEdit={vi.fn()}
    onDelete={vi.fn()} toggling={false} />);
  expect(screen.getByRole('alert')).toHaveTextContent("HTTPS_PROXY");
});

describe('ConnectorCard managed sign-in connector', () => {
  it('lets a regular user enable and disable a shared connector without granting deletion', () => {
    const connector = connectorSchema.parse({
      id: 'kautuka-web-tools',
      name: 'Kautuka Web Tools',
      transport: 'container',
      scope: 'system',
      enabled: false,
    });
    const onToggle = vi.fn();
    const props = { canManage: false, onToggle, onEdit: vi.fn(), onDelete: vi.fn() };
    const { rerender } = render(<ConnectorCard {...props} connector={connector} toggling={false} />);

    expect(screen.getByTitle('Only admins can delete system connectors')).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Disabled for me' })).toBeEnabled();
    fireEvent.click(screen.getByRole('button', { name: 'Disabled for me' }));
    expect(onToggle).toHaveBeenCalledWith(connector.id, true);

    rerender(<ConnectorCard {...props} connector={connector} toggling />);
    expect(screen.getByRole('button', { name: 'Enabling…' })).toBeDisabled();
    expect(screen.queryByRole('button', { name: 'Disabled for me' })).not.toBeInTheDocument();

    rerender(<ConnectorCard {...props} connector={{ ...connector, enabled: true }} toggling={false} />);
    const enabled = screen.getByRole('button', { name: 'Enabled for me' });
    expect(enabled).toBeEnabled();
    expect(screen.getByText(/open that helper in Workspace/)).toBeVisible();
    fireEvent.click(enabled);
    expect(onToggle).toHaveBeenLastCalledWith(connector.id, false);
  });

  it('offers to refresh the saved mail connector and displays its discovered tools', () => {
    render(<ConnectorCard connector={{ ...mail, toolCount: 2, toolNames: ['mail_list', 'mail_read'] }} canManage onToggle={vi.fn()} onEdit={vi.fn()} onDelete={vi.fn()} toggling={false} />);
    expect(screen.getByText('mail_list')).toBeInTheDocument();
    expect(screen.getByText('mail_read')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Refresh tools' }));
    expect(testConnector).toHaveBeenCalledWith(expect.objectContaining({ id: mail.id }), expect.anything());
  });

  it('shows the discovery failure so the user can act on it', () => {
    testConnector.mockImplementationOnce((_request, options) => options.onSuccess({ success: false, message: 'Network approval required.' }));
    render(<ConnectorCard connector={mail} canManage onToggle={vi.fn()} onEdit={vi.fn()} onDelete={vi.fn()} toggling={false} />);
    fireEvent.click(screen.getByRole('button', { name: 'Refresh tools' }));
    expect(screen.getByRole('status')).toHaveTextContent('Network approval required.');
  });
  it('offers enable and sign-in without launch settings', () => {
    render(
      <ConnectorCard
        connector={mail}
        canManage
        onToggle={vi.fn()}
        onEdit={vi.fn()}
        onDelete={vi.fn()}
        toggling={false}
      />,
    );

    expect(screen.getByRole('button', { name: 'Enabled for me' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Disconnect' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Edit' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Delete' })).toBeNull();
    expect(screen.queryByText('mcp/markitdown')).toBeNull();
    expect(screen.queryByText('KAUTUKA_M365_REFRESH_TOKEN')).toBeNull();
  });

  it('shows the re-entry warning for a migrated remote connector', () => {
    const connector = connectorSchema.parse({ id: 'legacy', name: 'Legacy', transport: 'http', httpUrl: 'https://example.com/mcp', requiresCredentialReentry: true });
    render(<ConnectorCard connector={connector} canManage onToggle={vi.fn()} onEdit={vi.fn()} onDelete={vi.fn()} toggling={false} />);
    expect(screen.getByText('Key needs to be entered again')).toBeInTheDocument();
    expect(screen.getByRole('alert')).toHaveTextContent('enter its key again');
  });
});
