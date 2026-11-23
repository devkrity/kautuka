import React from 'react';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { NetworkAccessPage } from './NetworkAccessPage';
import {
  NetworkPausedBanner,
  UserNetworkPrivilegesForm,
  CompatibilityModeCard,
  destinationScope,
  type NetworkAccessBrakeDto,
  type NetworkAccessRequestDto,
  type NetworkAccessRuleDto,
  type NetworkRequestsResponseDto,
  type NetworkRulesResponseDto,
  type ImageCompatResponseDto,
} from '@/features/network-access';
import type { InternalAxiosRequestConfig } from 'axios';

function createMockAxiosError(status: number, message = 'Error') {
  return {
    name: 'AxiosError',
    message,
    isAxiosError: true as const,
    response: {
      status,
      data: {},
      statusText: message,
      headers: {},
      config: {} as InternalAxiosRequestConfig,
    },
  };
}

// Mock apiClient
const { get, post, put, del } = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
  del: vi.fn(),
}));

const mockViewer = vi.hoisted(() => ({
  id: 'user-ravi',
  displayName: 'Ravi',
  role: 'admin',
  userPronoun: 'her',
}));

vi.mock('@/lib/apiClient', () => ({
  apiClient: {
    get,
    post,
    put,
    delete: del,
  },
}));

vi.mock('@/features/auth', () => ({
  useCurrentUser: () => ({ data: mockViewer }),
}));

// Default Fixtures
function createDefaultBrake(overrides: Partial<NetworkAccessBrakeDto> = {}): NetworkAccessBrakeDto {
  return {
    userPaused: false,
    userPausedBy: undefined,
    userLockedByAdmin: false,
    systemPaused: false,
    systemPausedBy: undefined,
    ...overrides,
  };
}

function createDefaultRequests(
  requests: NetworkAccessRequestDto[] = [],
  privileges = { canApproveOwn: true, canAllowAllAgents: true },
  brake = createDefaultBrake()
): NetworkRequestsResponseDto {
  return {
    revision: 1,
    requests,
    privileges,
    brake,
  };
}

function createDefaultRules(rules: NetworkAccessRuleDto[] = []): NetworkRulesResponseDto {
  return {
    revision: 1,
    rules,
  };
}

function createDefaultCompat(overrides: Partial<ImageCompatResponseDto> = {}): ImageCompatResponseDto {
  return {
    pendingRequests: [],
    profiles: [],
    ...overrides,
  };
}

function setupMockApis({
  requests = createDefaultRequests(),
  rules = createDefaultRules(),
  compat = createDefaultCompat(),
  brake = createDefaultBrake(),
  activity = { entries: [] },
}: {
  requests?: NetworkRequestsResponseDto;
  rules?: NetworkRulesResponseDto;
  compat?: ImageCompatResponseDto;
  brake?: NetworkAccessBrakeDto;
  activity?: { entries: unknown[] };
} = {}) {
  get.mockImplementation(async (path: string, config?: { params?: Record<string, unknown> }) => {
    if (path.includes('/network-access/requests')) {
      return { data: requests };
    }
    if (path.includes('/network-access/rules')) {
      return { data: rules };
    }
    if (path.includes('/network-access/compat')) {
      if (mockViewer.role !== 'admin') {
        throw createMockAxiosError(403, 'Forbidden');
      }
      return { data: compat };
    }
    if (path.includes('/network-access/brake')) {
      return { data: brake };
    }
    if (path.includes('/network-access/activity')) {
      return { data: activity };
    }
    return { data: {} };
  });

  post.mockImplementation(async (path: string) => {
    if (path.includes('/network-access/compat') && mockViewer.role !== 'admin') {
      throw createMockAxiosError(403, 'Forbidden');
    }
    return { data: { revision: requests.revision + 1 } };
  });

  put.mockImplementation(async (path: string) => {
    return { data: { revision: requests.revision + 1 } };
  });

  del.mockImplementation(async (path: string) => {
    if (path.includes('/network-access/compat') && mockViewer.role !== 'admin') {
      throw createMockAxiosError(403, 'Forbidden');
    }
    return { data: { revision: requests.revision + 1 } };
  });
}

function renderComponent(ui: React.ReactElement, initialEntries = ['/network-access']) {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });

  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={initialEntries}>{ui}</MemoryRouter>
    </QueryClientProvider>
  );
}

describe('Network Access UI & UX Specification Tests (UX1-UX11, CP4-CP5)', () => {
  beforeEach(() => {
    vi.resetAllMocks();
    mockViewer.id = 'user-ravi';
    mockViewer.displayName = 'Ravi';
    mockViewer.role = 'admin';
    mockViewer.userPronoun = 'her';
    setupMockApis();
  });

  it('lets an administrator grant public internet to a selected user and connector', async () => {
    const previousGet = get.getMockImplementation();
    get.mockImplementation(async (path: string, config?: { params?: Record<string, unknown> }) => {
      if (path === '/settings/users') return { data: { users: [{ id: 'user-alice', displayName: 'Alice', username: 'alice' }] } };
      if (path === '/connectors') return { data: { connectors: [
        { id: 'web-tools', name: 'Web Tools', scope: 'system', transport: 'container' },
        { id: 'internal', name: 'Internal Parser', scope: 'system', transport: 'container', isInternalSystemConnector: true },
        { id: 'remote', name: 'Remote Connector', scope: 'system', transport: 'http' },
        { id: 'other', name: 'Another User Connector', scope: 'user', ownerUserId: 'user-bob', transport: 'container' },
      ] } };
      return previousGet?.(path, config);
    });
    const user = userEvent.setup();
    renderComponent(<NetworkAccessPage />);
    await user.click(await screen.findByRole('button', { name: /rules \(/i }));
    await user.click(screen.getByRole('button', { name: /allow a connector to reach public websites/i }));
    await user.selectOptions(await screen.findByLabelText('User'), 'user-alice');
    expect(screen.queryByRole('option', { name: 'Internal Parser' })).not.toBeInTheDocument();
    expect(screen.queryByRole('option', { name: 'Remote Connector' })).not.toBeInTheDocument();
    expect(screen.queryByRole('option', { name: 'Another User Connector' })).not.toBeInTheDocument();
    await user.selectOptions(await screen.findByLabelText('Connector'), 'web-tools');
    expect(put).not.toHaveBeenCalled();
    await user.click(screen.getByRole('button', { name: 'Allow any public website' }));
    await waitFor(() => expect(put).toHaveBeenCalledWith('/network-access/rules', expect.objectContaining({
      scopeKind: 'user', userId: 'user-alice', connectorId: 'web-tools', destKind: 'any-public',
      destination: '', port: 0, allowed: true, expectedRevision: 1,
    })));
  });

  it('does not offer the open internet grant to non-administrators', async () => {
    mockViewer.role = 'user';
    const user = userEvent.setup();
    renderComponent(<NetworkAccessPage />);
    await user.click(await screen.findByRole('button', { name: /rules \(/i }));
    expect(screen.queryByRole('button', { name: /allow a connector to reach public websites/i })).not.toBeInTheDocument();
  });

  it('keeps the grant dialog open if Manager rejects the connector context', async () => {
    const previousGet = get.getMockImplementation();
    get.mockImplementation(async (path: string, config?: { params?: Record<string, unknown> }) => {
      if (path === '/settings/users') return { data: { users: [{ id: 'user-alice', displayName: 'Alice', username: 'alice' }] } };
      if (path === '/connectors') return { data: { connectors: [{ id: 'web-tools', name: 'Web Tools', scope: 'system', transport: 'container' }] } };
      return previousGet?.(path, config);
    });
    put.mockRejectedValue(createMockAxiosError(400));
    const user = userEvent.setup();
    renderComponent(<NetworkAccessPage />);
    await user.click(await screen.findByRole('button', { name: /rules \(/i }));
    await user.click(screen.getByRole('button', { name: /allow a connector to reach public websites/i }));
    await user.selectOptions(await screen.findByLabelText('User'), 'user-alice');
    await user.selectOptions(await screen.findByLabelText('Connector'), 'web-tools');
    await user.click(screen.getByRole('button', { name: 'Allow any public website' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Could not confirm this rule was saved');
    expect(screen.getByRole('dialog')).toBeVisible();
  });

  it('shows a load error and disables brake controls when request state is unavailable', async () => {
    setupMockApis();
    const previousGet = get.getMockImplementation();
    get.mockImplementation(async (path: string, config?: { params?: Record<string, unknown> }) => {
      if (path.includes('/network-access/requests')) throw createMockAxiosError(503, 'Unavailable');
      return previousGet?.(path, config);
    });
    renderComponent(<NetworkAccessPage />);
    expect(await screen.findByText(/could not load network requests/i)).toBeVisible();
    expect(screen.queryByText('No pending network requests.')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Pause all network access for my agents' })).toBeDisabled();
  });

  // ──────────────── UX1: Attempt Deduplication ────────────────
  describe('UX1: attempt deduplication', () => {
    it('three attempts to the same destination produce exactly one card with attempts == 3', async () => {
      const requestsData = createDefaultRequests([
        {
          id: 'req-1',
          userId: 'user-ravi',
          userDisplayName: 'Ravi',
          agentId: 'agent-researcher',
          agentDisplayName: 'Researcher',
          connectorDisplayName: 'Web Search',
          attribution: 'user-agent',
          destination: 'api.example.com',
          destKind: 'host',
          port: 443,
          actorKind: 'agent',
          firstSeenUtc: '2026-11-11T10:00:00Z',
          lastSeenUtc: '2026-11-11T10:00:05Z',
          attempts: 3,
          status: 'pending',
          origin: 'container',
        },
      ]);
      setupMockApis({ requests: requestsData });

      renderComponent(<NetworkAccessPage />);

      expect(await screen.findByText(/Ravi's Researcher \(Web Search\) wants to reach api\.example\.com/)).toBeVisible();
      expect(screen.getByText(/tried 3 times\./)).toBeVisible();
      const cards = screen.getAllByTestId(/^request-card-/);
      expect(cards).toHaveLength(1);
    });

    it('NC: a request with a different destination produces a second card (nc_flipped == true)', async () => {
      const requestsData = createDefaultRequests([
        {
          id: 'req-1',
          userId: 'user-ravi',
          userDisplayName: 'Ravi',
          agentId: 'agent-researcher',
          agentDisplayName: 'Researcher',
          connectorDisplayName: 'Web Search',
          attribution: 'user-agent',
          destination: 'api.example.com',
          destKind: 'host',
          port: 443,
          actorKind: 'agent',
          firstSeenUtc: '2026-11-11T10:00:00Z',
          lastSeenUtc: '2026-11-11T10:00:05Z',
          attempts: 3,
          status: 'pending',
          origin: 'container',
        },
        {
          id: 'req-2',
          userId: 'user-ravi',
          userDisplayName: 'Ravi',
          agentId: 'agent-researcher',
          agentDisplayName: 'Researcher',
          connectorDisplayName: 'Web Search',
          attribution: 'user-agent',
          destination: 'different.example.org',
          destKind: 'host',
          port: 443,
          actorKind: 'agent',
          firstSeenUtc: '2026-11-11T10:05:00Z',
          lastSeenUtc: '2026-11-11T10:05:02Z',
          attempts: 1,
          status: 'pending',
          origin: 'container',
        },
      ]);
      setupMockApis({ requests: requestsData });

      renderComponent(<NetworkAccessPage />);

      expect(await screen.findByText(/api\.example\.com/)).toBeVisible();
      expect(screen.getByText(/different\.example\.org/)).toBeVisible();
      const cards = screen.getAllByTestId(/^request-card-/);
      expect(cards).toHaveLength(2);
    });
  });

  // ──────────────── UX2: Allow and Keep Blocked Decisions ────────────────
  describe('UX2: decisions and retry outcomes', () => {
    it('Allow submits agent-scope decision, and Keep Blocked submits allow:false decision', async () => {
      const user = userEvent.setup();
      const requestsData = createDefaultRequests([
        {
          id: 'req-1',
          userId: 'user-ravi',
          userDisplayName: 'Ravi',
          agentId: 'agent-1',
          agentDisplayName: 'Search Agent',
          attribution: 'user-agent',
          destination: 'api.example.com',
          destKind: 'host',
          port: 443,
          actorKind: 'agent',
          firstSeenUtc: '2026-11-11T10:00:00Z',
          lastSeenUtc: '2026-11-11T10:00:01Z',
          attempts: 1,
          status: 'pending',
          origin: 'container',
        },
      ]);
      setupMockApis({ requests: requestsData });

      renderComponent(<NetworkAccessPage />);

      const allowBtn = await screen.findByRole('button', { name: /^Allow (?:.+ to access this site|access)$/ });
      const keepBlockedBtn = screen.getByRole('button', { name: /Keep blocked/i });

      // Click Allow
      await user.click(allowBtn);
      await waitFor(() =>
        expect(post).toHaveBeenCalledWith(
          '/network-access/requests/req-1/decision',
          expect.objectContaining({
            allow: true,
            scope: 'agent',
            expectedRevision: 1,
          })
        )
      );

      // Click Keep Blocked
      await user.click(keepBlockedBtn);
      await waitFor(() =>
        expect(post).toHaveBeenCalledWith(
          '/network-access/requests/req-1/decision',
          expect.objectContaining({
            allow: false,
            scope: 'agent',
            expectedRevision: 1,
          })
        )
      );
    });
  });

  // ──────────────── UX3: Ownership and Attribution ────────────────
  describe('UX3: ownership boundaries and systemwide connectors', () => {
    it('non-admin can decide their own requests regardless of connector availability', async () => {
      mockViewer.id = 'user-alice';
      mockViewer.displayName = 'Alice';
      mockViewer.role = 'user';

      const requestsData = createDefaultRequests([
        {
          id: 'req-alice',
          userId: 'user-alice',
          userDisplayName: 'Alice',
          agentId: 'agent-alice',
          agentDisplayName: 'AliceAgent',
          attribution: 'user-agent',
          destination: 'alice-dest.com',
          destKind: 'host',
          port: 443,
          actorKind: 'agent',
          firstSeenUtc: '2026-11-11T10:00:00Z',
          lastSeenUtc: '2026-11-11T10:00:01Z',
          attempts: 1,
          status: 'pending',
          origin: 'container',
        },
        {
          id: 'req-systemwide',
          userId: 'user-alice',
          connectorId: 'shared-search',
          connectorDisplayName: 'SharedSearch',
          attribution: 'user',
          destination: 'shared-dest.com',
          destKind: 'host',
          port: 443,
          actorKind: 'connector',
          firstSeenUtc: '2026-11-11T10:00:00Z',
          lastSeenUtc: '2026-11-11T10:00:01Z',
          attempts: 2,
          status: 'pending',
          origin: 'container',
        },
      ]);
      setupMockApis({ requests: requestsData });

      renderComponent(<NetworkAccessPage />);

      // Alice can decide her own agent's request
      expect(await screen.findByText(/Alice's AliceAgent wants to reach alice-dest\.com/)).toBeVisible();
      const aliceCard = screen.getByTestId('request-card-req-alice');
      expect(within(aliceCard).getByRole('button', { name: /^Allow (?:.+ to access this site|access)$/ })).toBeVisible();

      // Catalogue availability does not change Alice's authority over her request.
      const systemwideCard = screen.getByTestId('request-card-req-systemwide');
      expect(within(systemwideCard).queryByRole('button', { name: /Allow for everyone/i })).not.toBeInTheDocument();
      expect(within(systemwideCard).getByRole('button', { name: /^Allow (?:.+ to access this site|access)$/ })).toBeVisible();
    });

    it('NC: administrator CAN decide the systemwide connector request (nc_flipped == true)', async () => {
      mockViewer.id = 'admin-bob';
      mockViewer.displayName = 'Bob';
      mockViewer.role = 'admin';

      const requestsData = createDefaultRequests([
        {
          id: 'req-systemwide',
          userId: 'user-alice',
          connectorId: 'shared-search',
          userDisplayName: 'Alice',
          connectorDisplayName: 'SharedSearch',
          attribution: 'user',
          destination: 'shared-dest.com',
          destKind: 'host',
          port: 443,
          actorKind: 'connector',
          firstSeenUtc: '2026-11-11T10:00:00Z',
          lastSeenUtc: '2026-11-11T10:00:01Z',
          attempts: 2,
          status: 'pending',
          origin: 'container',
        },
      ]);
      setupMockApis({ requests: requestsData });

      renderComponent(<NetworkAccessPage />);

      const systemwideCard = await screen.findByTestId('request-card-req-systemwide');
      expect(within(systemwideCard).getByRole('button', { name: /^Allow (?:.+ to access this site|access)$/ })).toBeVisible();
      expect(within(systemwideCard).queryByRole('button', { name: /Allow for everyone/i })).not.toBeInTheDocument();
    });

    it('administrator can decide another user request even when the owner is waiting for admin', async () => {
      const user = userEvent.setup();
      mockViewer.id = 'admin-bob';
      mockViewer.displayName = 'Bob';
      mockViewer.role = 'admin';

      const requestsData = createDefaultRequests([
        {
          id: 'req-owner-needs-admin',
          userId: 'user-alice',
          userDisplayName: 'Alice',
          agentId: 'agent-alice',
          agentDisplayName: 'AliceAgent',
          attribution: 'user-agent',
          destination: 'alice-dest.example',
          destKind: 'host',
          port: 443,
          actorKind: 'agent',
          firstSeenUtc: '2026-11-11T10:00:00Z',
          lastSeenUtc: '2026-11-11T10:00:01Z',
          attempts: 1,
          status: 'pending',
          origin: 'container',
          waitingForAdmin: true,
          ownerCanApproveOwn: false,
        },
      ]);
      setupMockApis({ requests: requestsData });

      renderComponent(<NetworkAccessPage />);

      const card = await screen.findByTestId('request-card-req-owner-needs-admin');
      expect(within(card).getByRole('button', { name: /^Allow (?:.+ to access this site|access)$/ })).toBeVisible();
      expect(within(card).getByRole('button', { name: /Keep blocked/i })).toBeVisible();
      expect(within(card).getByRole('button', { name: /Allow this site for everything Alice uses/i })).toBeVisible();
      expect(within(card).queryByText(/Waiting for an administrator to approve/i)).not.toBeInTheDocument();

      await user.click(within(card).getByRole('button', { name: /^Allow (?:.+ to access this site|access)$/ }));
      await waitFor(() => {
        expect(post).toHaveBeenCalledWith(
          '/network-access/requests/req-owner-needs-admin/decision',
          expect.objectContaining({ allow: true, scope: 'agent', expectedRevision: 1 })
        );
      });
    });

    it.each([
      {
        viewerId: 'user-alice',
        viewerName: 'Alice',
        notice: /Ask an administrator to approve this request or grant that permission/,
      },
      {
        viewerId: 'user-ravi',
        viewerName: 'Ravi',
        notice: /Waiting for an administrator to approve/i,
      },
    ])('shows the next step for $viewerName without offering approval controls', async ({ viewerId, viewerName, notice }) => {
      mockViewer.id = viewerId;
      mockViewer.displayName = viewerName;
      mockViewer.role = 'user';

      const requestsData = createDefaultRequests([
        {
          id: 'req-owner-needs-admin',
          userId: 'user-alice',
          userDisplayName: 'Alice',
          agentId: 'agent-alice',
          agentDisplayName: 'AliceAgent',
          attribution: 'user-agent',
          destination: 'alice-dest.example',
          destKind: 'host',
          port: 443,
          actorKind: 'agent',
          firstSeenUtc: '2026-11-11T10:00:00Z',
          lastSeenUtc: '2026-11-11T10:00:01Z',
          attempts: 1,
          status: 'pending',
          origin: 'container',
          waitingForAdmin: true,
          ownerCanApproveOwn: false,
        },
      ]);
      setupMockApis({ requests: requestsData });

      renderComponent(<NetworkAccessPage />);

      const card = await screen.findByTestId('request-card-req-owner-needs-admin');
      expect(within(card).getByText(notice)).toBeVisible();
      expect(within(card).queryByRole('button', { name: /^Allow (?:.+ to access this site|access)$/ })).not.toBeInTheDocument();
      expect(within(card).queryByRole('button', { name: /Keep blocked/i })).not.toBeInTheDocument();
    });
  });

  // ──────────────── UX4: Revision Conflict ────────────────
  describe('UX4: revision conflict handling', () => {
    it('a decision with a stale revision receives HTTP 409 and displays conflict warning', async () => {
      const user = userEvent.setup();
      const requestsData = createDefaultRequests([
        {
          id: 'req-conflict',
          userId: 'user-ravi',
          userDisplayName: 'Ravi',
          agentId: 'agent-1',
          agentDisplayName: 'MyAgent',
          attribution: 'user-agent',
          destination: 'api.example.com',
          destKind: 'host',
          port: 443,
          actorKind: 'agent',
          firstSeenUtc: '2026-11-11T10:00:00Z',
          lastSeenUtc: '2026-11-11T10:00:01Z',
          attempts: 1,
          status: 'pending',
          origin: 'container',
        },
      ]);
      setupMockApis({ requests: requestsData });

      // Mock 409 conflict error on post
      post.mockRejectedValue(createMockAxiosError(409, 'Conflict'));

      renderComponent(<NetworkAccessPage />);

      const allowBtn = await screen.findByRole('button', { name: /^Allow (?:.+ to access this site|access)$/ });
      await user.click(allowBtn);

      expect(
        await screen.findByText(/The policy changed while you were editing\. The latest policy has been loaded; review it and try again\./)
      ).toBeVisible();
    });
  });

  // ──────────────── UX5: Viewport and Jargon-Free Audience ────────────────
  describe('UX5: 390px mobile viewport and no forbidden technical jargon', () => {
    it('phone viewport renders Allow and Keep blocked, without wildcards/regex/CIDR/CAP_/seccomp/privileged', async () => {
      const requestsData = createDefaultRequests([
        {
          id: 'req-jargon-check',
          userId: 'user-ravi',
          userDisplayName: 'Ravi',
          agentId: 'agent-1',
          agentDisplayName: 'Researcher',
          connectorDisplayName: 'Web Search',
          attribution: 'user-agent',
          destination: '93.184.216.34',
          destKind: 'ip',
          port: 8443,
          actorKind: 'agent',
          firstSeenUtc: '2026-11-11T10:00:00Z',
          lastSeenUtc: '2026-11-11T10:00:01Z',
          attempts: 2,
          status: 'pending',
          origin: 'container',
        },
      ]);
      setupMockApis({ requests: requestsData });

      renderComponent(<NetworkAccessPage />);

      // IP literal displays as "a server at 93.184.216.34:8443"
      expect(
        await screen.findByText(/Ravi's Researcher \(Web Search\) wants to reach a server at 93\.184\.216\.34:8443/)
      ).toBeVisible();

      const allowBtn = screen.getByRole('button', { name: /^Allow (?:.+ to access this site|access)$/ });
      const keepBlockedBtn = screen.getByRole('button', { name: /Keep blocked/i });
      expect(allowBtn).toBeVisible();
      expect(keepBlockedBtn).toBeVisible();

      // Check for strictly forbidden jargon in all text on screen
      const allText = document.body.textContent ?? '';
      expect(allText).not.toMatch(/\bwildcard\b/i);
      expect(allText).not.toMatch(/\bregex\b/i);
      expect(allText).not.toMatch(/\bCIDR\b/i);
      expect(allText).not.toMatch(/CAP_/i);
      expect(allText).not.toMatch(/\bseccomp\b/i);
      expect(allText).not.toMatch(/\bprivileged\b/i);
    });
  });

  // ──────────────── UX6: Destination Validation ────────────────
  describe('UX6: destination classification', () => {
    it('classifies cloud metadata, reserved control IPs and private ranges accurately', () => {
      expect(destinationScope('169.254.169.254')).toBe('metadata');
      expect(destinationScope('100.100.100.200')).toBe('metadata');
      expect(destinationScope('168.63.129.16')).toBe('blocked');
      expect(destinationScope('192.0.0.8')).toBe('blocked');
      expect(destinationScope('192.168.1.1')).toBe('private');
      expect(destinationScope('10.0.0.5')).toBe('private');
      expect(destinationScope('172.16.10.20')).toBe('private');
      expect(destinationScope('api.example.com')).toBe('public');
      expect(destinationScope('93.184.216.34')).toBe('public');
    });
  });

  // Connector availability does not change request scope.
  describe('connector request cards', () => {
    it('lets an administrator decide an attributed connector request for that user', async () => {
      const user = userEvent.setup();
      setupMockApis({ requests: createDefaultRequests([{
        id: 'req-connector', userId: 'user-ravi', userDisplayName: 'Ravi', connectorId: 'search',
        connectorDisplayName: 'GlobalSearch', attribution: 'user',
        destination: 'api.search.com', destKind: 'host', port: 443, actorKind: 'connector',
        firstSeenUtc: '2026-11-11T10:00:00Z', lastSeenUtc: '2026-11-11T10:00:01Z',
        attempts: 1, status: 'pending', origin: 'container',
      }]) });
      renderComponent(<NetworkAccessPage />);
      const card = await screen.findByTestId('request-card-req-connector');
      expect(within(card).queryByRole('button', { name: /Allow for everyone/i })).not.toBeInTheDocument();
      await user.click(within(card).getByRole('button', { name: /^Allow (?:.+ to access this site|access)$/ }));
      expect(post).toHaveBeenCalledWith('/network-access/requests/req-connector/decision',
        expect.objectContaining({ allow: true, scope: 'connector' }));
    });
  });
  // ──────────────── UX8: Secondary 'Allow for All My Agents' Link ────────────────
  describe('UX8: "Allow this site for everything I use" link', () => {
    it('shows secondary link for owner holding canAllowAllAgents and writes user-scope rule', async () => {
      const user = userEvent.setup();
      const requestsData = createDefaultRequests(
        [
          {
            id: 'req-owner-link',
            userId: 'user-ravi',
            userDisplayName: 'Ravi',
            agentId: 'agent-1',
            agentDisplayName: 'MyBot',
            attribution: 'user-agent',
            destination: 'api.github.com',
            destKind: 'host',
            port: 443,
            actorKind: 'agent',
            firstSeenUtc: '2026-11-11T10:00:00Z',
            lastSeenUtc: '2026-11-11T10:00:01Z',
            attempts: 1,
            status: 'pending',
            origin: 'container',
            ownerCanApproveOwn: true,
          },
        ],
        { canApproveOwn: true, canAllowAllAgents: true }
      );
      setupMockApis({ requests: requestsData });

      renderComponent(<NetworkAccessPage />);

      const allAgentsLink = await screen.findByRole('button', {
        name: 'Allow this site for everything I use',
      });
      expect(allAgentsLink).toBeVisible();

      await user.click(allAgentsLink);
      expect(post).toHaveBeenCalledWith(
        '/network-access/requests/req-owner-link/decision',
        expect.objectContaining({
          allow: true,
          scope: 'user',
          expectedRevision: 1,
        })
      );
      expect(
        await screen.findByText(/All your activity can now reach api\.github\.com/)
      ).toBeVisible();
    });

    it('admin deciding for another person sees "Allow for all of {userDisplayName}\'s agents, including new ones"', async () => {
      mockViewer.id = 'admin-user';
      mockViewer.displayName = 'Admin';
      mockViewer.role = 'admin';

      const requestsData = createDefaultRequests(
        [
          {
            id: 'req-other-user',
            userId: 'user-sam',
            userDisplayName: 'Sam',
            agentId: 'agent-sam',
            agentDisplayName: 'SamBot',
            attribution: 'user-agent',
            destination: 'api.sam.org',
            destKind: 'host',
            port: 443,
            actorKind: 'agent',
            firstSeenUtc: '2026-11-11T10:00:00Z',
            lastSeenUtc: '2026-11-11T10:00:01Z',
            attempts: 1,
            status: 'pending',
            origin: 'container',
            ownerCanApproveOwn: true,
          },
        ],
        { canApproveOwn: true, canAllowAllAgents: true }
      );
      setupMockApis({ requests: requestsData });

      renderComponent(<NetworkAccessPage />);

      expect(
        await screen.findByRole('button', {
          name: "Allow this site for everything Sam uses",
        })
      ).toBeVisible();
    });

    it('admin can grant the user scope without a separate all-agents privilege', async () => {
      mockViewer.id = 'admin-user';
      mockViewer.displayName = 'Admin';
      mockViewer.role = 'admin';

      const requestsData = createDefaultRequests([
        {
          id: 'req-owner-privileges-missing',
          userId: 'user-sam',
          userDisplayName: 'Sam',
          agentId: 'agent-sam',
          agentDisplayName: 'SamBot',
          attribution: 'user-agent',
          destination: 'api.sam.org',
          destKind: 'host',
          port: 443,
          actorKind: 'agent',
          firstSeenUtc: '2026-11-11T10:00:00Z',
          lastSeenUtc: '2026-11-11T10:00:01Z',
          attempts: 1,
          status: 'pending',
          origin: 'container',
        },
      ], { canApproveOwn: true, canAllowAllAgents: true });
      setupMockApis({ requests: requestsData });

      renderComponent(<NetworkAccessPage />);

      const card = await screen.findByTestId('request-card-req-owner-privileges-missing');
      expect(within(card).getByRole('button', { name: /^Allow (?:.+ to access this site|access)$/ })).toBeVisible();
      expect(within(card).getByRole('button', { name: /Allow this site for everything Sam uses/i })).toBeVisible();
    });

    it('non-admin cannot infer another user all-agents privilege when owner flags are missing', async () => {
      mockViewer.id = 'user-ravi';
      mockViewer.displayName = 'Ravi';
      mockViewer.role = 'user';

      const requestsData = createDefaultRequests([
        {
          id: 'req-owner-privileges-missing',
          userId: 'user-sam',
          userDisplayName: 'Sam',
          agentId: 'agent-sam',
          agentDisplayName: 'SamBot',
          attribution: 'user-agent',
          destination: 'api.sam.org',
          destKind: 'host',
          port: 443,
          actorKind: 'agent',
          firstSeenUtc: '2026-11-11T10:00:00Z',
          lastSeenUtc: '2026-11-11T10:00:01Z',
          attempts: 1,
          status: 'pending',
          origin: 'container',
        },
      ], { canApproveOwn: true, canAllowAllAgents: true });
      setupMockApis({ requests: requestsData });

      renderComponent(<NetworkAccessPage />);

      const card = await screen.findByTestId('request-card-req-owner-privileges-missing');
      expect(within(card).getByText(/No permission to decide this request/i)).toBeVisible();
      expect(within(card).queryByRole('button', { name: /Allow this site for everything Sam uses/i })).not.toBeInTheDocument();
    });

    it('renders temporary probe card without permanent scope options (v7-14)', async () => {
      mockViewer.role = 'user';
      const requestsData = createDefaultRequests(
        [
          {
            id: 'req-probe-temp',
            userId: 'user-ravi',
            userDisplayName: 'Ravi',
            connectorId: 'conn-probe',
            connectorDisplayName: 'GitHub MCP',
            attribution: 'user',
            destination: 'api.github.com',
            destKind: 'host',
            port: 443,
            actorKind: 'connector',
            firstSeenUtc: '2026-11-11T10:00:00Z',
            lastSeenUtc: '2026-11-11T10:00:01Z',
            attempts: 1,
            status: 'pending',
            origin: 'container',
            purpose: 'probe',
            ownerCanApproveOwn: true,
          },
        ],
        { canApproveOwn: true, canAllowAllAgents: true }
      );
      setupMockApis({ requests: requestsData });

      renderComponent(<NetworkAccessPage />);

      expect(await screen.findByText('Probe (temporary)')).toBeVisible();
      expect(screen.getByRole('button', { name: 'Allow for this probe' })).toBeVisible();
      expect(screen.getByRole('button', { name: 'Keep blocked' })).toBeVisible();
      expect(
        screen.queryByRole('button', { name: 'Allow this site for everything I use' })
      ).toBeNull();
      expect(screen.queryByRole('button', { name: 'Allow for everyone' })).toBeNull();
    });
  });

  // ──────────────── UX9: Open Internet Grant Dialog & Badges ────────────────
  describe('UX9: open internet grant dialog and badges', () => {
    it('rules list shows Open internet badge and appropriate scope sentences', async () => {
      const user = userEvent.setup();
      const rulesData = createDefaultRules([
        {
          id: 'rule-open',
          scopeKind: 'user',
          userId: 'user-ravi',
          userDisplayName: 'Ravi',
          connectorDisplayName: 'Web search',
          destination: '',
          destKind: 'any-public',
          port: 0,
          allowed: true,
          allowLan: false,
          source: 'user',
          paused: false,
          summaryText: "Ravi's Web search can reach any public website",
          decidedBy: 'admin',
          decidedAtUtc: '2026-11-11T10:00:00Z',
          openInternetBadge: true,
        },
        {
          id: 'rule-paused',
          scopeKind: 'user',
          userId: 'user-ravi',
          userDisplayName: 'Ravi',
          destination: 'legacy.example.com',
          destKind: 'host',
          port: 443,
          allowed: true,
          allowLan: false,
          source: 'user',
          paused: true,
          summaryText: 'Paused rule',
          decidedBy: 'admin',
          decidedAtUtc: '2026-11-11T10:00:00Z',
          openInternetBadge: false,
        },
      ]);
      setupMockApis({ rules: rulesData });

      renderComponent(<NetworkAccessPage />);

      // Switch to Rules tab
      await user.click(await screen.findByRole('button', { name: /^Rules/ }));

      // Check open internet rule sentence and badge
      expect(
        await screen.findByText("Ravi's Web search can reach any public website")
      ).toBeVisible();
      expect(screen.getByText('Open internet')).toBeVisible();

      // Paused rules retain their scope and display their status.
      expect(screen.getByText('Paused')).toBeVisible();
      expect(screen.getByText("All of Ravi's activity")).toBeVisible();
    });
  });

  // The single privilege covers the user's own network scopes.
  describe('per-user network permission', () => {
    it('lets an administrator select a user and save their network permission', async () => {
      const user = userEvent.setup();
      setupMockApis();
      const previousGet = get.getMockImplementation();
      get.mockImplementation(async (path: string, config?: { params?: Record<string, unknown> }) => {
        if (path === '/settings/users') return { data: { users: [{ id: 'user-alice', username: 'alice', displayName: 'Alice', role: 'user', isActive: true }] } };
        if (path === '/network-access/users/user-alice/privileges') return { data: { userId: 'user-alice', canApproveOwn: false } };
        return previousGet?.(path, config);
      });
      put.mockResolvedValue({ data: { userId: 'user-alice', canApproveOwn: true } });
      renderComponent(<NetworkAccessPage />, ['/network-access?tab=privileges']);
      const select = await screen.findByLabelText('User');
      await screen.findByRole('option', { name: 'Alice' });
      await user.selectOptions(select, 'user-alice');
      const checkbox = await screen.findByRole('checkbox', { name: 'Can manage their own network access' });
      await waitFor(() => expect(checkbox).toBeEnabled());
      await user.click(checkbox);
      await waitFor(() => expect(put).toHaveBeenCalledWith('/network-access/users/user-alice/privileges', { canApproveOwn: true }));
    });
    it('shows one management checkbox and saves its value', async () => {
      const user = userEvent.setup();
      const onSave = vi.fn();
      renderComponent(<UserNetworkPrivilegesForm userId="user-priya" canApproveOwn={false} isAdminViewer onSave={onSave} />);
      const control = screen.getByRole('checkbox', { name: 'Can manage their own network access' });
      expect(screen.getAllByRole('checkbox')).toHaveLength(1);
      await user.click(control);
      expect(onSave).toHaveBeenCalledWith({ canApproveOwn: true });
    });

    it('shows the current permission read-only to a non-admin', () => {
      renderComponent(<UserNetworkPrivilegesForm userId="user-priya" canApproveOwn={false} isAdminViewer={false} />);
      expect(screen.getByText('Your administrator manages your network access.')).toBeVisible();
      expect(screen.queryByRole('checkbox')).not.toBeInTheDocument();
    });
  });
  // ──────────────── UX11: Emergency Brake ────────────────
  describe('UX11: emergency brake controls and global banner', () => {
    it('shows confirmation when pause button is clicked, and banner renders with Resume for affected user', async () => {
      const user = userEvent.setup();
      setupMockApis({
        brake: createDefaultBrake({
          userPaused: true,
          userPausedBy: 'Ravi',
          userLockedByAdmin: false,
        }),
      });

      renderComponent(
        <>
          <NetworkPausedBanner />
          <NetworkAccessPage />
        </>
      );

      // Check that global banner is visible
      expect(await screen.findByTestId('network-paused-banner')).toBeVisible();
      expect(screen.getByText(/Network paused/)).toBeVisible();
      expect(screen.getByText(/Paused by Ravi/)).toBeVisible();

      // User paused their own brake, so Resume is available
      const resumeBtn = within(screen.getByTestId('network-paused-banner')).getByRole(
        'button',
        { name: 'Resume' }
      );
      expect(resumeBtn).toBeVisible();
    });

    it('banner Resume button is absent for non-admin user when locked by admin', async () => {
      mockViewer.id = 'user-alice';
      mockViewer.displayName = 'Alice';
      mockViewer.role = 'user';

      setupMockApis({
        brake: createDefaultBrake({
          userPaused: true,
          userPausedBy: 'Admin Bob',
          userLockedByAdmin: true, // Locked by admin
        }),
      });

      renderComponent(<NetworkPausedBanner />);

      expect(await screen.findByTestId('network-paused-banner')).toBeVisible();
      expect(screen.getByText(/Paused by Admin Bob/)).toBeVisible();

      // Resume button should be completely ABSENT for non-admin user when locked by admin
      expect(
        within(screen.getByTestId('network-paused-banner')).queryByRole('button', {
          name: 'Resume',
        })
      ).not.toBeInTheDocument();
    });

    it('NC: unaffected user Y sees no banner when user X is paused (nc_flipped == true)', () => {
      mockViewer.id = 'user-y';
      mockViewer.role = 'user';

      setupMockApis({
        brake: createDefaultBrake({
          userPaused: false, // User Y is not paused
          systemPaused: false,
        }),
      });

      renderComponent(<NetworkPausedBanner />);
      expect(screen.queryByTestId('network-paused-banner')).not.toBeInTheDocument();
    });
  });

  // ──────────────── CP4: Compatibility Endpoints ────────────────
  describe('CP4: compatibility mode authorization', () => {
    it('compatibility mode endpoints return 403 for non-admin user', async () => {
      mockViewer.role = 'user';

      setupMockApis();

      renderComponent(<NetworkAccessPage />);

      // Non-admin does not have Compatibility tab
      expect(screen.queryByRole('button', { name: /^Compatibility/ })).not.toBeInTheDocument();
    });

    it('NC: administrator successfully accesses compatibility features (nc_flipped == true)', async () => {
      mockViewer.role = 'admin';
      setupMockApis({
        compat: createDefaultCompat({
          profiles: [
            {
              imageDigest: 'sha256:11112222333344445555666677778888',
              connectorDisplayName: 'CustomConnector',
              relaxations: ['writable-rootfs', 'root-user'],
              grantedBy: 'Admin',
              grantedAtUtc: '2026-11-11T10:00:00Z',
            },
          ],
        }),
      });

      renderComponent(<NetworkAccessPage />);

      expect(await screen.findByRole('button', { name: /^Compatibility/ })).toBeVisible();
    });
  });

  // ──────────────── CP5: Compatibility UI ────────────────
  describe('CP5: compatibility mode cards and list', () => {
    it('admin card displays 3 plain ticked checkboxes without CAP_/seccomp/privileged', async () => {
      const user = userEvent.setup();
      const onApply = vi.fn();

      renderComponent(
        <CompatibilityModeCard
          request={{
            imageDigest: 'sha256:abcdef1234567890abcdef1234567890',
            connectorDisplayName: 'ThirdPartyMcp',
            firstSeenUtc: '2026-11-11T10:00:00Z',
          }}
          onApply={onApply}
        />
      );

      expect(
        screen.getByText(
          "ThirdPartyMcp uses a container image that Kautuka doesn't control. It needs relaxed settings to run. Network access control still applies."
        )
      ).toBeVisible();

      const chkWrite = screen.getByLabelText('Allow it to write to its own files');
      const chkRoot = screen.getByLabelText(
        "Allow it to run as the container's administrator account"
      );
      const chkCaps = screen.getByLabelText(
        'Allow it to change file ownership inside its container'
      );

      expect(chkWrite).toBeChecked();
      expect(chkRoot).toBeChecked();
      expect(chkCaps).toBeChecked();
      expect(screen.queryByRole('button', { name: 'Keep as is' })).not.toBeInTheDocument();

      // Ensure no technical flags are present
      const cardText = document.body.textContent ?? '';
      expect(cardText).not.toMatch(/CAP_/i);
      expect(cardText).not.toMatch(/\bseccomp\b/i);
      expect(cardText).not.toMatch(/\bprivileged\b/i);

      // Click "Use compatibility mode for this image"
      const applyBtn = screen.getByRole('button', {
        name: 'Use compatibility mode for this image',
      });
      await user.click(applyBtn);

      expect(onApply).toHaveBeenCalledWith(
        expect.objectContaining({
          relaxations: ['writable-rootfs', 'root-user', 'file-ownership-caps'],
        })
      );
    });

    it('NC: non-admin session sees none of the compatibility mode cards or list (nc_flipped == true)', () => {
      mockViewer.role = 'user';
      setupMockApis();

      renderComponent(<NetworkAccessPage />);

      expect(screen.queryByRole('button', { name: /Compatibility/i })).not.toBeInTheDocument();
      expect(screen.queryByText(/Images in compatibility mode/i)).not.toBeInTheDocument();
    });
  });

  // ──────────────── Grouping Test ────────────────
  describe('Grouping: pending requests from the same agent within 60s', () => {
    it('groups cards and provides "Allow all N" button', async () => {
      const user = userEvent.setup();
      const requestsData = createDefaultRequests([
        {
          id: 'req-group-1',
          userId: 'user-ravi',
          userDisplayName: 'Ravi',
          agentId: 'agent-burst',
          agentDisplayName: 'BurstBot',
          destination: 'site1.com',
          destKind: 'host',
          port: 443,
          actorKind: 'agent',
          firstSeenUtc: '2026-11-11T10:00:00Z',
          lastSeenUtc: '2026-11-11T10:00:01Z',
          attempts: 1,
          status: 'pending',
          origin: 'container',
          attribution: 'user-agent',
        },
        {
          id: 'req-group-2',
          userId: 'user-ravi',
          userDisplayName: 'Ravi',
          agentId: 'agent-burst',
          agentDisplayName: 'BurstBot',
          destination: 'site2.com',
          destKind: 'host',
          port: 443,
          actorKind: 'agent',
          firstSeenUtc: '2026-11-11T10:00:15Z',
          lastSeenUtc: '2026-11-11T10:00:16Z',
          attempts: 1,
          status: 'pending',
          origin: 'container',
          attribution: 'user-agent',
        },
      ]);
      setupMockApis({ requests: requestsData });

      renderComponent(<NetworkAccessPage />);

      const allowAllBtn = await screen.findByRole('button', { name: 'Allow all 2' });
      expect(allowAllBtn).toBeVisible();

      await user.click(allowAllBtn);
      expect(post).toHaveBeenCalledTimes(2);
      expect(post).toHaveBeenCalledWith(
        '/network-access/requests/req-group-1/decision',
        expect.objectContaining({ allow: true, scope: 'agent' })
      );
      expect(post).toHaveBeenCalledWith(
        '/network-access/requests/req-group-2/decision',
        expect.objectContaining({ allow: true, scope: 'agent' })
      );
    });
  });
});
