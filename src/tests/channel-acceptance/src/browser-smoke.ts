import { expect } from '@playwright/test';
import { waitForAuthenticatedBrowserSession } from './browser-auth.js';
import { openAgent, sendPrompt } from './flows.js';
import type { AcceptanceScenario } from './model.js';

/** Mandatory browser load, navigation, and a direct response without tools. */
export const browserSmokeScenarios: readonly AcceptanceScenario[] = [
  {
    id: 'CRH-030-BROWSER-SMOKE',
    title: 'authenticated navigation and a direct assistant reply complete without crash loops',
    tags: ['smoke', 'regression', 'full', 'release-gate'],
    checklistIds: ['B-01', 'B-02', 'B-04', 'B-05', 'C-01'],
    async execute(context) {
      const { page, baseUrl, fixtures } = context;
      await page.goto(new URL('/', baseUrl).toString());
      await waitForAuthenticatedBrowserSession(page);

      for (const path of ['/dashboard', '/connectors', '/approvals', '/settings'] as const) {
        await page.goto(new URL(path, baseUrl).toString());
        await waitForAuthenticatedBrowserSession(page);
        await expect(page).toHaveURL(new RegExp(`${path.replace('/', '\\/')}`));
        await expect(page.locator('body')).toBeVisible();
      }

      await openAgent(page, baseUrl, fixtures.assistantId);
      const prompt = `Reply with exactly the word SMOKEOK for browser-smoke ${context.seed}.`;
      await sendPrompt(page, prompt);
      await expect(page.getByText(prompt, { exact: false }).last()).toBeVisible();
      await expect(page.getByText(/SMOKEOK/i).last()).toBeVisible({ timeout: 90_000 });
      await expect(page.locator('textarea').last()).toBeEnabled();
      await expect(page.locator('[data-approval-action]')).toHaveCount(0);
    },
  },
];
