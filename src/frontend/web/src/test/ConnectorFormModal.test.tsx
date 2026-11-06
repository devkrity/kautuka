import { ConnectorFormModal } from '@/features/connectors/components/ConnectorFormModal';
import type { ConnectorDraft } from '@/features/connectors/types/connectorDraft';
import { fireEvent, screen } from '@testing-library/dom';
import { render } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const mutateSpy = vi.fn();

vi.mock('@/features/connectors/api/queries', () => ({
  useTestConnector: () => ({
    isPending: false,
    mutate: mutateSpy,
  }),
}));

function createDraft(): ConnectorDraft {
  return {
    id: 'homeassistant-mcp',
    name: 'Home Assistant',
    description: 'Official Home Assistant MCP endpoint',
    iconUrl: '',
    presetId: null,
    transport: 'container',
    httpUrl: '',
    localCommand: '',
    localArgs: '',
    containerImage: 'example/container',
    containerCommand: '',
    containerArgs: '',
    containerSidecarName: '',
    bundlePath: '',
    serverType: '',
    readinessTimeoutSeconds: '',
    jitCooldownSeconds: '',
    healthcheckUrl: '',
    healthcheckIntervalMilliseconds: '',
    mcpProtocolVersion: '',
    envKeys: 'CONNECTOR_TOKEN',
    enabled: false,
    toolCount: null,
    toolNames: null,
  };
}

describe('ConnectorFormModal container validation credentials', () => {
  beforeEach(() => {
    mutateSpy.mockReset();
  });

  it('adds, renames and removes masked credential rows without moving entered values', () => {
    const onSave = vi.fn();
    render(
      <ConnectorFormModal
        draft={createDraft()}
        isNew={false}
        onSave={onSave}
        onClose={vi.fn()}
        saving={false}
      />
    );
    expect(screen.getByLabelText('Value for CONNECTOR_TOKEN')).toHaveAttribute('type', 'password');
    fireEvent.change(screen.getByLabelText('Value for CONNECTOR_TOKEN'), {
      target: { value: 'synthetic-old' },
    });
    fireEvent.click(screen.getByRole('button', { name: '+ Add key' }));
    expect(screen.getByRole('button', { name: 'Save changes' })).toBeDisabled();
    fireEvent.change(screen.getByLabelText('Environment key 2'), {
      target: { value: 'CONNECTOR_TOKEN' },
    });
    expect(screen.getByRole('button', { name: 'Test connection' })).toBeDisabled();
    fireEvent.change(screen.getByLabelText('Environment key 2'), {
      target: { value: 'OTHER_KEY' },
    });
    expect(screen.getByLabelText('Value for CONNECTOR_TOKEN')).toHaveValue('');
    fireEvent.change(screen.getByLabelText('Value for OTHER_KEY'), {
      target: { value: 'synthetic-new' },
    });
    fireEvent.change(screen.getByLabelText('Environment key 2'), {
      target: { value: 'RENAMED_KEY' },
    });
    expect(screen.getByLabelText('Value for RENAMED_KEY')).toHaveValue('');
    fireEvent.change(screen.getByLabelText('Value for RENAMED_KEY'), {
      target: { value: 'synthetic-final' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Test connection' }));
    expect(mutateSpy).toHaveBeenCalledWith(
      expect.objectContaining({
        envKeys: ['CONNECTOR_TOKEN', 'RENAMED_KEY'],
        testEnv: { RENAMED_KEY: 'synthetic-final' },
      }),
      expect.any(Object)
    );
    fireEvent.click(screen.getByRole('button', { name: 'Remove environment key 2' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    expect(onSave).toHaveBeenCalledWith(
      expect.objectContaining({ envKeys: 'CONNECTOR_TOKEN' }),
      {}
    );
  });

  it('keeps the managed mailbox credential declaration hidden and unchanged', () => {
    const draft = {
      ...createDraft(),
      id: 'kautuka-m365-mail',
      envKeys: 'KAUTUKA_M365_REFRESH_TOKEN\nOTHER_KEY',
    };
    const onSave = vi.fn();
    render(
      <ConnectorFormModal
        draft={draft}
        isNew={false}
        onSave={onSave}
        onClose={vi.fn()}
        saving={false}
      />
    );
    expect(screen.queryByDisplayValue('KAUTUKA_M365_REFRESH_TOKEN')).not.toBeInTheDocument();
    expect(screen.getByText(/Mailbox sign-in is on the connector card/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    expect(onSave).toHaveBeenCalledWith(expect.objectContaining({ envKeys: draft.envKeys }), {});
  });

  it('preserves saved custom commands and applies runner defaults only on selection', () => {
    const onSave = vi.fn();
    const draft = {
      ...createDraft(),
      transport: 'local',
      localCommand: 'my-runner',
      localArgs: '--existing\nvalue',
    };
    render(
      <ConnectorFormModal
        draft={draft}
        isNew={false}
        onSave={onSave}
        onClose={vi.fn()}
        saving={false}
      />
    );
    expect(screen.getByLabelText('Runner')).toHaveValue('custom');
    expect(screen.getByLabelText('Command')).toHaveValue('my-runner');
    expect(screen.getByLabelText('Local Arguments (one per line)')).toHaveValue(
      '--existing\nvalue'
    );
    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));
    expect(onSave).toHaveBeenCalledWith(
      expect.objectContaining({ localCommand: 'my-runner', localArgs: '--existing\nvalue' }),
      {}
    );
    fireEvent.change(screen.getByLabelText('Runner'), { target: { value: 'npx' } });
    expect(screen.getByLabelText('Local Arguments (one per line)')).toHaveValue('-y');
    fireEvent.change(screen.getByLabelText('Runner'), { target: { value: 'uvx' } });
    expect(screen.getByLabelText('Local Arguments (one per line)')).toHaveValue('');
    fireEvent.change(screen.getByLabelText('Runner'), { target: { value: 'python' } });
    expect(screen.getByLabelText('Local Arguments (one per line)')).toHaveValue('');
    fireEvent.click(screen.getByRole('button', { name: 'Test connection' }));
    expect(mutateSpy).toHaveBeenCalledWith(
      expect.objectContaining({ localCommand: 'python', localArgs: [] }),
      expect.any(Object)
    );
  });

  it('keeps discovered tools visible and in the save payload after parent rerenders', () => {
    mutateSpy.mockImplementation((_payload, options?: { onSuccess?: (value: unknown) => void }) => {
      options?.onSuccess?.({
        success: true,
        message: 'Connected. 2 tool(s) available.',
        toolCount: 2,
        packageChecked: null,
        toolNames: ['read_file', 'write_file'],
        toolDescriptions: {
          read_file: 'Read a file',
          write_file: 'Write a file',
        },
      });
    });

    const onSave = vi.fn();
    const initialDraft = createDraft();
    initialDraft.transport = 'container';
    initialDraft.httpUrl = '';
    initialDraft.containerImage = 'quay.io/fedora/fedora-minimal:44';
    initialDraft.containerCommand = 'npx';
    initialDraft.containerArgs = '-y\n@modelcontextprotocol/server-filesystem';

    const { rerender } = render(
      <ConnectorFormModal
        draft={initialDraft}
        isNew={false}
        onSave={onSave}
        onClose={() => undefined}
        saving={false}
      />
    );

    expect(screen.getByText('Advanced container settings').closest('details')).not.toHaveAttribute(
      'open'
    );
    fireEvent.click(screen.getByText('Advanced container settings'));
    expect(screen.getByLabelText('MCP protocol')).toHaveValue('');

    fireEvent.click(screen.getByRole('button', { name: 'Test connection' }));

    expect(screen.getByText('2 tools found')).toBeInTheDocument();
    expect(screen.getByText('read_file')).toBeInTheDocument();
    expect(screen.getByText('write_file')).toBeInTheDocument();

    rerender(
      <ConnectorFormModal
        draft={{ ...initialDraft }}
        isNew={false}
        onSave={onSave}
        onClose={() => undefined}
        saving={false}
      />
    );

    expect(screen.getByText('2 tools found')).toBeInTheDocument();
    expect(screen.getByText('read_file')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    const [savedDraft] = onSave.mock.calls[0] as [ConnectorDraft, Record<string, string>];
    expect(savedDraft.toolCount).toBe(2);
    expect(savedDraft.toolNames).toEqual(['read_file', 'write_file']);
  });

  it('sends test-only credentials for validation without persisting them on save', () => {
    const onSave = vi.fn();
    const credentialValue = ['test', 'credential', 'value'].join('-');

    render(
      <ConnectorFormModal
        draft={createDraft()}
        isNew={false}
        onSave={onSave}
        onClose={() => undefined}
        saving={false}
      />
    );

    expect(screen.getByRole('dialog')).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText('Value for CONNECTOR_TOKEN'), {
      target: { value: credentialValue },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Test connection' }));

    expect(mutateSpy).toHaveBeenCalledWith(
      expect.objectContaining({
        testEnv: {
          CONNECTOR_TOKEN: credentialValue,
        },
      }),
      expect.any(Object)
    );

    fireEvent.click(screen.getByRole('button', { name: 'Save changes' }));

    const [savedDraft, savedCredValues] = onSave.mock.calls[0] as [
      ConnectorDraft,
      Record<string, string>,
    ];

    expect(onSave).toHaveBeenCalledWith(
      expect.not.objectContaining({ testEnv: expect.anything() }),
      expect.any(Object)
    );
    expect(savedDraft.envKeys).toBe('CONNECTOR_TOKEN');
    expect(savedCredValues).toMatchObject({ CONNECTOR_TOKEN: credentialValue });
  });

  it('includes imported bundle metadata in the validation payload', () => {
    const draft = createDraft();
    draft.id = 'splose-mcp';
    draft.transport = 'container';
    draft.httpUrl = '';
    draft.containerImage = 'quay.io/fedora/fedora-minimal:44';
    draft.containerCommand = 'uv';
    draft.containerArgs = 'run\n-m\nsplose_mcp.server';
    draft.bundlePath = '/tmp/connectors/splose.mcpb';
    draft.serverType = 'uv';

    render(
      <ConnectorFormModal
        draft={draft}
        isNew={false}
        onSave={() => undefined}
        onClose={() => undefined}
        saving={false}
      />
    );

    fireEvent.click(screen.getByRole('button', { name: 'Test connection' }));

    expect(mutateSpy).toHaveBeenCalledWith(
      expect.objectContaining({
        transport: 'container',
        containerImage: 'quay.io/fedora/fedora-minimal:44',
        containerCommand: 'uv',
        containerArgs: ['run', '-m', 'splose_mcp.server'],
        bundlePath: '/tmp/connectors/splose.mcpb',
        serverType: 'uv',
      }),
      expect.any(Object)
    );
  });

  it('refreshes visible fields when reopened for a different connector draft', () => {
    const firstDraft = createDraft();
    firstDraft.id = 'markitdown';
    firstDraft.name = 'MarkItDown';
    firstDraft.transport = 'container';
    firstDraft.containerImage = 'docker.io/mcp/markitdown';
    firstDraft.containerCommand = '';
    firstDraft.containerArgs = '';

    const secondDraft = createDraft();
    secondDraft.id = 'filesystem';
    secondDraft.name = 'Filesystem';
    secondDraft.transport = 'container';
    secondDraft.containerImage = 'quay.io/fedora/fedora-minimal:44';
    secondDraft.containerCommand = 'npx';
    secondDraft.containerArgs =
      '-y\n@modelcontextprotocol/server-filesystem\n/Users/username/Desktop';

    const { rerender } = render(
      <ConnectorFormModal
        draft={firstDraft}
        isNew={false}
        onSave={() => undefined}
        onClose={() => undefined}
        saving={false}
      />
    );

    expect(screen.getByLabelText('Container Command')).toHaveValue('');
    expect(screen.getByLabelText('Container Arguments (one per line)')).toHaveValue('');

    rerender(
      <ConnectorFormModal
        draft={secondDraft}
        isNew={false}
        onSave={() => undefined}
        onClose={() => undefined}
        saving={false}
      />
    );

    expect(screen.getByLabelText('Container Command')).toHaveValue('npx');
    expect(screen.getByLabelText('Container Arguments (one per line)')).toHaveValue(
      '-y\n@modelcontextprotocol/server-filesystem\n/Users/username/Desktop'
    );
  });
});
