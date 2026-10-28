import { describe, expect, it } from 'vitest';
import { parseWorkflowJournalEvent } from '@/features/workflows/api/workflows';
import {
  describeWorkflowEvent,
  describeWorkflowEventFacts,
  describeWorkflowFailure,
  workflowStatusLabel,
  workflowPauseEventText,
  workflowRunStatusText,
} from '@/features/workflows/model/workflowLabels';

describe('workflow journal labels', () => {
  it('turns failure codes into readable sentences', () => {
    expect(describeWorkflowFailure('structured_output_invalid')).toBe(
      "The step's answer was missing some results."
    );
    expect(describeWorkflowFailure('gate.changes_exhausted')).toBe(
      'You asked for changes twice. Approve or stop this run.'
    );
    expect(describeWorkflowFailure('future.failure_code')).toBe('This step failed.');
    expect(describeWorkflowFailure('step.run_error', 'timeout')).toBe('The agent run failed.');
    expect(describeWorkflowEvent('workflow.step.started')).toBe('Step started');
    expect(describeWorkflowEvent('workflow.paused')).toBe('Waiting for your review');
    expect(workflowStatusLabel.paused).toBe('Paused');
    const pausedRun = { id: 'run-1', status: 'paused' };
    expect(
      workflowRunStatusText(pausedRun, [{ instanceId: 'run-1', kind: 'workflow_review' }])
    ).toBe('Waiting for your review');
    expect(workflowRunStatusText(pausedRun, [])).toBe('Paused');
    expect(
      workflowPauseEventText(
        ['workflow.paused', 'workflow.resumed', 'workflow.finished'],
        0,
        false,
        false,
        true
      )
    ).toBe('Reviewed');
    expect(
      workflowPauseEventText(
        ['workflow.paused', 'workflow.resumed', 'workflow.finished'],
        0,
        false,
        false
      )
    ).toBe('Paused');
    expect(workflowPauseEventText(['workflow.paused'], 0, true, false)).toBe('Paused');
    expect(describeWorkflowEvent('workflow.edge.taken')).toBe('Path taken');
    expect(
      describeWorkflowEventFacts({
        type: 'workflow.edge.taken',
        from: 'assess',
        to: 'proceed',
        pointer: '/decision',
        op: 'eq',
      })
    ).toEqual([
      { label: 'Path', value: 'assess → proceed' },
      { label: 'Condition', value: 'eq /decision' },
    ]);
    expect(
      describeWorkflowEventFacts({
        type: 'structured_output.extracted',
        droppedKeys: ['extra'],
      })
    ).toEqual([{ label: 'Ignored', value: 'extra' }]);
    expect(
      describeWorkflowEventFacts({
        type: 'workflow.started',
        timestamp: '2026-10-03T04:19:29.0000000+00:00',
        workflowSequence: 1,
        definitionId: 'wf-1',
        envelopeHash: 'secret-hash',
      })
    ).toEqual([{ label: 'Started', value: '2026-10-03T04:19:29.0000000+00:00' }]);
  });

  it('parses public Narad workflow events', () => {
    const parsed = parseWorkflowJournalEvent({
      type: 'workflow.step.failed',
      eventId: 'evt-9',
      timestamp: '2026-09-19T12:00:00Z',
      nodeId: 'step-1',
      code: 'structured_output_invalid',
      message: 'Step output was not JSON.',
      workflowSequence: 4,
    });
    expect(parsed).toMatchObject({
      type: 'workflow.step.failed',
      nodeId: 'step-1',
      code: 'structured_output_invalid',
      workflowSequence: 4,
    });
    expect(
      parseWorkflowJournalEvent({
        type: 'workflow.edge.taken',
        eventId: 'evt-10',
        timestamp: '2026-09-19T12:00:01Z',
        from: 'assess',
        to: 'proceed',
        pointer: '/decision',
        op: 'eq',
      })
    ).toMatchObject({ from: 'assess', to: 'proceed', op: 'eq' });
    expect(parseWorkflowJournalEvent({ type: 'workflow.started' })).toBeNull();
  });
});
