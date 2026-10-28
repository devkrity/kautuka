/**
 * @vitest-environment jsdom
 */
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import React, { useState } from 'react';
import { describe, expect, it } from 'vitest';
import {
  WorkflowStartSettings,
  WorkflowStepSettings,
} from '@/features/workflows/components/WorkflowCanvas';
import { disconnectPath } from '@/features/workflows/model/workflowCanvas';
import {
  createChoiceDraft,
  createFieldDraft,
  createStepDraft,
  type WorkflowFieldDraft,
  type WorkflowStepDraft,
} from '@/features/workflows/model/workflowDraft';

function ChoiceEditor({
  categoryValue = '',
  duplicateAmount = false,
  blankResult = false,
}: { categoryValue?: string; duplicateAmount?: boolean; blankResult?: boolean } = {}) {
  const [inputFields, setInputFields] = useState<WorkflowFieldDraft[]>(() =>
    duplicateAmount ? [createFieldDraft('amount', 'number')] : []
  );
  const [steps, setSteps] = useState(() => {
    const step = createStepDraft('assistant', 'Read invoice');
    step.key = 'accounts';
    step.writes = [
      createFieldDraft('amount', 'number'),
      createFieldDraft('approved', 'boolean'),
      { ...createFieldDraft('category', 'string'), enumValues: ['Refund', 'Complaint', 'Other'] },
      ...(blankResult ? [createFieldDraft('', 'string')] : []),
    ];
    step.handoffs = step.writes.map((item) => ({
      key: `handoff-${item.key}`,
      from: item.name,
      to: item.name,
    }));
    step.choices = [
      { ...createChoiceDraft('amount', 'manager'), key: 'amount-check', op: 'gt', value: '500.0' },
      { ...createChoiceDraft('approved', 'manager'), key: 'approved-check', op: 'eq', value: '' },
      {
        ...createChoiceDraft('category', 'manager'),
        key: 'category-check',
        op: 'eq',
        value: categoryValue,
      },
    ];
    return [step];
  });
  const step = steps[0]!;
  return (
    <>
      <output data-testid="draft-state">{JSON.stringify(steps)}</output>
      <output data-testid="start-fields">{JSON.stringify(inputFields)}</output>
      <WorkflowStepSettings
        step={step}
        steps={steps}
        inputFields={inputFields}
        agents={[{ id: 'assistant', name: 'Accounts assistant' }]}
        onSelect={(key) => {
          (window as unknown as { selectedStep: string }).selectedStep = key;
        }}
        onChange={(patch) =>
          setSteps((current) =>
            current.map((candidate) =>
              candidate.key === step.key ? { ...candidate, ...patch } : candidate
            )
          )
        }
        onInputFieldsChange={setInputFields}
        onDelete={() => undefined}
        onEditResult={(result) => {
          if (result.ok) setSteps(result.steps);
        }}
      />
    </>
  );
}

function ContinuationEditor() {
  const [steps, setSteps] = useState(() => {
    const check = createStepDraft('assistant', 'Check the request');
    check.key = 'check';
    check.choices = [
      { ...createChoiceDraft('approved', 'roller'), key: 'approved-rule', op: 'eq', value: 'true' },
    ];
    check.otherwiseKey = 'file';
    const file = createStepDraft('assistant', 'File the request');
    file.key = 'file';
    const roller = createStepDraft('assistant', 'Roll the request');
    roller.key = 'roller';
    roller.otherwiseKey = 'file';
    return [check, file, roller];
  });
  const step = steps.find((item) => item.key === 'roller')!;
  return (
    <>
      <output data-testid="draft-state">{JSON.stringify(steps)}</output>
      <WorkflowStepSettings
        step={step}
        steps={steps}
        inputFields={[]}
        agents={[{ id: 'assistant', name: 'Assistant' }]}
        onChange={() => undefined}
        onInputFieldsChange={() => undefined}
        onDelete={() => undefined}
        onEditResult={(result) => {
          if (result.ok) setSteps(result.steps);
        }}
      />
    </>
  );
}

function TestStepEditor({
  onTestStep,
}: {
  onTestStep: (input: Record<string, unknown>) => Promise<{
    results: Record<string, unknown> | null;
    error: string | null;
    droppedKeys: string[];
  }>;
}) {
  const startFields = [
    { ...createFieldDraft('customer_name', 'string', true), label: 'Customer name' },
    createFieldDraft('approved', 'boolean', false),
    createFieldDraft('amount', 'number', false),
    { ...createFieldDraft('category', 'string', false), enumValues: ['Refund', 'Other'] },
  ];
  const earlier = createStepDraft('assistant', 'Read request');
  earlier.key = 'earlier';
  earlier.writes = [{ ...createFieldDraft('priority', 'boolean'), label: 'Priority' }];
  earlier.handoffs = [{ key: 'priority-out', from: 'priority', to: 'priority' }];
  const current = createStepDraft('assistant', 'Review request');
  current.key = 'current';
  current.writes = [{ ...createFieldDraft('decision', 'boolean'), label: 'Decision' }];
  const steps = [earlier, current];
  return (
    <WorkflowStepSettings
      step={current}
      steps={steps}
      inputFields={startFields}
      agents={[{ id: 'assistant', name: 'Assistant' }]}
      onChange={() => undefined}
      onInputFieldsChange={() => undefined}
      onDelete={() => undefined}
      onEditResult={() => undefined}
      onTestStep={async (_stepKey, input) => onTestStep(input)}
    />
  );
}

function MentionEditor() {
  const [inputFields, setInputFields] = useState<WorkflowFieldDraft[]>([
    { ...createFieldDraft('customer_name'), label: 'Customer name', newField: true },
  ]);
  const [steps, setSteps] = useState(() => [createStepDraft('assistant', 'Read request')]);
  const step = steps[0]!;
  return (
    <>
      <output data-testid="draft-state">{JSON.stringify(steps)}</output>
      <output data-testid="start-fields">{JSON.stringify(inputFields)}</output>
      <WorkflowStepSettings
        step={step}
        steps={steps}
        inputFields={inputFields}
        agents={[{ id: 'assistant', name: 'Accounts assistant' }]}
        onChange={(patch) => setSteps((current) => current.map((item) => ({ ...item, ...patch })))}
        onInputFieldsChange={setInputFields}
        onDelete={() => undefined}
        onEditResult={() => undefined}
      />
    </>
  );
}

describe('WorkflowStepSettings result-aware checks', () => {
  it('does not show blank result rows as choices', () => {
    render(<ChoiceEditor blankResult />);

    const options = Array.from(screen.getByLabelText('If 1 result').querySelectorAll('option'));
    expect(options[0]).toHaveTextContent('Choose a result');
    expect(
      options.slice(1).some((option) => option.value === '' || !option.textContent?.trim())
    ).toBe(false);
  });

  it('does not show a blank answer in Pass results on', () => {
    render(<ChoiceEditor blankResult />);

    const selects = screen.getAllByLabelText('Result to pass on');
    for (const select of selects) {
      const options = Array.from(select.querySelectorAll('option'));
      expect(options[0]).toHaveTextContent('Choose a result');
      expect(options.slice(1).some((option) => !option.value || !option.textContent?.trim())).toBe(
        false
      );
    }
  });

  it('links a new check result after its name is finished, keeping the full generated key', async () => {
    const user = userEvent.setup();
    render(<ChoiceEditor />);
    fireEvent.change(screen.getByLabelText('If 1 result'), {
      target: { value: '__add_result__' },
    });
    const names = screen.getAllByLabelText('Results to pass on field name');
    const newName = names[names.length - 1]!;
    await user.type(newName, 'Decision');
    const editingSteps = JSON.parse(
      screen.getByTestId('draft-state').textContent ?? '[]'
    ) as WorkflowStepDraft[];
    expect(editingSteps[0]?.choices[0]?.field).toBe('decision');
    expect(editingSteps[0]?.writes[editingSteps[0].writes.length - 1]?.newField).toBe(true);
    fireEvent.blur(newName);

    const steps = JSON.parse(
      screen.getByTestId('draft-state').textContent ?? '[]'
    ) as WorkflowStepDraft[];
    expect(steps[0]?.writes[steps[0].writes.length - 1]).toMatchObject({
      name: 'decision',
      label: 'Decision',
      newField: false,
    });
    expect(steps[0]?.choices[0]?.field).toBe('decision');
  });

  it('locks a new field key when its mention is inserted into an instruction', () => {
    render(<MentionEditor />);
    fireEvent.click(screen.getByRole('button', { name: 'Customer name' }));

    expect(JSON.parse(screen.getByTestId('start-fields').textContent ?? '[]')[0]).toMatchObject({
      name: 'customer_name',
      newField: false,
    });
    expect(
      JSON.parse(screen.getByTestId('draft-state').textContent ?? '[]')[0].promptTemplate
    ).toContain('{{customer_name}}');
  });

  it('tests only Start and earlier results with typed controls, omits blank values, formats results, and restores focus', async () => {
    let sentInput: Record<string, unknown> | undefined;
    render(
      <TestStepEditor
        onTestStep={async (input) => {
          sentInput = input;
          return { results: { decision: true }, error: null, droppedKeys: [] };
        }}
      />
    );
    const opener = screen.getByRole('button', { name: 'Test this step' });
    fireEvent.click(opener);

    expect(screen.getByLabelText('Customer name (required)')).toBeRequired();
    expect(screen.getByLabelText('Approved')).toHaveTextContent('Yes');
    expect(screen.getByLabelText('Amount')).toHaveAttribute('type', 'number');
    expect(screen.getByLabelText('Category')).toHaveTextContent('Refund');
    expect(screen.getByLabelText('Priority')).toBeInTheDocument();
    expect(screen.queryByLabelText('Decision')).toBeNull();

    fireEvent.change(screen.getByLabelText('Customer name (required)'), {
      target: { value: 'Asha' },
    });
    fireEvent.change(screen.getByLabelText('Approved'), { target: { value: 'false' } });
    fireEvent.change(screen.getByLabelText('Amount'), { target: { value: '' } });
    fireEvent.click(screen.getByRole('button', { name: 'Test step' }));
    await waitFor(() => expect(sentInput).toEqual({ customer_name: 'Asha', approved: 'false' }));
    expect(await screen.findByText('Decision: Yes')).toBeInTheDocument();
    fireEvent.keyDown(document, { key: 'Escape' });
    await waitFor(() => expect(opener).toHaveFocus());
  });

  it('offers the current step results and adds a focused result on that step', async () => {
    render(<ChoiceEditor />);
    const resultPicker = screen.getByLabelText('If 1 result') as HTMLSelectElement;
    expect(Array.from(resultPicker.options).map((option) => option.text)).toContain('Amount');
    expect(Array.from(resultPicker.options).map((option) => option.text)).toContain(
      'Add a result to Step 1 · Accounts assistant'
    );
    fireEvent.change(resultPicker, { target: { value: '__add_result__' } });
    const names = screen.getAllByLabelText('Results to pass on field name');
    await waitFor(() => expect(names).toHaveLength(4));
    await waitFor(() => expect(names[3]).toHaveFocus());
    expect(names[3]).toHaveValue('');
  });

  it('lists a field only once when a step result shares a start field name', () => {
    render(<ChoiceEditor duplicateAmount />);
    const resultPicker = screen.getByLabelText('If 1 result') as HTMLSelectElement;
    expect(
      Array.from(resultPicker.options).filter((option) => option.value === 'amount')
    ).toHaveLength(1);
  });

  it('uses each check result kind, saves yes/no as eq true or false, and uses pick-list answers', () => {
    render(<ChoiceEditor />);
    const numberComparison = screen.getByLabelText('If 1 comparison') as HTMLSelectElement;
    const booleanComparison = screen.getByLabelText('If 2 comparison') as HTMLSelectElement;
    const pickAnswer = screen.getByLabelText('If 3 answer') as HTMLInputElement;
    expect(Array.from(numberComparison.options).map((option) => option.text)).toContain(
      'is more than'
    );
    expect(Array.from(numberComparison.options).map((option) => option.text)).toEqual([
      'is more than',
      'is less than',
      'is',
      'is at least',
      'is at most',
      'has a value',
    ]);
    expect(Array.from(booleanComparison.options).map((option) => option.text)).toEqual([
      'is Yes',
      'is No',
      'has a value',
    ]);
    expect(screen.queryByLabelText('If 2 answer')).toBeNull();
    fireEvent.focus(screen.getByLabelText('If 3 answer'));
    expect(screen.getByRole('listbox', { name: 'If 3 answer options' })).toHaveTextContent(
      'RefundComplaintOther'
    );
    expect(screen.queryByLabelText('If 3 value')).toBeNull();
    fireEvent.change(booleanComparison, { target: { value: 'false' } });
    expect((screen.getByLabelText('If 2 comparison') as HTMLSelectElement).value).toBe('false');
    expect(
      JSON.parse(screen.getByTestId('draft-state').textContent ?? '[]')[0].choices[1]
    ).toMatchObject({ op: 'eq', value: 'false' });
    fireEvent.click(screen.getByRole('option', { name: 'Refund' }));
    expect(screen.getByText('If Category is Refund')).toBeInTheDocument();
  });

  it('adds a typed answer to its producing result only after the Add choice', () => {
    render(<ChoiceEditor categoryValue="Refund" />);
    const answer = screen.getByLabelText('If 3 answer') as HTMLInputElement;
    expect(answer).toHaveValue('Refund');
    fireEvent.focus(answer);
    expect(screen.getByRole('listbox', { name: 'If 3 answer options' })).toBeInTheDocument();
    fireEvent.change(answer, { target: { value: 'Escalate' } });
    let step = JSON.parse(
      screen.getByTestId('draft-state').textContent ?? '[]'
    )[0] as WorkflowStepDraft;
    expect(step.writes.find((field) => field.name === 'category')?.enumValues).toEqual([
      'Refund',
      'Complaint',
      'Other',
    ]);

    fireEvent.click(screen.getByRole('option', { name: 'Add ‘Escalate’ as an answer' }));
    step = JSON.parse(
      screen.getByTestId('draft-state').textContent ?? '[]'
    )[0] as WorkflowStepDraft;
    expect(step.writes.find((field) => field.name === 'category')?.enumValues).toEqual([
      'Refund',
      'Complaint',
      'Other',
      'Escalate',
    ]);
    expect(step.choices[2]).toMatchObject({ op: 'eq', value: 'Escalate' });
  });

  it('restores the saved answer and closes the picker on blur or Escape', () => {
    render(<ChoiceEditor categoryValue="Refund" />);
    const answer = screen.getByLabelText('If 3 answer') as HTMLInputElement;
    fireEvent.focus(answer);
    fireEvent.change(answer, { target: { value: 'Compla' } });
    fireEvent.blur(answer);
    expect(answer).toHaveValue('Refund');
    expect(screen.queryByRole('listbox', { name: 'If 3 answer options' })).not.toBeInTheDocument();

    fireEvent.focus(answer);
    fireEvent.change(answer, { target: { value: 'Compla' } });
    fireEvent.keyDown(answer, { key: 'Escape' });
    expect(answer).toHaveValue('Refund');
    expect(screen.queryByRole('listbox', { name: 'If 3 answer options' })).not.toBeInTheDocument();
  });

  it('typing into a saved answer replaces it instead of appending', async () => {
    const user = userEvent.setup();
    render(<ChoiceEditor categoryValue="Paid" />);
    const answer = screen.getByLabelText('If 3 answer') as HTMLInputElement;
    await user.click(answer);
    await user.type(answer, 'Un');
    expect(answer).toHaveValue('Un');
    expect(answer).not.toHaveValue('PaidUn');
  });

  it('adds a start field from the first step panel and stays on that step', () => {
    render(<ChoiceEditor />);
    fireEvent.click(screen.getByRole('button', { name: 'Add a start field' }));
    fireEvent.change(screen.getByLabelText('New start field name'), {
      target: { value: 'Customer name' },
    });
    fireEvent.change(screen.getByLabelText('New start field type'), {
      target: { value: 'number' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Add input' }));

    expect(JSON.parse(screen.getByTestId('start-fields').textContent ?? '[]')[0]).toMatchObject({
      name: 'customer_name',
      label: 'Customer name',
      type: 'number',
    });
    expect(screen.getByRole('button', { name: 'Customer name' })).toBeInTheDocument();
    expect(screen.getByLabelText('What should they do?')).toBeInTheDocument();
  });

  it('shows one start-field add form and blocks a pick-list without answers', () => {
    const StartEditor = () => {
      const [fields, setFields] = useState<WorkflowFieldDraft[]>([]);
      return (
        <>
          <output data-testid="start-fields">{JSON.stringify(fields)}</output>
          <WorkflowStartSettings
            name="Invoice review"
            inputFields={fields}
            agents={[]}
            canAddFirstStep={false}
            onAddFirstStep={() => undefined}
            onNameChange={() => undefined}
            onInputFieldsChange={setFields}
          />
        </>
      );
    };
    render(<StartEditor />);
    expect(screen.queryByRole('button', { name: 'Add an input' })).toBeNull();
    fireEvent.change(screen.getByLabelText('New start field name'), {
      target: { value: 'Payment status' },
    });
    fireEvent.change(screen.getByLabelText('New start field type'), {
      target: { value: 'pick-list' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Add' }));
    expect(screen.getByRole('alert')).toHaveTextContent('Add at least one answer.');
    expect(JSON.parse(screen.getByTestId('start-fields').textContent ?? '[]')).toEqual([]);

    fireEvent.change(screen.getByLabelText('New start field answers'), {
      target: { value: 'Paid, Unpaid' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Add' }));
    expect(JSON.parse(screen.getByTestId('start-fields').textContent ?? '[]')[0]).toMatchObject({
      name: 'payment_status',
      enumValues: ['Paid', 'Unpaid'],
    });
  });

  it('keeps the raw pick-list answer text while typing a comma and another answer', async () => {
    const user = userEvent.setup();
    const StartEditor = () => {
      const [fields, setFields] = useState<WorkflowFieldDraft[]>([
        { ...createFieldDraft('status'), enumValues: ['Unpaid'] },
      ]);
      return (
        <>
          <output data-testid="start-fields">{JSON.stringify(fields)}</output>
          <WorkflowStartSettings
            name="Invoice review"
            inputFields={fields}
            agents={[]}
            canAddFirstStep={false}
            onAddFirstStep={() => undefined}
            onNameChange={() => undefined}
            onInputFieldsChange={setFields}
          />
        </>
      );
    };
    render(<StartEditor />);

    const answers = screen.getByLabelText('Start fields answers');
    await user.click(answers);
    await user.keyboard('{End}, Overdue');

    expect(answers).toHaveValue('Unpaid, Overdue');
    expect(
      JSON.parse(screen.getByTestId('start-fields').textContent ?? '[]')[0].enumValues
    ).toEqual(['Unpaid', 'Overdue']);
    await user.tab();
    expect(answers).toHaveValue('Unpaid, Overdue');
  });

  it('normalizes numbers and displays a sentence for every check', () => {
    render(<ChoiceEditor />);
    const amount = screen.getByLabelText('If 1 value') as HTMLInputElement;
    fireEvent.blur(amount);
    expect(amount).toHaveValue(500);
    expect(
      JSON.parse(screen.getByTestId('draft-state').textContent ?? '[]')[0].choices[0].value
    ).toBe('500');
    expect(screen.getByText('If Approved is Yes')).toBeInTheDocument();
    expect(screen.getByText('If Category is …')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Stop here instead' })).toBeNull();
  });

  it('lets a continuation stop here while keeping the other path into its destination', () => {
    render(<ContinuationEditor />);
    expect(screen.getByText(/Then/)).toHaveTextContent(
      'Then Step 3 · Assistant (File the request)'
    );
    fireEvent.click(screen.getByRole('button', { name: 'Stop here instead' }));
    const steps = JSON.parse(
      screen.getByTestId('draft-state').textContent ?? '[]'
    ) as WorkflowStepDraft[];
    expect(steps.find((item) => item.key === 'roller')?.otherwiseKey).toBe('');
    expect(disconnectPath(steps, 'roller').ok).toBe(false);
    expect(screen.queryByRole('button', { name: 'Stop here instead' })).toBeNull();
  });
});
