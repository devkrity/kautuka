import {
  type SaveWorkflowRequest,
  type WorkflowDefinition,
  type WorkflowNode,
} from '../schemas/workflowSchema';
import {
  describeStepProblems,
  effectiveTransitions,
  guaranteedFields,
  workflowDisplayNumbers,
} from './workflowCanvas';

export { workflowDisplayNumbers };

export const workflowFieldTypes = ['string', 'number', 'integer', 'boolean'] as const;
export type WorkflowFieldType = (typeof workflowFieldTypes)[number];

export interface WorkflowFieldDraft {
  key: string;
  label?: string;
  name: string;
  type: WorkflowFieldType;
  required: boolean;
  enumValues?: string[];
  schema?: Record<string, unknown>;
  hasTitle?: boolean;
  isArray?: boolean;
  isReadOnlyType?: boolean;
  newField?: boolean;
}

export interface WorkflowHandoffDraft {
  key: string;
  from: string;
  to: string;
}

export const workflowChoiceOps = [
  { id: 'eq', label: 'is' },
  { id: 'ne', label: 'is not' },
  { id: 'gt', label: 'is more than' },
  { id: 'gte', label: 'is at least' },
  { id: 'lt', label: 'is less than' },
  { id: 'lte', label: 'is at most' },
  { id: 'in', label: 'is one of' },
  { id: 'exists', label: 'has a value' },
] as const;

export interface WorkflowChoiceDraft {
  key: string;
  field: string;
  op: string;
  value: string;
  goToKey: string;
}

export interface WorkflowStepDraft {
  key: string;
  agentId: string;
  promptTemplate: string;
  requiresReview: boolean;
  writes: WorkflowFieldDraft[];
  handoffs: WorkflowHandoffDraft[];
  choices: WorkflowChoiceDraft[];
  otherwiseKey: string;
}

const fieldNamePattern = /^[A-Za-z_][A-Za-z0-9_]*$/;
const reservedFieldNames = new Set(['__proto__', 'prototype', 'constructor']);

export function isSafeFieldName(name: string): boolean {
  return fieldNamePattern.test(name) && !reservedFieldNames.has(name);
}

export function fieldDisplayName(field: Pick<WorkflowFieldDraft, 'name' | 'label'>): string {
  const source = field.label === undefined ? field.name : field.label.trim();
  const shouldHumanize = field.label === undefined || source === field.name;
  const readable = shouldHumanize
    ? source
        .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
        .replace(/_/g, ' ')
        .toLowerCase()
    : source;
  return readable ? readable[0]!.toUpperCase() + readable.slice(1) : '';
}

export function workflowTestInput(
  input: Record<string, unknown>,
  fields: WorkflowFieldDraft[]
): Record<string, unknown> {
  return Object.fromEntries(
    Object.entries(input)
      .filter(([, value]) => typeof value !== 'string' || value.trim() !== '')
      .map(([key, value]) => {
        const field = fields.find((candidate) => candidate.name === key);
        if (typeof value === 'string' && (field?.type === 'number' || field?.type === 'integer'))
          return [key, Number(value)];
        if (typeof value === 'string' && field?.type === 'boolean') return [key, value === 'true'];
        return [key, value];
      })
  );
}

export function fieldKeyFromLabel(
  label: string,
  fields: WorkflowFieldDraft[],
  key: string
): string {
  let base =
    label
      .trim()
      .toLowerCase()
      .replace(/[^a-z0-9]+/g, '_')
      .replace(/^_+|_+$/g, '') || 'field';
  if (/^\d/.test(base)) base = `field_${base}`;
  if (reservedFieldNames.has(base)) base = `${base}_field`;
  let candidate = base;
  let suffix = 2;
  while (fields.some((field) => field.key !== key && field.name === candidate))
    candidate = `${base}_${suffix++}`;
  return candidate;
}

function normalizeUnkeyedField(
  field: WorkflowFieldDraft,
  fields: WorkflowFieldDraft[]
): WorkflowFieldDraft {
  if (field.newField && !field.name.trim() && field.label?.trim()) {
    return { ...field, name: fieldKeyFromLabel(field.label, fields, field.key) };
  }
  return field;
}

function normalizeUnkeyedFields(fields: WorkflowFieldDraft[]): WorkflowFieldDraft[] {
  return fields.map((field) => normalizeUnkeyedField(field, fields));
}

export function createDraftKey(prefix: string): string {
  return `${prefix}-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
}

export function createFieldDraft(
  name = '',
  type: WorkflowFieldType = 'string',
  required = true
): WorkflowFieldDraft {
  return {
    key: createDraftKey('field'),
    name,
    type,
    required,
    ...(name ? {} : { newField: true }),
  };
}

export function addStartField(
  fields: WorkflowFieldDraft[],
  label = '',
  type: WorkflowFieldType | 'pick-list' = 'string'
): WorkflowFieldDraft[] {
  const trimmedLabel = label.trim();
  const field = createFieldDraft('', type === 'pick-list' ? 'string' : type);
  return [
    ...fields,
    {
      ...field,
      ...(trimmedLabel
        ? {
            name: fieldKeyFromLabel(trimmedLabel, fields, field.key),
            label: trimmedLabel,
            hasTitle: true,
            newField: false,
          }
        : {}),
      ...(type === 'pick-list' ? { enumValues: [] } : {}),
    },
  ];
}

export function createHandoffDraft(from = '', to = ''): WorkflowHandoffDraft {
  return { key: createDraftKey('map'), from, to };
}

export function createStepDraft(
  agentId: string,
  promptTemplate = '',
  requiresReview = false
): WorkflowStepDraft {
  return {
    key: createDraftKey('step'),
    agentId,
    promptTemplate,
    requiresReview,
    writes: [createFieldDraft('', 'string', false)],
    handoffs: [],
    choices: [],
    otherwiseKey: '',
  };
}

export function createChoiceDraft(field = '', goToKey = ''): WorkflowChoiceDraft {
  return { key: createDraftKey('choice'), field, op: 'eq', value: '', goToKey };
}

export function pointerToField(pointer: string): string {
  const trimmed = pointer.trim();
  if (trimmed.startsWith('/')) {
    return trimmed.slice(1);
  }
  return trimmed;
}

export function fieldToPointer(field: string): string {
  const trimmed = field.trim();
  if (!trimmed) {
    return '/';
  }
  return trimmed.startsWith('/') ? trimmed : `/${trimmed}`;
}

export function fieldsFromSchema(
  schema: Record<string, unknown> | null | undefined
): WorkflowFieldDraft[] {
  const properties = schema?.properties;
  if (!properties || typeof properties !== 'object' || Array.isArray(properties)) {
    return [];
  }

  const required = new Set(
    Array.isArray(schema?.required)
      ? schema.required.filter((item): item is string => typeof item === 'string')
      : []
  );
  return Object.entries(properties as Record<string, unknown>).map(([name, value]) => {
    const originalSchema =
      value && typeof value === 'object' && !Array.isArray(value)
        ? (value as Record<string, unknown>)
        : {};
    const type =
      value && typeof value === 'object' && !Array.isArray(value) && 'type' in value
        ? readFieldType((value as { type?: unknown }).type)
        : 'string';
    const enumValues =
      value && typeof value === 'object' && !Array.isArray(value) && 'enum' in value
        ? (value as { enum?: unknown }).enum
        : undefined;
    const label =
      value &&
      typeof value === 'object' &&
      !Array.isArray(value) &&
      'title' in value &&
      typeof (value as { title?: unknown }).title === 'string'
        ? (value as { title: string }).title
        : fieldDisplayName({ name });
    return {
      ...createFieldDraft(name, type, required.has(name)),
      label,
      schema: structuredClone(originalSchema),
      hasTitle: typeof originalSchema.title === 'string',
      isArray: originalSchema.type === 'array',
      isReadOnlyType: !['string', 'number', 'integer', 'boolean'].includes(
        String(originalSchema.type)
      ),
      ...(Array.isArray(enumValues) && enumValues.every((item) => typeof item === 'string')
        ? { enumValues }
        : {}),
    };
  });
}

export function schemaFromFields(fields: WorkflowFieldDraft[]): Record<string, unknown> {
  const properties = Object.create(null) as Record<string, Record<string, unknown>>;
  const required: string[] = [];
  for (const field of fields) {
    const name = field.name.trim();
    if (!name || !isSafeFieldName(name) || Object.prototype.hasOwnProperty.call(properties, name)) {
      continue;
    }
    const enumValues = field.enumValues
      ?.map((item) => item.trim())
      .filter((item, index, all) => Boolean(item) && all.indexOf(item) === index);
    const originalEnum = field.schema?.enum;
    const originalKind =
      field.schema?.type === 'array'
        ? 'array'
        : Array.isArray(originalEnum) && originalEnum.every((item) => typeof item === 'string')
          ? 'pick-list'
          : field.schema?.type;
    const currentKind = field.isArray
      ? 'array'
      : field.enumValues !== undefined
        ? 'pick-list'
        : field.isReadOnlyType
          ? field.schema?.type
          : field.type;
    const preserveSchemaDetails = field.schema === undefined || originalKind === currentKind;
    const baseSchema = preserveSchemaDetails
      ? (field.schema ?? {})
      : {
          ...(typeof field.schema?.title === 'string' ? { title: field.schema.title } : {}),
          ...(typeof field.schema?.description === 'string'
            ? { description: field.schema.description }
            : {}),
        };
    properties[name] = {
      ...baseSchema,
      type: field.isReadOnlyType ? field.schema?.type : field.type,
      ...(field.isArray || field.enumValues === undefined
        ? {}
        : enumValues && enumValues.length > 0
          ? { enum: enumValues }
          : { enum: [] }),
      ...(field.hasTitle || field.schema === undefined
        ? { title: field.label?.trim() || fieldDisplayName({ name }) }
        : {}),
    };
    if (field.required) {
      required.push(name);
    }
  }
  return {
    type: 'object',
    required,
    properties,
  };
}

export function stepsFromDefinition(definition: WorkflowDefinition): WorkflowStepDraft[] {
  return definition.nodes.map((node) => {
    const writes = fieldsFromSchema(node.outputSchema ?? undefined);
    const handoffs = node.maps.map((map) =>
      createHandoffDraft(pointerToField(map.from), pointerToField(map.to))
    );
    return {
      key: node.nodeId,
      agentId: node.agentId,
      promptTemplate: node.promptTemplate,
      requiresReview: node.requiresReview,
      writes,
      handoffs,
      choices: (node.switchCases ?? []).map((rule) => ({
        key: createDraftKey('choice'),
        field: pointerToField(rule.pointer),
        op: rule.op,
        value: rule.value ?? '',
        goToKey: rule.goTo,
      })),
      otherwiseKey: node.defaultGoTo ?? '',
    };
  });
}

export function inputFieldsFromDefinition(
  definition?: WorkflowDefinition | null
): WorkflowFieldDraft[] {
  const fields = fieldsFromSchema(definition?.inputSchema);
  return fields;
}

export function sequentialContinuationLabel(
  steps: WorkflowStepDraft[],
  index: number
): 'The next step' | 'The workflow finishes' {
  const following = index >= 0 ? steps[index + 1] : undefined;
  if (!following) {
    return 'The workflow finishes';
  }
  const transition = effectiveTransitions(steps).find((item) => item.source === steps[index]?.key);
  return transition?.target === following.key ? 'The next step' : 'The workflow finishes';
}

export function choiceFieldNames(
  inputFields: WorkflowFieldDraft[],
  steps: WorkflowStepDraft[],
  stepIndex: number
): string[] {
  const key = steps[stepIndex]?.key;
  if (!key) return inputFields.map((field) => field.name.trim()).filter(Boolean);
  const step = steps[stepIndex]!;
  const ownResults = step.handoffs.length
    ? step.handoffs.map((handoff) => handoff.to.trim())
    : step.writes.map((field) => field.name.trim());
  return [
    ...new Set([...guaranteedFields(steps, inputFields, key), ...ownResults.filter(Boolean)]),
  ];
}

export function workingFieldNames(
  inputFields: WorkflowFieldDraft[],
  steps: WorkflowStepDraft[]
): string[] {
  const names: string[] = [];
  const seen = new Set<string>();
  const add = (name: string) => {
    const trimmed = name.trim();
    if (!trimmed || seen.has(trimmed)) {
      return;
    }
    seen.add(trimmed);
    names.push(trimmed);
  };
  for (const field of inputFields) {
    add(field.name);
  }
  for (const step of steps) {
    for (const handoff of step.handoffs) {
      add(handoff.to);
    }
    for (const field of step.writes) {
      add(field.name);
    }
  }
  return names;
}

export function alignHandoffs(
  writes: WorkflowFieldDraft[],
  previousWrites: WorkflowFieldDraft[],
  handoffs: WorkflowHandoffDraft[]
): WorkflowHandoffDraft[] {
  let next = handoffs.map((handoff) => {
    const owner = previousWrites.find((field) => field.name.trim() === handoff.from.trim());
    if (!owner) {
      return handoff;
    }
    const updated = writes.find((field) => field.key === owner.key);
    if (!updated || updated.name.trim() === handoff.from.trim()) {
      return handoff;
    }
    const nextName = updated.name.trim();
    return {
      ...handoff,
      from: nextName,
      to: handoff.to.trim() === handoff.from.trim() ? nextName : handoff.to,
    };
  });
  for (const field of writes) {
    const name = field.name.trim();
    if (!name || next.some((handoff) => handoff.from.trim() === name)) {
      continue;
    }
    next = [...next, createHandoffDraft(name, name)];
  }
  return next;
}

export function automaticHandoffs(writes: WorkflowFieldDraft[]): WorkflowHandoffDraft[] {
  return writes
    .filter((field) => field.name.trim())
    .map((field) => createHandoffDraft(field.name, field.name));
}

export function updateStepResults(
  step: WorkflowStepDraft,
  writes: WorkflowFieldDraft[]
): Pick<WorkflowStepDraft, 'writes' | 'handoffs'> {
  const wasAutomatic =
    step.handoffs.length === step.writes.length &&
    step.writes.every(
      (field, index) =>
        step.handoffs[index]?.from.trim() === field.name.trim() &&
        step.handoffs[index]?.to.trim() === field.name.trim()
    );
  if (wasAutomatic || step.handoffs.length === 0) {
    return { writes, handoffs: automaticHandoffs(writes) };
  }

  const aligned = alignHandoffs(writes, step.writes, step.handoffs).filter((handoff) =>
    writes.some((field) => field.name.trim() === handoff.from.trim())
  );
  for (const field of writes) {
    const fieldName = field.name.trim();
    if (!fieldName || aligned.some((handoff) => handoff.from.trim() === fieldName)) continue;
    aligned.push(createHandoffDraft(fieldName, fieldName));
  }
  return { writes, handoffs: aligned };
}

export function describeDraftProblems(
  name: string,
  steps: WorkflowStepDraft[],
  inputFields: WorkflowFieldDraft[],
  agents?: Array<{ id: string; name: string }>,
  governed = false
): string | null {
  inputFields = normalizeUnkeyedFields(inputFields);
  steps = steps.map((step) => ({ ...step, writes: normalizeUnkeyedFields(step.writes) }));
  if (!name.trim()) {
    return 'Give this workflow a name.';
  }
  if (steps.length === 0) {
    return 'Add at least one step.';
  }
  const inputProblem = describeFieldProblems(inputFields, 'start field');
  if (inputProblem) {
    return inputProblem;
  }

  const report = describeStepProblems(steps, inputFields, agents, governed, true);
  if (report.workflowProblems[0]) return report.workflowProblems[0].message;
  const displayNumbers = workflowDisplayNumbers(steps);
  for (const [index, step] of steps.entries()) {
    const problems = report.byStepKey[step.key] ?? [];
    if (
      problems.some((problem) => problem.code === 'missing-agent') &&
      problems.some((problem) => problem.code === 'missing-instruction')
    ) {
      return `Step ${displayNumbers.get(step.key) ?? index + 1}: choose who does this and add an instruction.`;
    }
    const problem = problems[0];
    if (problem) return problem.message;
  }
  return null;
}

export function toSaveWorkflowRequest(
  name: string,
  steps: WorkflowStepDraft[],
  existing?: WorkflowDefinition,
  inputFields: WorkflowFieldDraft[] = inputFieldsFromDefinition(existing)
): SaveWorkflowRequest {
  inputFields = normalizeUnkeyedFields(inputFields);
  steps = steps.map((step) => ({
    ...step,
    writes: normalizeUnkeyedFields(step.writes),
  }));
  const inputSchema = schemaFromFields(inputFields);
  const existingWorkingFields = existing ? fieldsFromSchema(existing.workingSchema) : [];
  const existingEditorFieldNames = new Set([
    ...fieldsFromSchema(existing?.inputSchema).map((field) => field.name),
    ...(existing?.nodes ?? []).flatMap((node) => [
      ...fieldsFromSchema(node.outputSchema).map((field) => field.name),
      ...node.maps.map((handoff) => pointerToField(handoff.to)),
    ]),
  ]);
  const existingWorkingFieldNames = new Set(existingWorkingFields.map((field) => field.name));
  const existingInputFieldNames = new Set(
    fieldsFromSchema(existing?.inputSchema).map((field) => field.name)
  );
  const currentInputFields =
    existingWorkingFields.length === 0
      ? inputFields
      : inputFields.filter(
          (field) =>
            !existingInputFieldNames.has(field.name) || existingWorkingFieldNames.has(field.name)
        );
  const workingProperties = mergeFieldLists(
    currentInputFields,
    steps.flatMap((step) => step.writes.filter((field) => field.name.trim())),
    steps.flatMap((step) =>
      step.handoffs.map((handoff) => {
        const write = step.writes.find((field) => field.name.trim() === handoff.from.trim());
        return createFieldDraft(handoff.to, write?.type ?? 'string', false);
      })
    ),
    existingWorkingFields.filter((field) => !existingEditorFieldNames.has(field.name))
  );
  const workingSchema = schemaFromFields(
    workingProperties.map((field) => ({
      ...field,
      required:
        currentInputFields.some(
          (input) => input.name.trim() === field.name.trim() && input.required
        ) ||
        existingWorkingFields.some((original) => original.name === field.name && original.required),
    }))
  );

  const usedNodeIds = new Set<string>();
  const nodeIds = steps.map((step, index) => {
    const existingNode = matchExistingNode(existing, step);
    const nodeId = nextNodeId(step, index, existingNode, usedNodeIds);
    usedNodeIds.add(nodeId);
    return nodeId;
  });
  const idByKey = new Map(steps.map((step, index) => [step.key, nodeIds[index] ?? step.key]));
  return {
    name: name.trim(),
    governed: existing?.governed ?? false,
    inputSchema,
    workingSchema,
    outputSchema: workingSchema,
    nodes: steps.map((step, index) => {
      const existingNode = matchExistingNode(existing, step);
      const choosing = step.choices.length > 0 || step.otherwiseKey.trim().length > 0;
      return {
        nodeId: nodeIds[index] ?? step.key,
        agentId: step.agentId,
        promptTemplate: step.promptTemplate.trim(),
        outputKind: existingNode?.outputKind ?? 'schema',
        outputSchema: schemaFromFields(step.writes.filter((field) => field.name.trim())),
        maps: step.handoffs
          .filter((handoff) => handoff.from.trim() && handoff.to.trim())
          .map((handoff) => ({
            from: fieldToPointer(handoff.from),
            to: fieldToPointer(handoff.to),
          })),
        sideEffects: existingNode?.sideEffects ?? 'none',
        memoryWrites: existingNode?.memoryWrites ?? false,
        requiresReview: step.requiresReview,
        maxRetries: existingNode?.maxRetries ?? 0,
        switchCases: choosing
          ? step.choices.map((choice) => ({
              pointer: fieldToPointer(choice.field),
              op: choice.op,
              value: choice.op === 'exists' ? null : choice.value.trim(),
              goTo: idByKey.get(choice.goToKey) ?? choice.goToKey,
            }))
          : undefined,
        defaultGoTo: choosing ? (idByKey.get(step.otherwiseKey) ?? step.otherwiseKey) : undefined,
      };
    }),
  };
}

export function coerceFieldValue(
  type: WorkflowFieldType,
  value: string
): string | number | boolean {
  const trimmed = value.trim();
  if (type === 'boolean') {
    return trimmed === 'true';
  }
  if (type === 'integer' || type === 'number') {
    const parsed = type === 'integer' ? Number.parseInt(trimmed, 10) : Number(trimmed);
    return Number.isFinite(parsed) ? parsed : trimmed;
  }
  return trimmed;
}

function readFieldType(value: unknown): WorkflowFieldType {
  return workflowFieldTypes.includes(value as WorkflowFieldType)
    ? (value as WorkflowFieldType)
    : 'string';
}

function describeFieldProblems(fields: WorkflowFieldDraft[], label: string): string | null {
  const seen = new Set<string>();
  for (const field of fields) {
    const name = field.name.trim();
    if (!name || (field.label !== undefined && !field.label.trim())) {
      return 'Add a name for this field.';
    }
    if (field.enumValues !== undefined && field.enumValues.length === 0)
      return 'Add at least one answer.';
    if (!fieldNamePattern.test(name) || reservedFieldNames.has(name)) {
      return `'${name}' is not a valid ${label} name.`;
    }
    if (seen.has(name)) {
      return `The ${label} '${name}' is listed twice.`;
    }
    seen.add(name);
  }
  return null;
}

function mergeFieldLists(...groups: WorkflowFieldDraft[][]): WorkflowFieldDraft[] {
  const merged: WorkflowFieldDraft[] = [];
  const seen = new Set<string>();
  for (const group of groups) {
    for (const field of group) {
      const name = field.name.trim();
      if (!name || seen.has(name)) {
        continue;
      }
      seen.add(name);
      merged.push({ ...field, name });
    }
  }
  return merged;
}

function matchExistingNode(
  existing: WorkflowDefinition | undefined,
  step: WorkflowStepDraft
): WorkflowNode | undefined {
  return existing?.nodes.find((node) => node.nodeId === step.key);
}

function nextNodeId(
  step: WorkflowStepDraft,
  index: number,
  existingNode: WorkflowNode | undefined,
  used: Set<string>
): string {
  const candidates = [existingNode?.nodeId, step.key, `step-${index + 1}`];
  for (const candidate of candidates) {
    if (candidate && !candidate.startsWith('step-') && !used.has(candidate)) {
      return candidate;
    }
    if (candidate && /^step-\d+$/.test(candidate) && !used.has(candidate)) {
      return candidate;
    }
  }
  let suffix = index + 1;
  let nodeId = `step-${suffix}`;
  while (used.has(nodeId)) {
    suffix += 1;
    nodeId = `step-${suffix}`;
  }
  return nodeId;
}
