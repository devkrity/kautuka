import { expect, test } from '@playwright/test';
import { authenticateAcceptanceUser } from '../src/acceptance-login.js';
import { openAgent, sendPrompt } from '../src/flows.js';

test('My Assistant HITL continuation updates UI without reload', async ({ page }) => {
  const baseUrl = new URL(process.env.KAUTUKA_ACCEPTANCE_WEB_BASE_URI ?? 'http://127.0.0.1:20229/');
  const agentId = 'agent-1786000128846';

  await authenticateAcceptanceUser(page, baseUrl, { username: 'admin', password: 'passw0rd' });

  await openAgent(page, baseUrl, agentId);
  await sendPrompt(page, 'roll a d347');

  const approval = page.locator('[data-testid="hitl-approval"]').first();
  await expect(approval, 'approval card must appear').toBeVisible({ timeout: 180_000 });
  await expect(page.getByText(/Waiting for approval/i)).toBeVisible();

  await approval.locator('[data-approval-action="approve"]').click();
  await expect(approval).toHaveCount(0, { timeout: 60_000 });

  await expect
    .poll(
      async () => {
        const text = await page.locator('body').innerText();
        return /Ready|roll is \d+/i.test(text) && !/Working/i.test(text);
      },
      { timeout: 120_000, message: 'UI must show completed result without manual reload' },
    )
    .toBe(true);

  await expect(page.getByText(/Live session connection failed/i)).toHaveCount(0);
  await page.screenshot({ path: 'test-results/hitl-ui-live-success.png', fullPage: true });
});
