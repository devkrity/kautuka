/**
 * @vitest-environment jsdom
 */
import { WorkflowDiagram } from '@/features/workflows/components/WorkflowDiagram';
import { markTakenChoices, takenChoiceIds } from '@/features/workflows/model/workflowDiagramMark';
import { render, screen, waitFor } from '@testing-library/react';
import React from 'react';
import { beforeEach, describe, expect, it } from 'vitest';

const payroll = `flowchart TD
  prepare["Step 1 · Roller (starts here)"]
  review_prepare["Pause after step 1"]
  lodge["Step 2 · My Assistant"]
  review_lodge["Pause after step 2"]
  prepare --> review_prepare
  review_prepare --> lodge
  lodge --> review_lodge
  classDef step fill:#eef3ff,stroke:#315fad,color:#142033
  class prepare,lodge step
  classDef pause fill:#fff7e8,stroke:#a87412,color:#3a2a08
  class review_prepare,review_lodge pause`;

describe('WorkflowDiagram', () => {
  beforeEach(() => {
    Object.defineProperty(SVGElement.prototype, 'getBBox', {
      configurable: true,
      value: () => ({ x: 0, y: 0, width: 120, height: 24 }),
    });
    Object.defineProperty(SVGElement.prototype, 'getComputedTextLength', {
      configurable: true,
      value: () => 80,
    });
  });

  it('draws the saved workflow', async () => {
    render(<WorkflowDiagram chart={payroll} />);
    expect(screen.getByText('How this workflow runs')).toBeInTheDocument();
    await waitFor(
      () => {
        expect(document.querySelector('[data-testid="workflow-diagram"] svg')).not.toBeNull();
      },
      { timeout: 10000 }
    );
    expect(document.body.textContent).toContain('starts here');
    expect(document.body.textContent).toContain('Step 1 · Waits for your OK');
  }, 15000);

  it('draws a run picture that marks the taken choice', async () => {
    const chart = markTakenChoices(
      `${payroll}
%% choice 1 edge-prepare-lodge-0`,
      takenChoiceIds([{ type: 'workflow.edge.taken', from: 'prepare', to: 'lodge', caseIndex: 0 }])
    );
    render(
      <WorkflowDiagram chart={chart} description="The marked arrow is the path this run took." />
    );
    expect(screen.getByText('The marked arrow is the path this run took.')).toBeInTheDocument();
    await waitFor(
      () => {
        expect(document.querySelector('[data-testid="workflow-diagram"] svg')).not.toBeNull();
      },
      { timeout: 10000 }
    );
    expect(screen.queryByText('The picture could not be drawn.')).not.toBeInTheDocument();
  }, 15000);
});
