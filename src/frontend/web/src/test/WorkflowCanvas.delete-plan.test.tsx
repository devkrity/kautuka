/**
 * @vitest-environment jsdom
 */
import { fireEvent, render, screen, within } from '@testing-library/react';
import React, { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { WorkflowStepSettings } from '@/features/workflows/components/WorkflowCanvas';
import {
  createChoiceDraft,
  createStepDraft,
  type WorkflowStepDraft,
} from '@/features/workflows/model/workflowDraft';

function makeStep(key: string, promptTemplate: string): WorkflowStepDraft {
  return { ...createStepDraft('agent-a', promptTemplate), key };
}

function Harness({ onDelete }: { onDelete: (steps: WorkflowStepDraft[]) => void }) {
  const [first, second] = [makeStep('first', 'Original'), makeStep('second', 'Next')];
  const choice = {
    ...createChoiceDraft('agent-a'),
    key: 'rule',
    field: 'amount',
    op: 'exists',
    value: '',
    goToKey: 'second',
  };
  const [steps, setSteps] = useState([
    { ...first, choices: [choice], otherwiseKey: 'second' },
    second,
  ]);
  const step = steps[0]!;
  return (
    <WorkflowStepSettings
      step={step}
      steps={steps}
      inputFields={[]}
      agents={[{ id: 'agent-a', name: 'Writer' }]}
      onInputFieldsChange={() => undefined}
      onChange={(patch) =>
        setSteps((current) =>
          current.map((item) => (item.key === step.key ? { ...item, ...patch } : item))
        )
      }
      onDelete={onDelete}
      onEditResult={(result) => {
        if (result.ok) setSteps(result.steps);
      }}
    />
  );
}

describe('WorkflowStepSettings delete plan', () => {
  it('keeps edits made after opening the delete plan when confirmed', () => {
    const onDelete = vi.fn();
    render(<Harness onDelete={onDelete} />);

    fireEvent.click(screen.getByRole('button', { name: 'Remove this check' }));
    fireEvent.change(screen.getByLabelText('What should they do?'), {
      target: { value: 'Updated instructions' },
    });
    fireEvent.click(
      within(screen.getByRole('alertdialog')).getByRole('button', { name: 'Remove this check' })
    );

    expect(screen.getByLabelText('What should they do?')).toHaveValue('Updated instructions');
    expect(screen.queryByRole('alertdialog')).toBeNull();
    expect(screen.queryByLabelText('If 1 field')).toBeNull();
    expect(onDelete).not.toHaveBeenCalled();
  });
});
