import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { findMyAssistant, login, newSession, sendPrompt, verifyWebBaseUrl } from './verify-workspace-helpers.js';

const evidenceDir = path.resolve('../../../.artifacts/approvals-e2e-evidence');

test('fresh Approvals-page approve continues HITL run', async ({ page }) => {
  fs.mkdirSync(evidenceDir, { recursive: true });
  const origin = new URL(verifyWebBaseUrl).origin;
  const headers = await login(page);
  const agentId = await findMyAssistant(headers);
  const sessionId = await newSession(page, agentId);

  const prompt = 'roll a d347';
  const promptLatencyMs = await sendPrompt(page, prompt);
  fs.writeFileSync(path.join(evidenceDir, 'prompt-latency-hitl.json'), JSON.stringify({ promptLatencyMs }, null, 2));

  await expect(page.locator('[data-testid="hitl-approval"]').first()).toBeVisible({ timeout: 180_000 });
  await expect(page.getByText(/Waiting for approval/i)).toBeVisible();
  await page.screenshot({ path: path.join(evidenceDir, '01-chat-waiting-approval.png'), fullPage: true });

  const pendingBefore = await page.request.get(`${origin}/api/approvals/pending`, { headers });
  expect(pendingBefore.ok()).toBeTruthy();
  const pendingPayload = await pendingBefore.json() as Array<{ id?: string; sessionId?: string; toolName?: string }>;
  const pending = pendingPayload.find((item) => item.sessionId === sessionId);
  expect(pending?.id, 'pending approval must exist for session').toBeTruthy();
  fs.writeFileSync(path.join(evidenceDir, 'pending-before.json'), JSON.stringify(pendingPayload, null, 2));

  const resolveResponsePromise = page.waitForResponse(
    (response) =>
      response.request().method() === 'POST'
      && new URL(response.url()).pathname === '/api/narad/v1/commands/resolve-interrupt',
    { timeout: 60_000 },
  );

  await page.goto(`${origin}/approvals`);
  await expect(page.getByRole('heading', { name: 'Approvals' })).toBeVisible({ timeout: 30_000 });
  const card = page.locator(`[data-testid="pending-approval"][data-approval-request-id="${pending!.id}"]`);
  await expect(card).toBeVisible({ timeout: 30_000 });
  await page.screenshot({ path: path.join(evidenceDir, '02-approvals-page-pending.png'), fullPage: true });

  await card.locator('[data-approval-action="approve"]').click();
  const resolveResponse = await resolveResponsePromise;
  const resolveBody = await resolveResponse.text();
  fs.writeFileSync(
    path.join(evidenceDir, 'resolve-response.json'),
    JSON.stringify({ status: resolveResponse.status(), body: resolveBody }, null, 2),
  );
  expect(resolveResponse.ok(), `resolve must succeed: ${resolveBody}`).toBeTruthy();
  await expect(card).toBeHidden({ timeout: 60_000 });

  async function pendingForSession(): Promise<Array<{ id: string }>> {
    const pendingResponse = await page.request.get(`${origin}/api/approvals/pending`, { headers });
    const payload = await pendingResponse.json() as Array<{ id: string; sessionId?: string; outerSessionId?: string }>;
    return payload.filter((item) => item.sessionId === sessionId || item.outerSessionId === sessionId);
  }

  for (let step = 0; step < 3; step += 1) {
    const nested = await pendingForSession();
    if (nested.length === 0) break;
    const nestedId = nested[0]!.id;
    const nestedResolve = page.waitForResponse(
      (response) =>
        response.request().method() === 'POST'
        && new URL(response.url()).pathname === '/api/narad/v1/commands/resolve-interrupt',
      { timeout: 60_000 },
    );
    await page.goto(`${origin}/approvals`);
    const nestedCard = page.locator(`[data-testid="pending-approval"][data-approval-request-id="${nestedId}"]`);
    await expect(nestedCard).toBeVisible({ timeout: 30_000 });
    await nestedCard.locator('[data-approval-action="approve"]').click();
    const nestedResponse = await nestedResolve;
    fs.appendFileSync(
      path.join(evidenceDir, 'resolve-response.jsonl'),
      JSON.stringify({ requestId: nestedId, status: nestedResponse.status(), body: await nestedResponse.text() }) + '\n',
    );
    expect(nestedResponse.ok()).toBeTruthy();
    await expect(nestedCard).toBeHidden({ timeout: 60_000 });
  }

  await page.screenshot({ path: path.join(evidenceDir, '03-approvals-page-after-approve.png'), fullPage: true });

  await page.goto(`${origin}/workspace/${encodeURIComponent(agentId)}?sessionId=${encodeURIComponent(sessionId)}`);
  await expect
    .poll(
      async () => {
        const text = await page.locator('body').innerText();
        return /Ready|roll is \d+|rolled a \d+|result.*\d+/i.test(text) && !/Waiting for approval|Working/i.test(text);
      },
      { timeout: 180_000, message: 'chat must continue after Approvals-page approve' },
    )
    .toBe(true);

  await expect(page.getByText(/Live session connection failed/i)).toHaveCount(0);
  await page.screenshot({ path: path.join(evidenceDir, '04-chat-completed.png'), fullPage: true });

  const sessionApprovals = await page.request.get(`${origin}/api/approvals/session/${encodeURIComponent(sessionId)}`, { headers });
  expect(sessionApprovals.ok()).toBeTruthy();
  fs.writeFileSync(path.join(evidenceDir, 'session-approvals-after.json'), JSON.stringify(await sessionApprovals.json(), null, 2));

  const health = await page.request.get('http://127.0.0.1:20000/health');
  fs.writeFileSync(path.join(evidenceDir, 'gateway-health.json'), JSON.stringify({ status: health.status() }, null, 2));
  expect(health.ok(), 'Gateway must remain healthy').toBeTruthy();
});
