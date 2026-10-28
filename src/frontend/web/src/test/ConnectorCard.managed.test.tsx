import { ConnectorCard } from '@/features/connectors/components/ConnectorCard';
import type { Connector } from '@/features/connectors/schemas/connectorSchema';
import { screen } from '@testing-library/dom';
import { render } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

vi.mock('@/features/connectors/api/queries', () => ({
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

describe('ConnectorCard managed sign-in connector', () => {
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

    expect(screen.getByRole('button', { name: 'Enabled' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Disconnect' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Edit' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Delete' })).toBeNull();
    expect(screen.queryByText('mcp/markitdown')).toBeNull();
    expect(screen.queryByText('KAUTUKA_M365_REFRESH_TOKEN')).toBeNull();
  });
});
