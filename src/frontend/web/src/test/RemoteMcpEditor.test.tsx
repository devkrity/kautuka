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
      id: 'mcp-example-com',
      httpUrl: 'https://mcp.example.com/mcp',
      envKeys: '',
    });
    expect(onTest.mock.calls[0][1]).toEqual([]);
    expect(await screen.findByRole('status')).toHaveTextContent('search');
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Changed' } });
    expect(screen.queryByRole('status')).not.toBeInTheDocument();
  });

  it('keeps an imported ID while offering an optional description without an ID field', async () => {
    const onSave = vi.fn().mockResolvedValue(undefined);
    const { blankDraft } = await import('@/features/connectors/types/connectorDraft');
    render(
      <RemoteMcpEditor
        initialDraft={{
          ...blankDraft(),
          id: 'imported-id',
          name: 'Imported',
          httpUrl: 'https://example.com/mcp',
        }}
        onSave={onSave}
        onTest={vi.fn()}
        onClose={vi.fn()}
      />
    );
    fireEvent.click(screen.getByText('Advanced', { selector: 'summary' }));
    expect(screen.queryByLabelText('Connector ID')).not.toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('Description (optional)'), {
      target: { value: 'A useful note' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(onSave).toHaveBeenCalledOnce());
    expect(onSave.mock.calls[0][0]).toMatchObject({
      id: 'imported-id',
      description: 'A useful note',
    });
  });

  it('keeps bearer secrets out of the connector definition and defaults manual credentials to required', async () => {
    const { onSave } = setup();
    expect(screen.getByRole('combobox', { name: 'Authentication' })).toBeVisible();
    fireEvent.change(screen.getByLabelText('Authentication'), { target: { value: 'bearer' } });
    expect(screen.getByLabelText('Bearer token (required)')).toBeVisible();
    fireEvent.change(screen.getByLabelText('Bearer token (required)'), {
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
    fireEvent.click(screen.getByText('Advanced', { selector: 'summary' }));
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

  it('changes the primary credential label with authentication without changing its binding', async () => {
    const { onSave } = setup();
    fireEvent.change(screen.getByLabelText('Authentication'), { target: { value: 'bearer' } });
    expect(screen.getByLabelText('Bearer token (required)')).toHaveAttribute('type', 'password');
    fireEvent.change(screen.getByLabelText('Bearer token (required)'), {
      target: { value: 'synthetic-value' },
    });
    fireEvent.change(screen.getByLabelText('Authentication'), { target: { value: 'api-key' } });
    expect(screen.getByLabelText('API key (required)')).toHaveValue('synthetic-value');
    expect(screen.getByLabelText('API key (required)')).toHaveAttribute('type', 'password');
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(onSave).toHaveBeenCalledOnce());
    expect(onSave.mock.calls[0][0].credentials).toEqual([
      { key: 'api-key', label: 'API key', required: true },
    ]);
    expect(onSave.mock.calls[0][0].httpHeaders).toEqual([
      { name: 'x-api-key', credentialKey: 'api-key' },
    ]);
    fireEvent.change(screen.getByLabelText('Authentication'), { target: { value: 'none' } });
    expect(screen.queryByLabelText('API key (required)')).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(onSave).toHaveBeenCalledTimes(2));
    expect(onSave.mock.calls[1][0]).toMatchObject({ credentials: [], httpHeaders: [] });
    expect(onSave.mock.calls[1][1]).toEqual([]);
  });

  it('retains custom labels for additional credentials', () => {
    const connector = connectorSchema.parse({
      id: 'custom',
      name: 'Custom',
      transport: 'http',
      httpUrl: 'https://example.com/mcp',
      credentials: [
        { key: 'primary', label: 'Original', required: false },
        { key: 'extra', label: 'Workspace secret', required: true },
      ],
      httpHeaders: [
        { name: 'Authorization', credentialKey: 'primary', prefix: 'Bearer ' },
        { name: 'X-Workspace', credentialKey: 'extra', prefix: 'Token ' },
      ],
    });
    render(
      <RemoteMcpEditor connector={connector} onSave={vi.fn()} onTest={vi.fn()} onClose={vi.fn()} />
    );
    expect(screen.getByLabelText('Bearer token (optional)')).toHaveAttribute('type', 'password');
    expect(screen.getByLabelText('Workspace secret (required)')).toHaveAttribute(
      'type',
      'password'
    );
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
    fireEvent.change(screen.getByLabelText('Bearer token (required)'), {
      target: { value: 'synthetic-owner-key' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findByRole('alert')).not.toHaveTextContent('synthetic-owner-key');
    expect(screen.getByLabelText('Bearer token (required)')).toHaveValue('synthetic-owner-key');
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
    expect(screen.getByText(/Everyone's credential remains available/)).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('Credential scope'), { target: { value: 'system' } });
    fireEvent.click(screen.getByRole('button', { name: 'Remove API key' }));
    expect(screen.getByText('Will remove the shared system credential.')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(onSave).toHaveBeenCalledOnce());
    expect(onSave.mock.calls[0][2]).toBe('system');
    expect(onSave.mock.calls[0][1]).toEqual([{ key: 'api-key', action: 'remove' }]);
  });

  it('requires acknowledgement before changing a shared address', async () => {
    const onSave = vi.fn().mockResolvedValue(undefined);
    const connector = connectorSchema.parse({
      id: 'shared',
      name: 'Shared',
      transport: 'http',
      scope: 'system',
      httpUrl: 'https://old.example/mcp',
      credentials: [],
      httpHeaders: [],
    });
    render(
      <RemoteMcpEditor connector={connector} onSave={onSave} onTest={vi.fn()} onClose={vi.fn()} />
    );
    fireEvent.change(screen.getByLabelText('Server URL'), {
      target: { value: 'https://new.example/mcp' },
    });
    expect(screen.getByRole('alert')).toHaveTextContent(
      'everyone who uses it will need to enter their key again'
    );
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
    fireEvent.click(screen.getByRole('checkbox', { name: /I understand/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(onSave).toHaveBeenCalledOnce());
    fireEvent.change(screen.getByLabelText('Server URL'), {
      target: { value: 'https://another.example/mcp' },
    });
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
  });

  it('requires explicit review before replacing a legacy key with no authentication', async () => {
    const onSave = vi.fn().mockResolvedValue(undefined);
    const connector = connectorSchema.parse({
      id: 'legacy',
      name: 'Legacy',
      transport: 'http',
      httpUrl: 'https://example.com/mcp',
      requiresCredentialReentry: true,
      credentials: [],
      httpHeaders: [],
    });
    render(
      <RemoteMcpEditor connector={connector} onSave={onSave} onTest={vi.fn()} onClose={vi.fn()} />
    );
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Confirm the new authentication choice'
    );
    expect(onSave).not.toHaveBeenCalled();
    fireEvent.click(
      screen.getByRole('checkbox', { name: /I reviewed the new authentication choice/ })
    );
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(onSave).toHaveBeenCalledOnce());
    expect(onSave.mock.calls[0][0].confirmLegacyAuthReplacement).toBe(true);
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
