/**
 * @vitest-environment jsdom
 */
import { WorkflowsPage } from '@/pages/WorkflowsPage';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, within } from '@testing-library/react';
import React from 'react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { WorkflowDefinition, WorkflowInstance } from '@/features/workflows';
import {
  addChoice,
  addChoiceRule,
  connectPathTo,
  disconnectPath,
  moveRule,
  replaceAgent,
} from '@/features/workflows/model/workflowCanvas';
import {
  createChoiceDraft,
  type WorkflowStepDraft,
} from '@/features/workflows/model/workflowDraft';
import type { EditResult } from '@/features/workflows/model/workflowCanvas';

const {
  listWorkflowsMock,
  getWorkflowMock,
  listWorkflowInstancesMock,
  getWorkflowInstanceMock,
  listWorkflowJournalMock,
} = vi.hoisted(() => ({
  listWorkflowsMock: vi.fn(),
  getWorkflowMock: vi.fn(),
  listWorkflowInstancesMock: vi.fn(),
  getWorkflowInstanceMock: vi.fn(),
  listWorkflowJournalMock: vi.fn(),
}));

vi.mock('@/features/workflows/api/workflows', () => ({
  listWorkflows: listWorkflowsMock,
  archiveWorkflow: vi.fn(),
  restoreWorkflow: vi.fn(),
  deleteWorkflow: vi.fn(),
  createWorkflow: vi.fn(),
  getWorkflow: getWorkflowMock,
  updateWorkflow: vi.fn(),
  listWorkflowInstances: listWorkflowInstancesMock,
  startWorkflowInstance: vi.fn(),
  getWorkflowInstance: getWorkflowInstanceMock,
  stopWorkflowInstance: vi.fn(),
  listWorkflowJournal: listWorkflowJournalMock,
}));

vi.mock('@/features/settings', () => ({
  useAgents: () => ({ data: [{ id: 'agent-a', name: 'Writer' }] }),
  apiError: (error: unknown) => (error instanceof Error ? error.message : 'Request failed'),
}));

vi.mock('@/features/workflows/components/WorkflowCanvas', () => {
  const createElement = React.createElement;
  return {
    WorkflowCanvas: ({
      steps,
      selectedKey,
      onSelect,
      onStepChange,
      onExtendPath,
      onEditResult,
    }: {
      steps: WorkflowStepDraft[];
      selectedKey: string;
      onSelect: (key: string) => void;
      onStepChange: (key: string, patch: { promptTemplate: string }) => void;
      onExtendPath: (key: string, agentId: string) => void;
      onEditResult: (result: EditResult) => void;
    }) => {
      const selected = steps.find((step) => step.key === selectedKey);
      return createElement(
        'div',
        { 'aria-label': 'Workflow topology' },
        createElement(
          'button',
          { type: 'button', onClick: () => onSelect('__workflow_start__') },
          'Start'
        ),
        ...steps.map((step, index) =>
          createElement(
            'button',
            { key: step.key, type: 'button', onClick: () => onSelect(step.key) },
            `Step ${index + 1} · Writer`
          )
        ),
        steps[0]
          ? createElement(
              'button',
              {
                type: 'button',
                onClick: () => onExtendPath(steps[0]!.key, 'agent-a'),
              },
              `Add a step after Step 1 · Writer`
            )
          : null,
        selected
          ? createElement(
              'div',
              null,
              createElement(
                'select',
                {
                  'aria-label': 'Who does this step?',
                  value: selected.agentId,
                  onChange: (event: React.ChangeEvent<HTMLSelectElement>) =>
                    onEditResult(replaceAgent(steps, selected.key, event.target.value)),
                },
                createElement('option', { value: 'agent-a' }, 'Writer'),
                createElement('option', { value: 'agent-b' }, 'Reviewer')
              ),
              createElement('textarea', {
                id: `canvas-prompt-${selected.key}`,
                'aria-label': `Canvas instructions for ${selected.key}`,
                value: selected.promptTemplate,
                onChange: (event: React.ChangeEvent<HTMLTextAreaElement>) =>
                  onStepChange(selected.key, { promptTemplate: event.target.value }),
              })
            )
          : null,
        selected
          ? createElement(
              'div',
              null,
              createElement(
                'button',
                {
                  type: 'button',
                  onClick: () => onEditResult(replaceAgent(steps, selected.key, 'agent-b')),
                },
                'Action: change agent'
              ),
              createElement(
                'button',
                {
                  type: 'button',
                  onClick: () => onEditResult(addChoice(steps, selected.key, 'agent-a')),
                },
                'Action: add choice'
              ),
              createElement(
                'button',
                {
                  type: 'button',
                  onClick: () => {
                    const choice = addChoice(steps, selected.key, 'agent-a');
                    onEditResult(
                      choice.ok ? addChoiceRule(choice.steps, selected.key, 'agent-a') : choice
                    );
                  },
                },
                'Action: add another If'
              ),
              createElement(
                'button',
                {
                  type: 'button',
                  onClick: () => {
                    const choice = addChoice(steps, selected.key, 'agent-a');
                    if (!choice.ok) return onEditResult(choice);
                    const more = addChoiceRule(choice.steps, selected.key, 'agent-a');
                    const source = more.ok && more.steps.find((step) => step.key === selected.key);
                    const addedChoice = source && source.choices[source.choices.length - 1];
                    onEditResult(
                      addedChoice ? moveRule(more.steps, selected.key, addedChoice.key, 'up') : more
                    );
                  },
                },
                'Action: move If'
              ),
              createElement(
                'button',
                {
                  type: 'button',
                  onClick: () => {
                    const root = {
                      ...selected,
                      key: 'root',
                      choices: [createChoiceDraft('title', 'selected')],
                      otherwiseKey: 'neighbor',
                    };
                    const branch = [
                      root,
                      { ...selected, key: 'selected', otherwiseKey: 'tail' },
                      { ...selected, key: 'neighbor', otherwiseKey: 'tail' },
                      { ...selected, key: 'tail', otherwiseKey: '' },
                    ];
                    onEditResult(disconnectPath(branch, 'selected'));
                  },
                },
                'Action: Stop here instead'
              ),
              createElement(
                'button',
                {
                  type: 'button',
                  onClick: () => {
                    const root = {
                      ...selected,
                      key: 'root',
                      choices: [createChoiceDraft('title', 'selected')],
                      otherwiseKey: 'neighbor',
                    };
                    const branch = [
                      root,
                      { ...selected, key: 'selected', otherwiseKey: '' },
                      { ...selected, key: 'neighbor', otherwiseKey: 'tail' },
                      { ...selected, key: 'tail', otherwiseKey: '' },
                    ];
                    onEditResult(connectPathTo(branch, 'selected', 'tail'));
                  },
                },
                'Action: Continue with an existing step'
              )
            )
          : null
      );
    },
  };
});

const definition: WorkflowDefinition = {
  id: 'wf-1',
  ownerUserId: 'user-1',
  name: 'Review draft',
  governed: false,
  revision: 1,
  inputSchema: {
    type: 'object',
    required: ['title'],
    properties: { title: { type: 'string' } },
  },
  workingSchema: { type: 'object' },
  outputSchema: { type: 'object' },
  nodes: [
    {
      nodeId: 'review',
      agentId: 'agent-a',
      promptTemplate: 'Review {{input.title}}',
      outputKind: 'schema',
      outputSchema: {
        type: 'object',
        required: ['title'],
        properties: { title: { type: 'string' } },
      },
      maps: [{ from: '/title', to: '/title' }],
      sideEffects: 'none',
      memoryWrites: false,
      requiresReview: false,
      maxRetries: 0,
    },
  ],
  createdAt: '2026-09-19T00:00:00Z',
  updatedAt: '2026-09-19T00:00:00Z',
  diagram: 'flowchart TD\n  review["Step 1 · Writer (starts here)"]',
};

const instance: WorkflowInstance = {
  id: 'wfi-1',
  workflowId: 'wf-1',
  definitionRevision: 1,
  ownerUserId: 'user-1',
  correlationKey: null,
  status: 'running',
  stopRequested: false,
  currentNodeId: 'review',
  envelope: { title: 'Draft' },
  envelopeHash: 'abc',
  envelopeRevision: 0,
  snapshot: definition,
  steps: [
    {
      id: 'wattempt-1',
      instanceId: 'wfi-1',
      nodeId: 'review',
      attempt: 1,
      runId: 'wrun-1',
      sessionId: null,
      status: 'pending',
      failureCode: null,
      startedAt: null,
      endedAt: null,
      createdAt: '2026-09-19T00:00:00Z',
    },
  ],
  failureCode: null,
  error: null,
  createdAt: '2026-09-19T00:00:00Z',
  updatedAt: '2026-09-19T00:00:00Z',
  startedAt: '2026-09-19T00:00:00Z',
  endedAt: null,
};

function renderAt(path: string) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[path]}>
        <Routes>
          <Route path="/workflows" element={<WorkflowsPage />} />
          <Route path="/workflows/:workflowId" element={<WorkflowsPage />} />
          <Route path="/workflows/:workflowId/edit" element={<WorkflowsPage />} />
          <Route path="/workflows/:workflowId/instances/:instanceId" element={<WorkflowsPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>
  );
}

describe('WorkflowsPage first-class surface', () => {
  beforeEach(() => {
    window.localStorage.removeItem('kautuka-workflows-sidebar-open');
    listWorkflowsMock
      .mockReset()
      .mockImplementation((archived = false) => Promise.resolve(archived ? [] : [definition]));
    getWorkflowMock.mockReset().mockResolvedValue(definition);
    listWorkflowInstancesMock.mockReset().mockResolvedValue([instance]);
    getWorkflowInstanceMock.mockReset().mockResolvedValue(instance);
    listWorkflowJournalMock.mockReset().mockResolvedValue([
      {
        type: 'workflow.started',
        eventId: 'evt-1',
        timestamp: '2026-09-19T00:00:01Z',
        workflowSequence: 1,
        definitionId: 'wf-1',
      },
    ]);
  });

  it('keeps the workflow list visible outside the editor when its saved editor preference is hidden', async () => {
    window.localStorage.setItem('kautuka-workflows-sidebar-open', 'false');
    const editor = renderAt('/workflows/wf-1/edit');
    expect(await screen.findByRole('button', { name: 'Show workflows' })).toBeVisible();
    expect(
      screen.queryByRole('navigation', { name: 'Workflows and runs' })
    ).not.toBeInTheDocument();
    editor.unmount();

    const catalog = renderAt('/workflows');
    expect(screen.getByRole('navigation', { name: 'Workflows and runs' })).toBeVisible();
    catalog.unmount();

    renderAt('/workflows/wf-1/instances/run-1');
    expect(screen.getByRole('navigation', { name: 'Workflows and runs' })).toBeVisible();
    expect(await screen.findByText('Workflow run')).toBeInTheDocument();
  });

  it('renders the catalog outside agent chat', async () => {
    renderAt('/workflows');
    expect(
      await screen.findByText(
        'Jobs your agents run step by step. You approve the steps you choose.'
      )
    ).toBeInTheDocument();
    expect(await screen.findByText('Payroll lodge')).toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: 'New workflow' })).toHaveLength(2);
    expect(screen.getByRole('button', { name: 'Settings' })).toBeInTheDocument();
    const workflowButton = await within(
      screen.getByRole('navigation', { name: 'Workflows and runs' })
    ).findByRole('button', { name: /Review draft.*1 step/ });
    expect(workflowButton).toHaveAttribute('aria-expanded', 'false');
    fireEvent.click(workflowButton);
    expect(
      await within(screen.getByRole('navigation', { name: 'Workflows and runs' })).findByRole(
        'button',
        { name: /Review draft.*1 step/ }
      )
    ).toHaveAttribute('aria-expanded', 'true');
    expect(screen.queryByRole('tab', { name: 'Channels' })).not.toBeInTheDocument();
  });

  it('shows the topology canvas, opens settings, and adds a step on request', async () => {
    renderAt('/workflows/wf-1/edit');
    expect(
      await screen.findByText(
        "Fill in the start fields when you run it. Each step's results are passed to the next steps."
      )
    ).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Review draft' })).toBeInTheDocument();
    expect(await screen.findByRole('button', { name: 'Start' })).toBeInTheDocument();
    expect(await screen.findByRole('button', { name: 'Step 1 · Writer' })).toBeInTheDocument();
    expect(screen.queryByRole('tab', { name: 'Channels' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save workflow' })).toBeInTheDocument();
    expect(screen.queryByText('Unsaved changes')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Undo' })).toBeDisabled();
    expect(screen.queryByText('How this workflow runs')).not.toBeInTheDocument();
    expect(
      within(document.querySelector('.workflow-canvas-actions')!).getByRole('button', {
        name: 'Run workflow',
      })
    ).toBeInTheDocument();
    const stepBox = screen.getByRole('button', { name: 'Step 1 · Writer' });
    expect(stepBox).toBeInTheDocument();
    fireEvent.click(stepBox);
    expect(document.getElementById('canvas-prompt-review')).toHaveValue('Review {{input.title}}');
    expect(screen.getByLabelText('Who does this step?')).toHaveValue('agent-a');
    expect(screen.queryByRole('button', { name: 'Add the next step' })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Add a step after Step 1 · Writer' }));
    expect(await screen.findByRole('button', { name: 'Step 2 · Writer' })).toBeInTheDocument();
  }, 10000);

  it('routes all workflow panel actions through the page edit dispatcher', async () => {
    renderAt('/workflows/wf-1/edit');
    fireEvent.click(await screen.findByRole('button', { name: 'Step 1 · Writer' }));

    fireEvent.click(screen.getByRole('button', { name: 'Action: change agent' }));
    fireEvent.click(screen.getByRole('button', { name: 'Action: add choice' }));
    fireEvent.click(screen.getByRole('button', { name: 'Action: add another If' }));
    fireEvent.click(screen.getByRole('button', { name: 'Action: move If' }));
    fireEvent.click(screen.getByRole('button', { name: 'Action: Stop here instead' }));
    fireEvent.click(screen.getByRole('button', { name: 'Action: Continue with an existing step' }));

    expect(screen.getByRole('button', { name: 'Save workflow' })).toBeInTheDocument();
    expect(screen.queryByText(/Cannot read properties of undefined/)).not.toBeInTheDocument();
  }, 10000);

  it('renders instance working data without using the chat workspace', async () => {
    renderAt('/workflows/wf-1/instances/wfi-1');
    expect(await screen.findByText('Run information')).toBeInTheDocument();
    expect(screen.getByText('Step journal')).toBeInTheDocument();
    expect(screen.queryByText('Results passed on')).not.toBeInTheDocument();
    expect(screen.queryByText(/Asked:/)).not.toBeInTheDocument();
    expect(screen.getByText('Stop')).toBeInTheDocument();
    expect(screen.getByText('All run values')).toBeInTheDocument();
    expect(screen.getByText('Title')).toBeInTheDocument();
    expect(screen.getAllByText('Draft').length).toBeGreaterThan(0);
    expect(screen.getByText('Writer')).toBeInTheDocument();
    expect(screen.getByText('Hide activity')).toBeInTheDocument();
    expect(screen.getByText('Run started')).toBeInTheDocument();
  });
});
