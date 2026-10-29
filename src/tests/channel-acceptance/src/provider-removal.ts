import { expect, type Page } from '@playwright/test';
import type { Provider } from './model.js';
import { connectProvider } from './provider-flows.js';

/** Permanent remove stops traffic; recovery + Save & Connect re-provisions. */
export async function verifyProviderRemoval(page: Page, provider: Provider, agentId: string): Promise<void> {
  const card = page.getByRole('heading', { name: provider[0].toUpperCase() + provider.slice(1), exact: true })
    .locator('..').locator('..').locator('..');
  await card.getByRole('button', { name: 'Remove' }).click();
  await expect(card.getByText('Status: deleted')).toBeVisible();
  await expect(page.getByText('Channel removed. Credentials were deleted', { exact: false })).toBeVisible();
  await expect(card.getByRole('button', { name: 'Verify with Test Prompt' })).toBeDisabled();
  await card.getByRole('button', { name: 'Start recovery' }).click();
  await expect(page.getByText(/moved to recovery/i)).toBeVisible();
  await connectProvider(page, provider, agentId);
  await expect(card.getByText('Status: active')).toBeVisible();
}
