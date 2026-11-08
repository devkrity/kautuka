import React from 'react';
import { render } from '@testing-library/react';
import { screen, waitFor, within } from '@testing-library/dom';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { NetworkAccessPage } from '@/pages/NetworkAccessPage';
import type { NetworkAccessView } from '@/features/network-access/networkAccess';

const { get, post, put } = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn(), put: vi.fn() }));
const viewer = vi.hoisted(() => ({ role: 'admin' }));
vi.mock('@/features/auth', () => ({
  useCurrentUser: () => ({ data: { id: 'owner', role: viewer.role } }),
}));
vi.mock('@/lib/apiClient', () => ({ apiClient: { get, post, put } }));
const identity = {
  scopeType: 'agent',
  userId: 'owner',
  agentId: 'agent-1',
  role: 'connector',
  componentId: 'mail',
};
const fixture = (): NetworkAccessView => ({
  enabled: true,
  revision: 7,
  activeRevision: 7,
  activeVersion: 'accepted',
  activationState: 'active',
  lastError: null,
  rules: [],
  defaults: [],
  sources: [identity],
  requests: [
    {
      id: 'request-1',
      identity,
      destination: 'graph.microsoft.com',
      port: 443,
      attempts: 3,
      status: 'pending',
      createdAtUtc: '2026-11-07T00:00:00Z',
      lastSeenAtUtc: '2026-11-07T00:01:00Z',
    },
  ],
});
const mockView = (data: NetworkAccessView) =>
  get.mockImplementation(async (path: string) => ({
    data: path.endsWith('/audit')
      ? { events: [], nextBefore: 0, coverageGaps: 0, unattributedConnections: 0, lastError: null }
      : data,
  }));
const renderPage = () => {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <NetworkAccessPage />
    </QueryClientProvider>
  );
};

describe('NetworkAccessPage', () => {
  beforeEach(() => {
    vi.resetAllMocks();
    viewer.role = 'admin';
    mockView(fixture());
  });

  it('loads further policy pages with the displayed revision', async () => {
    const first = { ...fixture(), nextOffset: 50, ruleCount: 51 };
    const second = {
      ...fixture(),
      nextOffset: 0,
      requests: [],
      rules: [
        {
          id: 'rule-51',
          identity,
          destination: 'next.example.com',
          port: 443,
          allowed: true,
          updatedAtUtc: null,
        },
      ],
    };
    get.mockImplementation(async (path: string, options?: { params?: { offset?: number } }) => ({
      data: path.endsWith('/audit')
        ? {
            events: [],
            nextBefore: 0,
            coverageGaps: 0,
            unattributedConnections: 0,
            lastError: null,
          }
        : options?.params?.offset === 50
          ? second
          : first,
    }));
    renderPage();
    await userEvent.click(
      await screen.findByRole('button', { name: 'Load more policy and requests' })
    );
    await userEvent.click(screen.getByRole('tab', { name: 'Permissions' }));
    expect(await screen.findByText('Allow next.example.com:443')).toBeInTheDocument();
    expect(get).toHaveBeenCalledWith('/network-access', { params: { offset: 50, revision: 7 } });
    expect(
      screen.queryByRole('button', { name: 'Load more policy and requests' })
    ).not.toBeInTheDocument();
  });

  it.each([
    ['Allow', true],
    ['Keep blocked', false],
  ] as const)(
    'persists %s with the displayed revision and does not replay a tool',
    async (label, allowed) => {
      const next = fixture();
      next.revision = 8;
      next.activeRevision = 8;
      next.requests[0].status = allowed ? 'allowed' : 'blocked';
      post.mockResolvedValue({ data: next });
      renderPage();
      await userEvent.click(await screen.findByRole('button', { name: label }));
      if (allowed) {
        expect(post).not.toHaveBeenCalled();
        await userEvent.click(screen.getByRole('button', { name: 'Confirm allow' }));
      }
      await waitFor(() =>
        expect(post).toHaveBeenCalledWith('/network-access/requests/request-1/decision', {
          allowed,
          expectedRevision: 7,
        })
      );
      expect(post).toHaveBeenCalledTimes(1);
      expect(await screen.findByText('No pending network approvals.')).toBeInTheDocument();
      expect(screen.getByText(/approving access never repeats an action/)).toBeInTheDocument();
    }
  );

  it('saves an exact component rule without a caller-supplied actor', async () => {
    put.mockResolvedValue({ data: fixture() });
    renderPage();
    await userEvent.click(await screen.findByRole('tab', { name: 'Permissions' }));
    await userEvent.click(screen.getByRole('button', { name: 'Add rule' }));
    const component = await screen.findByLabelText('Component');
    await userEvent.selectOptions(
      component,
      screen.getByRole('option', { name: /Connector.*owner/ })
    );
    await userEvent.type(screen.getByLabelText('Hostname or IPv4 address'), 'api.example.com');
    await userEvent.click(screen.getByRole('button', { name: 'Save rule' }));
    await waitFor(() =>
      expect(put).toHaveBeenCalledWith('/network-access/rules', {
        identity,
        destination: 'api.example.com',
        port: 443,
        allowed: true,
        expectedRevision: 7,
      })
    );
  });

  it('distinguishes owners of the same connector when saving rules and approving requests', async () => {
    const data = fixture();
    const otherIdentity = { ...identity, userId: 'other-owner' };
    data.sources.push(otherIdentity);
    data.requests.push({ ...data.requests[0], id: 'request-2', identity: otherIdentity });
    mockView(data);
    put.mockResolvedValue({ data });
    post.mockResolvedValue({ data });
    renderPage();
    await userEvent.click(await screen.findByRole('tab', { name: 'Permissions' }));
    await userEvent.click(screen.getByRole('button', { name: 'Add rule' }));
    const component = await screen.findByLabelText('Component');
    const ownerOption = screen.getByRole('option', { name: /Agent agent-1 · owner/ });
    const otherOption = screen.getByRole('option', { name: /Agent agent-1 · other-owner/ });
    expect(ownerOption.textContent).not.toBe(otherOption.textContent);
    await userEvent.selectOptions(component, otherOption);
    await userEvent.type(screen.getByLabelText('Hostname or IPv4 address'), 'api.example.com');
    await userEvent.click(screen.getByRole('button', { name: 'Save rule' }));
    await waitFor(() =>
      expect(put).toHaveBeenCalledWith('/network-access/rules', {
        identity: otherIdentity,
        destination: 'api.example.com',
        port: 443,
        allowed: true,
        expectedRevision: 7,
      })
    );

    await userEvent.click(screen.getByRole('tab', { name: /Requests/ }));
    const request = screen
      .getAllByRole('article')
      .find((article) => within(article).queryByText(/Agent agent-1 · other-owner/));
    expect(request).toBeDefined();
    await userEvent.click(within(request!).getByRole('button', { name: 'Allow' }));
    await userEvent.click(screen.getByRole('button', { name: 'Confirm allow' }));
    await waitFor(() =>
      expect(post).toHaveBeenCalledWith('/network-access/requests/request-2/decision', {
        allowed: true,
        expectedRevision: 7,
      })
    );
  });

  it('does not claim a rejected policy is active', async () => {
    const data = fixture();
    data.activationState = 'failed';
    data.revision = 8;
    data.lastError = 'The proxy did not accept the new policy.';
    mockView(data);
    renderPage();
    expect(await screen.findByText('Changes could not be applied')).toBeInTheDocument();
    expect(screen.queryByText('Policy active')).not.toBeInTheDocument();
    expect(screen.getByText(/Containers on the same network/)).toBeInTheDocument();
  });

  it('refreshes stale policy after a conflicting decision', async () => {
    post.mockRejectedValue({ isAxiosError: true, response: { status: 409 } });
    renderPage();
    await userEvent.click(await screen.findByRole('button', { name: 'Allow' }));
    await userEvent.click(screen.getByRole('button', { name: 'Confirm allow' }));
    expect(await screen.findByText(/policy changed while you were editing/)).toBeInTheDocument();
    await waitFor(() => expect(get.mock.calls.length).toBeGreaterThan(1));
  });

  it('disables policy decisions when enforcement is disabled', async () => {
    const data = fixture();
    data.enabled = false;
    data.activationState = 'disabled';
    mockView(data);
    renderPage();
    expect(await screen.findByRole('button', { name: 'Allow' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Keep blocked' })).toBeDisabled();
  });

  it('shows configured names and the grant scope before a decision, with identifiers in details', async () => {
    const data = fixture();
    data.requests[0].identity = {
      ...identity,
      displayName: 'Microsoft mail',
      agentName: 'Travel planner',
      userName: 'Amshu',
    };
    mockView(data);
    renderPage();
    expect(await screen.findByText('Microsoft mail')).toBeVisible();
    expect(screen.getByText('Travel planner · Amshu')).toBeVisible();
    expect(screen.getByText(/connector: mail · scope agent/)).not.toBeVisible();
    await userEvent.click(screen.getByRole('button', { name: 'Allow' }));
    expect(screen.getByText(/This gives/)).toHaveTextContent(
      'Microsoft mail · Travel planner · Amshu'
    );
    expect(post).not.toHaveBeenCalled();
    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(post).not.toHaveBeenCalled();
  });

  it.each([
    ['user', '192.168.1.10', true],
    ['admin', '192.168.1.10', false],
    ['admin', '169.254.169.254', true],
    ['user', '169.254.169.254', true],
  ])('explains privileged destinations for %s at %s', async (role, destination, disabled) => {
    viewer.role = role;
    const data = fixture();
    data.requests[0].destination = destination;
    mockView(data);
    renderPage();
    const allow = await screen.findByRole('button', { name: 'Allow' });
    if (disabled) expect(allow).toBeDisabled();
    else expect(allow).toBeEnabled();
    if (destination === '169.254.169.254')
      expect(screen.getByText(/Cloud-metadata access is always blocked/)).toBeVisible();
    if (role === 'user' && destination.startsWith('192.168'))
      expect(screen.getByText(/administrator must approve LAN/)).toBeVisible();
  });

  it('filters loaded requests and supports keyboard navigation between views', async () => {
    renderPage();
    await screen.findByText('graph.microsoft.com:443');
    await userEvent.type(screen.getByLabelText('Search network access'), 'other.example');
    expect(screen.getByText('No requests match your search.')).toBeVisible();
    const tab = screen.getByRole('tab', { name: /Requests/ });
    tab.focus();
    await userEvent.keyboard('{ArrowRight}');
    expect(screen.getByRole('tab', { name: 'Permissions' })).toHaveAttribute(
      'aria-selected',
      'true'
    );
    expect(screen.getByRole('heading', { name: 'Policy rules' })).toBeVisible();
  });

  it('does not send display metadata when saving a named component rule', async () => {
    const data = fixture();
    data.sources = [
      {
        ...identity,
        displayName: 'Microsoft mail',
        agentName: 'Travel planner',
        userName: 'Amshu',
      },
    ];
    data.requests = [];
    mockView(data);
    put.mockResolvedValue({ data });
    renderPage();
    await userEvent.click(await screen.findByRole('tab', { name: 'Permissions' }));
    await userEvent.click(screen.getByRole('button', { name: 'Add rule' }));
    await userEvent.selectOptions(
      screen.getByLabelText('Component'),
      screen.getByRole('option', { name: /Microsoft mail/ })
    );
    await userEvent.type(screen.getByLabelText('Hostname or IPv4 address'), 'api.example.com');
    await userEvent.click(screen.getByRole('button', { name: 'Save rule' }));
    await waitFor(() =>
      expect(put).toHaveBeenCalledWith('/network-access/rules', {
        identity,
        destination: 'api.example.com',
        port: 443,
        allowed: true,
        expectedRevision: 7,
      })
    );
  });

  it('explains that Gateway permission is shared with remote HTTP connectors', async () => {
    const data = fixture();
    data.requests[0].identity = {
      scopeType: 'core',
      userId: 'system',
      agentId: 'gateway',
      role: 'core',
      componentId: 'gateway',
      displayName: 'Gateway',
    };
    mockView(data);
    renderPage();
    await userEvent.click(await screen.findByRole('button', { name: 'Allow' }));
    expect(screen.getByText(/This is a shared service permission/)).toHaveTextContent(
      'not a per-agent grant'
    );
    expect(post).not.toHaveBeenCalled();
  });

  it('distinguishes public-only rules from administrator private-network grants', async () => {
    const data = fixture();
    data.rules = [
      {
        id: 'public',
        identity,
        destination: 'api.example.com',
        port: 443,
        allowed: true,
        updatedAtUtc: null,
      },
      {
        id: 'private',
        identity,
        destination: '192.168.1.20',
        port: 11434,
        allowed: true,
        allowPrivateNetwork: true,
        updatedAtUtc: null,
      },
    ];
    mockView(data);
    renderPage();
    await userEvent.click(await screen.findByRole('tab', { name: 'Permissions' }));
    expect(screen.getByText('Public destinations only')).toBeVisible();
    expect(screen.getByText('Includes private-network access')).toBeVisible();
  });

  it('does not overwrite a newer policy with a stale rule editor after a background refresh', async () => {
    renderPage();
    await userEvent.click(await screen.findByRole('tab', { name: 'Permissions' }));
    await userEvent.click(screen.getByRole('button', { name: 'Add rule' }));
    await userEvent.selectOptions(
      screen.getByLabelText('Component'),
      screen.getByRole('option', { name: /Connector.*owner/ })
    );
    await userEvent.type(screen.getByLabelText('Hostname or IPv4 address'), 'api.example.com');
    const latest = fixture();
    latest.revision = 8;
    latest.activationState = 'pending';
    mockView(latest);
    await userEvent.click(screen.getByRole('button', { name: 'Refresh network policy' }));
    await screen.findByText('Applying changes');
    put.mockRejectedValue({ isAxiosError: true, response: { status: 409 } });
    await userEvent.click(screen.getByRole('button', { name: 'Save rule' }));
    await waitFor(() =>
      expect(put).toHaveBeenCalledWith('/network-access/rules', {
        identity,
        destination: 'api.example.com',
        port: 443,
        allowed: true,
        expectedRevision: 7,
      })
    );
    expect(await screen.findByText(/policy changed while you were editing/)).toBeVisible();
    expect(screen.queryByRole('button', { name: 'Save rule' })).not.toBeInTheDocument();
  });

  it.each(['api.example.com', '192.168.1.20'])(
    'requires a visible administrator opt-in to grant local access for %s',
    async (destination) => {
      const data = fixture();
      data.requests[0].destination = destination;
      mockView(data);
      post.mockResolvedValue({ data });
      renderPage();
      await userEvent.click(await screen.findByRole('button', { name: 'Allow' }));
      const checkbox = screen.getByRole('checkbox', { name: /Allow local-network access/ });
      expect(checkbox).not.toBeChecked();
      if (destination.startsWith('192.168'))
        expect(screen.getByRole('button', { name: 'Confirm allow' })).toBeDisabled();
      await userEvent.click(checkbox);
      await userEvent.click(screen.getByRole('button', { name: 'Confirm allow' }));
      await waitFor(() =>
        expect(post).toHaveBeenCalledWith('/network-access/requests/request-1/decision', {
          allowed: true,
          expectedRevision: 7,
          allowPrivateNetwork: true,
        })
      );
    }
  );

  it('does not offer the local-network choice to an ordinary user', async () => {
    viewer.role = 'user';
    renderPage();
    await userEvent.click(await screen.findByRole('button', { name: 'Allow' }));
    expect(
      screen.queryByRole('checkbox', { name: /Allow local-network access/ })
    ).not.toBeInTheDocument();
  });

  it('keeps a new administrator rule public-only until local access is explicitly selected', async () => {
    put.mockResolvedValue({ data: fixture() });
    renderPage();
    await userEvent.click(await screen.findByRole('tab', { name: 'Permissions' }));
    await userEvent.click(screen.getByRole('button', { name: 'Add rule' }));
    await userEvent.selectOptions(
      screen.getByLabelText('Component'),
      screen.getByRole('option', { name: /Connector.*owner/ })
    );
    await userEvent.type(screen.getByLabelText('Hostname or IPv4 address'), '192.168.1.20');
    expect(screen.getByRole('button', { name: 'Save rule' })).toBeDisabled();
    expect(screen.getByRole('checkbox', { name: /Allow local-network access/ })).not.toBeChecked();
    await userEvent.click(screen.getByRole('checkbox', { name: /Allow local-network access/ }));
    await userEvent.click(screen.getByRole('button', { name: 'Save rule' }));
    await waitFor(() =>
      expect(put).toHaveBeenCalledWith('/network-access/rules', {
        identity,
        destination: '192.168.1.20',
        port: 443,
        allowed: true,
        expectedRevision: 7,
        allowPrivateNetwork: true,
      })
    );
  });
});
