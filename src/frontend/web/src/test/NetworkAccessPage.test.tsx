import React from 'react';
import { render } from '@testing-library/react';
import { screen, waitFor, within } from '@testing-library/dom';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { NetworkAccessPage } from '@/pages/NetworkAccessPage';
import type { NetworkAccessView } from '@/features/network-access/networkAccess';

const { get, post, put } = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn(), put: vi.fn() }));
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
      next.requests[0].status = allowed ? 'allowed' : 'blocked';
      post.mockResolvedValue({ data: next });
      renderPage();
      await userEvent.click(await screen.findByRole('button', { name: label }));
      await waitFor(() =>
        expect(post).toHaveBeenCalledWith('/network-access/requests/request-1/decision', {
          allowed,
          expectedRevision: 7,
        })
      );
      expect(post).toHaveBeenCalledTimes(1);
      expect(await screen.findByText('No pending network approvals.')).toBeInTheDocument();
      expect(screen.getByText(/Approving access does not resume a tool/)).toBeInTheDocument();
    }
  );

  it('saves an exact component rule without a caller-supplied actor', async () => {
    put.mockResolvedValue({ data: fixture() });
    renderPage();
    const component = await screen.findByLabelText('Component');
    await userEvent.selectOptions(
      component,
      screen.getByRole('option', { name: /connector: mail/ })
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

    const component = await screen.findByLabelText('Component');
    const ownerOption = screen.getByRole('option', { name: /scope agent · user owner ·/ });
    const otherOption = screen.getByRole('option', { name: /scope agent · user other-owner ·/ });
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

    const request = screen
      .getAllByRole('article')
      .find((article) => within(article).queryByText(/scope agent · user other-owner ·/));
    expect(request).toBeDefined();
    await userEvent.click(within(request!).getByRole('button', { name: 'Allow' }));
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
});
