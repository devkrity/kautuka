import { expect, test } from '@playwright/test';
import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { acceptanceScenarios } from '../src/scenarios.js';
import { approvalActions, providers } from '../src/model.js';
import { approvalActionSelector } from '../src/approval-action-vocabulary.js';

const requiredNaradScenarioIds = [
  'Narad-001-CONVERSATION-STREAM-REPLAY',
  'Narad-002-INTERRUPT-PAUSE-RESUME',
  'Narad-003-NESTED-RUN-ORDERING',
  'Narad-004-RECONNECT-SEQUENCE',
  'Narad-005-GRAPH-HYDRATE',
  'Narad-006-PROGRESS-REPLAY',
  'Narad-007-WATCH-REVISION',
  'Narad-008-STOP-CASCADE',
  'Narad-009-CONTROL-SEPARATE',
] as const;

test('maps every typed acceptance action to the exact product action value', () => {
  expect(Object.fromEntries(approvalActions.map((action) => [action, approvalActionSelector(action)]))).toEqual({
    approve: '[data-approval-action="approve"]',
    deny: '[data-approval-action="deny"]',
    'stop-run': '[data-approval-action="stop_run"]',
  });
});

test('declares Narad-native runtime scenarios and provider HITL combinations', () => {
  for (const id of requiredNaradScenarioIds) {
    expect(acceptanceScenarios.some((scenario) => scenario.id === id), id).toBeTruthy();
  }
  for (const provider of providers) {
    expect(acceptanceScenarios.some((scenario) => scenario.id === `CRH-031-${provider.toUpperCase()}-SETUP`)).toBeTruthy();
    for (const action of approvalActions) {
      expect(acceptanceScenarios.some((scenario) => scenario.id === `CRH-033-${provider.toUpperCase()}-${action.toUpperCase()}`)).toBeTruthy();
    }
  }
  for (const surface of ['CHAT', 'APPROVALS'] as const) {
    for (const action of approvalActions) {
      const id = surface === 'CHAT' && action === 'approve' ? 'CRH-033-BROWSER-APPROVE' : `CRH-033-${surface}-${action.toUpperCase()}`;
      expect(acceptanceScenarios.some((scenario) => scenario.id === id)).toBeTruthy();
    }
  }
});

test('Narad reconnect/progress/watch scenarios invoke dedicated assertion helpers', async () => {
  const source = await readFile(resolve(process.cwd(), 'src/narad-scenarios.ts'), 'utf8');
  expect(source).toMatch(/Narad-004-RECONNECT-SEQUENCE[\s\S]*capture\.assertReconnectSequenceIntegrity\(\)/);
  expect(source).toMatch(/Narad-006-PROGRESS-REPLAY[\s\S]*capture\.assertProgressReplayPrivacy\(\)/);
  expect(source).toMatch(/Narad-007-WATCH-REVISION[\s\S]*capture\.assertWatchRevisionMonotonic\(\)/);
});

test('acceptance source does not reference legacy AG-UI runtime endpoints', async () => {
  const sourceRoot = resolve(process.cwd(), 'src');
  const files = (await import('node:fs/promises')).readdir(sourceRoot);
  const names = (await files).filter((name) => name.endsWith('.ts'));
  const forbidden = /agent-runtime\/agent\/|RUN_FINISHED|RUN_STARTED|ACTIVITY_DELTA|tool\.approval\.|run\.failed|agui-stream-capture/;
  for (const name of names) {
    const text = await readFile(resolve(sourceRoot, name), 'utf8');
    expect(text, name).not.toMatch(forbidden);
  }
});
