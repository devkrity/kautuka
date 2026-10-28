import {
  createChoiceDraft,
  createStepDraft,
  fieldDisplayName,
  type WorkflowChoiceDraft,
  type WorkflowFieldDraft,
  type WorkflowStepDraft,
} from './workflowDraft';

export type TransitionKind = 'next' | 'rule' | 'otherwise' | 'continue';

export interface WorkflowTransition {
  source: string;
  target: string;
  kind: TransitionKind;
  ruleKey?: string;
}

export type ConnectionRef =
  | { kind: 'next'; stepKey: string }
  | { kind: 'rule'; stepKey: string; ruleKey: string }
  | { kind: 'otherwise'; stepKey: string };

export type EditResult = { ok: true; steps: WorkflowStepDraft[] } | { ok: false; reason: string };

export type AddPickListAnswerResult =
  | { ok: true; steps: WorkflowStepDraft[]; inputFields: WorkflowFieldDraft[] }
  | { ok: false; reason: string };

/** Adds an answer only after the user explicitly chooses the typed value. */
export function addPickListAnswer(
  steps: WorkflowStepDraft[],
  inputFields: WorkflowFieldDraft[],
  sourceStepKey: string | null,
  fieldName: string,
  answer: string
): AddPickListAnswerResult {
  const value = answer.trim();
  if (!value) return { ok: false, reason: 'Type an answer first.' };

  const updateField = (field: WorkflowFieldDraft): WorkflowFieldDraft => ({
    ...field,
    enumValues: [...(field.enumValues ?? []), value],
  });
  const hasAnswer = (field: WorkflowFieldDraft) =>
    (field.enumValues ?? []).some((existing) => existing.toLowerCase() === value.toLowerCase());

  if (sourceStepKey === null) {
    const source = inputFields.find(
      (field) => field.name === fieldName && field.enumValues !== undefined
    );
    if (!source) return { ok: false, reason: 'That start field no longer has a pick-list.' };
    if (hasAnswer(source)) return { ok: false, reason: 'That answer is already in the list.' };
    return {
      ok: true,
      steps,
      inputFields: inputFields.map((field) => (field === source ? updateField(field) : field)),
    };
  }

  const source = steps.find((step) => step.key === sourceStepKey);
  if (!source) return { ok: false, reason: 'That result is no longer in this workflow.' };
  const mappedSourceName = source.handoffs.find((handoff) => handoff.to === fieldName)?.from;
  const sourceField = source.writes.find(
    (field) => field.name === (mappedSourceName ?? fieldName) && field.enumValues !== undefined
  );
  if (!sourceField) return { ok: false, reason: 'That result no longer has a pick-list.' };
  if (hasAnswer(sourceField)) return { ok: false, reason: 'That answer is already in the list.' };
  return {
    ok: true,
    inputFields,
    steps: steps.map((step) =>
      step.key === sourceStepKey
        ? {
            ...step,
            writes: step.writes.map((field) =>
              field === sourceField ? updateField(field) : field
            ),
          }
        : step
    ),
  };
}

export interface DeleteStepPlan {
  ok: true;
  steps: WorkflowStepDraft[];
  removedStepKeys: string[];
  affectedPathCount: number;
  confirmation: string;
}

export type DeletePlan = DeleteStepPlan | { ok: false; reason: string };

export interface WorkflowProblem {
  code: string;
  stepKey?: string;
  message: string;
  severity: 'error' | 'hint';
}

export interface WorkflowProblemReport {
  byStepKey: Record<string, WorkflowProblem[]>;
  workflowProblems: WorkflowProblem[];
  hints: WorkflowProblem[];
}

export interface WorkflowGraphNode {
  key: string;
  step: WorkflowStepDraft;
  index: number;
  isStart: boolean;
  isTerminal: boolean;
  displayName: string;
  instructionPreview: string;
  problems: WorkflowProblem[];
}

export interface WorkflowGraph {
  nodes: WorkflowGraphNode[];
  connections: WorkflowTransition[];
  startFields: string[];
}

function indexByKey(steps: WorkflowStepDraft[]): Map<string, number> {
  return new Map(steps.map((step, index) => [step.key, index]));
}

function explicitTargets(steps: WorkflowStepDraft[]): Set<string> {
  const targets = new Set<string>();
  for (const step of steps) {
    for (const choice of step.choices) targets.add(choice.goToKey);
    if (step.otherwiseKey) targets.add(step.otherwiseKey);
  }
  return targets;
}

/** The only derivation of workflow execution transitions from the ordered draft. */
export function effectiveTransitions(steps: WorkflowStepDraft[]): WorkflowTransition[] {
  const targetKeys = explicitTargets(steps);
  const transitions: WorkflowTransition[] = [];
  for (let index = 0; index < steps.length; index++) {
    const step = steps[index]!;
    if (step.choices.length > 0) {
      for (const choice of step.choices) {
        transitions.push({
          source: step.key,
          target: choice.goToKey,
          kind: 'rule',
          ruleKey: choice.key,
        });
      }
      if (step.otherwiseKey) {
        transitions.push({ source: step.key, target: step.otherwiseKey, kind: 'otherwise' });
      }
      continue;
    }
    if (step.otherwiseKey) {
      transitions.push({ source: step.key, target: step.otherwiseKey, kind: 'continue' });
      continue;
    }
    const next = steps[index + 1];
    if (next && (!targetKeys.has(next.key) || index + 1 === 0)) {
      transitions.push({ source: step.key, target: next.key, kind: 'next' });
    }
  }
  return transitions;
}

/** Display steps in flow order without changing their saved or execution order. */
export function workflowDisplayNumbers(steps: WorkflowStepDraft[]): Map<string, number> {
  const outgoing = transitionsBySource(effectiveTransitions(steps));
  const order: string[] = [];
  const visited = new Set<string>();
  const queue = steps[0] ? [steps[0].key] : [];
  while (queue.length) {
    const key = queue.shift()!;
    if (visited.has(key)) continue;
    visited.add(key);
    order.push(key);
    for (const transition of outgoing.get(key) ?? []) {
      if (!visited.has(transition.target)) queue.push(transition.target);
    }
  }
  for (const step of steps) {
    if (!visited.has(step.key)) order.push(step.key);
  }
  return new Map(order.map((key, index) => [key, index + 1]));
}

function equivalentExecution(left: WorkflowTransition[], right: WorkflowTransition[]): boolean {
  const normalize = (items: WorkflowTransition[]) => {
    const grouped = transitionsBySource(items);
    return [...grouped.entries()]
      .sort(([leftSource], [rightSource]) => leftSource.localeCompare(rightSource))
      .map(([source, transitions]) => [
        source,
        ...transitions.map((transition) => [
          transition.target,
          transition.kind === 'next' || transition.kind === 'continue' ? 'linear' : transition.kind,
          transition.ruleKey ?? '',
        ]),
      ]);
  };
  return JSON.stringify(normalize(left)) === JSON.stringify(normalize(right));
}

function isPlainFallThrough(
  steps: WorkflowStepDraft[],
  source: string,
  target: string,
  targets: Set<string>,
  positions: Map<string, number>
): boolean {
  const sourceIndex = positions.get(source);
  const targetIndex = positions.get(target);
  return (
    sourceIndex !== undefined &&
    targetIndex === sourceIndex + 1 &&
    !targets.has(target) &&
    steps[sourceIndex]?.key !== target
  );
}

/** Writes the requested graph using fall-through only when its full target set permits it. */
export function writeTransitions(
  steps: WorkflowStepDraft[],
  desired: WorkflowTransition[]
): EditResult {
  const byKey = new Map(steps.map((step) => [step.key, step]));
  const positions = indexByKey(steps);
  const grouped = new Map<string, WorkflowTransition[]>();
  for (const transition of desired) {
    if (!byKey.has(transition.source) || !byKey.has(transition.target)) {
      return { ok: false, reason: 'A path points to a step that is no longer in this workflow.' };
    }
    const group = grouped.get(transition.source) ?? [];
    group.push(transition);
    grouped.set(transition.source, group);
  }

  const existing = new Map<string, WorkflowStepDraft>();
  for (const step of steps) existing.set(step.key, step);
  const choicesBySource = new Map<string, WorkflowChoiceDraft[]>();
  const defaultBySource = new Map<string, string>();
  const explicit = new Set<string>();
  for (const [source, transitions] of grouped) {
    for (const transition of transitions) {
      if (transition.kind === 'rule' || transition.kind === 'otherwise') {
        explicit.add(transition.target);
      } else if (!isPlainFallThrough(steps, source, transition.target, new Set(), positions)) {
        explicit.add(transition.target);
      }
    }
  }

  for (const step of steps) {
    const transitions = grouped.get(step.key) ?? [];
    const rules = transitions.filter((transition) => transition.kind === 'rule');
    const defaultTransition = transitions.find((transition) => transition.kind === 'otherwise');
    const linearTransition = transitions.find(
      (transition) => transition.kind === 'next' || transition.kind === 'continue'
    );
    const preserved = new Map(step.choices.map((choice) => [choice.key, choice]));
    const choices: WorkflowChoiceDraft[] = [];
    for (const transition of rules) {
      if (!transition.ruleKey)
        return { ok: false, reason: 'A choice path is missing its identity.' };
      const old = preserved.get(transition.ruleKey);
      if (!old) return { ok: false, reason: 'A choice path no longer matches this step.' };
      choices.push({ ...old, goToKey: transition.target });
    }

    let otherwiseKey = defaultTransition?.target ?? '';
    if (rules.length === 0 && !defaultTransition && linearTransition) {
      if (!isPlainFallThrough(steps, step.key, linearTransition.target, explicit, positions)) {
        otherwiseKey = linearTransition.target;
      }
    }
    if (rules.length > 0 && !otherwiseKey) {
      return { ok: false, reason: 'A choice always needs an Otherwise path.' };
    }
    existing.set(step.key, { ...step, choices, otherwiseKey });
    choicesBySource.set(step.key, choices);
    defaultBySource.set(step.key, otherwiseKey);
  }

  const result = steps.map((step) => existing.get(step.key)!);
  // A terminal cannot be represented before a list neighbour unless another explicit path
  // targets that neighbour and therefore cuts its implicit fall-through.
  const resultTargets = explicitTargets(result);
  for (let index = 0; index < result.length - 1; index++) {
    const step = result[index]!;
    if ((grouped.get(step.key) ?? []).length === 0 && !resultTargets.has(result[index + 1]!.key)) {
      return {
        ok: false,
        reason: `This step would carry on into Step ${workflowDisplayNumbers(result).get(result[index + 1]!.key)}. Choose where it continues instead.`,
      };
    }
  }

  if (!equivalentExecution(effectiveTransitions(result), desired)) {
    return { ok: false, reason: 'This change cannot be saved without changing another path.' };
  }
  return { ok: true, steps: result };
}

function replaceTransition(
  transitions: WorkflowTransition[],
  source: string,
  oldTarget: string,
  next: WorkflowTransition | null,
  ruleKey?: string
): WorkflowTransition[] {
  const index = transitions.findIndex(
    (item) =>
      item.source === source &&
      item.target === oldTarget &&
      (ruleKey === undefined ? item.kind !== 'rule' : item.ruleKey === ruleKey)
  );
  if (index < 0) return transitions;
  const result = transitions.slice();
  result.splice(index, 1, ...(next ? [next] : []));
  return result;
}

function pathForRef(
  steps: WorkflowStepDraft[],
  ref: ConnectionRef
): WorkflowTransition | undefined {
  return effectiveTransitions(steps).find((transition) => {
    if (transition.source !== ref.stepKey) return false;
    if (ref.kind === 'rule')
      return transition.kind === 'rule' && transition.ruleKey === ref.ruleKey;
    if (ref.kind === 'otherwise')
      return transition.kind === 'otherwise' || transition.kind === 'continue';
    return transition.kind === 'next';
  });
}

export function addStepOn(
  steps: WorkflowStepDraft[],
  ref: ConnectionRef,
  agentId: string
): EditResult {
  const old = pathForRef(steps, ref);
  if (!old) return { ok: false, reason: 'That path is no longer available.' };
  const created = createStepDraft(agentId);
  const next = steps.slice();
  if (old.kind === 'next') {
    const index = next.findIndex((step) => step.key === ref.stepKey);
    next.splice(index + 1, 0, created);
  } else {
    next.push(created);
  }
  let desired = effectiveTransitions(steps);
  desired = replaceTransition(
    desired,
    old.source,
    old.target,
    { source: old.source, target: created.key, kind: old.kind, ruleKey: old.ruleKey },
    old.ruleKey
  );
  desired = [...desired, { source: created.key, target: old.target, kind: 'continue' }];
  return writeTransitions(next, desired);
}

export function extendPath(
  steps: WorkflowStepDraft[],
  terminalKey: string,
  agentId: string
): EditResult {
  if (!steps.some((step) => step.key === terminalKey))
    return { ok: false, reason: 'That step is no longer in this workflow.' };
  if (effectiveTransitions(steps).some((transition) => transition.source === terminalKey)) {
    return { ok: false, reason: 'Add a step from one of this step’s paths.' };
  }
  const created = createStepDraft(agentId);
  const next = steps.slice();
  next.splice(next.findIndex((step) => step.key === terminalKey) + 1, 0, created);
  return writeTransitions(next, [
    ...effectiveTransitions(steps),
    { source: terminalKey, target: created.key, kind: 'continue' },
  ]);
}

export function addChoice(
  steps: WorkflowStepDraft[],
  key: string,
  ifAgentId: string,
  otherwiseAgentId = ifAgentId
): EditResult {
  const step = steps.find((item) => item.key === key);
  if (!step) return { ok: false, reason: 'That step is no longer in this workflow.' };
  if (step.choices.length > 0)
    return { ok: false, reason: 'This step already has checks. Add another check instead.' };
  const outgoing = effectiveTransitions(steps).filter((transition) => transition.source === key);
  if (outgoing.length > 1)
    return { ok: false, reason: 'Add another check to this choice instead.' };
  const ifStep = createStepDraft(ifAgentId);
  const next = steps.slice();
  next.push(ifStep);
  let otherwiseKey = outgoing[0]?.target ?? '';
  if (!otherwiseKey) {
    const otherwiseStep = createStepDraft(otherwiseAgentId);
    next.push(otherwiseStep);
    otherwiseKey = otherwiseStep.key;
  }
  const choice = createChoiceDraft('', ifStep.key);
  const updated = next.map((item) =>
    item.key === key ? { ...item, choices: [choice], otherwiseKey } : item
  );
  return writeTransitions(updated, [
    ...effectiveTransitions(steps).filter((transition) => transition.source !== key),
    { source: key, target: ifStep.key, kind: 'rule', ruleKey: choice.key },
    { source: key, target: otherwiseKey, kind: 'otherwise' },
  ]);
}

export function addChoiceRule(
  steps: WorkflowStepDraft[],
  key: string,
  agentId: string
): EditResult {
  const step = steps.find((item) => item.key === key);
  if (!step) return { ok: false, reason: 'That step is no longer in this workflow.' };
  if (step.choices.length === 0 || !step.otherwiseKey)
    return { ok: false, reason: 'Add another check to a step that already has checks.' };
  const created = createStepDraft(agentId);
  const choice = createChoiceDraft('', created.key);
  const next = steps.slice();
  // Add the new route at the end of the ordered list so existing branch
  // numbers stay stable. The new check targets this final, empty step.
  next.push(created);
  const updated = next.map((item) =>
    item.key === key ? { ...item, choices: [...item.choices, choice] } : item
  );
  const desired = effectiveTransitions(steps);
  const otherwiseIndex = desired.findIndex(
    (transition) => transition.source === key && transition.kind === 'otherwise'
  );
  desired.splice(otherwiseIndex < 0 ? desired.length : otherwiseIndex, 0, {
    source: key,
    target: created.key,
    kind: 'rule',
    ruleKey: choice.key,
  });
  return writeTransitions(updated, desired);
}

export function moveRule(
  steps: WorkflowStepDraft[],
  key: string,
  ruleKey: string,
  direction: 'up' | 'down'
): EditResult {
  const step = steps.find((item) => item.key === key);
  if (!step) return { ok: false, reason: 'That step is no longer in this workflow.' };
  const index = step.choices.findIndex((choice) => choice.key === ruleKey);
  const destination = index + (direction === 'up' ? -1 : 1);
  if (index < 0 || destination < 0 || destination >= step.choices.length)
    return { ok: false, reason: 'That If cannot move any farther.' };
  const choices = step.choices.slice();
  [choices[index], choices[destination]] = [choices[destination]!, choices[index]!];
  return { ok: true, steps: steps.map((item) => (item.key === key ? { ...item, choices } : item)) };
}

export function replaceAgent(steps: WorkflowStepDraft[], key: string, agentId: string): EditResult {
  if (!steps.some((step) => step.key === key))
    return { ok: false, reason: 'That step is no longer in this workflow.' };
  return { ok: true, steps: steps.map((step) => (step.key === key ? { ...step, agentId } : step)) };
}

export function findUnreachable(steps: WorkflowStepDraft[]): string[] {
  if (steps.length === 0) return [];
  const reachable = new Set<string>([steps[0]!.key]);
  const outgoing = transitionsBySource(effectiveTransitions(steps));
  const queue = [steps[0]!.key];
  while (queue.length > 0) {
    for (const transition of outgoing.get(queue.shift()!) ?? []) {
      if (!reachable.has(transition.target)) {
        reachable.add(transition.target);
        queue.push(transition.target);
      }
    }
  }
  return steps.filter((step) => !reachable.has(step.key)).map((step) => step.key);
}

export function findLoop(steps: WorkflowStepDraft[]): string | null {
  const outgoing = transitionsBySource(effectiveTransitions(steps));
  const color = new Map<string, 0 | 1 | 2>();
  const visit = (key: string): string | null => {
    color.set(key, 1);
    for (const transition of outgoing.get(key) ?? []) {
      if (!steps.some((step) => step.key === transition.target)) continue;
      const state = color.get(transition.target) ?? 0;
      if (state === 1) return key;
      if (state === 0) {
        const loop = visit(transition.target);
        if (loop) return loop;
      }
    }
    color.set(key, 2);
    return null;
  };
  for (const step of steps) {
    if ((color.get(step.key) ?? 0) === 0) {
      const loop = visit(step.key);
      if (loop) return loop;
    }
  }
  return null;
}

function transitionsBySource(transitions: WorkflowTransition[]): Map<string, WorkflowTransition[]> {
  const bySource = new Map<string, WorkflowTransition[]>();
  for (const transition of transitions) {
    const list = bySource.get(transition.source) ?? [];
    list.push(transition);
    bySource.set(transition.source, list);
  }
  return bySource;
}

function canReach(steps: WorkflowStepDraft[], from: string, target: string): boolean {
  const outgoing = transitionsBySource(effectiveTransitions(steps));
  const seen = new Set<string>();
  const queue = [from];
  while (queue.length > 0) {
    const key = queue.shift()!;
    if (key === target) return true;
    if (seen.has(key)) continue;
    seen.add(key);
    for (const transition of outgoing.get(key) ?? []) queue.push(transition.target);
  }
  return false;
}

export function joinCandidates(steps: WorkflowStepDraft[], terminalKey: string): string[] {
  const start = steps[0]?.key;
  if (!start) return [];
  return steps
    .filter(
      (step) =>
        step.key !== terminalKey && step.key !== start && !canReach(steps, step.key, terminalKey)
    )
    .map((step) => step.key);
}

export function connectPathTo(
  steps: WorkflowStepDraft[],
  terminalKey: string,
  targetKey: string,
  agents: Array<{ id: string; name: string }> = []
): EditResult {
  const known = new Set(steps.map((step) => step.key));
  if (!known.has(terminalKey) || !known.has(targetKey))
    return { ok: false, reason: 'Choose a step in this workflow.' };
  if (effectiveTransitions(steps).some((transition) => transition.source === terminalKey)) {
    return { ok: false, reason: 'Choose a step at the end of this path.' };
  }
  if (!joinCandidates(steps, terminalKey).includes(targetKey))
    return { ok: false, reason: 'That choice could send the workflow back to an earlier step.' };
  return writeTransitions(steps, [
    ...effectiveTransitions(steps),
    { source: terminalKey, target: targetKey, kind: 'continue' },
  ]);
}

export function disconnectPath(
  steps: WorkflowStepDraft[],
  key: string,
  agents: Array<{ id: string; name: string }> = []
): EditResult {
  const step = steps.find((item) => item.key === key);
  if (!step) return { ok: false, reason: 'That step is no longer in this workflow.' };
  if (step.choices.length > 0)
    return { ok: false, reason: 'A choice always needs an Otherwise path.' };
  const oldTransitions = effectiveTransitions(steps);
  const outgoing = oldTransitions.find(
    (transition) => transition.source === key && transition.kind === 'continue'
  );
  if (!outgoing) return { ok: false, reason: 'This step does not have a continuation to stop.' };
  const desired = oldTransitions.filter((transition) => transition !== outgoing);
  const beforeUnreachable = new Set(findUnreachable(steps));
  const desiredBySource = transitionsBySource(desired);
  const desiredReachable = new Set<string>();
  const queue = steps[0] ? [steps[0].key] : [];
  while (queue.length > 0) {
    const current = queue.shift()!;
    if (desiredReachable.has(current)) continue;
    desiredReachable.add(current);
    for (const transition of desiredBySource.get(current) ?? []) queue.push(transition.target);
  }
  const newlyUnreachable = steps.find(
    (candidateStep) =>
      !beforeUnreachable.has(candidateStep.key) && !desiredReachable.has(candidateStep.key)
  );
  if (newlyUnreachable) {
    return {
      ok: false,
      reason: `'${displayStepName(steps, newlyUnreachable.key, agents)}' would no longer be reached. Delete it instead if you don't need it.`,
    };
  }
  const candidate = writeTransitions(
    steps.map((item) => (item.key === key ? { ...item, otherwiseKey: '' } : item)),
    desired
  );
  if (!candidate.ok) return candidate;
  if (!equivalentExecution(effectiveTransitions(candidate.steps), desired)) {
    const index = steps.findIndex((item) => item.key === key);
    return {
      ok: false,
      reason: `This step would carry on into Step ${workflowDisplayNumbers(steps).get(steps[index + 1]!.key)}. Choose where it continues instead.`,
    };
  }
  return candidate;
}

function reachableFromTransitions(
  steps: WorkflowStepDraft[],
  transitions: WorkflowTransition[],
  startKey = steps[0]?.key
): Set<string> {
  if (!startKey || !steps.some((step) => step.key === startKey)) return new Set();
  const known = new Set(steps.map((step) => step.key));
  const outgoing = transitionsBySource(transitions);
  const reachable = new Set<string>();
  const queue = [startKey];
  while (queue.length > 0) {
    const current = queue.shift()!;
    if (!known.has(current) || reachable.has(current)) continue;
    reachable.add(current);
    for (const transition of outgoing.get(current) ?? []) {
      if (known.has(transition.target) && !reachable.has(transition.target)) {
        queue.push(transition.target);
      }
    }
  }
  return reachable;
}

function reachableFromStart(
  steps: WorkflowStepDraft[],
  transitions = effectiveTransitions(steps)
): Set<string> {
  return reachableFromTransitions(steps, transitions);
}

function normalizeDesiredAfterRemoving(
  steps: WorkflowStepDraft[],
  removing: Set<string>
): WorkflowTransition[] {
  return effectiveTransitions(steps).flatMap((transition) => {
    if (removing.has(transition.source)) return [];
    if (removing.has(transition.target)) return [];
    return [transition];
  });
}

function removeUnreachableNewSteps(
  original: WorkflowStepDraft[],
  candidate: WorkflowStepDraft[],
  desired: WorkflowTransition[],
  initiallyRemoved: Set<string>
): { steps: WorkflowStepDraft[]; transitions: WorkflowTransition[]; removed: string[] } {
  const originallyReachable = reachableFromStart(original);
  let removing = new Set(initiallyRemoved);
  let currentSteps = candidate.filter((step) => !removing.has(step.key));
  let currentTransitions = desired.filter(
    (transition) => !removing.has(transition.source) && !removing.has(transition.target)
  );
  while (true) {
    const reachable = reachableFromStart(currentSteps, currentTransitions);
    const newlyUnreachable = currentSteps.filter(
      (step) => !reachable.has(step.key) && originallyReachable.has(step.key)
    );
    if (newlyUnreachable.length === 0) break;
    for (const step of newlyUnreachable) removing.add(step.key);
    currentSteps = currentSteps.filter((step) => !removing.has(step.key));
    currentTransitions = currentTransitions.filter(
      (transition) => !removing.has(transition.source) && !removing.has(transition.target)
    );
  }
  return { steps: currentSteps, transitions: currentTransitions, removed: [...removing] };
}

function rewriteDeletedTarget(
  steps: WorkflowStepDraft[],
  deletedKey: string,
  keepTarget: string | null
): { steps: WorkflowStepDraft[]; transitions: WorkflowTransition[]; affected: number } {
  let affected = 0;
  const transitions: WorkflowTransition[] = [];
  for (const transition of effectiveTransitions(steps)) {
    if (transition.target !== deletedKey) {
      transitions.push(transition);
      continue;
    }
    affected++;
    if (keepTarget) transitions.push({ ...transition, target: keepTarget });
  }
  const rewritten = steps.map((step) => {
    if (step.key === deletedKey) return step;
    const choices = step.choices.flatMap((choice) => {
      if (choice.goToKey !== deletedKey) return [choice];
      return keepTarget ? [{ ...choice, goToKey: keepTarget }] : [];
    });
    const otherwiseKey = step.otherwiseKey === deletedKey ? (keepTarget ?? '') : step.otherwiseKey;
    return { ...step, choices, otherwiseKey };
  });
  return { steps: rewritten, transitions, affected };
}

export function planDeleteStep(
  steps: WorkflowStepDraft[],
  key: string,
  agents: Array<{ id: string; name: string }> = []
): DeletePlan {
  const deletedIndex = steps.findIndex((step) => step.key === key);
  if (deletedIndex < 0) return { ok: false, reason: 'That step is no longer in this workflow.' };
  if (steps.length === 1) return { ok: false, reason: 'A workflow needs at least one step.' };
  const deleted = steps[deletedIndex]!;
  const outgoing = effectiveTransitions(steps).filter((transition) => transition.source === key);
  const otherwise = outgoing.find(
    (transition) => transition.kind === 'otherwise' || transition.kind === 'continue'
  );
  const keepTarget =
    deleted.choices.length > 0
      ? (otherwise?.target ?? null)
      : outgoing.length === 1
        ? outgoing[0]!.target
        : null;
  const rewritten = rewriteDeletedTarget(steps, key, keepTarget);
  const choiceSourcesMissingDefault = rewritten.steps.filter(
    (step) => step.key !== key && step.choices.length > 0 && !step.otherwiseKey
  );
  if (choiceSourcesMissingDefault.length > 0) {
    return {
      ok: false,
      reason:
        'A choice always needs an Otherwise path. Add another step to this path first, or remove the other paths to undo the choice.',
    };
  }

  const withoutDeleted = rewritten.steps.filter((step) => step.key !== key);
  if (deletedIndex === 0 && keepTarget) {
    const startIndex = withoutDeleted.findIndex((step) => step.key === keepTarget);
    if (startIndex > 0) {
      const [newStart] = withoutDeleted.splice(startIndex, 1);
      withoutDeleted.unshift(newStart!);
    }
  }
  const rewrittenTransitions = rewritten.transitions.map((transition) => {
    const source = rewritten.steps.find((step) => step.key === transition.source);
    if (source && source.choices.length === 0 && transition.kind === 'otherwise') {
      return { ...transition, kind: 'continue' as const };
    }
    return transition;
  });
  const retainedTransitions = rewrittenTransitions.filter(
    (transition) => transition.source !== key && transition.target !== key
  );
  if (deletedIndex === 0 && keepTarget) {
    const originallyReachable = reachableFromStart(steps);
    const previouslyUnreachable = new Set(
      steps.filter((step) => !originallyReachable.has(step.key)).map((step) => step.key)
    );
    const reachableFromNewStart = reachableFromStart(
      withoutDeleted,
      effectiveTransitions(withoutDeleted)
    );
    if (
      [...previouslyUnreachable].some((unreachableKey) => reachableFromNewStart.has(unreachableKey))
    ) {
      return {
        ok: false,
        reason: 'Remove the unreachable step first, then delete this one.',
      };
    }
  }
  const cleanup = removeUnreachableNewSteps(
    steps,
    withoutDeleted,
    retainedTransitions,
    new Set([key])
  );
  const written = writeTransitions(cleanup.steps, cleanup.transitions);
  if (!written.ok) return written;
  const labels = cleanup.removed
    .filter((removedKey) => removedKey !== key)
    .map((removedKey) => displayStepName(steps, removedKey, agents));
  const repointed = keepTarget ? rewritten.affected : 0;
  const confirmation =
    labels.length === 0 && repointed === 0
      ? `Delete ${deleteConfirmStepName(steps, key, agents)}?`
      : `Delete ${deleteConfirmStepName(steps, key, agents)}${labels.length ? ` and remove ${labels.join(', ')}` : ''}${repointed ? `; ${repointed} path${repointed === 1 ? '' : 's'} will continue to ${displayStepName(steps, keepTarget!, agents)}` : ''}?`;
  return {
    ok: true,
    steps: written.steps,
    removedStepKeys: cleanup.removed,
    affectedPathCount: rewritten.affected,
    confirmation,
  };
}

export function planRemoveChoiceRule(
  steps: WorkflowStepDraft[],
  stepKey: string,
  ruleKey: string,
  agents: Array<{ id: string; name: string }> = []
): DeletePlan {
  const step = steps.find((item) => item.key === stepKey);
  if (!step) return { ok: false, reason: 'That step is no longer in this workflow.' };
  if (!step.choices.some((choice) => choice.key === ruleKey))
    return { ok: false, reason: 'That If is no longer in this choice.' };
  const oldTransitions = effectiveTransitions(steps);
  const rule = oldTransitions.find(
    (transition) => transition.source === stepKey && transition.ruleKey === ruleKey
  );
  if (!rule) return { ok: false, reason: 'That If path is no longer available.' };
  const next = steps.map((item) =>
    item.key === stepKey
      ? { ...item, choices: item.choices.filter((choice) => choice.key !== ruleKey) }
      : item
  );
  let desired = oldTransitions.filter((transition) => transition !== rule);
  if (step.choices.length === 1) {
    // With its final If removed, Otherwise remains a plain continuation.
    desired = desired.map((transition) =>
      transition.source === stepKey && transition.kind === 'otherwise'
        ? { ...transition, kind: 'continue' }
        : transition
    );
  }
  const reachableBefore = reachableFromStart(steps);
  const reachableAfter = new Set<string>();
  if (next[0]) {
    const bySource = transitionsBySource(desired);
    const queue = [next[0].key];
    while (queue.length) {
      const current = queue.shift()!;
      if (reachableAfter.has(current)) continue;
      reachableAfter.add(current);
      for (const transition of bySource.get(current) ?? []) queue.push(transition.target);
    }
  }
  const removing = new Set(
    next
      .filter((item) => reachableBefore.has(item.key) && !reachableAfter.has(item.key))
      .map((item) => item.key)
  );
  const cleaned = removeUnreachableNewSteps(steps, next, desired, removing);
  const written = writeTransitions(cleaned.steps, cleaned.transitions);
  if (!written.ok) return written;
  const labels = cleaned.removed.map((removedKey) => displayStepName(steps, removedKey, agents));
  return {
    ok: true,
    steps: written.steps,
    removedStepKeys: cleaned.removed,
    affectedPathCount: 0,
    confirmation: labels.length
      ? `Remove this If and remove ${labels.join(', ')}?`
      : 'Remove this If?',
  };
}

function displayStepName(
  steps: WorkflowStepDraft[],
  key: string,
  agents: Array<{ id: string; name: string }> = []
): string {
  const index = steps.findIndex((step) => step.key === key);
  if (index < 0) return key;
  const step = steps[index]!;
  return `Step ${workflowDisplayNumbers(steps).get(key)} (${agents.find((agent) => agent.id === step.agentId)?.name ?? (step.agentId ? 'Removed agent' : 'Unassigned')})`;
}

function deleteConfirmStepName(
  steps: WorkflowStepDraft[],
  key: string,
  agents: Array<{ id: string; name: string }> = []
): string {
  const index = steps.findIndex((step) => step.key === key);
  if (index < 0) return key;
  const step = steps[index]!;
  const agentName =
    agents.find((agent) => agent.id === step.agentId)?.name ??
    (step.agentId ? 'Agent removed' : 'Unassigned');
  return `Step ${workflowDisplayNumbers(steps).get(key)} (${agentName})`;
}

function fieldsFilledBy(step: WorkflowStepDraft): Set<string> {
  const fields =
    step.handoffs.length > 0
      ? step.handoffs.map((handoff) => handoff.to.trim())
      : step.writes.map((field) => field.name.trim());
  return new Set(fields.filter(Boolean));
}

export function guaranteedFields(
  steps: WorkflowStepDraft[],
  inputFields: WorkflowFieldDraft[],
  key: string
): string[] {
  const inputs = inputFields.map((field) => field.name.trim()).filter(Boolean);
  const target = steps.find((step) => step.key === key);
  if (!target) return [...new Set(inputs)];
  if (findLoop(steps) || findUnreachable(steps).includes(key)) {
    return [...new Set(inputs)];
  }

  const transitions = effectiveTransitions(steps);
  const reachable = reachableFromStart(steps);
  const predecessors = new Map<string, string[]>();
  for (const transition of transitions) {
    if (!reachable.has(transition.source) || !reachable.has(transition.target)) continue;
    const list = predecessors.get(transition.target) ?? [];
    if (!list.includes(transition.source)) list.push(transition.source);
    predecessors.set(transition.target, list);
  }
  const indegree = new Map<string, number>();
  for (const step of steps)
    if (reachable.has(step.key)) indegree.set(step.key, predecessors.get(step.key)?.length ?? 0);
  const queue = steps
    .filter((step) => reachable.has(step.key) && indegree.get(step.key) === 0)
    .map((step) => step.key);
  const after = new Map<string, Set<string>>();
  const beforeByStep = new Map<string, Set<string>>();
  const bySource = transitionsBySource(transitions);
  while (queue.length) {
    const current = queue.shift()!;
    const incoming = predecessors.get(current) ?? [];
    const before = new Set(inputs);
    if (current !== steps[0]?.key && incoming.length > 0) {
      const first = after.get(incoming[0]!);
      if (first) for (const field of first) before.add(field);
      for (const predecessor of incoming.slice(1)) {
        const fields = after.get(predecessor) ?? new Set<string>();
        for (const field of [...before]) if (!fields.has(field)) before.delete(field);
      }
      for (const field of inputs) before.add(field);
    }
    beforeByStep.set(current, new Set(before));
    const filled = fieldsFilledBy(steps.find((step) => step.key === current)!);
    for (const field of filled) before.add(field);
    after.set(current, before);
    for (const transition of bySource.get(current) ?? []) {
      const degree = (indegree.get(transition.target) ?? 0) - 1;
      indegree.set(transition.target, degree);
      if (degree === 0) queue.push(transition.target);
    }
  }
  return [...(beforeByStep.get(key) ?? new Set(inputs))];
}

export function draftToGraph(
  steps: WorkflowStepDraft[],
  inputFields: WorkflowFieldDraft[] = [],
  agents: Array<{ id: string; name: string }> = [],
  governed = false,
  showValidation = false
): WorkflowGraph {
  const transitions = effectiveTransitions(steps);
  const outgoing = transitionsBySource(transitions);
  const displayNumbers = workflowDisplayNumbers(steps);
  const problems = describeStepProblems(steps, inputFields, agents, governed, showValidation);
  return {
    nodes: steps.map((step, index) => ({
      key: step.key,
      step,
      index,
      isStart: index === 0,
      isTerminal: (outgoing.get(step.key)?.length ?? 0) === 0,
      displayName: `Step ${displayNumbers.get(step.key)} · ${agents.find((agent) => agent.id === step.agentId)?.name ?? (step.agentId ? 'Removed agent' : 'Choose who does this')}`,
      instructionPreview: previewInstructions(step.promptTemplate, [
        ...inputFields,
        ...steps.flatMap((candidate) => candidate.writes),
      ]),
      problems: [
        ...(problems.byStepKey[step.key] ?? []),
        ...problems.hints.filter((problem) => problem.stepKey === step.key),
      ],
    })),
    connections: transitions,
    startFields: inputFields.map((field) => field.name.trim()).filter(Boolean),
  };
}

function previewInstructions(promptTemplate: string, fields: WorkflowFieldDraft[]): string {
  const plain = promptTemplate
    .replace(/\{\{\s*([^{}]+?)\s*\}\}/g, (_, key: string) => {
      const fieldKey = key.trim();
      const field = fields.find((candidate) => candidate.name === fieldKey);
      return field ? fieldDisplayName(field) : fieldDisplayName({ name: fieldKey });
    })
    .replace(/\s+/g, ' ')
    .trim();
  const firstSentence = plain.match(/^.*?[.!?](?=\s|$)/)?.[0];
  return firstSentence ?? plain;
}

export function describeStepProblems(
  steps: WorkflowStepDraft[],
  inputFields: WorkflowFieldDraft[],
  agents?: Array<{ id: string; name: string }>,
  governed = false,
  showIncompleteErrors = false
): WorkflowProblemReport {
  const normalizeNewFields = (fields: WorkflowFieldDraft[]) =>
    fields.map((field) => {
      if (!field.newField || field.name.trim() || !field.label?.trim()) return field;
      let base =
        field.label
          .trim()
          .toLowerCase()
          .replace(/[^a-z0-9]+/g, '_')
          .replace(/^_+|_+$/g, '') || 'field';
      if (/^\d/.test(base)) base = `field_${base}`;
      if (['__proto__', 'prototype', 'constructor'].includes(base)) base = `${base}_field`;
      let candidate = base;
      let suffix = 2;
      while (fields.some((item) => item.key !== field.key && item.name === candidate))
        candidate = `${base}_${suffix++}`;
      return { ...field, name: candidate };
    });
  inputFields = normalizeNewFields(inputFields);
  steps = steps.map((step) => ({ ...step, writes: normalizeNewFields(step.writes) }));
  const byStepKey: Record<string, WorkflowProblem[]> = Object.create(null) as Record<
    string,
    WorkflowProblem[]
  >;
  const workflowProblems: WorkflowProblem[] = [];
  const hints: WorkflowProblem[] = [];
  const displayNumbers = workflowDisplayNumbers(steps);
  const add = (problem: WorkflowProblem) => {
    if (problem.severity === 'hint') hints.push(problem);
    else if (problem.stepKey) (byStepKey[problem.stepKey] ??= []).push(problem);
    else workflowProblems.push(problem);
  };

  const inputProblem = describeFieldProblemsLocal(inputFields, 'start field');
  if (inputProblem) add({ code: 'invalid-input-fields', message: inputProblem, severity: 'error' });
  for (const [index, step] of steps.entries()) {
    const label = problemStepName(index, step, agents ?? [], displayNumbers);
    const displayField = (name: string) => problemFieldName(name, inputFields, steps);
    const addStepError = (code: string, message: string) =>
      add({ code, stepKey: step.key, message: `${label} ${message}`, severity: 'error' });
    const addStepTodo = (code: string, message: string) =>
      add({
        code,
        stepKey: step.key,
        message: `${label} ${message}`,
        severity: showIncompleteErrors ? 'error' : 'hint',
      });
    const agent = agents?.find((candidate) => candidate.id === step.agentId);
    if (!step.agentId) addStepTodo('missing-agent', 'needs an agent.');
    else if (agents && !agent) addStepError('removed-agent', 'needs a different agent.');
    if (!step.promptTemplate.trim()) addStepTodo('missing-instruction', 'needs an instruction.');
    const availableFields = new Set(guaranteedFields(steps, inputFields, step.key));
    const mentionedFields = new Set(
      [...step.promptTemplate.matchAll(/\{\{\s*([^{}]+?)\s*\}\}/g)]
        .map((match) => match[1]?.trim() ?? '')
        .filter(Boolean)
    );
    for (const field of mentionedFields) {
      if (!availableFields.has(field)) {
        addStepError(
          'missing-mentioned-field',
          `uses ${displayField(field)}, which it doesn't have yet.`
        );
      }
    }
    const validWrites = step.writes.filter((field) => field.name.trim());
    const hasMissingFieldLabel = step.writes.some(
      (field) => field.name.trim() && field.label !== undefined && !field.label.trim()
    );
    if (hasMissingFieldLabel) {
      add({
        code: 'missing-field-label',
        stepKey: step.key,
        message: `${label}: add a name for this field.`,
        severity: 'error',
      });
    }
    if (governed && validWrites.length === 0)
      add({
        code: 'missing-result',
        stepKey: step.key,
        message: `${label} needs at least one result to pass on.`,
        severity: showIncompleteErrors ? 'error' : 'hint',
      });
    const writeProblem = hasMissingFieldLabel
      ? null
      : describeFieldProblemsLocal(validWrites, 'result');
    if (writeProblem) addStepError('invalid-results', writeProblem);
    const writeNames = new Set(step.writes.map((field) => field.name.trim()));
    for (const handoff of step.handoffs) {
      const from = handoff.from.trim();
      const to = handoff.to.trim();
      if (!from || !to) addStepError('incomplete-mapping', 'needs a complete result mapping.');
      else if (!writeNames.has(from))
        addStepError('missing-mapped-result', `doesn't create ${displayField(from)}.`);
      else if (
        !/^[A-Za-z_][A-Za-z0-9_]*$/.test(to) ||
        ['__proto__', 'prototype', 'constructor'].includes(to)
      ) {
        addStepError('invalid-map-target', `${displayField(to)} needs a simpler field name.`);
      }
    }
    for (const choice of step.choices) {
      if (
        !choice.field.trim() ||
        !['eq', 'ne', 'gt', 'gte', 'lt', 'lte', 'in', 'exists'].includes(choice.op) ||
        (choice.op !== 'exists' && !choice.value.trim())
      ) {
        add({
          code: 'incomplete-check',
          stepKey: step.key,
          message: `${label} needs a complete check with a field, comparison, and value.`,
          severity: showIncompleteErrors ? 'error' : 'hint',
        });
      }
      const ownResults = step.handoffs.length
        ? step.handoffs.map((handoff) => handoff.to.trim())
        : step.writes.map((field) => field.name.trim());
      const fields = new Set([
        ...guaranteedFields(steps, inputFields, step.key),
        ...ownResults.filter(Boolean),
      ]);
      if (choice.field.trim() && !fields.has(choice.field.trim())) {
        add({
          code: 'missing-check-field',
          stepKey: step.key,
          message: `${label} uses ${displayField(choice.field.trim())}, but not every path into it provides it.`,
          severity: 'error',
        });
        hints.push({
          code: 'field-may-be-empty',
          stepKey: step.key,
          message: `${displayField(choice.field.trim())} may be empty on some paths.`,
          severity: 'hint',
        });
      }
      if (
        !steps.some((candidate) => candidate.key === choice.goToKey) ||
        choice.goToKey === step.key
      ) {
        add({
          code: 'missing-check-destination',
          stepKey: step.key,
          message: `${label} needs a step for this check to go to.`,
          severity: 'error',
        });
      }
    }
    if (step.choices.length > 0 && !step.otherwiseKey)
      add({
        code: 'missing-otherwise-destination',
        stepKey: step.key,
        message: `${label} needs a step for Otherwise to go to.`,
        severity: 'error',
      });
    if (step.otherwiseKey && !steps.some((candidate) => candidate.key === step.otherwiseKey)) {
      add({
        code: 'missing-continuation-destination',
        stepKey: step.key,
        message: `${label} needs a step for this continuation to go to.`,
        severity: 'error',
      });
    }
  }

  const loop = findLoop(steps);
  if (loop) {
    add({
      code: 'loop',
      stepKey: loop,
      message: `${problemStepName(
        steps.findIndex((step) => step.key === loop),
        steps.find((step) => step.key === loop)!,
        agents ?? [],
        displayNumbers
      )} leads back to an earlier step, creating a loop.`,
      severity: 'error',
    });
  }
  for (const key of findUnreachable(steps)) {
    add({
      code: 'unreachable-step',
      stepKey: key,
      message: `${problemStepName(
        steps.findIndex((step) => step.key === key),
        steps.find((step) => step.key === key)!,
        agents ?? [],
        displayNumbers
      )} can't be reached from Start.`,
      severity: 'error',
    });
  }
  return { byStepKey, workflowProblems, hints };
}

function problemStepName(
  index: number,
  step: WorkflowStepDraft,
  agents: Array<{ id: string; name: string }>,
  displayNumbers = workflowDisplayNumbers([step])
): string {
  const agentName =
    agents.find((agent) => agent.id === step.agentId)?.name ??
    (step.agentId ? 'Removed agent' : 'No agent assigned');
  return `Step ${displayNumbers.get(step.key) ?? index + 1} (${agentName})`;
}

function problemFieldName(
  name: string,
  inputFields: WorkflowFieldDraft[],
  steps: WorkflowStepDraft[]
): string {
  const field = [...inputFields, ...steps.flatMap((step) => step.writes)].find(
    (candidate) => candidate.name === name
  );
  if (field) return fieldDisplayName(field);
  for (const step of steps) {
    const handoff = step.handoffs.find((candidate) => candidate.to === name);
    const source = handoff && step.writes.find((candidate) => candidate.name === handoff.from);
    if (source) return fieldDisplayName(source);
  }
  return fieldDisplayName({ name });
}

function describeFieldProblemsLocal(fields: WorkflowFieldDraft[], label: string): string | null {
  const seen = new Set<string>();
  for (const field of fields) {
    const name = field.name.trim();
    const displayName = fieldDisplayName(field);
    if (!name || (field.label !== undefined && !field.label.trim()))
      return 'Add a name for this field.';
    if (field.enumValues !== undefined && field.enumValues.length === 0)
      return 'Add at least one answer.';
    if (
      !/^[A-Za-z_][A-Za-z0-9_]*$/.test(name) ||
      ['__proto__', 'prototype', 'constructor'].includes(name)
    ) {
      return `${displayName} is not a valid ${label} name.`;
    }
    if (seen.has(name)) return `${displayName} is listed twice.`;
    seen.add(name);
  }
  return null;
}
