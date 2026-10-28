import { describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import {
  createChoiceDraft,
  createFieldDraft,
  createHandoffDraft,
  createStepDraft,
  describeDraftProblems,
  type WorkflowStepDraft,
} from '@/features/workflows/model/workflowDraft';
import {
  addChoice,
  addChoiceRule,
  addPickListAnswer,
  addStepOn,
  connectPathTo,
  describeStepProblems,
  disconnectPath,
  draftToGraph,
  effectiveTransitions,
  extendPath,
  findLoop,
  findUnreachable,
  guaranteedFields,
  joinCandidates,
  moveRule,
  planDeleteStep,
  planRemoveChoiceRule,
  replaceAgent,
  workflowDisplayNumbers,
  writeTransitions,
} from '@/features/workflows/model/workflowCanvas';

const inputs = [createFieldDraft('amount', 'number', true)];

function step(key: string, agentId = key): WorkflowStepDraft {
  const value = createStepDraft(agentId, `Do ${agentId}`);
  return { ...value, key };
}

function rule(key: string, field: string, op: string, value: string, goToKey: string) {
  return { ...createChoiceDraft(field, goToKey), key, op, value };
}

function semantics(steps: WorkflowStepDraft[]) {
  return effectiveTransitions(steps).map(({ source, target, kind, ruleKey }) => ({
    source,
    target,
    kind,
    ruleKey,
  }));
}

describe('workflow canvas transition model', () => {
  it('numbers steps by flow layer and check order without reordering saved steps', () => {
    const root = step('root');
    root.choices = [
      rule('first-check', 'amount', 'gt', '100', 'first-branch'),
      rule('second-check', 'amount', 'lt', '0', 'second-branch'),
    ];
    root.otherwiseKey = 'otherwise-branch';
    const firstBranch = step('first-branch');
    firstBranch.otherwiseKey = 'first-leaf';
    const secondBranch = step('second-branch');
    secondBranch.otherwiseKey = 'second-leaf';
    const otherwiseBranch = step('otherwise-branch');
    otherwiseBranch.otherwiseKey = 'join';
    const firstLeaf = step('first-leaf');
    firstLeaf.otherwiseKey = 'join';
    const secondLeaf = step('second-leaf');
    secondLeaf.otherwiseKey = 'join';
    const savedSteps = [
      root,
      firstLeaf,
      secondLeaf,
      otherwiseBranch,
      step('join'),
      secondBranch,
      firstBranch,
    ];

    const displayNumbers = workflowDisplayNumbers(savedSteps);
    const graph = draftToGraph(savedSteps);

    expect([...displayNumbers.entries()]).toEqual([
      ['root', 1],
      ['first-branch', 2],
      ['second-branch', 3],
      ['otherwise-branch', 4],
      ['first-leaf', 5],
      ['second-leaf', 6],
      ['join', 7],
    ]);
    expect(graph.nodes.map((node) => node.key)).toEqual(savedSteps.map((item) => item.key));
    expect(graph.nodes.find((node) => node.key === 'first-branch')?.displayName).toMatch(
      /^Step 2 ·/
    );
    expect(graph.nodes.find((node) => node.key === 'join')?.displayName).toMatch(/^Step 7 ·/);
  });

  it('adds an explicitly chosen answer to a pick-list result immutably', () => {
    const producer = step('producer');
    const pickList = { ...createFieldDraft('category'), enumValues: ['Refund', 'Complaint'] };
    producer.writes = [pickList];
    producer.handoffs = [{ key: 'map', from: 'category', to: 'case_type' }];
    const original = [producer, step('consumer')];

    const result = addPickListAnswer(original, [], 'producer', 'case_type', 'Other');

    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.steps[0]!.writes[0]!.enumValues).toEqual(['Refund', 'Complaint', 'Other']);
    expect(original[0]!.writes[0]!.enumValues).toEqual(['Refund', 'Complaint']);
  });

  it('adds a chosen answer to a start pick-list and refuses duplicates', () => {
    const startField = { ...createFieldDraft('decision'), enumValues: ['Proceed', 'Decline'] };
    const inputs = [startField];

    const result = addPickListAnswer([], inputs, null, 'decision', 'Refer');
    const duplicate = addPickListAnswer([], inputs, null, 'decision', 'proceed');

    expect(result.ok && result.inputFields[0]!.enumValues).toEqual(['Proceed', 'Decline', 'Refer']);
    expect(duplicate).toEqual({ ok: false, reason: 'That answer is already in the list.' });
    expect(inputs[0]!.enumValues).toEqual(['Proceed', 'Decline']);
  });

  it('matches the shared Compose transition parity fixtures', () => {
    const fixtures = JSON.parse(
      readFileSync(
        resolve(process.cwd(), '../../tests/Fixtures/workflow-transition-parity.json'),
        'utf8'
      )
    ) as Array<{
      name: string;
      nodes: Array<{
        nodeId: string;
        switchCases?: Array<{ pointer: string; op: string; value: string | null; goTo: string }>;
        defaultGoTo?: string;
      }>;
      transitions: Array<{ source: string; target: string }>;
    }>;
    for (const fixture of fixtures) {
      const steps = fixture.nodes.map((node) => {
        const base = step(node.nodeId);
        return {
          ...base,
          choices: (node.switchCases ?? []).map((item, index) =>
            rule(`r${index}`, item.pointer.slice(1), item.op, item.value ?? '', item.goTo)
          ),
          otherwiseKey: node.defaultGoTo ?? '',
        };
      });
      expect(
        effectiveTransitions(steps).map(({ source, target }) => ({ source, target })),
        fixture.name
      ).toEqual(fixture.transitions);
    }
  });

  it('derives fall-through with targeted cuts and explicit continuations', () => {
    const steps = [step('start'), step('file'), step('ask', 'manager')];
    steps[0] = {
      ...steps[0]!,
      choices: [rule('r1', 'amount', 'gt', '500', 'ask')],
      otherwiseKey: 'file',
    };
    steps[2] = { ...steps[2]!, otherwiseKey: 'file' };
    expect(semantics(steps)).toEqual([
      { source: 'start', target: 'ask', kind: 'rule', ruleKey: 'r1' },
      { source: 'start', target: 'file', kind: 'otherwise', ruleKey: undefined },
      { source: 'ask', target: 'file', kind: 'continue', ruleKey: undefined },
    ]);
    expect(findUnreachable(steps)).toEqual([]);
    expect(findLoop(steps)).toBeNull();
  });

  it('round trips a chain and rejects an unrepresentable terminal', () => {
    const chain = [step('a'), step('b'), step('c')];
    const before = semantics(chain);
    const written = writeTransitions(chain, effectiveTransitions(chain));
    expect(written).toEqual({ ok: true, steps: chain });
    expect(written.ok && semantics(written.steps)).toEqual(before);

    const terminalBeforeNext = [step('a'), step('b'), step('c')];
    const desired = effectiveTransitions(terminalBeforeNext).filter(
      (transition) => transition.source !== 'a'
    );
    const rejected = writeTransitions(terminalBeforeNext, desired);
    expect(rejected).toMatchObject({
      ok: false,
      reason: expect.stringContaining('carry on into Step 2'),
    });
    expect(semantics(terminalBeforeNext)).toEqual(before);
  });

  it('adds a step on a choice branch without changing its sibling route', () => {
    const original = [step('review'), step('auto'), step('ask')];
    original[0] = {
      ...original[0]!,
      choices: [rule('r', 'amount', 'gt', '500', 'ask')],
      otherwiseKey: 'auto',
    };
    const before = semantics(original);
    const result = addStepOn(
      original,
      { kind: 'rule', stepKey: 'review', ruleKey: 'r' },
      'new-agent'
    );
    expect(result.ok, result.ok ? undefined : result.reason).toBe(true);
    if (!result.ok) return;
    const added = result.steps.find((candidate) => candidate.agentId === 'new-agent')!;
    expect(result.steps[0]!.choices[0]).toMatchObject({ key: 'r', goToKey: added.key });
    expect(result.steps[0]!.otherwiseKey).toBe('auto');
    expect(
      effectiveTransitions(result.steps).filter(
        (transition) => transition.source === 'review' && transition.kind === 'otherwise'
      )
    ).toEqual(
      effectiveTransitions(original).filter(
        (transition) => transition.source === 'review' && transition.kind === 'otherwise'
      )
    );
    expect(effectiveTransitions(result.steps)).toContainEqual({
      source: added.key,
      target: 'ask',
      kind: 'continue',
    });
    expect(
      before.filter((transition) => transition.source !== 'review' && transition.source !== 'ask')
    ).toEqual(
      semantics(result.steps).filter(
        (transition) => transition.source !== 'review' && transition.source !== added.key
      )
    );
  });

  it('adds an incomplete choice without inventing its condition and blocks Save', () => {
    const original = [step('start'), step('end')];
    const result = addChoice(original, 'start', 'if-agent');
    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.steps[0]!.choices[0]).toMatchObject({ field: '', value: '' });
    expect(result.steps[0]!.otherwiseKey).toBe('end');
    expect(
      describeStepProblems(result.steps, inputs, undefined, false, true).byStepKey.start?.[0]
        ?.message
    ).toContain('complete check');
    expect(addChoice(result.steps, 'start', 'again')).toMatchObject({ ok: false });
  });

  it('adds a new check with an empty agent without making the last existing branch carry on', () => {
    const choice = addChoice([step('start')], 'start', '', '');
    expect(choice.ok).toBe(true);
    if (!choice.ok) return;

    const result = addChoiceRule(choice.steps, 'start', '');
    expect(result.ok, result.ok ? undefined : result.reason).toBe(true);
    if (!result.ok) return;
    expect(result.steps[0]!.choices).toHaveLength(2);
    expect(result.steps[0]!.choices[1]!.field).toBe('');
    expect(result.steps.slice(0, choice.steps.length).map((item) => item.key)).toEqual(
      choice.steps.map((item) => item.key)
    );
    const created = result.steps[result.steps.length - 1]!;
    expect(created.agentId).toBe('');
    expect(effectiveTransitions(result.steps)).toContainEqual({
      source: 'start',
      target: created.key,
      kind: 'rule',
      ruleKey: result.steps[0]!.choices[1]!.key,
    });
  });

  it('permits only terminal paths to join without introducing loops', () => {
    const chain = [step('start'), step('a'), step('b'), step('c')];
    expect(connectPathTo(chain, 'a', 'c')).toMatchObject({ ok: false });
    expect(findUnreachable(chain)).toEqual([]);

    const branch = [step('start'), step('path-a'), step('path-b'), step('join')];
    branch[0] = {
      ...branch[0]!,
      choices: [rule('r', 'amount', 'gt', '1', 'path-a')],
      otherwiseKey: 'path-b',
    };
    branch[1] = { ...branch[1]!, otherwiseKey: 'join' };
    expect(joinCandidates(branch, 'path-b')).toContain('join');
    const joined = connectPathTo(branch, 'path-b', 'join');
    expect(joined.ok).toBe(true);
    expect(
      joined.ok &&
        effectiveTransitions(joined.steps).filter((transition) => transition.target === 'join')
    ).toHaveLength(2);
  });

  it('rejects stopping when that would orphan the only continuation route', () => {
    const chain = [step('start'), step('middle'), step('other-route'), step('end')];
    chain[1] = { ...chain[1]!, otherwiseKey: 'end' };
    const before = semantics(chain);
    expect(disconnectPath(chain, 'middle')).toMatchObject({
      ok: false,
      reason: expect.stringContaining('no longer be reached'),
    });
    expect(semantics(chain)).toEqual(before);
  });

  it('removes only a selected rule and keeps shared targets and rule identity', () => {
    const steps = [step('start'), step('shared')];
    steps[0] = {
      ...steps[0]!,
      choices: [
        rule('r1', 'amount', 'gt', '100', 'shared'),
        rule('r2', 'amount', 'gt', '500', 'shared'),
      ],
      otherwiseKey: 'shared',
    };
    const result = planRemoveChoiceRule(steps, 'start', 'r1');
    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.steps.map((candidate) => candidate.key)).toEqual(['start', 'shared']);
    expect(result.steps[0]!.choices).toEqual([steps[0]!.choices[1]]);
    expect(result.steps[0]!.otherwiseKey).toBe('shared');
  });

  it('reports guaranteed fields at a join by intersecting incoming paths', () => {
    const steps = [step('start'), step('a'), step('b'), step('join')];
    steps[0] = { ...steps[0]!, choices: [rule('r', 'amount', 'gt', '1', 'a')], otherwiseKey: 'b' };
    steps[1] = {
      ...steps[1]!,
      handoffs: [createHandoffDraft('title', 'sharedField'), createHandoffDraft('title', 'aOnly')],
    };
    steps[2] = { ...steps[2]!, handoffs: [createHandoffDraft('title', 'sharedField')] };
    expect(guaranteedFields(steps, inputs, 'join')).toContain('sharedField');
    expect(guaranteedFields(steps, inputs, 'join')).not.toContain('aOnly');
    const graph = draftToGraph(steps, inputs, [{ id: 'start', name: 'Accounts assistant' }]);
    expect(graph.connections).toEqual(effectiveTransitions(steps));
    expect(graph.nodes[0]).toMatchObject({
      displayName: 'Step 1 · Accounts assistant',
      instructionPreview: 'Do start',
    });
    expect(graph.startFields).toEqual(['amount']);
  });

  it('uses step and field labels in short missing-path problem messages', () => {
    const steps = [
      step('check'),
      step('producer', 'producer'),
      step('other'),
      step('filing', 'filer'),
    ];
    const status = { ...createFieldDraft('status'), label: 'Invoice status' };
    steps[0] = {
      ...steps[0]!,
      choices: [rule('go-producer', 'amount', 'gt', '500', 'producer')],
      otherwiseKey: 'other',
    };
    steps[1] = { ...steps[1]!, writes: [status], otherwiseKey: 'filing' };
    steps[2] = { ...steps[2]!, otherwiseKey: 'filing' };
    steps[3] = { ...steps[3]!, choices: [rule('check-status', 'status', 'eq', 'ready', 'check')] };

    const report = describeStepProblems(steps, inputs, [
      { id: 'filer', name: 'Filing clerk' },
      { id: 'producer', name: 'Accounts assistant' },
    ]);

    expect(report.byStepKey.filing?.map((problem) => problem.message)).toContain(
      'Step 4 (Filing clerk) uses Invoice status, but not every path into it provides it.'
    );
  });

  it('uses current field labels in instruction previews and flags a cleared result label', () => {
    const field = { ...createFieldDraft('amount'), label: 'Total due' };
    const current = step('current', 'filing');
    current.promptTemplate = 'Approve paying {{amount}}';
    current.writes = [{ ...createFieldDraft('approval'), label: '' }];
    const graph = draftToGraph(
      [step('one'), step('two'), current],
      [field],
      [
        { id: 'one', name: 'Agent one' },
        { id: 'two', name: 'Agent two' },
        { id: 'filing', name: 'Filing clerk' },
      ]
    );
    expect(graph.nodes[2]?.instructionPreview).toBe('Approve paying Total due');
    expect(graph.nodes[2]?.problems).toContainEqual(
      expect.objectContaining({ code: 'missing-field-label' })
    );
    expect(
      describeDraftProblems(
        'Test workflow',
        [step('one', 'one'), step('two', 'two'), current],
        [field],
        [
          { id: 'one', name: 'Agent one' },
          { id: 'two', name: 'Agent two' },
          { id: 'filing', name: 'Filing clerk' },
        ]
      )
    ).toBe('Step 3 (Filing clerk): add a name for this field.');
  });

  it('shows a complete sentence in the box preview without cutting off the instruction', () => {
    const instruction = 'Check the amount and due date. Approve the payment after review.';
    const review = step('review', 'filing');
    review.promptTemplate = instruction;
    const previewGraph = draftToGraph([review], [], [{ id: 'filing', name: 'Filing clerk' }]);

    expect(previewGraph.nodes[0]?.instructionPreview).toBe('Check the amount and due date.');
    expect(previewGraph.nodes[0]?.instructionPreview).not.toContain('…');
    expect(previewGraph.nodes[0]?.step.promptTemplate).toBe(instruction);
  });

  it('handles legacy loops and unreachable steps without changing them', () => {
    const steps = [step('start'), step('back'), step('orphan')];
    steps[1] = { ...steps[1]!, otherwiseKey: 'start' };
    expect(findLoop(steps)).toBe('back');
    expect(findUnreachable(steps)).toContain('orphan');
    expect(() => guaranteedFields(steps, inputs, 'orphan')).not.toThrow();
    expect(replaceAgent(steps, 'back', 'new-agent').ok).toBe(true);
    expect(steps.map((candidate) => candidate.key)).toEqual(['start', 'back', 'orphan']);
  });

  it('moves only evaluation order and preserves every rule condition and destination', () => {
    const steps = [step('start'), step('a'), step('b'), step('otherwise')];
    steps[0] = {
      ...steps[0]!,
      choices: [rule('r1', 'amount', 'gt', '100', 'a'), rule('r2', 'amount', 'gt', '500', 'b')],
      otherwiseKey: 'otherwise',
    };
    const moved = moveRule(steps, 'start', 'r2', 'up');
    expect(moved.ok).toBe(true);
    if (!moved.ok) return;
    expect(moved.steps[0]!.choices).toEqual([steps[0]!.choices[1], steps[0]!.choices[0]]);
    expect(
      semantics(moved.steps).map(({ source, target, kind, ruleKey }) => ({
        source,
        target,
        kind,
        ruleKey,
      }))
    ).toEqual(
      semantics(steps)
        .filter((transition) => transition.source !== 'start')
        .concat([
          { source: 'start', target: 'b', kind: 'rule', ruleKey: 'r2' },
          { source: 'start', target: 'a', kind: 'rule', ruleKey: 'r1' },
          { source: 'start', target: 'otherwise', kind: 'otherwise', ruleKey: undefined },
        ])
    );
  });

  it('plans deletion of a choice branch while preserving a shared downstream tail', () => {
    const steps = [step('review'), step('if-only'), step('auto'), step('file'), step('email')];
    steps[0] = {
      ...steps[0]!,
      choices: [rule('r', 'amount', 'gt', '500', 'if-only')],
      otherwiseKey: 'auto',
    };
    steps[1] = { ...steps[1]!, otherwiseKey: 'file' };
    steps[2] = { ...steps[2]!, otherwiseKey: 'file' };
    steps[3] = { ...steps[3]!, otherwiseKey: 'email' };
    const before = semantics(steps);
    expect(before).toEqual([
      { source: 'review', target: 'if-only', kind: 'rule', ruleKey: 'r' },
      { source: 'review', target: 'auto', kind: 'otherwise', ruleKey: undefined },
      { source: 'if-only', target: 'file', kind: 'continue', ruleKey: undefined },
      { source: 'auto', target: 'file', kind: 'continue', ruleKey: undefined },
      { source: 'file', target: 'email', kind: 'continue', ruleKey: undefined },
    ]);
    const result = planDeleteStep(steps, 'if-only');
    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.removedStepKeys).toEqual(['if-only']);
    expect(result.steps.map((candidate) => candidate.key)).toEqual([
      'review',
      'auto',
      'file',
      'email',
    ]);
    expect(semantics(result.steps)).toEqual([
      { source: 'review', target: 'file', kind: 'rule', ruleKey: 'r' },
      { source: 'review', target: 'auto', kind: 'otherwise', ruleKey: undefined },
      { source: 'auto', target: 'file', kind: 'continue', ruleKey: undefined },
      { source: 'file', target: 'email', kind: 'next', ruleKey: undefined },
    ]);
    expect(result.confirmation).toContain('Delete Step 2 (Agent removed)');
  });

  it('extends a terminal path with a plain fall-through when adjacent', () => {
    const original = [step('start'), step('end')];
    const extended = extendPath(original, 'end', 'manager');
    expect(extended.ok).toBe(true);
    if (!extended.ok) return;
    expect(effectiveTransitions(extended.steps)).toContainEqual({
      source: 'end',
      target: extended.steps[2]!.key,
      kind: 'next',
    });
    expect(extended.steps[1]!.otherwiseKey).toBe('');
  });

  it('rejects deleting the only Otherwise step and gives the plain reason', () => {
    const steps = [step('check'), step('if-path'), step('otherwise')];
    steps[0] = {
      ...steps[0]!,
      choices: [rule('r', 'amount', 'gt', '500', 'if-path')],
      otherwiseKey: 'otherwise',
    };
    expect(planDeleteStep(steps, 'otherwise')).toEqual({
      ok: false,
      reason:
        'A choice always needs an Otherwise path. Add another step to this path first, or remove the other paths to undo the choice.',
    });
    expect(steps[0]!.otherwiseKey).toBe('otherwise');
  });

  it('rejects disconnecting a continuation when it would enable a fall-through', () => {
    const steps = [step('check'), step('a'), step('b'), step('c')];
    steps[0] = {
      ...steps[0]!,
      choices: [rule('r', 'amount', 'gt', '5', 'a')],
      otherwiseKey: 'c',
    };
    steps[1] = { ...steps[1]!, otherwiseKey: 'c' };
    const before = semantics(steps);
    expect(disconnectPath(steps, 'a')).toMatchObject({
      ok: false,
      reason: expect.stringContaining('carry on into Step 4'),
    });
    expect(semantics(steps)).toEqual(before);
  });

  it('leaves the draft unchanged when a real edit would create an unrepresentable terminal', () => {
    const steps = [step('check'), step('selected'), step('neighbor'), step('tail')];
    steps[0] = {
      ...steps[0]!,
      choices: [rule('r', 'amount', 'gt', '5', 'selected')],
      otherwiseKey: 'tail',
    };
    steps[1] = { ...steps[1]!, otherwiseKey: 'tail' };
    const before = structuredClone(steps);
    expect(disconnectPath(steps, 'selected')).toEqual({
      ok: false,
      reason: 'This step would carry on into Step 4. Choose where it continues instead.',
    });
    expect(steps).toEqual(before);
  });

  it('deletes the first step and preserves the new start, both for a diamond and legacy orphan', () => {
    const diamond = [step('check'), step('ask'), step('auto'), step('file'), step('email')];
    diamond[0] = {
      ...diamond[0]!,
      choices: [rule('r', 'amount', 'gt', '500', 'ask')],
      otherwiseKey: 'auto',
    };
    diamond[1] = { ...diamond[1]!, otherwiseKey: 'file' };
    diamond[2] = { ...diamond[2]!, otherwiseKey: 'file' };
    diamond[3] = { ...diamond[3]!, otherwiseKey: 'email' };
    const deletedCheck = planDeleteStep(diamond, 'check');
    expect(deletedCheck.ok).toBe(true);
    if (deletedCheck.ok) {
      expect(deletedCheck.removedStepKeys).toEqual(['check', 'ask']);
      expect(deletedCheck.steps.map(({ key }) => key)).toEqual(['auto', 'file', 'email']);
      expect(semantics(deletedCheck.steps)).toEqual([
        { source: 'auto', target: 'file', kind: 'next', ruleKey: undefined },
        { source: 'file', target: 'email', kind: 'next', ruleKey: undefined },
      ]);
    }

    const legacy = [step('a'), step('b'), step('c')];
    legacy[0] = { ...legacy[0]!, otherwiseKey: 'c' };
    legacy[1] = { ...legacy[1]!, otherwiseKey: 'c' };
    const deletedA = planDeleteStep(legacy, 'a');
    expect(deletedA).toEqual({
      ok: false,
      reason: 'Remove the unreachable step first, then delete this one.',
    });
    expect(semantics(legacy)).toEqual([
      { source: 'a', target: 'c', kind: 'continue', ruleKey: undefined },
      { source: 'b', target: 'c', kind: 'continue', ruleKey: undefined },
    ]);
  });

  it('cleans up steps unreachable from a deleted first choice before encoding fall-through', () => {
    const steps = [step('A'), step('B'), step('C')];
    steps[0] = {
      ...steps[0]!,
      choices: [rule('r', 'amount', 'gt', '500', 'B')],
      otherwiseKey: 'C',
    };
    const before = semantics(steps);
    expect(before).toEqual([
      { source: 'A', target: 'B', kind: 'rule', ruleKey: 'r' },
      { source: 'A', target: 'C', kind: 'otherwise', ruleKey: undefined },
    ]);

    const deleted = planDeleteStep(steps, 'A');
    expect(deleted.ok).toBe(true);
    if (deleted.ok) {
      expect(deleted.removedStepKeys).toEqual(['A', 'B']);
      expect(deleted.steps.map(({ key }) => key)).toEqual(['C']);
      expect(deleted.confirmation).toContain('Step 2 (Removed agent)');
      expect(semantics(deleted.steps)).toEqual([]);
    }

    const added = addChoice([step('X')], 'X', 'if-agent', 'else-agent');
    expect(added.ok).toBe(true);
    if (added.ok) {
      const ruleTarget = added.steps[0]!.choices[0]!.goToKey;
      const otherwiseTarget = added.steps[0]!.otherwiseKey;
      const choiceBefore = semantics(added.steps);
      expect(choiceBefore).toEqual([
        { source: 'X', target: ruleTarget, kind: 'rule', ruleKey: added.steps[0]!.choices[0]!.key },
        { source: 'X', target: otherwiseTarget, kind: 'otherwise', ruleKey: undefined },
      ]);
      const choiceDeleted = planDeleteStep(added.steps, 'X');
      expect(choiceDeleted.ok).toBe(true);
      if (choiceDeleted.ok) {
        expect(choiceDeleted.removedStepKeys).toEqual(['X', ruleTarget]);
        expect(choiceDeleted.steps.map(({ key }) => key)).toEqual([otherwiseTarget]);
        expect(choiceDeleted.confirmation).toContain('Step 2 (Removed agent)');
        expect(semantics(choiceDeleted.steps)).toEqual([]);
      }
    }

    const withContinuation = [step('A'), step('B'), step('C'), step('B2')];
    withContinuation[0] = {
      ...withContinuation[0]!,
      choices: [rule('r', 'amount', 'gt', '500', 'B')],
      otherwiseKey: 'C',
    };
    withContinuation[1] = { ...withContinuation[1]!, otherwiseKey: 'B2' };
    const continuationBefore = semantics(withContinuation);
    expect(continuationBefore).toEqual([
      { source: 'A', target: 'B', kind: 'rule', ruleKey: 'r' },
      { source: 'A', target: 'C', kind: 'otherwise', ruleKey: undefined },
      { source: 'B', target: 'B2', kind: 'continue', ruleKey: undefined },
    ]);
    const continuationDeleted = planDeleteStep(withContinuation, 'A');
    expect(continuationDeleted.ok).toBe(true);
    if (continuationDeleted.ok) {
      expect(continuationDeleted.removedStepKeys).toEqual(['A', 'B', 'B2']);
      expect(continuationDeleted.steps.map(({ key }) => key)).toEqual(['C']);
      expect(semantics(continuationDeleted.steps)).toEqual([]);
    }
  });

  it('turns Otherwise into a continuation when deleting a choice’s only If target', () => {
    const steps = [step('check'), step('otherwise'), step('if-only')];
    steps[0] = {
      ...steps[0]!,
      choices: [rule('r', 'amount', 'gt', '500', 'if-only')],
      otherwiseKey: 'otherwise',
    };
    const result = planDeleteStep(steps, 'if-only');
    expect(result.ok).toBe(true);
    if (result.ok) {
      expect(result.steps[0]!.choices).toEqual([]);
      expect(result.steps[0]!.otherwiseKey).toBe('');
      expect(effectiveTransitions(result.steps)[0]).toMatchObject({
        source: 'check',
        target: 'otherwise',
        kind: 'next',
      });
    }
  });

  it('counts only paths actually repointed and uses agent names in the confirmation', () => {
    const steps = [
      step('check', 'agent-check'),
      step('ask', 'agent-ask'),
      step('auto', 'agent-auto'),
      step('file', 'agent-file'),
    ];
    steps[0] = {
      ...steps[0]!,
      choices: [rule('r', 'amount', 'gt', '500', 'ask')],
      otherwiseKey: 'auto',
    };
    steps[1] = { ...steps[1]!, otherwiseKey: 'file' };
    steps[2] = { ...steps[2]!, otherwiseKey: 'file' };
    const result = planDeleteStep(steps, 'ask', [
      { id: 'agent-check', name: 'Reviewer' },
      { id: 'agent-ask', name: 'Manager' },
      { id: 'agent-auto', name: 'Approver' },
      { id: 'agent-file', name: 'Bookkeeper' },
    ]);
    expect(result.ok).toBe(true);
    if (result.ok) {
      expect(result.confirmation).toBe(
        'Delete Step 2 (Manager); 1 path will continue to Step 4 (Bookkeeper)?'
      );
    }
  });

  it('has no incomplete-rule problem for a valid is-set rule', () => {
    const steps = [step('check'), step('done')];
    steps[0] = {
      ...steps[0]!,
      choices: [rule('r', 'amount', 'exists', '', 'done')],
      otherwiseKey: 'done',
    };
    expect(describeStepProblems(steps, inputs).byStepKey.check ?? []).not.toContainEqual(
      expect.objectContaining({ message: expect.stringContaining("finish the 'If'") })
    );
  });

  it('blocks a step that mentions a field it cannot see yet', () => {
    const steps = [
      { ...step('first', 'roller'), promptTemplate: 'Read {{title}} and count {{sides}}.' },
    ];
    const agents = [{ id: 'roller', name: 'Roller' }];
    const report = describeStepProblems(steps, [createFieldDraft('sides')], agents, false, true);

    expect(report.byStepKey.first).toContainEqual({
      code: 'missing-mentioned-field',
      stepKey: 'first',
      message: "Step 1 (Roller) uses Title, which it doesn't have yet.",
      severity: 'error',
    });
    expect(describeDraftProblems('Count sides', steps, [createFieldDraft('sides')], agents)).toBe(
      "Step 1 (Roller) uses Title, which it doesn't have yet."
    );
  });

  it('does not let a step use its own results in its instruction', () => {
    const steps = [
      {
        ...step('first', 'roller'),
        promptTemplate: 'Read {{notes}}.',
        writes: [createFieldDraft('notes')],
      },
    ];
    const report = describeStepProblems(steps, [], [{ id: 'roller', name: 'Roller' }], false, true);
    expect(report.byStepKey.first).toContainEqual(
      expect.objectContaining({
        code: 'missing-mentioned-field',
        message: "Step 1 (Roller) uses Notes, which it doesn't have yet.",
      })
    );
  });

  it('names a governed step that has no result before saving', () => {
    const steps = [step('first'), step('second')];
    steps[0] = { ...steps[0]!, writes: [createFieldDraft('checked', 'boolean')] };
    steps[1] = { ...steps[1]!, writes: [] };
    const report = describeStepProblems(
      steps,
      [],
      [
        { id: 'first', name: 'Reviewer' },
        { id: 'second', name: 'Manager' },
      ],
      true,
      true
    );
    expect(report.byStepKey.second?.[0]).toEqual({
      code: 'missing-result',
      stepKey: 'second',
      message: 'Step 2 (Manager) needs at least one result to pass on.',
      severity: 'error',
    });
  });
});
