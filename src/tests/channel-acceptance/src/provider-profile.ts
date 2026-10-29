import { expect, type Page } from '@playwright/test';
import type { Provider } from './model.js';

export function channelCard(page: Page, provider: Provider) {
  return page.getByRole('heading', { name: provider[0].toUpperCase() + provider.slice(1), exact: true }).locator('..').locator('..').locator('..');
}

export async function ensureProfileOpen(page: Page): Promise<void> {
  if (!await page.getByRole('heading', { name: 'Your Profile' }).isVisible()) {
    await expect(page.getByTitle('Open profile'), 'shell must expose the profile trigger before channel setup').toBeVisible({
      timeout: 60_000,
    });
    await page.getByTitle('Open profile').click();
  }
  await expect(page.getByRole('heading', { name: 'Your Profile' })).toBeVisible({ timeout: 30_000 });
}
