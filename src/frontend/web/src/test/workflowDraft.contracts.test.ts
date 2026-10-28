import { describe, expect, it } from 'vitest';
import type {
  SaveWorkflowRequest,
  WorkflowDefinition,
} from '@/features/workflows/schemas/workflowSchema';
import {
  addStartField,
  alignHandoffs,
  choiceFieldNames,
  createFieldDraft,
  createHandoffDraft,
  createStepDraft,
  describeDraftProblems,
  fieldsFromSchema,
  fieldDisplayName,
  fieldKeyFromLabel,
  inputFieldsFromDefinition,
  pointerToField,
  sequentialContinuationLabel,
  stepsFromDefinition,
  schemaFromFields,
  toSaveWorkflowRequest,
  updateStepResults,
  workflowTestInput,
} from '@/features/workflows/model/workflowDraft';
import { effectiveTransitions } from '@/features/workflows/model/workflowCanvas';
import { buildWorkflowTemplate } from '@/features/workflows/model/workflowTemplates';

const payroll: WorkflowDefinition = {
  id: 'wf-pay',
  ownerUserId: 'user-1',
  name: 'Payroll lodge',
  governed: true,
  revision: 1,
  inputSchema: {
    type: 'object',
    required: ['title', 'employeeId', 'period', 'amount'],
    properties: {
      title: { type: 'string' },
      employeeId: { type: 'string' },
      period: { type: 'string' },
      amount: { type: 'number' },
      recommendation: { type: 'string' },
      lodgeRef: { type: 'string' },
    },
  },
  workingSchema: { type: 'object' },
  outputSchema: { type: 'object' },
  nodes: [
    {
      nodeId: 'prepare',
      agentId: 'agent-a',
      promptTemplate: 'Check {{employeeId}}',
      outputKind: 'schema',
      outputSchema: {
        type: 'object',
        required: ['title', 'recommendation'],
        properties: { title: { type: 'string' }, recommendation: { type: 'string' } },
      },
      maps: [
        { from: '/title', to: '/title' },
        { from: '/recommendation', to: '/recommendation' },
      ],
      sideEffects: 'none',
      memoryWrites: false,
      requiresReview: true,
      maxRetries: 1,
    },
    {
      nodeId: 'lodge',
      agentId: 'agent-b',
      promptTemplate: 'Set lodgeRef',
      outputKind: 'schema',
      outputSchema: {
        type: 'object',
        required: ['lodgeRef'],
        properties: { lodgeRef: { type: 'string' } },
      },
      maps: [{ from: '/lodgeRef', to: '/lodgeRef' }],
      sideEffects: 'external',
      memoryWrites: false,
      requiresReview: false,
      maxRetries: 0,
    },
  ],
  createdAt: '2026-09-19T00:00:00Z',
  updatedAt: '2026-09-19T00:00:00Z',
};

describe('workflow draft contracts', () => {
  it('preserves schema details the editor does not change, including arrays and non-string enums', () => {
    const source = {
      type: 'object',
      required: ['message', 'tags'],
      properties: {
        message: {
          type: 'string',
          title: 'Message',
          description: 'The message',
          format: 'email',
          minLength: 2,
          maxLength: 80,
        },
        tags: { type: 'array', title: 'Tags', description: 'Labels', items: { type: 'string' } },
        score: { type: 'number', title: 'Score', minimum: 0, maximum: 5, enum: [1, 2, 3] },
        detail: { type: 'object', title: 'Detail', properties: { code: { type: 'string' } } },
      },
    };
    const fields = fieldsFromSchema(source);
    expect(fields.find((field) => field.name === 'tags')?.isArray).toBe(true);
    expect(schemaFromFields(fields)).toEqual(source);
  });

  it('drops pick-list and type-specific schema details when changed to text', () => {
    const fields = fieldsFromSchema({
      type: 'object',
      properties: {
        status: {
          type: 'string',
          title: 'Status',
          description: 'Approval status',
          enum: ['New', 'Approved'],
          format: 'email',
          minimum: 1,
          maximum: 5,
          items: { type: 'number' },
        },
      },
    });
    const status = fields[0]!;
    const saved = schemaFromFields([{ ...status, enumValues: undefined }]);

    expect((saved.properties as Record<string, unknown>).status).toEqual({
      type: 'string',
      title: 'Status',
      description: 'Approval status',
    });
  });

  it('round trips the email template schema through a saved workflow request', () => {
    const template = buildWorkflowTemplate('email-attachments', ['agent-a']);
    const definition: WorkflowDefinition = {
      ...template,
      id: 'email-template',
      ownerUserId: 'user-1',
      revision: 1,
      createdAt: '2026-01-01T00:00:00Z',
      updatedAt: '2026-01-01T00:00:00Z',
    };
    const fields = fieldsFromSchema(definition.inputSchema);
    const request = toSaveWorkflowRequest(
      definition.name,
      stepsFromDefinition(definition),
      definition,
      fields
    );
    expect(request.inputSchema).toEqual(template.inputSchema);
    expect(request.workingSchema).toEqual(template.workingSchema);
    expect(request.outputSchema).toEqual(template.outputSchema);
    expect(request.nodes.map((node) => node.outputSchema)).toEqual(
      template.nodes.map((node) => node.outputSchema)
    );
  });

  it('uses edited editor fields in the working schema and removes deleted editor fields', () => {
    const existing: WorkflowDefinition = {
      ...payroll,
      inputSchema: {
        type: 'object',
        required: ['customer_name'],
        properties: {
          customer_name: { type: 'string', title: 'Customer name' },
          retired_input: { type: 'string' },
        },
      },
      workingSchema: {
        type: 'object',
        properties: {
          customer_name: { type: 'string', title: 'Customer name' },
          decision: { type: 'string', enum: ['old'] },
          retired_input: { type: 'string' },
          retired_output: { type: 'string' },
          extension_value: { type: 'boolean', description: 'Gateway-owned value' },
        },
        required: ['customer_name', 'retired_output'],
      },
      nodes: payroll.nodes.map((node, index) =>
        index === 0
          ? {
              ...node,
              outputSchema: {
                type: 'object',
                properties: {
                  decision: { type: 'string', enum: ['old'] },
                  retired_output: { type: 'string' },
                },
              },
              maps: [
                { from: '/decision', to: '/decision' },
                { from: '/retired_output', to: '/retired_output' },
              ],
            }
          : node
      ),
    };
    const inputs = fieldsFromSchema(existing.inputSchema).filter(
      (field) => field.name === 'customer_name'
    );
    inputs[0] = { ...inputs[0]!, type: 'number', label: 'Invoice total', required: false };
    inputs.push(addStartField([], 'Review status')[0]!);
    const steps = stepsFromDefinition(existing);
    steps[0] = {
      ...steps[0]!,
      writes: [
        {
          ...steps[0]!.writes.find((field) => field.name === 'decision')!,
          type: 'string',
          label: 'Decision',
          hasTitle: true,
          enumValues: ['proceed', 'hold'],
        },
      ],
      handoffs: [{ key: 'decision-map', from: 'decision', to: 'decision' }],
      choices: [
        {
          key: 'review-status-check',
          field: 'review_status',
          op: 'eq',
          value: 'approved',
          goToKey: 'lodge',
        },
      ],
      otherwiseKey: 'lodge',
    };

    const saved = toSaveWorkflowRequest(existing.name, steps, existing, inputs);
    const working = saved.workingSchema.properties as Record<string, Record<string, unknown>>;

    expect(working.customer_name).toMatchObject({ type: 'number', title: 'Invoice total' });
    expect(working.review_status).toMatchObject({ type: 'string', title: 'Review status' });
    expect(working.decision).toMatchObject({
      type: 'string',
      title: 'Decision',
      enum: ['proceed', 'hold'],
    });
    expect(working.extension_value).toMatchObject({
      type: 'boolean',
      description: 'Gateway-owned value',
    });
    expect(working).not.toHaveProperty('retired_input');
    expect(working).not.toHaveProperty('retired_output');
    expect(saved.nodes[0]?.switchCases).toEqual([
      { pointer: '/review_status', op: 'eq', value: 'approved', goTo: 'lodge' },
    ]);
    expect(saved.outputSchema).toEqual(saved.workingSchema);
  });

  it('saves a newly named field with a stable generated key before blur or submit', () => {
    let field = createFieldDraft('', 'string', false);
    field = { ...field, label: 'S', name: fieldKeyFromLabel('S', [field], field.key) };
    for (const letter of 'ummary') {
      const label = `${field.label}${letter}`;
      field = { ...field, label, name: fieldKeyFromLabel(label, [field], field.key) };
    }
    expect(field.name).toBe('summary');
    expect(field.label).toBe('Summary');
    expect(schemaFromFields([field]).properties).toMatchObject({ summary: { title: 'Summary' } });
    const pendingKey = { ...createFieldDraft('', 'string', false), label: 'Invoice number' };
    expect(
      describeDraftProblems('Review', [createStepDraft('agent-a', 'Review')], [pendingKey])
    ).toBeNull();
  });

  it('blocks unnamed fields and empty pick-lists with plain messages', () => {
    const emptyStart = addStartField([], 'Decision', 'pick-list');
    expect(
      describeDraftProblems('Review', [createStepDraft('agent-a', 'Review')], emptyStart)
    ).toBe('Add at least one answer.');
    const unnamed = [{ ...createFieldDraft('status'), label: '' }];
    expect(describeDraftProblems('Review', [createStepDraft('agent-a', 'Review')], unnamed)).toBe(
      'Add a name for this field.'
    );
  });

  it('omits blank test inputs and converts only provided typed values', () => {
    const fields = [createFieldDraft('amount', 'number'), createFieldDraft('approved', 'boolean')];
    expect(workflowTestInput({ title: ' ', amount: '', approved: 'false' }, fields)).toEqual({
      approved: false,
    });
    expect(workflowTestInput({ amount: '500.0', approved: 'true' }, fields)).toEqual({
      amount: 500,
      approved: true,
    });
  });

  it('adds labeled start fields with safe unique keys and the chosen type', () => {
    const existing = [createFieldDraft('customer_name')];
    const added = addStartField(existing, 'Customer name', 'number');
    const pickListFields = addStartField(existing, 'Decision', 'pick-list');

    expect(added[added.length - 1]).toMatchObject({
      label: 'Customer name',
      name: 'customer_name_2',
      type: 'number',
    });
    expect(pickListFields[pickListFields.length - 1]).toMatchObject({
      name: 'decision',
      label: 'Decision',
      enumValues: [],
    });
    expect(existing).toHaveLength(1);
  });

  it('starts a new workflow without prefilled fields and with one empty result row', () => {
    const step = createStepDraft('agent-a', 'Read the request');
    expect(inputFieldsFromDefinition(undefined)).toEqual([]);
    expect(step.writes).toHaveLength(1);
    expect(step.writes[0]).toMatchObject({ name: '' });
    expect(step.handoffs).toEqual([]);
  });

  it('hydrates start fields and step handoffs from the saved schema', () => {
    const fields = inputFieldsFromDefinition(payroll);
    expect(fields.map((field) => field.name)).toEqual([
      'title',
      'employeeId',
      'period',
      'amount',
      'recommendation',
      'lodgeRef',
    ]);
    const steps = stepsFromDefinition(payroll);
    expect(steps[0]?.handoffs.map((item) => `${item.from}->${item.to}`)).toEqual([
      'title->title',
      'recommendation->recommendation',
    ]);
    expect(pointerToField('/lodgeRef')).toBe('lodgeRef');
  });

  it('round trips pick-list answers as a JSON Schema enum', () => {
    const fields = fieldsFromSchema({
      type: 'object',
      properties: {
        type: { type: 'string', enum: ['Refund', 'Complaint', 'Other'], title: 'type' },
      },
    });
    expect(fields[0]).toMatchObject({
      name: 'type',
      type: 'string',
      enumValues: ['Refund', 'Complaint', 'Other'],
    });
    expect(schemaFromFields(fields)).toMatchObject({
      properties: { type: { type: 'string', enum: ['Refund', 'Complaint', 'Other'] } },
    });
  });

  it('keeps a friendly field title while generating a unique safe key', () => {
    const fields = [createFieldDraft('customer_name')];
    const nextKey = fieldKeyFromLabel('Customer name', fields, 'new-field');
    expect(nextKey).toBe('customer_name_2');
    const friendly = { ...createFieldDraft(nextKey), label: 'Customer name' };
    expect(fieldDisplayName(friendly)).toBe('Customer name');
    expect(schemaFromFields([friendly])).toMatchObject({
      properties: { customer_name_2: { title: 'Customer name' } },
    });
  });

  it('shows key-only field names as readable labels', () => {
    expect(fieldDisplayName({ name: 'dueDate' })).toBe('Due date');
    expect(fieldDisplayName({ name: 'priority_level' })).toBe('Priority level');
    expect(fieldDisplayName({ name: 'title' })).toBe('Title');
  });

  it('drops an empty result row when saving and blocks governed steps without a result', () => {
    const step = createStepDraft('agent-a', 'Read the request');
    const saved = toSaveWorkflowRequest(
      'Simple workflow',
      [step],
      {
        ...payroll,
        governed: false,
        nodes: [],
      },
      []
    );
    expect(saved.nodes[0]?.outputSchema).toMatchObject({ properties: {} });
    expect(
      describeDraftProblems('Payroll', [step], [], [{ id: 'agent-a', name: 'Assistant' }], true)
    ).toContain('Step 1 (Assistant) needs at least one result to pass on.');
  });

  it('combines a missing agent and instruction into one short save message', () => {
    const steps = [createStepDraft('agent-a', 'Read the request'), createStepDraft('', '')];

    expect(describeDraftProblems('Payroll', steps, [])).toBe(
      'Step 2: choose who does this and add an instruction.'
    );
  });

  it('keeps automatic handoffs equal to results when results change', () => {
    const step = createStepDraft('agent-a', 'Extract data');
    const writes = [createFieldDraft('amount', 'number'), createFieldDraft('approved', 'boolean')];
    const updated = updateStepResults(step, writes);
    expect(updated.writes).toEqual(writes);
    expect(updated.handoffs.map(({ from, to }) => [from, to])).toEqual([
      ['amount', 'amount'],
      ['approved', 'approved'],
    ]);
  });

  it('preserves deliberate mappings while syncing renamed and added results', () => {
    const step = createStepDraft('agent-a', 'Extract data');
    const first = step.writes[0]!;
    const customized = {
      ...step,
      writes: [{ ...first, name: 'amount' }],
      handoffs: [createHandoffDraft('amount', 'invoiceTotal')],
    };
    const updated = updateStepResults(customized, [
      { ...first, name: 'total' },
      createFieldDraft('approved', 'boolean'),
    ]);
    expect(updated.handoffs.map(({ from, to }) => [from, to])).toEqual([
      ['total', 'invoiceTotal'],
      ['approved', 'approved'],
    ]);
  });

  it('saves designer-defined fields instead of a hardcoded title schema', () => {
    const inputFields = fieldsFromSchema(payroll.inputSchema).filter((field) =>
      ['title', 'employeeId', 'period', 'amount'].includes(field.name)
    );
    const saved = toSaveWorkflowRequest(
      payroll.name,
      stepsFromDefinition(payroll),
      payroll,
      inputFields
    );
    expect(saved.governed).toBe(true);
    expect(saved.inputSchema).toMatchObject({
      required: ['title', 'employeeId', 'period', 'amount'],
      properties: {
        employeeId: { type: 'string' },
        amount: { type: 'number' },
      },
    });
    expect(saved.nodes[0]).toMatchObject({
      nodeId: 'prepare',
      maps: [
        { from: '/title', to: '/title' },
        { from: '/recommendation', to: '/recommendation' },
      ],
    });
    expect(saved.nodes[1]).toMatchObject({
      nodeId: 'lodge',
      sideEffects: 'external',
      maps: [{ from: '/lodgeRef', to: '/lodgeRef' }],
    });
    expect((saved.workingSchema.properties as Record<string, unknown>).lodgeRef).toMatchObject({
      type: 'string',
    });
    expect(saved.workingSchema.required).toEqual(['title', 'employeeId', 'period', 'amount']);
  });

  it('saves a choice as the first matching path plus otherwise', () => {
    const steps = stepsFromDefinition(payroll);
    steps[0] = {
      ...steps[0]!,
      choices: [
        {
          key: 'choice-1',
          field: 'recommendation',
          op: 'eq',
          value: 'lodge',
          goToKey: 'lodge',
        },
      ],
      otherwiseKey: 'lodge',
    };
    const saved = toSaveWorkflowRequest(
      payroll.name,
      steps,
      payroll,
      inputFieldsFromDefinition(payroll)
    );
    expect(saved.nodes[0]?.switchCases).toEqual([
      { pointer: '/recommendation', op: 'eq', value: 'lodge', goTo: 'lodge' },
    ]);
    expect(saved.nodes[0]?.defaultGoTo).toBe('lodge');
    expect(saved.nodes[1]?.switchCases).toBeUndefined();
  });

  it('keeps a saved choice when the definition is opened and saved again', () => {
    const withChoice: WorkflowDefinition = {
      ...payroll,
      nodes: payroll.nodes.map((node, index) =>
        index === 0
          ? {
              ...node,
              switchCases: [
                { pointer: '/recommendation', op: 'eq', value: 'lodge', goTo: 'lodge' },
              ],
              defaultGoTo: 'lodge',
            }
          : node
      ),
    };
    const saved = toSaveWorkflowRequest(
      withChoice.name,
      stepsFromDefinition(withChoice),
      withChoice,
      inputFieldsFromDefinition(withChoice)
    );
    expect(saved.nodes[0]?.switchCases).toEqual([
      { pointer: '/recommendation', op: 'eq', value: 'lodge', goTo: 'lodge' },
    ]);
    expect(saved.nodes[0]?.defaultGoTo).toBe('lodge');
  });

  it('writes a new step id onto the choice path', () => {
    const steps = stepsFromDefinition(payroll);
    const added = createStepDraft('agent-b', 'Handle the other outcome');
    steps.push(added);
    steps[0] = {
      ...steps[0]!,
      choices: [
        {
          key: 'choice-1',
          field: 'recommendation',
          op: 'eq',
          value: 'hold',
          goToKey: added.key,
        },
      ],
      otherwiseKey: 'lodge',
    };
    const saved = toSaveWorkflowRequest(
      payroll.name,
      steps,
      payroll,
      inputFieldsFromDefinition(payroll)
    );
    expect(saved.nodes[2]?.nodeId).toBe('step-3');
    expect(saved.nodes[0]?.switchCases?.[0]?.goTo).toBe('step-3');
    expect(saved.nodes[0]?.defaultGoTo).toBe('lodge');
  });

  it('keeps the sequential label when this step is the one choosing the next step', () => {
    const steps = stepsFromDefinition(payroll);
    const added = createStepDraft('agent-b', 'Decline');
    steps.push(added);
    steps[0] = {
      ...steps[0]!,
      choices: [
        {
          key: 'choice-1',
          field: 'recommendation',
          op: 'eq',
          value: 'lodge',
          goToKey: 'lodge',
        },
      ],
      otherwiseKey: added.key,
    };
    expect(sequentialContinuationLabel(steps, 0)).toBe('The next step');
    expect(sequentialContinuationLabel(steps, 1)).toBe('The workflow finishes');
  });

  it('rejects a choice on a field a later step has not handed off yet', () => {
    const steps = stepsFromDefinition(payroll);
    steps[1] = {
      ...steps[1]!,
      handoffs: [...steps[1]!.handoffs, createHandoffDraft('lodgeRef', 'laterNote')],
    };
    steps[0] = {
      ...steps[0]!,
      choices: [
        {
          key: 'choice-1',
          field: 'laterNote',
          op: 'eq',
          value: 'yes',
          goToKey: 'lodge',
        },
      ],
      otherwiseKey: 'lodge',
    };
    expect(choiceFieldNames(inputFieldsFromDefinition(payroll), steps, 0)).not.toContain(
      'laterNote'
    );
    expect(
      describeDraftProblems('Payroll lodge', steps, inputFieldsFromDefinition(payroll))
    ).toMatch(/not every path into it provides it/i);
  });

  it('saves an exists path without a value and can clear the choice', () => {
    const steps = stepsFromDefinition(payroll);
    steps[0] = {
      ...steps[0]!,
      choices: [
        {
          key: 'choice-1',
          field: 'recommendation',
          op: 'exists',
          value: 'ignored',
          goToKey: 'lodge',
        },
      ],
      otherwiseKey: 'lodge',
    };
    const saved = toSaveWorkflowRequest(
      payroll.name,
      steps,
      payroll,
      inputFieldsFromDefinition(payroll)
    );
    expect(saved.nodes[0]?.switchCases).toEqual([
      { pointer: '/recommendation', op: 'exists', value: null, goTo: 'lodge' },
    ]);
    steps[0] = { ...steps[0]!, choices: [], otherwiseKey: '' };
    const cleared = toSaveWorkflowRequest(
      payroll.name,
      steps,
      payroll,
      inputFieldsFromDefinition(payroll)
    );
    expect(cleared.nodes[0]?.switchCases).toBeUndefined();
    expect(cleared.nodes[0]?.defaultGoTo).toBeUndefined();
  });

  it('rejects a choice that has no otherwise step', () => {
    const steps = stepsFromDefinition(payroll);
    steps[0] = {
      ...steps[0]!,
      choices: [
        {
          key: 'choice-1',
          field: 'recommendation',
          op: 'eq',
          value: 'lodge',
          goToKey: 'lodge',
        },
      ],
      otherwiseKey: '',
    };
    expect(
      describeDraftProblems('Payroll lodge', steps, inputFieldsFromDefinition(payroll))
    ).toMatch(/otherwise/i);
  });

  it('rejects an empty handoff contract', () => {
    const steps = stepsFromDefinition(payroll);
    steps[0] = { ...steps[0]!, handoffs: [createHandoffDraft('', '')] };
    expect(
      describeDraftProblems('Payroll lodge', steps, inputFieldsFromDefinition(payroll))
    ).toMatch(/needs a complete result mapping/i);
  });

  it('keeps a 1:1 handoff when a return field is renamed', () => {
    const previous = [createFieldDraft('recommendation', 'string', true)];
    const writes = [{ ...previous[0]!, name: 'decision' }];
    const aligned = alignHandoffs(writes, previous, [
      createHandoffDraft('recommendation', 'recommendation'),
    ]);
    expect(aligned).toEqual([expect.objectContaining({ from: 'decision', to: 'decision' })]);
  });

  it('matches existing nodes by identity when a new step is inserted in the middle', () => {
    const steps = stepsFromDefinition(payroll);
    steps.splice(1, 0, createStepDraft('agent-b', 'New middle step'));
    const saved = toSaveWorkflowRequest(
      payroll.name,
      steps,
      payroll,
      inputFieldsFromDefinition(payroll)
    );
    expect(saved.nodes.map((node) => node.nodeId)).toEqual(['prepare', 'step-2', 'lodge']);
    expect(saved.nodes[2]).toMatchObject({ nodeId: 'lodge', sideEffects: 'external' });
  });

  it('round trips the supported graph shapes through SaveWorkflowRequest', () => {
    const base = (key: string) => ({ ...createStepDraft('agent-a', `Do ${key}`), key });
    const choice = (key: string, goToKey: string, field = 'amount', op = 'gt', value = '5') => ({
      key,
      field,
      op,
      value,
      goToKey,
    });
    const linear = (source: string, target: string) => ({ source, target, kind: 'linear' });
    const canonical = (steps: ReturnType<typeof base>[]) =>
      effectiveTransitions(steps).map((edge) =>
        edge.kind === 'rule'
          ? {
              source: edge.source,
              target: edge.target,
              kind: 'rule',
              rule: (() => {
                const item = steps
                  .find((step) => step.key === edge.source)!
                  .choices.find((choiceItem) => choiceItem.key === edge.ruleKey)!;
                return {
                  field: item.field,
                  op: item.op,
                  value: item.op === 'exists' ? '' : item.value,
                  goToKey: item.goToKey,
                };
              })(),
            }
          : linear(edge.source, edge.target)
      );
    const one = [base('one')];
    const chain = [base('a'), base('b'), base('c')];
    const review = [base('a'), { ...base('b'), requiresReview: true }];
    const singleChoice = [base('check'), base('yes'), base('no')];
    singleChoice[0] = { ...singleChoice[0]!, choices: [choice('r1', 'yes')], otherwiseKey: 'no' };
    const manyChoice = [base('check'), base('yes'), base('middle'), base('no')];
    manyChoice[0] = {
      ...manyChoice[0]!,
      choices: [choice('r1', 'yes'), choice('r2', 'middle', 'amount', 'lt', '1')],
      otherwiseKey: 'no',
    };
    const paths = [base('check'), base('yes'), base('yes2'), base('no'), base('join')];
    paths[0] = { ...paths[0]!, choices: [choice('r1', 'yes')], otherwiseKey: 'no' };
    paths[1] = { ...paths[1]!, otherwiseKey: 'yes2' };
    paths[2] = { ...paths[2]!, otherwiseKey: 'join' };
    paths[3] = { ...paths[3]!, otherwiseKey: 'join' };
    const insidePath = [base('start'), base('check'), base('yes'), base('no')];
    insidePath[0] = { ...insidePath[0]!, otherwiseKey: 'check' };
    insidePath[1] = { ...insidePath[1]!, choices: [choice('r1', 'yes')], otherwiseKey: 'no' };
    const diamond = [base('check'), base('ask'), base('auto'), base('file')];
    diamond[0] = { ...diamond[0]!, choices: [choice('r1', 'ask')], otherwiseKey: 'auto' };
    diamond[1] = { ...diamond[1]!, otherwiseKey: 'file' };
    diamond[2] = { ...diamond[2]!, otherwiseKey: 'file' };
    const reverseDiamond = [base('check'), base('auto'), base('ask'), base('file')];
    reverseDiamond[0] = {
      ...reverseDiamond[0]!,
      choices: [choice('r1', 'ask')],
      otherwiseKey: 'auto',
    };
    reverseDiamond[1] = { ...reverseDiamond[1]!, otherwiseKey: 'file' };
    reverseDiamond[2] = { ...reverseDiamond[2]!, otherwiseKey: 'file' };
    const emptyPath = [base('check'), base('yes'), base('no')];
    emptyPath[0] = { ...emptyPath[0]!, choices: [choice('r1', 'yes')], otherwiseKey: 'no' };
    const joinChoice = [base('check'), base('yes'), base('no'), base('join'), base('last')];
    joinChoice[0] = { ...joinChoice[0]!, choices: [choice('r1', 'yes')], otherwiseKey: 'no' };
    joinChoice[1] = { ...joinChoice[1]!, otherwiseKey: 'join' };
    joinChoice[2] = { ...joinChoice[2]!, otherwiseKey: 'join' };
    joinChoice[3] = { ...joinChoice[3]!, choices: [choice('r2', 'last')], otherwiseKey: 'last' };
    const threeWay = [base('check'), base('one'), base('two'), base('three'), base('join')];
    threeWay[0] = {
      ...threeWay[0]!,
      choices: [choice('r1', 'one'), choice('r2', 'two')],
      otherwiseKey: 'three',
    };
    threeWay[1] = { ...threeWay[1]!, otherwiseKey: 'join' };
    threeWay[2] = { ...threeWay[2]!, otherwiseKey: 'join' };
    threeWay[3] = { ...threeWay[3]!, otherwiseKey: 'join' };
    const duplicateTargets = [base('check'), base('shared'), base('default')];
    duplicateTargets[0] = {
      ...duplicateTargets[0]!,
      choices: [choice('r1', 'shared'), choice('r2', 'shared', 'amount', 'lt', '1')],
      otherwiseKey: 'default',
    };
    const backJump = [base('a'), base('b')];
    backJump[1] = { ...backJump[1]!, otherwiseKey: 'a' };
    const shapes = [
      one,
      chain,
      review,
      singleChoice,
      manyChoice,
      paths,
      insidePath,
      diamond,
      reverseDiamond,
      emptyPath,
      joinChoice,
      threeWay,
      duplicateTargets,
      backJump,
    ];

    for (const original of shapes) {
      const saved = toSaveWorkflowRequest('Round trip', original, undefined, []);
      const definition = definitionFromSave(payroll, saved);
      const loaded = stepsFromDefinition(definition);
      expect(canonical(loaded), original.map((item) => item.key).join(',')).toEqual(
        canonical(original)
      );
      if (original === backJump) {
        expect(describeDraftProblems('Legacy back-jump', loaded, [])).toMatch(/creating a loop/);
      }
    }
  });
});

function definitionFromSave(
  base: WorkflowDefinition,
  saved: SaveWorkflowRequest
): WorkflowDefinition {
  return {
    ...base,
    name: saved.name,
    nodes: saved.nodes.map((node) => ({
      ...node,
      outputSchema: node.outputSchema ?? null,
      switchCases: node.switchCases ?? null,
      defaultGoTo: node.defaultGoTo ?? null,
    })),
  };
}
