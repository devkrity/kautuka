import React from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';

import { ExecutionGraphPanel } from '@/features/chat/components/ExecutionGraphPanel';
import type { ExecutionGraphSnapshot } from '@/features/chat/types/executionGraph';

const workflowGraph: ExecutionGraphSnapshot = {
  schemaVersion: 'narad.graph/v1',
  executionId: 'root-run',
  revision: 3,
  rootNodeId: 'run:root-run',
  nodes: [
    {
      nodeId: 'agent:inv-1',
      kind: 'agent',
      status: 'failed',
      label: 'child-agent',
      retryTarget: { kind: 'approval', id: 'approval-1' },
    },
    {
      nodeId: 'run:child-run',
      kind: 'run',
      status: 'running',
      label: 'Nested run',
      cancellableTarget: { kind: 'run', id: 'child-run' },
    },
    {
      nodeId: 'tool:call-a',
      kind: 'tool',
      status: 'failed',
      label: 'read_file',
      retryTarget: { kind: 'tool', id: 'call-a' },
      cancellableTarget: { kind: 'tool', id: 'call-a' },
    },
    {
      nodeId: 'tool:call-b',
      kind: 'tool',
      status: 'completed',
      label: 'run_shell',
      cancellableTarget: { kind: 'run', id: 'child-run' },
    },
  ],
  edges: [],
};

const intents = {
  retryTarget: vi.fn(async () => ({ status: 'accepted' as const })),
  cancelGraphNode: vi.fn(async () => ({ status: 'accepted' as const })),
};

describe('ExecutionGraphPanel', () => {
  it('hides nested multi-agent graphs until workflow UI is designed', () => {
    render(<ExecutionGraphPanel graph={workflowGraph} resyncNeeded={false} intents={intents} />);
    expect(screen.queryByTestId('execution-graph-panel')).not.toBeInTheDocument();
  });

  it('surfaces graph command failures through onCommandError for forced presentation', async () => {
    const user = userEvent.setup();
    const onCommandError = vi.fn();
    const failingIntents = {
      retryTarget: vi.fn(async () => ({ status: 'conflict' as const, detail: 'Already retried.' })),
      cancelGraphNode: vi.fn(async () => ({ status: 'accepted' as const })),
    };
    render(
      <ExecutionGraphPanel
        graph={workflowGraph}
        resyncNeeded={false}
        intents={failingIntents}
        presentation="execution"
        onCommandError={onCommandError}
      />,
    );

    await user.click(screen.getByTestId('retry-target-approval-approval-1'));
    await waitFor(() => expect(onCommandError).toHaveBeenCalledWith('Already retried.'));
  });

  it('shows cancel/retry only for honest running and failed approval targets when forced', async () => {
    const user = userEvent.setup();
    render(
      <ExecutionGraphPanel
        graph={workflowGraph}
        resyncNeeded={false}
        intents={intents}
        presentation="execution"
      />,
    );

    expect(screen.getByTestId('execution-graph-panel')).toBeInTheDocument();
    expect(screen.getByTestId('retry-target-approval-approval-1')).toBeInTheDocument();
    expect(screen.getByTestId('cancel-graph-node-run:child-run')).toBeInTheDocument();
    expect(screen.queryByTestId('retry-target-tool-call-a')).not.toBeInTheDocument();
    expect(screen.queryByTestId('cancel-graph-node-tool:call-a')).not.toBeInTheDocument();
    expect(screen.queryByTestId('cancel-graph-node-tool:call-b')).not.toBeInTheDocument();

    await user.click(screen.getByTestId('retry-target-approval-approval-1'));
    expect(intents.retryTarget).toHaveBeenCalledWith({ targetType: 'approval', targetId: 'approval-1' });

    await user.click(screen.getByTestId('cancel-graph-node-run:child-run'));
    expect(intents.cancelGraphNode).toHaveBeenCalledWith({ nodeId: 'run:child-run' });
  });

  it('hides root-only graphs', () => {
    render(
      <ExecutionGraphPanel
        graph={{
          schemaVersion: 'narad.graph/v1',
          executionId: 'root-run',
          revision: 1,
          rootNodeId: 'run:root-run',
          nodes: [{ nodeId: 'run:root-run', kind: 'run', status: 'running', label: 'Root run' }],
          edges: [],
        }}
        resyncNeeded={false}
        intents={intents}
      />,
    );
    expect(screen.queryByTestId('execution-graph-panel')).not.toBeInTheDocument();
  });
});
