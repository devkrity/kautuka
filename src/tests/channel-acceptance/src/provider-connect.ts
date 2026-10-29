import { expect, type Page } from '@playwright/test';
import type { Provider } from './model.js';
import { providerBotToken, providerDestination } from './provider-fixtures.js';
import { channelCard, ensureProfileOpen } from './provider-profile.js';

export async function connectProvider(page: Page, provider: Provider, agentId: string, destination = providerDestination(provider)): Promise<void> {
  await ensureProfileOpen(page);
  const card = channelCard(page, provider);
  await expect(card.getByText(/Guided setup \(\d+ steps\)/)).toBeVisible();
  await card.locator('details').evaluate((details) => { (details as HTMLDetailsElement).open = true; });
  await expect(card.locator('details')).toHaveAttribute('open');
  const tokenField = card.locator(`#${provider}-bot_token`);
  await expect(tokenField).toBeVisible();
  const recovery = card.getByText('Status: recovery required');
  const suspended = card.getByText('Status: suspended');
  const replaceCredentials = card.getByRole('button', { name: 'Replace credentials' });
  const startRecovery = card.getByRole('button', { name: 'Start recovery' });
  if (await recovery.isVisible() || await suspended.isVisible()) {
    await startRecovery.click();
    await expect(page.getByText(/moved to recovery/i)).toBeVisible();
    await expect(card.getByRole('button', { name: 'Save & Connect' })).toBeEnabled();
  } else if (await replaceCredentials.isVisible()) {
    await replaceCredentials.click();
  } else if (await tokenField.isDisabled()) {
    if (await startRecovery.isVisible()) {
      await startRecovery.click();
      await expect(page.getByText(/moved to recovery/i)).toBeVisible();
    } else if (await replaceCredentials.isVisible()) {
      await replaceCredentials.click();
    }
  }
  await expect(tokenField).toBeEnabled({ timeout: 30_000 });
  await card.locator(`#${provider}-channel_device_id`).fill(destination);
  await tokenField.fill(providerBotToken(provider));
  if (provider === 'slack') await card.locator('#slack-app_token').fill('xapp-acceptance-simulator');
  const target = card.locator(`#${provider}-default-target`);
  await expect.poll(async () => target.locator(`option[value="agent:${agentId}"]`).count(), {
    timeout: 60_000,
    message: `${provider} default-target catalogue must include agent ${agentId}`,
  }).toBeGreaterThan(0);
  await target.selectOption(`agent:${agentId}`);
  // Finals-only channel delivery; Mirror chat is opt-in for token streaming.
  await card.locator(`#${provider}-tether-mode`).selectOption('hitl');
  await expect(card.getByRole('button', { name: 'Save & Connect' })).toBeEnabled({ timeout: 30_000 });
  await card.getByRole('button', { name: 'Save & Connect' }).click();
  await settleProviderSave(page, card, recovery);
  if (await recovery.isVisible()) {
    await startRecovery.click();
    await expect(page.getByText(/moved to recovery/i)).toBeVisible();
    await card.getByRole('button', { name: 'Save & Connect' }).click();
  }
  await expect(card.getByText('Status: active')).toBeVisible({ timeout: 120_000 });
}

async function settleProviderSave(
  page: Page,
  card: ReturnType<typeof channelCard>,
  recovery: ReturnType<Page['getByText']>,
): Promise<void> {
  try {
    await expect.poll(async () =>
      await card.getByText('Status: active').isVisible()
        ? 'active'
        : await recovery.isVisible() ? 'recovery' : 'pending',
    {
      timeout: 120_000,
      message: 'provider save must settle as active or require explicit recovery',
    }).not.toBe('pending');
  } catch (error) {
    const status = (await card.locator('text=/^Status:/').allTextContents()).join(' | ');
    const resultText = (await page.getByRole('alert').allTextContents().catch(() => [])).join(' | ');
    throw new Error(
      `provider save did not settle (status=${status || 'missing'}; alerts=${resultText || 'none'}): ${
        error instanceof Error ? error.message : String(error)
      }`);
  }
}
