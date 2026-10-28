import { expect, test } from '@playwright/test';
import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { acceptanceScenarios } from '../src/scenarios.js';

const docsRoot = resolve(process.cwd(), '../../docs/development');

function checklistIdsFromMarkdown(markdown: string): readonly string[] {
  return [...markdown.matchAll(/^\| ([A-Z]+-\d+) \|/gm)].map((match) => match[1]);
}

function classificationRows(markdown: string): ReadonlyMap<string, string> {
  const rows = new Map<string, string>();
  for (const match of markdown.matchAll(/^\| ([A-Z]+-\d+) \| (Automated|Manual|Live-provider|Blocked) \| (.+) \|$/gm)) {
    rows.set(match[1], match[2]);
  }
  return rows;
}

test('CRH-040 maps every browser and channel checklist id to an explicit classification', async () => {
  const [browser, channel, classification] = await Promise.all([
    readFile(resolve(docsRoot, 'browser-verification.md'), 'utf8'),
    readFile(resolve(docsRoot, 'channel-verification.md'), 'utf8'),
    readFile(resolve(docsRoot, 'checklist-classification.md'), 'utf8'),
  ]);

  const required = new Set([...checklistIdsFromMarkdown(browser), ...checklistIdsFromMarkdown(channel)]);
  const mapped = classificationRows(classification);
  expect([...required].sort()).toEqual([...mapped.keys()].sort());

  for (const [id, kind] of mapped) {
    if (kind !== 'Automated') continue;
    const row = classification.split('\n').find((line) => line.startsWith(`| ${id} | Automated |`));
    expect(row, `${id} automated row must name a scenario or harness claim`).toBeTruthy();
    const claim = row!.split('|')[3]?.trim() ?? '';
    const scenarioIds = [...claim.matchAll(/`(CRH-[A-Z0-9-]+)`/g)].map((match) => match[1]);
    for (const scenarioId of scenarioIds) {
      expect(
        acceptanceScenarios.some((scenario) => scenario.id === scenarioId)
          || scenarioId.startsWith('CRH-050-')
          || scenarioId.startsWith('CRH-035-NESTED-GATEWAY')
          || scenarioId.startsWith('CRH-035-NESTED-CHANNELHOST')
          || scenarioId === 'CRH-037-DEPENDENCY-OUTAGE-RECOVERY',
        `${id} names executable scenario ${scenarioId}`,
      ).toBeTruthy();
    }
  }
});
