import { ImportModal } from '@/features/connectors/components/ImportModal';
import { fireEvent, screen } from '@testing-library/dom';
import { render } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

vi.mock('@/features/connectors/api/connectors', () => ({
  importMcpbConnectorBundle: vi.fn(),
  saveConnector: vi.fn(),
}));

describe('ImportModal JSON preview', () => {
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
