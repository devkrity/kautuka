import { expect } from '@playwright/test';
import { authenticatedBrowserHeaders } from './browser-auth.js';
import { assertAuthenticatedFileSafety, assertUnauthenticatedFileBoundary } from './flows.js';
import { providers, type AcceptanceScenario } from './model.js';
import { exerciseProviderNeutralConversation } from './provider-neutral-conversation.js';

const definitions: ReadonlyArray<readonly [string, string, readonly string[]]> = [
  ['CRH-032-PROVIDER-NEUTRAL-CONVERSATION', 'inbound Unicode messages, native outbound replies, reply identities, and typing/degradation', ['B-04', 'P-07', 'L-05']],
  ['CRH-037-SECURITY-BOUNDARY', 'negative delegation, ownership, callback and production simulator admission', ['A-05', 'F-06', 'P-02', 'P-03', 'P-04', 'P-05']],
  ['CRH-038-FILES-CONTENT-SAFETY', 'file ownership, limits, malformed content and redacted errors', ['F-05', 'F-06']],
];

export const catalogueScenarios: readonly AcceptanceScenario[] = definitions.map(([id, title, checklistIds]) => ({
  id, title, tags: ['full', 'release-gate'] as const, providers, checklistIds,
  async execute(context) {
    const { page, baseUrl, simulator, seed, fixtures } = context;
    await simulator.reset();
    await Promise.all(providers.map((provider) => simulator.configure(provider, { scenarioId: id, seed, ownerReference: 'acceptance-owner', conversationReference: 'acceptance-conversation' })));
    expect(fixtures.assistantId).not.toBe(fixtures.rollerId);
    if (id === 'CRH-032-PROVIDER-NEUTRAL-CONVERSATION') await exerciseProviderNeutralConversation(page, baseUrl, simulator, fixtures.rollerId, context.evidence);
    if (id === 'CRH-037-SECURITY-BOUNDARY') await securityBoundary(page, baseUrl, fixtures.assistantId);
    if (id === 'CRH-038-FILES-CONTENT-SAFETY') await fileSafety(page, baseUrl, fixtures.rollerId);
  },
}));

async function securityBoundary(page: import('@playwright/test').Page, baseUrl: URL, agentId: string) {
  await assertUnauthenticatedFileBoundary(baseUrl, agentId);
  await assertAuthenticatedFileSafety(page, baseUrl, agentId);
  const base = process.env.KAUTUKA_ACCEPTANCE_SIMULATOR_BASE_URI;
  if (!base) throw new Error('Simulator address must be configured for security acceptance.');
  const rejected = await fetch(new URL('/control/evidence', base), { headers: { 'X-Kautuka-Simulator-Key': 'forged-control-key' } });
  expect(rejected.status).toBe(401);
}

async function fileSafety(page: import('@playwright/test').Page, baseUrl: URL, agentId: string) {
  await assertUnauthenticatedFileBoundary(baseUrl, agentId);
  await assertAuthenticatedFileSafety(page, baseUrl, agentId);
  const request = page.context().request;
  const headers = await authenticatedBrowserHeaders(page, baseUrl);
  const resultResponse = await request.post(new URL(`/api/agents/${encodeURIComponent(agentId)}/files`, baseUrl).toString(), {
    headers,
    multipart: { file: { name: '../unsafe-name.txt', mimeType: 'text/plain', buffer: Buffer.from('acceptance') } },
  });
  const result = { status: resultResponse.status(), body: (await resultResponse.text()).slice(0, 400) };
  expect(result.status, `path-like upload names must be safely normalized or rejected: ${result.body}`).toBeGreaterThanOrEqual(200);
  if (result.status < 300) expect(result.body).toContain('"path":"unsafe-name.txt"');
  else expect(result.status).toBeGreaterThanOrEqual(400);
  expect(result.body).not.toMatch(/\.\.[\\/]/);
  expect(result.body).not.toMatch(/[A-Za-z]:\\|\/etc\/|BEGIN [A-Z ]+ KEY/);
}
