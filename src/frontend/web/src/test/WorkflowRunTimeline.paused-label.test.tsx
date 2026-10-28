/**
 * @vitest-environment jsdom
 */
import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { WorkflowJournalEvent } from '@/features/workflows/api/workflows';

vi.mock('@/features/chat', () => ({ RunTraceInspector: () => null }));

import { WorkflowRunTimeline } from '@/features/workflows/components/WorkflowRunTimeline';

const pausedEvent = {
  eventId: 'paused-1',
  type: 'workflow.paused',
  timestamp: '2026-10-25T00:00:00Z',
  nodeId: 'review',
} as WorkflowJournalEvent;

describe('WorkflowRunTimeline paused labels', () => {
  it('says waiting for review when a review is open', () => {
    render(
      <WorkflowRunTimeline
        instanceId="run-1"
        events={[pausedEvent]}
        stepNames={new Map([['review', 'Step 1 (Reviewer)']])}
        pausedWithReview
      />
    );

    fireEvent.click(screen.getByRole('button', { name: /View activity/ }));
    expect(screen.getByText('Waiting for your review')).toBeInTheDocument();
  });

  it('says paused when there is no open review', () => {
    render(
      <WorkflowRunTimeline
        instanceId="run-1"
        events={[pausedEvent]}
        stepNames={new Map([['review', 'Step 1 (Reviewer)']])}
        pausedWithoutReview
      />
    );

    fireEvent.click(screen.getByRole('button', { name: /View activity/ }));
    expect(screen.getByText('Paused')).toBeInTheDocument();
  });

  it('marks an earlier pause as reviewed after the run continues and finishes', () => {
    render(
      <WorkflowRunTimeline
        instanceId="run-1"
        events={[
          { ...pausedEvent, interruptId: 'review-1' },
          { ...pausedEvent, eventId: 'resumed-1', type: 'workflow.resumed' },
          { ...pausedEvent, eventId: 'finished-1', type: 'workflow.finished' },
        ]}
        stepNames={new Map([['review', 'Step 1 (Reviewer)']])}
      />
    );

    fireEvent.click(screen.getByRole('button', { name: /View activity/ }));
    expect(screen.getByText('Reviewed')).toBeInTheDocument();
    expect(screen.queryByText('Waiting for your review')).not.toBeInTheDocument();
  });

  it('does not call a plain pause Reviewed when the run continues and finishes', () => {
    render(
      <WorkflowRunTimeline
        instanceId="run-1"
        events={[
          pausedEvent,
          { ...pausedEvent, eventId: 'resumed-1', type: 'workflow.resumed' },
          { ...pausedEvent, eventId: 'finished-1', type: 'workflow.finished' },
        ]}
        stepNames={new Map([['review', 'Step 1 (Reviewer)']])}
      />
    );

    fireEvent.click(screen.getByRole('button', { name: /View activity/ }));
    expect(screen.getByText('Paused')).toBeInTheDocument();
    expect(screen.queryByText('Reviewed')).not.toBeInTheDocument();
  });
});
