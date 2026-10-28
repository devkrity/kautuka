import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const webRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const workspaceRoot = path.resolve(webRoot, '..');
const packageJson = JSON.parse(
  fs.readFileSync(path.join(webRoot, 'package.json'), 'utf8')
) as {
  dependencies?: Record<string, string>;
  devDependencies?: Record<string, string>;
};

const forbiddenDependencyPatterns = [
  /^@copilotkit\//,
  /^@assistant-ui\//,
  /^assistant-ui$/,
  /^@kautuka\/ag-ui-/,
  /^@ag-ui\//,
];

function collectDependencyNames(record: Record<string, string> | undefined): string[] {
  return record ? Object.keys(record) : [];
}

function extractWorkspaceWebImporter(lockContents: string): string {
  const match = lockContents.match(/^  web:\n([\s\S]*?)(?=^  [^\s]|\Z)/m);
  return match?.[1] ?? '';
}

describe('production web dependency exclusions', () => {
  it('does not declare CopilotKit, assistant-ui, or legacy AG-UI packages', () => {
    const names = [
      ...collectDependencyNames(packageJson.dependencies),
      ...collectDependencyNames(packageJson.devDependencies),
    ];

    const violations = names.filter((name) =>
      forbiddenDependencyPatterns.some((pattern) => pattern.test(name))
    );

    expect(violations).toEqual([]);
  });

  it('does not lock CopilotKit, assistant-ui, or legacy AG-UI for the web workspace package', () => {
    const lockPath = path.join(workspaceRoot, 'pnpm-lock.yaml');
    expect(fs.existsSync(lockPath)).toBe(true);

    const webImporter = extractWorkspaceWebImporter(fs.readFileSync(lockPath, 'utf8'));
    expect(webImporter.length).toBeGreaterThan(0);

    const lockViolations = forbiddenDependencyPatterns.filter((pattern) =>
      webImporter.split('\n').some((line) => pattern.test(line.trim()))
    );

    expect(lockViolations).toEqual([]);
  });
});
