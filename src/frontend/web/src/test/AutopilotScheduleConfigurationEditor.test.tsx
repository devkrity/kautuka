import {
  AutopilotScheduleConfigurationEditor,
  type SaveAutopilotScheduleRequest,
} from '@/features/autopilot';
import { fireEvent, render, screen } from '@testing-library/react';
import React from 'react';
import { describe, expect, it } from 'vitest';

const initialDraft: SaveAutopilotScheduleRequest = {
  targetType: 'agent',
  targetId: '',
  cronExpression: '*/5 * * * *',
  mode: 'on',
  schedulePromptContext: '',
  workspaceFilePaths: [],
  notifyUiInbox: true,
  notifyChannels: false,
  preRunApprovalExpiryPolicy: 'next_occurrence',
  preRunApprovalTimeoutSeconds: null,
  overlapPolicy: 'skip_and_notify',
};

function Harness() {
  const [draft, setDraft] = React.useState(initialDraft);
  return (
    <>
      <AutopilotScheduleConfigurationEditor
        draft={draft}
        setDraft={setDraft}
        agents={[{ id: 'agent-1', name: 'Agent One' }]}
      >
        <div>Prompt owner slot</div>
      </AutopilotScheduleConfigurationEditor>
      <output data-testid="draft">{JSON.stringify(draft)}</output>
    </>
  );
}

describe('AutopilotScheduleConfigurationEditor', () => {
  it('owns schedule target, frequency, approval, overlap, and notification mutations', () => {
    render(<Harness />);

    fireEvent.change(screen.getByLabelText('Which agent?'), { target: { value: 'agent-1' } });
    fireEvent.change(screen.getByLabelText('How often should it run?'), {
      target: { value: '0 * * * *' },
    });
    fireEvent.click(screen.getByRole('button', { name: /Ask me first/ }));
    fireEvent.change(screen.getByLabelText('Pre-run approval expires'), {
      target: { value: 'duration' },
    });
    fireEvent.change(screen.getByLabelText('Approval window in minutes'), {
      target: { value: '30' },
    });
    fireEvent.click(screen.getByLabelText(/Send channel message/));

    const draft = JSON.parse(
      screen.getByTestId('draft').textContent ?? '{}'
    ) as SaveAutopilotScheduleRequest;
    expect(draft).toMatchObject({
      targetId: 'agent-1',
      cronExpression: '0 * * * *',
      mode: 'needs_approval',
      preRunApprovalExpiryPolicy: 'duration',
      preRunApprovalTimeoutSeconds: 1800,
      notifyChannels: true,
    });
  });

  it('chooses a target type before selecting a workflow', () => {
    function WorkflowHarness() {
      const [draft, setDraft] = React.useState<SaveAutopilotScheduleRequest>({
        ...initialDraft,
        targetType: 'workflow',
      });
      return (
        <>
          <AutopilotScheduleConfigurationEditor
            draft={draft}
            setDraft={setDraft}
            agents={[{ id: 'agent-1', name: 'Agent One' }]}
            workflows={[{ id: 'workflow-1', name: 'Weekly review', hasStartFields: false }]}
          >
            <div />
          </AutopilotScheduleConfigurationEditor>
          <output data-testid="workflow-draft">{JSON.stringify(draft)}</output>
        </>
      );
    }

    render(<WorkflowHarness />);
    expect(screen.getByLabelText('What should run?')).toHaveValue('workflow');
    expect(screen.getByLabelText('Which workflow?')).toHaveDisplayValue('— select —');
    expect(screen.getByLabelText('Which workflow?')).toContainElement(
      screen.getByRole('option', { name: 'Weekly review' })
    );
    expect(screen.queryByRole('option', { name: 'Agent One' })).not.toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('Which workflow?'), { target: { value: 'workflow-1' } });
    expect(JSON.parse(screen.getByTestId('workflow-draft').textContent ?? '{}')).toMatchObject({
      targetType: 'workflow',
      targetId: 'workflow-1',
    });
    expect(
      screen.queryByText('The workflow runs from its saved start fields.')
    ).not.toBeInTheDocument();
  });

  it('mentions saved start fields only when the selected workflow has them', () => {
    function WorkflowWithInputsHarness() {
      const [draft, setDraft] = React.useState<SaveAutopilotScheduleRequest>({
        ...initialDraft,
        targetType: 'workflow',
        targetId: 'workflow-1',
      });
      return (
        <AutopilotScheduleConfigurationEditor
          draft={draft}
          setDraft={setDraft}
          agents={[]}
          workflows={[{ id: 'workflow-1', name: 'Weekly review', hasStartFields: true }]}
        >
          <div />
        </AutopilotScheduleConfigurationEditor>
      );
    }
    render(<WorkflowWithInputsHarness />);
    expect(screen.getByText('The workflow runs from its saved start fields.')).toBeInTheDocument();
  });

  it('defaults a new workflow schedule to daily at 9 am', () => {
    render(<Harness />);
    fireEvent.change(screen.getByLabelText('What should run?'), { target: { value: 'workflow' } });
    expect(JSON.parse(screen.getByTestId('draft').textContent ?? '{}')).toMatchObject({
      targetType: 'workflow',
      cronExpression: '0 9 * * *',
    });
  });
});
