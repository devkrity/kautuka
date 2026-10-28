import { describe, expect, it } from 'vitest';
import { markTakenChoices, takenChoiceIds } from '@/features/workflows/model/workflowDiagramMark';

const chart = [
  'flowchart TD',
  '  n0["Step 1"]',
  '  n1["Step 3"]',
  '  n0 -->|Otherwise| n1',
  '%% choice 0 edge-step-1-step-3-1',
  '  n0 --> n2',
].join('\n');

describe('workflow diagram marks', () => {
  it('marks the arrow whose choice id the journal recorded', () => {
    const taken = takenChoiceIds([
      { type: 'workflow.started' },
      { type: 'workflow.edge.taken', from: 'step-1', to: 'step-3', caseIndex: 1, op: 'default' },
    ]);
    expect(markTakenChoices(chart, taken)).toContain(
      'linkStyle 0 stroke:#1b7f4e,stroke-width:2.5px'
    );
  });

  it('leaves the definition picture unchanged when this run has taken no choice', () => {
    expect(markTakenChoices(chart, takenChoiceIds([]))).toBe(chart);
  });
});
