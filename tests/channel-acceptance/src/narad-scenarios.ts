import { expect } from '@playwright/test';
import { installNaradStreamCapture } from './narad-stream-capture.js';
import { openAgent, sendPrompt } from './thread-flows.js';
import { resolveApprovalFromSurface, waitForResolvedPublicApproval, type ApprovalSurface } from './hitl-parity.js';
import type { AcceptanceScenario, ApprovalAction } from './model.js';

const scenario = (id: string, title: string, checklistIds: readonly string[], execute: AcceptanceScenario['execute']): AcceptanceScenario =>
  ({ id, title, tags: ['regression', 'full'], checklistIds, execute });

const naradContractScenarios: readonly AcceptanceScenario[] = [
  scenario('Narad-001-CONVERSATION-STREAM-REPLAY', 'conversation user/assistant stream and replay', ['B-01', 'R-01'], async ({ page, baseUrl, fixtures }) => {
    await openAgent(page, baseUrl, fixtures.rollerId);
    const capture = await installNaradStreamCapture(page);
    await sendPrompt(page, 'Say hello in one short sentence.');
    await expect(page.getByText(/hello/i).last()).toBeVisible({ timeout: 120_000 });
    capture.markBoundary();
    await page.reload();
    await expect(page.getByText(/hello/i).last()).toBeVisible({ timeout: 120_000 });
    capture.assertRunTerminalOrdering();
  }),
  scenario('Narad-002-INTERRUPT-PAUSE-RESUME', 'interrupt requested/resolved with pause/resume and authority wait', ['C-03', 'C-04'], async ({ page, baseUrl, fixtures }) => {
    await openAgent(page, baseUrl, fixtures.rollerId);
    const sessionId = new URL(page.url()).searchParams.get('sessionId');
    expect(sessionId).toBeTruthy();
    await sendPrompt(page, 'Roll a d19. Do not simulate the roll.');
    await resolveApprovalFromSurface(page, baseUrl, 'chat', 'approve');
    await waitForResolvedPublicApproval(page, baseUrl, sessionId!, 'approve', 'chat');
  }),
  scenario('Narad-004-RECONNECT-SEQUENCE', 'reconnect recovery from sessionSequence without duplicates/gaps', ['B-03', 'R-06'], async ({ page, baseUrl, fixtures }) => {
    await openAgent(page, baseUrl, fixtures.rollerId);
    const capture = await installNaradStreamCapture(page);
    await sendPrompt(page, 'Reply with the word reconnect.');
    await expect(page.getByText(/reconnect/i).last()).toBeVisible({ timeout: 120_000 });
    capture.markBoundary();
    await page.reload();
    await expect(page.getByText(/reconnect/i).last()).toBeVisible({ timeout: 120_000 });
    capture.assertReconnectSequenceIntegrity();
  }),
];

export const naradRuntimeScenarios: readonly AcceptanceScenario[] = [
  ...naradContractScenarios,
  {
    id: 'CRH-036-SNAPSHOT-RECONNECT-REPLAY',
    title: 'browser reconnect at a paused or completed run replays Narad session history without forking lifecycle',
    tags: ['full', 'release-gate'],
    providers: ['telegram'],
    checklistIds: ['B-03', 'C-08', 'C-11', 'R-06'],
    execute: naradContractScenarios.find((scenario) => scenario.id === 'Narad-004-RECONNECT-SEQUENCE')!.execute,
  },
  scenario('Narad-003-NESTED-RUN-ORDERING', 'nested child runs/tools and parent terminal ordering', ['R-02'], async ({ page, baseUrl, fixtures }) => {
    await openAgent(page, baseUrl, fixtures.rollerId);
    const capture = await installNaradStreamCapture(page);
    await sendPrompt(page, 'Use the nested child tool once and summarize.');
    await expect(page.locator('textarea').last()).toBeEnabled({ timeout: 180_000 });
    capture.assertRunTerminalOrdering();
  }),
  scenario('Narad-005-GRAPH-HYDRATE', 'run-scoped graph hydrate snapshot/delta anchored by sourceRunCursor', ['R-03'], async ({ page, baseUrl, fixtures }) => {
    const capture = await installNaradStreamCapture(page);
    await openAgent(page, baseUrl, fixtures.rollerId);
    await sendPrompt(page, 'Search for Narad graph hydrate.');
    await expect(page.locator('textarea').last()).toBeEnabled({ timeout: 180_000 });
    expect(capture.records().some((record) => record.type === 'graph.snapshot' || record.type === 'graph.delta')).toBeTruthy();
  }),
  scenario('Narad-006-PROGRESS-REPLAY', 'durable progress replay and privacy behavior', ['R-04'], async ({ page, baseUrl, fixtures }) => {
    await openAgent(page, baseUrl, fixtures.rollerId);
    const capture = await installNaradStreamCapture(page);
    await sendPrompt(page, 'Perform a multi-step task and report progress.');
    await expect(page.locator('textarea').last()).toBeEnabled({ timeout: 180_000 });
    capture.markBoundary();
    await page.reload();
    await expect(page.locator('textarea').last()).toBeEnabled({ timeout: 180_000 });
    capture.assertProgressReplayPrivacy();
  }),
  scenario('Narad-007-WATCH-REVISION', 'workspace/session watch revision', ['R-05'], async ({ page, baseUrl, fixtures }) => {
    await openAgent(page, baseUrl, fixtures.rollerId);
    const capture = await installNaradStreamCapture(page);
    await sendPrompt(page, 'Acknowledge watch revision.');
    await expect(page.locator('textarea').last()).toBeEnabled({ timeout: 120_000 });
    capture.assertWatchRevisionMonotonic();
  }),
  scenario('Narad-008-STOP-CASCADE', 'stop cascade descendants/tools/interrupts before parent terminal', ['C-06'], async ({ page, baseUrl, fixtures }) => {
    await openAgent(page, baseUrl, fixtures.rollerId);
    await sendPrompt(page, 'Roll a d19. Do not simulate the roll.');
    await resolveApprovalFromSurface(page, baseUrl, 'chat', 'stop-run');
    await expect(page.getByText(/stopped/i).last()).toBeVisible({ timeout: 120_000 });
  }),
  scenario('Narad-009-CONTROL-SEPARATE', 'server heartbeat/control separate from journal', ['R-07'], async ({ page, baseUrl, fixtures }) => {
    const capture = await installNaradStreamCapture(page);
    await openAgent(page, baseUrl, fixtures.rollerId);
    await sendPrompt(page, 'Ping.');
    await expect(page.locator('textarea').last()).toBeEnabled({ timeout: 120_000 });
    expect(capture.records().some((record) => record.type === 'server.heartbeat' || record.type === 'client.attached')).toBeTruthy();
  }),
];

export function browserApproval(surface: ApprovalSurface, action: ApprovalAction): AcceptanceScenario {
  return {
    id: surface === 'chat' && action === 'approve' ? 'CRH-033-BROWSER-APPROVE' : `CRH-033-${surface.toUpperCase()}-${action.toUpperCase()}`,
    title: `${surface === 'chat' ? 'Chat' : 'Approvals page'} ${action} preserves Narad interrupt semantics`,
    tags: ['regression', 'full', 'release-gate'],
    checklistIds: ['C-02', 'C-03', 'U-04'],
    execute: async ({ page, baseUrl, fixtures }) => {
      await openAgent(page, baseUrl, fixtures.rollerId);
      const threadUrl = page.url();
      const threadId = new URL(threadUrl).searchParams.get('sessionId');
      expect(threadId).toBeTruthy();
      await sendPrompt(page, `Roll a d19 for ${surface}-${action}. Do not simulate the roll.`);
      await expect(page.locator('[data-approval-action="approve"]').first()).toBeVisible({ timeout: 180_000 });
      await resolveApprovalFromSurface(page, baseUrl, surface, action);
      await waitForResolvedPublicApproval(page, baseUrl, threadId!, action, surface);
      if (surface === 'approvals') await page.goto(threadUrl);
    },
  };
}
