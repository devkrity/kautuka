import { fireEvent, screen, waitFor } from '@testing-library/dom';
import { render } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import {
  RemoteMcpEditor,
  type RemoteCredentialMutation,
} from '@/features/connectors/components/RemoteMcpEditor';
import {
  connectorSchema,
  type ConnectorTestResult,
} from '@/features/connectors/schemas/connectorSchema';
import type { ConnectorDraft } from '@/features/connectors/types/connectorDraft';

const success = {
  success: true,
  message: 'Connected.',
  toolCount: 1,
  packageChecked: null,
  toolNames: ['search'],
  toolDescriptions: {},
};
function setup() {
  const onSave = vi
    .fn<(draft: ConnectorDraft, changes: RemoteCredentialMutation[]) => Promise<void>>()
    .mockResolvedValue(undefined);
  const onTest = vi
    .fn<
      (draft: ConnectorDraft, changes: RemoteCredentialMutation[]) => Promise<ConnectorTestResult>
    >()
    .mockResolvedValue(success);
  render(<RemoteMcpEditor onSave={onSave} onTest={onTest} onClose={vi.fn()} />);
  fireEvent.change(screen.getByLabelText('Server URL'), {
    target: { value: 'https://mcp.example.com/mcp' },
  });
  return { onSave, onTest };
}

describe('RemoteMcpEditor', () => {
  it('connects from a URL without JSON or advanced settings and invalidates stale test results', async () => {
    const { onTest } = setup();
    expect(screen.getByLabelText('Name')).toHaveValue('mcp.example.com');
    fireEvent.click(screen.getByRole('button', { name: 'Test connection' }));
    await waitFor(() => expect(onTest).toHaveBeenCalledOnce());
    expect(onTest.mock.calls[0][0]).toMatchObject({
      transport: 'http',
      httpUrl: 'https://mcp.example.com/mcp',
      envKeys: '',
    });
    expect(onTest.mock.calls[0][1]).toEqual([]);
    expect(await screen.findByRole('status')).toHaveTextContent('search');
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Changed' } });
    expect(screen.queryByRole('status')).not.toBeInTheDocument();
  });

  it('keeps bearer secrets out of the connector definition and defaults manual credentials to required', async () => {
    const { onSave } = setup();
    fireEvent.change(screen.getByLabelText('Authentication'), { target: { value: 'bearer' } });
    fireEvent.change(screen.getByLabelText('API key (required)'), {
      target: { value: 'synthetic-owner-key' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(onSave).toHaveBeenCalledOnce());
    const [draft, mutations] = onSave.mock.calls[0];
    expect(draft.httpHeaders).toEqual([
      { name: 'Authorization', credentialKey: 'api-key', prefix: 'Bearer ' },
    ]);
    expect(draft.credentials).toEqual([{ key: 'api-key', label: 'API key', required: true }]);
    expect(JSON.stringify(draft)).not.toContain('synthetic-owner-key');
    expect(mutations).toEqual([
      { key: 'api-key', action: 'replace', value: 'synthetic-owner-key' },
    ]);
  });

  it('supports an arbitrary API-key header without losing the authentication fields', async () => {
    const { onSave } = setup();
    fireEvent.change(screen.getByLabelText('Authentication'), { target: { value: 'api-key' } });
    fireEvent.change(screen.getByLabelText('API key header name'), {
      target: { value: 'X-Subscription-Token' },
    });
    expect(screen.getByLabelText('Authentication')).toHaveValue('api-key');
    fireEvent.change(screen.getByLabelText('API key (required)'), {
      target: { value: 'synthetic-key' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(onSave).toHaveBeenCalledOnce());
    expect(onSave.mock.calls[0][0].httpHeaders).toEqual([
      { name: 'X-Subscription-Token', credentialKey: 'api-key' },
    ]);
  });

  it('preserves a configured key on blank input and sends explicit removal', async () => {
    const onSave = vi
      .fn<(draft: ConnectorDraft, changes: RemoteCredentialMutation[]) => Promise<void>>()
      .mockResolvedValue(undefined);
    const connector = connectorSchema.parse({
      id: 'exa',
      name: 'Exa',
      transport: 'http',
      httpUrl: 'https://mcp.exa.ai/mcp',
      presetId: 'exa',
      credentials: [{ key: 'api-key', label: 'API key', required: false }],
      httpHeaders: [{ name: 'x-api-key', credentialKey: 'api-key' }],
    });
    render(
      <RemoteMcpEditor
        connector={connector}
        configuredKeys={['api-key']}
        onSave={onSave}
        onTest={vi.fn()}
        onClose={vi.fn()}
      />
    );
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(onSave).toHaveBeenCalledTimes(1));
    expect(onSave.mock.calls[0][1]).toEqual([]);
    fireEvent.click(screen.getByRole('button', { name: 'Remove API key' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(onSave).toHaveBeenCalledTimes(2));
    expect(onSave.mock.calls[1][1]).toEqual([{ key: 'api-key', action: 'remove' }]);
    expect(onSave.mock.calls[1][0].credentials?.[0]?.required).toBe(false);
  });

  it('retains form values and hides provider error details after save failure', async () => {
    const { onSave } = setup();
    onSave.mockRejectedValue(new Error('provider echoed synthetic-owner-key'));
    fireEvent.change(screen.getByLabelText('Authentication'), { target: { value: 'bearer' } });
    fireEvent.change(screen.getByLabelText('API key (required)'), {
      target: { value: 'synthetic-owner-key' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findByRole('alert')).not.toHaveTextContent('synthetic-owner-key');
    expect(screen.getByLabelText('API key (required)')).toHaveValue('synthetic-owner-key');
  });

  it('distinguishes shared fallback from personal removal and uses the selected scope', async () => {
    const onSave = vi
      .fn<
        (
          draft: ConnectorDraft,
          changes: RemoteCredentialMutation[],
          scope?: 'user' | 'system'
        ) => Promise<void>
      >()
      .mockResolvedValue(undefined);
    const connector = connectorSchema.parse({
      id: 'exa',
      name: 'Exa',
      transport: 'http',
      httpUrl: 'https://mcp.exa.ai/mcp',
      scope: 'system',
      credentials: [{ key: 'api-key', label: 'API key', required: false }],
      httpHeaders: [{ name: 'x-api-key', credentialKey: 'api-key' }],
    });
    render(
      <RemoteMcpEditor
        connector={connector}
        configuredKeys={['api-key']}
        credentialPresence={[
          {
            key: 'api-key',
            userConfigured: false,
            systemConfigured: true,
            effectiveScope: 'system',
          },
        ]}
        canManageSystemCredentials
        onSave={onSave}
        onTest={vi.fn()}
        onClose={vi.fn()}
      />
    );
    expect(screen.queryByRole('button', { name: 'Remove API key' })).not.toBeInTheDocument();
    expect(screen.getByText(/Shared system access remains available/)).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('Credential scope'), { target: { value: 'system' } });
    fireEvent.click(screen.getByRole('button', { name: 'Remove API key' }));
    expect(screen.getByText('Will remove the shared system credential.')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(onSave).toHaveBeenCalledOnce());
    expect(onSave.mock.calls[0][2]).toBe('system');
    expect(onSave.mock.calls[0][1]).toEqual([{ key: 'api-key', action: 'remove' }]);
  });

  it('requires an explicit credential choice for a changed personal destination', async () => {
    const onSave = vi
      .fn<(draft: ConnectorDraft, changes: RemoteCredentialMutation[]) => Promise<void>>()
      .mockResolvedValue(undefined);
    const connector = connectorSchema.parse({
      id: 'remote',
      name: 'Shared',
      transport: 'http',
      httpUrl: 'https://shared.example/mcp',
      credentials: [{ key: 'api-key', label: 'API key', required: false }],
      httpHeaders: [{ name: 'x-api-key', credentialKey: 'api-key' }],
    });
    render(
      <RemoteMcpEditor
        connector={connector}
        canManage={false}
        configuredKeys={['api-key']}
        onSave={onSave}
        onTest={vi.fn()}
        onClose={vi.fn()}
      />
    );
    fireEvent.change(screen.getByLabelText('Server URL'), {
      target: { value: 'https://personal.example/mcp' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('replace or explicitly omit');
    expect(onSave).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Omit API key at new URL' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(onSave).toHaveBeenCalledOnce());
    expect(onSave.mock.calls[0][1]).toEqual([{ key: 'api-key', action: 'remove' }]);
  });
});
