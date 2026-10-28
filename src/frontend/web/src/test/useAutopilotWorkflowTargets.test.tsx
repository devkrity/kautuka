/**
 * @vitest-environment jsdom
 */
import { render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import React from 'react';
import { describe, expect, it, vi } from 'vitest';
import type { SaveAutopilotScheduleRequest } from '@/features/autopilot/api/autopilot';

vi.mock('@/features/workflows', () => ({
  useWorkflow: () => ({ data: undefined }),
  useWorkflows: () => ({ data: [] }),
}));

import { useAutopilotWorkflowTargets } from '@/features/autopilot/hooks/useAutopilotWorkflowTargets';

const initialDraft: SaveAutopilotScheduleRequest = {
  targetType: 'agent',
  targetId: '',
  cronExpression: '*/5 * * * *',
  mode: 'on',
  schedulePromptContext: '{}',
  workspaceFilePaths: [],
  notifyUiInbox: true,
  notifyChannels: false,
  preRunApprovalExpiryPolicy: 'next_occurrence',
  overlapPolicy: 'skip_and_notify',
};

function Harness() {
  const [draft, setDraft] = React.useState(initialDraft);
  useAutopilotWorkflowTargets(
    draft,
    setDraft,
    () => undefined,
    () => undefined
  );
  return <output data-testid="draft">{JSON.stringify(draft)}</output>;
}

describe('workflow-target Autopilot navigation', () => {
  it('starts a new workflow schedule daily at 9 am', async () => {
    render(
      <MemoryRouter
        initialEntries={[
          {
            pathname: '/autopilot',
            state: { targetType: 'workflow', targetId: 'wf-1' },
          },
        ]}
      >
        <Harness />
      </MemoryRouter>
    );

    await waitFor(() => {
      expect(JSON.parse(screen.getByTestId('draft').textContent ?? '{}')).toMatchObject({
        targetType: 'workflow',
        targetId: 'wf-1',
        cronExpression: '0 9 * * *',
      });
    });
  });
});
