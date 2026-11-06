import { screen } from '@testing-library/dom';
import { render } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { apiClient } from '@/lib/apiClient';
import { fetchConnector, fetchConnectors } from '@/features/connectors/api/connectors';
import { ConnectorCard } from '@/features/connectors/components/ConnectorCard';
import { RemoteMcpEditor } from '@/features/connectors/components/RemoteMcpEditor';

vi.mock('@/lib/apiClient', () => ({ apiClient: { get: vi.fn(), post: vi.fn() } }));
vi.mock('@/features/connectors/api/queries', () => ({
  useConnectorOAuth: () => ({ data: [] }),
  useConnectorCredentialStatus: () => ({ data: undefined, isLoading: false }),
  useBeginConnectorOAuth: () => ({ mutate: vi.fn(), isPending: false }),
  useDisconnectConnectorOAuth: () => ({ mutate: vi.fn(), isPending: false, data: null }),
}));
vi.mock('@/features/approvals', () => ({ HitlFunctionStat: () => null, useHitlStats: () => ({ data: { items: [] } }) }));

const legacy = { id: 'legacy', name: 'Legacy', transport: 'http', httpUrl: 'https://example.com/mcp', scope: 'system', requiresCredentialReentry: true, credentials: [], httpHeaders: [] };

describe('remote connector API mapping', () => {
  it('carries the server warning through list and detail to the card and edit confirmation', async () => {
    vi.mocked(apiClient.get).mockResolvedValueOnce({ data: { connectors: [legacy] } }).mockResolvedValueOnce({ data: legacy });
    const [fromList] = await fetchConnectors();
    const fromDetail = await fetchConnector('legacy');
    expect(fromList.requiresCredentialReentry).toBe(true);
    expect(fromDetail.requiresCredentialReentry).toBe(true);
    const card = render(<ConnectorCard connector={fromList} canManage onToggle={vi.fn()} onEdit={vi.fn()} onDelete={vi.fn()} toggling={false} />);
    expect(screen.getByRole('alert')).toHaveTextContent('enter its key again');
    card.unmount();
    render(<RemoteMcpEditor connector={fromDetail} onSave={vi.fn()} onTest={vi.fn()} onClose={vi.fn()} />);
    expect(screen.getByRole('checkbox', { name: /I reviewed the new authentication choice/ })).not.toBeChecked();
  });

  it('tells a nonmanager to contact an administrator and offers no unusable edit action', async () => {
    vi.mocked(apiClient.get).mockResolvedValueOnce({ data: legacy });
    const connector = await fetchConnector('legacy');
    render(<ConnectorCard connector={connector} canManage={false} onToggle={vi.fn()} onEdit={vi.fn()} onDelete={vi.fn()} toggling={false} />);
    expect(screen.getByRole('alert')).toHaveTextContent('Ask the connector owner or an administrator');
    expect(screen.queryByRole('button', { name: 'Edit' })).not.toBeInTheDocument();
  });
});
