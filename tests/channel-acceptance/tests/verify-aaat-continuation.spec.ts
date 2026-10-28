import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { findMyAssistant, login, newSession, sendPrompt, verifyWebBaseUrl } from './verify-workspace-helpers.js';

const evidenceDir = path.resolve('../../.artifacts/approvals-e2e-evidence');

test('My Assistant roller AAAT completes with stable Gateway', async ({ page }) => {
  fs.mkdirSync(evidenceDir, { recursive: true });
  const origin = new URL(verifyWebBaseUrl).origin;
  const headers = await login(page);
  const agentId = await findMyAssistant(headers);
  const sessionId = await newSession(page, agentId);

  const prompt = 'Use the Roller agent exactly once to roll a d47. Do not simulate the roll.';
  const promptLatencyMs = await sendPrompt(page, prompt);
  fs.writeFileSync(path.join(evidenceDir, 'prompt-latency-aaat.json'), JSON.stringify({ promptLatencyMs }, null, 2));
  expect(promptLatencyMs, 'prompt should appear instantly in chat').toBeLessThan(5000);

  async function approveSessionFromApprovalsPage(requestId: string): Promise<void> {
    const resolveResponsePromise = page.waitForResponse(
      (response) =>
        response.request().method() === 'POST'
        && new URL(response.url()).pathname === '/api/narad/v1/commands/resolve-interrupt',
      { timeout: 60_000 },
    );
    await page.goto(`${origin}/approvals`);
    await expect(page.getByRole('heading', { name: 'Approvals' })).toBeVisible({ timeout: 30_000 });
    const card = page.locator(`[data-testid="pending-approval"][data-approval-request-id="${requestId}"]`);
    await expect(card).toBeVisible({ timeout: 30_000 });
    await card.locator('[data-approval-action="approve"]').click();
    const resolveResponse = await resolveResponsePromise;
    const resolveBody = await resolveResponse.text();
    fs.appendFileSync(
      path.join(evidenceDir, 'aaat-resolve-responses.jsonl'),
      JSON.stringify({ requestId, status: resolveResponse.status(), body: resolveBody }) + '\n',
    );
    expect(resolveResponse.ok(), `resolve ${requestId} must succeed: ${resolveBody}`).toBeTruthy();
    await expect(card).toBeHidden({ timeout: 60_000 });
    await page.goto(`${origin}/workspace/${encodeURIComponent(agentId)}?sessionId=${encodeURIComponent(sessionId)}`);
  }

  async function pendingForSession(): Promise<Array<{ id: string; sessionId?: string; outerSessionId?: string; toolName?: string }>> {
    const pendingResponse = await page.request.get(`${origin}/api/approvals/pending`, { headers });
    expect(pendingResponse.ok()).toBeTruthy();
    const pendingPayload = await pendingResponse.json() as Array<{ id: string; sessionId?: string; outerSessionId?: string; toolName?: string }>;
    return pendingPayload.filter((item) => item.sessionId === sessionId || item.outerSessionId === sessionId);
  }

  await expect
    .poll(async () => (await pendingForSession()).length, {
      timeout: 180_000,
      message: 'AAAT approval must appear in pending API',
    })
    .toBeGreaterThan(0);
  await page.screenshot({ path: path.join(evidenceDir, '05-aaat-outer-approval.png'), fullPage: true });

  for (let step = 0; step < 3; step += 1) {
    const pending = await pendingForSession();
    if (pending.length === 0) break;
    fs.writeFileSync(path.join(evidenceDir, `aaat-pending-step-${step}.json`), JSON.stringify(pending, null, 2));
    await approveSessionFromApprovalsPage(pending[0]!.id);
    await page.waitForTimeout(2_000);
  }

  await expect
    .poll(
      async () => {
        const text = await page.locator('body').innerText();
        return /Ready|roll is \d+|rolled a \d+|result.*\d+/i.test(text) && !/Working|Waiting for approval/i.test(text);
      },
      { timeout: 360_000, message: 'AAAT roller run must complete' },
    )
    .toBe(true);

  await page.screenshot({ path: path.join(evidenceDir, '05-aaat-completed.png'), fullPage: true });
  const health = await page.request.get('http://127.0.0.1:20000/health');
  fs.writeFileSync(path.join(evidenceDir, 'gateway-health-aaat.json'), JSON.stringify({ status: health.status() }, null, 2));
  expect(health.ok(), 'Gateway must remain healthy after AAAT').toBeTruthy();
});
