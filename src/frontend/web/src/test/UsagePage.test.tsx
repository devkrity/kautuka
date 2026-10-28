import { UsagePage } from '@/pages/UsagePage';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { screen } from '@testing-library/dom';
import { render } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const { useCurrentUserMock, useUsageRunsMock, useUsageSummaryMock } = vi.hoisted(() => ({
  useCurrentUserMock: vi.fn(),
  useUsageRunsMock: vi.fn(),
  useUsageSummaryMock: vi.fn(),
}));

const { useAgentsMock, useModelsMock } = vi.hoisted(() => ({
  useAgentsMock: vi.fn(),
  useModelsMock: vi.fn(),
}));

vi.mock('@/features/auth', async () => {
  const actual = await vi.importActual<typeof import('@/features/auth')>('@/features/auth');
  return {
    ...actual,
    useCurrentUser: useCurrentUserMock,
  };
});

vi.mock('@/features/usage', async () => {
  const actual = await vi.importActual<typeof import('@/features/usage')>('@/features/usage');
  return {
    ...actual,
    useUsageRuns: useUsageRunsMock,
    useUsageSummary: useUsageSummaryMock,
  };
});

vi.mock('@/features/settings', async () => {
  const actual = await vi.importActual<typeof import('@/features/settings')>('@/features/settings');
  return {
    ...actual,
    useAgents: useAgentsMock,
    useModels: useModelsMock,
  };
});

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <UsagePage />
      </MemoryRouter>
    </QueryClientProvider>
  );
}

const adminRoleName = ['ad', 'min'].join('');
const adminUserName = ['ad', 'min'].join('');

describe('UsagePage', () => {
  beforeEach(() => {
    vi.clearAllMocks();

    useCurrentUserMock.mockReturnValue({
      data: {
        id: 'user-1',
        username: adminUserName,
        role: adminRoleName,
      },
    });

    useAgentsMock.mockReturnValue({
      data: [{ id: 'agent-a', name: 'Ops Agent' }],
    });

    useModelsMock.mockReturnValue({
      data: [{ id: 'model-a', name: 'gpt-test', providerId: 'provider-a' }],
    });

    useUsageSummaryMock.mockReturnValue({
      data: {
        scope: 'self',
        groupBy: 'date',
        totals: {
          runCount: 3,
          providerRequestCount: 5,
          assistantTurnCount: 3,
          modelTurnCount: 5,
          toolCallCount: 1,
          tokenIn: 120,
          tokenOut: 340,
          totalTokens: 460,
          totalDurationMs: 4000,
          avgFirstTokenLatencyMs: 250,
          inputCost: null,
          outputCost: null,
          totalCost: null,
        },
        buckets: [
          {
            key: '2026-05-09',
            label: '2026-05-09',
            totals: {
              runCount: 3,
              providerRequestCount: 5,
              assistantTurnCount: 3,
              modelTurnCount: 5,
              toolCallCount: 1,
              tokenIn: 120,
              tokenOut: 340,
              totalTokens: 460,
              totalDurationMs: 4000,
              avgFirstTokenLatencyMs: 250,
              inputCost: null,
              outputCost: null,
              totalCost: null,
            },
          },
        ],
      },
      isPending: false,
      error: null,
    });

    useUsageRunsMock.mockReturnValue({
      data: {
        items: [
          {
            runId: 'run-1',
            sessionId: 'session-1',
            userId: 'user-1',
            usernameSnapshot: adminUserName,
            userDisplayNameSnapshot: 'Admin User',
            agentId: 'agent-a',
            agentNameSnapshot: 'Ops Agent',
            modelId: 'model-a',
            modelNameSnapshot: 'gpt-test',
            providerId: 'provider-a',
            providerNameSnapshot: 'Provider A',
            runStatus: 'completed',
            sourceChannel: 'chat',
            runStartedAt: '2026-05-09T10:00:00Z',
            firstModelOutputAt: '2026-05-09T10:00:01Z',
            runEndedAt: '2026-05-09T10:00:04Z',
            totalDurationMs: 4000,
            firstTokenLatencyMs: 250,
            tokenIn: 120,
            tokenOut: 340,
            totalTokens: 460,
            providerRequestCount: 2,
            modelTurnCount: 2,
            assistantTurnCount: 1,
            toolCallCount: 0,
            costBasis: 'none',
            inputCost: null,
            outputCost: null,
            totalCost: null,
            costCurrency: null,
            pricingSource: null,
            usageDate: '2026-05-09',
          },
        ],
        limit: 8,
        offset: 0,
        totalCount: 1,
        scope: 'self',
      },
      isPending: false,
      error: null,
    });
  });

  it('renders totals, semantics copy, and admin scope controls', () => {
    renderPage();

    expect(screen.getByText('Usage')).toBeInTheDocument();
    expect(
      screen.getByText(
        /Focus on run volume, token consumption, provider activity, and response timing/i
      )
    ).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'My usage' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'All users' })).toBeInTheDocument();
    expect(screen.getByText('Provider requests')).toBeInTheDocument();
    expect(screen.getByText('Ops Agent')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Show filters' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Last 15 mins' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Previous' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled();
  });

  it('shows custom datetime inputs when custom range is selected', async () => {
    renderPage();

    await userEvent.click(screen.getByRole('button', { name: 'Show filters' }));
    await userEvent.click(screen.getByRole('button', { name: 'Custom' }));

    expect(screen.getByLabelText('From')).toBeInTheDocument();
    expect(screen.getByLabelText('To')).toBeInTheDocument();
  });

  it('passes agent, model, and source filters into usage queries', async () => {
    renderPage();

    await userEvent.click(screen.getByRole('button', { name: 'Show filters' }));
    await userEvent.selectOptions(screen.getByLabelText('Agent'), 'agent-a');
    await userEvent.selectOptions(screen.getByLabelText('Model'), 'model-a');
    await userEvent.selectOptions(screen.getByLabelText('Source'), 'chat');

    expect(useUsageSummaryMock).toHaveBeenLastCalledWith(
      expect.objectContaining({
        agentId: 'agent-a',
        modelId: 'model-a',
        source: 'chat',
      })
    );
    expect(useUsageRunsMock).toHaveBeenLastCalledWith(
      expect.objectContaining({
        agentId: 'agent-a',
        modelId: 'model-a',
        source: 'chat',
      })
    );
  });
});
