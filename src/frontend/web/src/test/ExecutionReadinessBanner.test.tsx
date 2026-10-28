import { ExecutionReadinessBanner } from '@/components/ExecutionReadinessBanner';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

const { fetchExecutionReadinessMock } = vi.hoisted(() => ({
  fetchExecutionReadinessMock: vi.fn(),
}));

vi.mock('@/features/dashboard/api/dashboard', async () => {
  const actual = await vi.importActual<
    typeof import('@/features/dashboard/api/dashboard')
  >('@/features/dashboard/api/dashboard');
  return {
    ...actual,
    fetchExecutionReadiness: fetchExecutionReadinessMock,
  };
});

function renderBanner(authenticated = true) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });

  return render(
    <QueryClientProvider client={queryClient}>
      <ExecutionReadinessBanner authenticated={authenticated} />
    </QueryClientProvider>
  );
}

describe('ExecutionReadinessBanner', () => {
  afterEach(() => {
    vi.clearAllMocks();
    vi.useRealTimers();
  });

  it('shows failed dependencies and restoration guidance without a dismiss action', async () => {
    fetchExecutionReadinessMock.mockResolvedValue({
      ready: false,
      code: 'MANAGER_UNAVAILABLE',
      message: 'Manager is unavailable.',
      dependencies: [
        {
          dependency: 'Manager',
          ready: false,
          code: 'MANAGER_UNAVAILABLE',
          message: 'The Manager service cannot be reached.',
          detail: 'Connection refused.',
        },
        {
          dependency: 'CredentialsHost',
          ready: true,
          code: 'READY',
          message: 'CredentialsHost is ready.',
          detail: null,
        },
      ],
    });

    renderBanner();

    expect(await screen.findByRole('alert')).toHaveTextContent('Agent runs are unavailable');
    expect(screen.getByRole('alert')).toHaveTextContent('Manager is unavailable.');
    expect(screen.getByRole('alert')).toHaveTextContent(
      'Manager: The Manager service cannot be reached.'
    );
    expect(screen.getByRole('alert')).not.toHaveTextContent('Connection refused.');
    expect(screen.getByRole('alert')).toHaveTextContent(
      'Restore the services listed below, then retry a chat.'
    );
    expect(screen.queryByRole('button', { name: /dismiss/i })).not.toBeInTheDocument();
    expect(screen.queryByText(/CredentialsHost is ready/i)).not.toBeInTheDocument();
  });

  it('uses friendly dependency names and does not duplicate restoration guidance', async () => {
    fetchExecutionReadinessMock.mockResolvedValue({
      ready: false,
      code: 'CREDENTIALS_HOST_UNAVAILABLE',
      message: 'Secure credential storage is unavailable, so agent execution cannot start. Check that CredentialsHost is running, then retry.',
      dependencies: [
        {
          dependency: 'credentials_host',
          ready: false,
          code: 'CREDENTIALS_HOST_UNAVAILABLE',
          message: 'Credentials cannot be resolved.',
          detail: 'RpcException: endpoint /run/kautuka/credentialshost/credentialshost.sock refused the connection.',
        },
      ],
    });

    renderBanner();

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('CredentialsHost: Credentials cannot be resolved.');
    expect(alert.textContent?.match(/Check that CredentialsHost is running/g)).toHaveLength(1);
    expect(alert).not.toHaveTextContent('RpcException');
  });

  it('does not show a banner when all execution dependencies are ready', async () => {
    fetchExecutionReadinessMock.mockResolvedValue({
      ready: true,
      code: 'READY',
      message: 'Agent execution dependencies are ready.',
      dependencies: [],
    });

    renderBanner();

    await waitFor(() => expect(fetchExecutionReadinessMock).toHaveBeenCalledTimes(1));
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('fails visibly when readiness cannot be verified', async () => {
    fetchExecutionReadinessMock.mockRejectedValue(new Error('network error'));

    renderBanner();

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Kautuka could not verify the services required to run agents.'
    );
  });

  it('does not poll before authentication', async () => {
    renderBanner(false);

    await waitFor(() => expect(fetchExecutionReadinessMock).not.toHaveBeenCalled());
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });
});
