import { describe, expect, it } from 'vitest';
import type { WorkflowJournalEvent } from '@/features/workflows/api/workflows';
import { projectWorkflowTrace } from '@/features/workflows/model/workflowTrace';

function event(
  partial: Partial<WorkflowJournalEvent> &
    Pick<WorkflowJournalEvent, 'type' | 'eventId' | 'timestamp'>
): WorkflowJournalEvent {
  return partial;
}

describe('projectWorkflowTrace', () => {
  it('turns the journal into a run, one bar per step attempt, and a review wait', () => {
    const names = new Map([
      ['prepare', 'Step 1 · Roller'],
      ['lodge', 'Step 2 · My Assistant'],
    ]);
    const trace = projectWorkflowTrace(
      [
        event({ type: 'workflow.started', eventId: '1', timestamp: '2026-10-03T04:19:29.000Z' }),
        event({
          type: 'workflow.step.started',
          eventId: '2',
          timestamp: '2026-10-03T04:19:30.000Z',
          nodeId: 'prepare',
          stepAttempt: 1,
        }),
        event({
          type: 'workflow.step.finished',
          eventId: '3',
          timestamp: '2026-10-03T04:19:33.000Z',
          nodeId: 'prepare',
          stepAttempt: 1,
        }),
        event({
          type: 'workflow.paused',
          eventId: '4',
          timestamp: '2026-10-03T04:19:33.000Z',
          nodeId: 'prepare',
          interruptId: 'review-1',
        }),
        event({
          type: 'workflow.resumed',
          eventId: '5',
          timestamp: '2026-10-03T04:22:26.000Z',
          nodeId: 'prepare',
        }),
        event({
          type: 'workflow.step.started',
          eventId: '6',
          timestamp: '2026-10-03T04:22:27.000Z',
          nodeId: 'prepare',
          stepAttempt: 2,
          agentRunId: 'wrun-2',
        }),
        event({
          type: 'workflow.step.finished',
          eventId: '7',
          timestamp: '2026-10-03T04:22:31.000Z',
          nodeId: 'prepare',
          stepAttempt: 2,
        }),
        event({
          type: 'workflow.paused',
          eventId: '8',
          timestamp: '2026-10-03T04:22:31.000Z',
          nodeId: 'prepare',
          interruptId: 'review-2',
        }),
        event({ type: 'workflow.resumed', eventId: '9', timestamp: '2026-10-03T04:23:07.000Z' }),
        event({ type: 'workflow.resumed', eventId: '10', timestamp: '2026-10-03T04:23:07.100Z' }),
        event({
          type: 'workflow.step.started',
          eventId: '11',
          timestamp: '2026-10-03T04:23:09.000Z',
          nodeId: 'lodge',
          stepAttempt: 1,
        }),
        event({
          type: 'workflow.step.finished',
          eventId: '12',
          timestamp: '2026-10-03T04:23:11.000Z',
          nodeId: 'lodge',
          stepAttempt: 1,
        }),
        event({ type: 'workflow.finished', eventId: '13', timestamp: '2026-10-03T04:23:11.000Z' }),
      ],
      names,
      'wfi-1'
    );

    expect(trace.spans.map((span) => `${span.name}:${span.status}`)).toEqual([
      'Workflow run:completed',
      'Step 1 · Roller:completed',
      'Reviewed:completed',
      'Step 1 · Roller · attempt 2:completed',
      'Reviewed:completed',
      'Step 2 · My Assistant:completed',
    ]);
    const root = trace.spans[0]!;
    expect(root.latencyMs).toBe(
      Date.parse('2026-10-03T04:23:11.000Z') - Date.parse('2026-10-03T04:19:29.000Z')
    );
    const review = trace.spans[2]!;
    expect(review.latencyMs).toBe(
      Date.parse('2026-10-03T04:22:26.000Z') - Date.parse('2026-10-03T04:19:33.000Z')
    );
    expect(trace.spans[3]?.payloadJson).toContain('wrun-2');
  });

  it('closes an open step when the run is stopped', () => {
    const trace = projectWorkflowTrace(
      [
        event({ type: 'workflow.started', eventId: '1', timestamp: '2026-10-03T04:27:08.000Z' }),
        event({
          type: 'workflow.step.started',
          eventId: '2',
          timestamp: '2026-10-03T04:27:08.000Z',
          nodeId: 'prepare',
          stepAttempt: 1,
        }),
        event({ type: 'workflow.cancelled', eventId: '3', timestamp: '2026-10-03T04:27:14.000Z' }),
      ],
      new Map([['prepare', 'Step 1 · Roller']]),
      'wfi-stop'
    );

    expect(trace.spans.map((span) => `${span.name}:${span.status}`)).toEqual([
      'Workflow run:skipped',
      'Step 1 · Roller:skipped',
    ]);
  });
});
