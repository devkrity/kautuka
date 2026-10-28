import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const distRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../dist/assets');
const forbiddenPatterns = [
  /@copilotkit\//i,
  /copilotkit-web-components/i,
  /@assistant-ui\//i,
  /assistant-ui/i,
  /@kautuka\/ag-ui/i,
  /@ag-ui\//i,
  /ResumeEntry/i,
  /waitForCanonicalResolution/i,
  /GatewayRuntimeEvent/i,
];

function collectAssetFiles(directory: string): string[] {
  if (!fs.existsSync(directory)) {
    return [];
  }

  const files: string[] = [];
  for (const entry of fs.readdirSync(directory)) {
    const absolute = path.join(directory, entry);
    if (fs.statSync(absolute).isDirectory()) {
      files.push(...collectAssetFiles(absolute));
      continue;
    }
    if (/\.(js|css|map)$/i.test(entry)) {
      files.push(absolute);
    }
  }
  return files;
}

describe('production bundle exclusions', () => {
  it('does not ship CopilotKit, assistant-ui, or legacy AG-UI in built app chunks', () => {
    const assets = collectAssetFiles(distRoot);
    expect(assets.length).toBeGreaterThan(0);

    const violations: string[] = [];
    for (const assetPath of assets) {
      const contents = fs.readFileSync(assetPath, 'utf8');
      for (const pattern of forbiddenPatterns) {
        if (pattern.test(contents)) {
          violations.push(`${path.basename(assetPath)} matched ${pattern}`);
        }
      }
    }

    expect(violations).toEqual([]);
  });
});
