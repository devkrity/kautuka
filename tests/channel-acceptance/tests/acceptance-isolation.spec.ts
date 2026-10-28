import { join, resolve } from 'node:path';

import { expect, test } from '@playwright/test';
import { createAcceptanceWorkspace } from '../src/executor.js';
import { ownedWorkspacePath } from '../src/workspace-safety.js';
import { fixtureConfiguration } from '../src/public-fixture-bootstrap.js';

test('normal acceptance keeps Kautuka and simulator state inside one owned workspace', () => {
  const root = resolve('/acceptance');
  const workspace = createAcceptanceWorkspace(root, 'run-123');
  expect(workspace).toEqual({ id: 'run-123', stateRoot: join(root, 'run-123', 'state') });
});

test('only an exact child of the acceptance root can become a cleanup workspace', () => {
  const root = resolve('/acceptance');
  expect(ownedWorkspacePath(root, 'run-123')).toBe(join(root, 'run-123'));
  expect(() => ownedWorkspacePath(root, '../outside')).toThrow(/safe path segment/);
});

test('public fixture configuration is explicit and has no developer-state dependency', () => {
  expect(fixtureConfiguration({
    KAUTUKA_ACCEPTANCE_PROVIDER_BASE_URL: 'http://127.0.0.1:11434', KAUTUKA_ACCEPTANCE_MODEL_NAME: 'local-model',
  })).toEqual({ providerBaseUrl: 'http://127.0.0.1:11434', modelName: 'local-model' });
  expect(() => fixtureConfiguration({})).toThrow(/KAUTUKA_ACCEPTANCE_MODEL_NAME/);
});
