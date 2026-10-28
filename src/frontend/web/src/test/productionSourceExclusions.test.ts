import { readFileSync, readdirSync, statSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const srcRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const forbiddenPatterns = [
  /@copilotkit\//,
  /@assistant-ui\//,
  /from ['"]@copilotkit/,
  /from ['"]@assistant-ui/,
  /@kautuka\/ag-ui/,
  /from ['"]@kautuka\/ag-ui/,
  /@ag-ui\//,
  /from ['"]@ag-ui/,
  /ResumeEntry/,
  /waitForCanonicalResolution/,
  /canonicalResumeEntries/,
  /GatewayRuntimeEvent/,
  /createInterruptPresentationCache/,
  /useThreadRevisionSync/,
];
const forbiddenLegacyPatterns = [
  /CopilotBaselinePage/,
  /CanonicalThreadResync/,
  /DefaultToolRenderer/,
  /CopilotDefaultToolUi/,
  /connectCanonicalThread/,
  /stopAgentRuntimeRun/,
  /agentRuntimeControl/,
  /toolTerminalEnvelope/,
  /\/runs\/\$\{encodeURIComponent\([^)]+\)\}\/stop/,
];

function collectProductionSourceFiles(directory: string): string[] {
  const files: string[] = [];
  for (const entry of readdirSync(directory)) {
    const absolute = path.join(directory, entry);
    if (statSync(absolute).isDirectory()) {
      files.push(...collectProductionSourceFiles(absolute));
      continue;
    }
    if (/\.(ts|tsx)$/.test(entry) && !/\.test\.(ts|tsx)$/.test(entry)) {
      files.push(absolute);
    }
  }
  return files;
}

describe('production source exclusions', () => {
  it('contains no CopilotKit, assistant-ui, or legacy AG-UI imports under web/src', () => {
    const violations: string[] = [];
    for (const filePath of collectProductionSourceFiles(srcRoot)) {
      const contents = readFileSync(filePath, 'utf8');
      for (const pattern of forbiddenPatterns) {
        if (pattern.test(contents)) {
          violations.push(`${path.relative(srcRoot, filePath)} matched ${pattern}`);
        }
      }
    }
    expect(violations).toEqual([]);
  });

  it('contains no unrouted legacy chat compatibility modules', () => {
    const violations: string[] = [];
    for (const filePath of collectProductionSourceFiles(srcRoot)) {
      const contents = readFileSync(filePath, 'utf8');
      for (const pattern of forbiddenLegacyPatterns) {
        if (pattern.test(contents)) {
          violations.push(`${path.relative(srcRoot, filePath)} matched ${pattern}`);
        }
      }
    }
    expect(violations).toEqual([]);
  });
});
