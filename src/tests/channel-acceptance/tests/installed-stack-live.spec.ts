import { expect, test } from '@playwright/test';
import { authenticateAcceptanceUser } from '../src/acceptance-login.js';
import { openAgent, sendPrompt } from '../src/flows.js';

const baseUrl = new URL(process.env.KAUTUKA_ACCEPTANCE_WEB_BASE_URI ?? 'http://127.0.0.1:20229/');
const credentials = {
  username: process.env.KAUTUKA_ACCEPTANCE_USERNAME ?? 'smoke-admin',
  password: process.env.KAUTUKA_ACCEPTANCE_PASSWORD ?? 'SmokeAdmin1-local-only',
};
const agentId = process.env.KAUTUKA_ACCEPTANCE_AGENT_ID ?? 'smoke-agent';

test.describe.configure({ mode: 'serial' });

test('installed stack login navigation and a direct assistant reply', async ({ page }) => {
  await authenticateAcceptanceUser(page, baseUrl, credentials);

  for (const path of ['/dashboard', '/connectors', '/approvals', '/settings'] as const) {
    await page.goto(new URL(path, baseUrl).toString());
    await expect(page).toHaveURL(new RegExp(`${path.replace('/', '\\/')}`));
    await expect(page.locator('body')).toBeVisible();
  }

  await openAgent(page, baseUrl, agentId);
  const prompt = 'Reply with exactly the word SMOKEOK.';
  await sendPrompt(page, prompt);
  await expect(page.getByText(/SMOKEOK/i).last()).toBeVisible({ timeout: 90_000 });
  await expect(page.locator('textarea').last()).toBeEnabled();
  await expect(page.locator('[data-approval-action]')).toHaveCount(0);
});

test('installed stack HITL approve continues the chat without reload', async ({ page }) => {
  await authenticateAcceptanceUser(page, baseUrl, credentials);
  await openAgent(page, baseUrl, agentId);
  await sendPrompt(page, 'Call run_shell with command exactly: echo HITL_OK');

  const approval = page.locator('[data-testid="hitl-approval"]').first();
  await expect(approval, 'approval card must appear').toBeVisible({ timeout: 180_000 });
  await approval.locator('[data-approval-action="approve"]').click();
  await expect(approval).toHaveCount(0, { timeout: 60_000 });

  await expect(page.getByText(/HITL_OK/i).last()).toBeVisible({ timeout: 120_000 });
  await expect(page.getByText(/Live session connection failed/i)).toHaveCount(0);
});

test('installed stack Telegram settings surface is reachable after login', async ({ page }) => {
  await authenticateAcceptanceUser(page, baseUrl, credentials);
  await page.goto(new URL('/settings?tab=users', baseUrl).toString());
  await expect(page.getByText(/Still reconnecting to services|Reconnecting to your AI services/i)).toHaveCount(0, {
    timeout: 120_000,
  });
  await expect(page.getByText(/Telegram device:/i)).toBeVisible({ timeout: 60_000 });
  await page.getByRole('button', { name: 'Edit' }).click();
  await expect(page.getByText('Telegram Settings')).toBeVisible();
  await expect(page.getByLabel('Bot Token').first()).toBeVisible();
  await expect(page.getByLabel('Device ID').first()).toBeVisible();
});
