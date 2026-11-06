import { ImportModal } from '@/features/connectors/components/ImportModal';
import { fireEvent, screen, waitFor } from '@testing-library/dom';
import { render } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import {
  createRemoteMcpDraft,
  fetchConnector,
  saveConnector,
  saveRemoteMcpDraft,
} from '@/features/connectors/api/connectors';
import { connectorSchema } from '@/features/connectors/schemas/connectorSchema';

vi.mock('@/features/connectors/api/connectors', () => ({
  importMcpbConnectorBundle: vi.fn(),
  saveConnector: vi.fn(),
  fetchConnector: vi.fn(),
  fetchConnectorCredentialStatus: vi.fn(),
  createRemoteMcpDraft: vi.fn(),
  testRemoteMcpDraft: vi.fn(),
  saveRemoteMcpDraft: vi.fn(),
  cancelRemoteMcpDraft: vi.fn(),
}));

describe('ImportModal JSON preview', () => {
  beforeEach(() => vi.resetAllMocks());

  it('returns to the import result when a remote review is canceled', async () => {
    const onClose = vi.fn();
    render(<ImportModal existingIds={new Set()} onImported={vi.fn()} onClose={onClose} />);
    fireEvent.change(screen.getByLabelText('Paste JSON'), {
      target: {
        value: JSON.stringify({ mcpServers: { remote: { url: 'https://example.com/mcp' } } }),
      },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Import 1 connector' }));
    expect(await screen.findByLabelText('Server URL')).toHaveValue('https://example.com/mcp');
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(await screen.findByRole('dialog', { name: 'Import connector' })).toBeInTheDocument();
    expect(onClose).not.toHaveBeenCalled();
    expect(saveRemoteMcpDraft).not.toHaveBeenCalled();
  });

  it('rejects replacement of an existing connector the importer cannot manage', async () => {
    vi.mocked(fetchConnector).mockResolvedValue(
      connectorSchema.parse({ id: 'shared', name: 'Shared', transport: 'http', scope: 'system' })
    );
    render(
      <ImportModal
        existingIds={new Set(['shared'])}
        canManageConnector={() => false}
        onImported={vi.fn()}
        onClose={vi.fn()}
      />
    );
    fireEvent.change(screen.getByLabelText('Paste JSON'), {
      target: {
        value: JSON.stringify({
          mcpServers: {
            shared: {
              url: 'https://search.example/mcp',
              headers: { Authorization: 'Bearer synthetic-import-key' },
            },
          },
        }),
      },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Import 1 connector' }));
    expect(await screen.findByText(/do not have permission to replace/)).toBeInTheDocument();
    expect(createRemoteMcpDraft).not.toHaveBeenCalled();
    expect(saveRemoteMcpDraft).not.toHaveBeenCalled();
    expect(saveConnector).not.toHaveBeenCalled();
    expect(screen.queryByText(/synthetic-import-key/)).not.toBeInTheDocument();
  });
  it('reviews remote headers in the ordinary editor and saves secrets only through credential administration', async () => {
    const onImported = vi.fn();
    const saved = connectorSchema.parse({
      id: 'remote',
      name: 'Remote',
      transport: 'http',
      httpUrl: 'https://example.com/mcp',
    });
    vi.mocked(createRemoteMcpDraft).mockResolvedValue({
      draftId: 'import-draft',
      expiresAt: 'later',
    });
    vi.mocked(saveRemoteMcpDraft).mockResolvedValue(saved);
    render(<ImportModal existingIds={new Set()} onImported={onImported} onClose={vi.fn()} />);
    fireEvent.change(screen.getByLabelText('Paste JSON'), {
      target: {
        value: JSON.stringify({
          mcpServers: {
            remote: {
              url: 'https://example.com/mcp',
              headers: { Authorization: 'Bearer synthetic-import-key', 'X-Mode': 'free' },
            },
          },
        }),
      },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Import 1 connector' }));
    expect(await screen.findByLabelText('Bearer token (required)')).toHaveValue(
      'synthetic-import-key'
    );
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(onImported).toHaveBeenCalledWith(saved));
    const request = vi.mocked(createRemoteMcpDraft).mock.calls[0][0];
    expect(request.entries).toEqual({ 'header-1': 'synthetic-import-key' });
    expect(request.candidate.httpHeaders).toEqual([
      { name: 'Authorization', credentialKey: 'header-1', prefix: 'Bearer ' },
      { name: 'X-Mode', value: 'free' },
    ]);
    expect(JSON.stringify(request.candidate)).not.toContain('synthetic-import-key');
    expect(saveConnector).not.toHaveBeenCalled();
    expect(await screen.findByText('1 of 1 connectors imported successfully.')).toBeInTheDocument();
  });

  it('shows imported command args before saving', () => {
    render(
      <ImportModal
        existingIds={new Set<string>()}
        onImported={() => undefined}
        onClose={() => undefined}
      />
    );

    fireEvent.change(screen.getByLabelText('Paste JSON'), {
      target: {
        value: JSON.stringify({
          mcpServers: {
            filesystem: {
              command: 'npx',
              args: ['-y', '@modelcontextprotocol/server-filesystem', '/Users/username/Desktop'],
            },
          },
        }),
      },
    });

    expect(screen.getByText('Local command')).toBeInTheDocument();
    expect(screen.getByText('npx')).toBeInTheDocument();
    expect(
      screen.getByText('-y @modelcontextprotocol/server-filesystem /Users/username/Desktop')
    ).toBeInTheDocument();
  });
});
