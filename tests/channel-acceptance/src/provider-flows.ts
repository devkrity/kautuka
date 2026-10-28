import { expect, type Page } from '@playwright/test';
import type { Provider } from './model.js';
import { connectProvider } from './provider-connect.js';
import { providerBotToken, providerDestination } from './provider-fixtures.js';
import { channelCard, ensureProfileOpen } from './provider-profile.js';

export type Fixtures = Readonly<{ assistantId: string; rollerId: string }>;
export { connectProvider } from './provider-connect.js';

export async function discoverFixtures(page: Page): Promise<Fixtures> {
  await ensureProfileOpen(page);
  await expect.poll(() => page.locator('#telegram-default-target option').count(), 'profile target catalogue must load before fixture discovery').toBeGreaterThan(2);
  const options = await page.locator('#telegram-default-target option').evaluateAll((nodes) => nodes.map((node) => ({ text: node.textContent?.trim() ?? '', value: (node as HTMLOptionElement).value })));
  const named = (name: string) => options.filter((option) => option.text === `Agent: ${name}` && option.value.startsWith('agent:'));
  const assistant = named('Assistant'); const roller = named('Roller');
  expect(assistant, 'exactly one Assistant fixture is required').toHaveLength(1);
  expect(roller, 'exactly one Roller fixture is required').toHaveLength(1);
  return { assistantId: assistant[0].value.slice('agent:'.length), rollerId: roller[0].value.slice('agent:'.length) };
}

/** Exercises only the owner-visible setup contract; no provider secret is read back. */
export async function verifyProviderSetupGuidance(page: Page, provider: Provider): Promise<void> {
  await ensureProfileOpen(page);
  const card = channelCard(page, provider);
  const guide = card.locator('details');
  await expect(card.getByRole('link', { name: /open .*setup|open botfather|open .*portal/i })).toBeVisible();
  await expect(guide.getByText(/Guided setup \(\d+ steps\)/)).toBeVisible();
  await guide.evaluate((details) => { (details as HTMLDetailsElement).open = true; });
  await expect(guide.locator('ol > li')).toHaveCount(provider === 'telegram' ? 3 : 4);
  await expect(card.locator(`#${provider}-channel_device_id`)).toBeVisible();
  await expect(card.locator(`#${provider}-default-target`)).toBeVisible();
  await expect(card.locator(`#${provider}-tether-mode`)).toBeVisible();
  await expect(card.locator(`#${provider}-tether-mode option[value="hitl"]`)).toHaveText(/Approvals \+ final reply/i);
  await expect(card.locator(`#${provider}-tether-mode option[value="mirror"]`)).toHaveText(/Mirror completed replies/i);
  await expect(card.locator(`#${provider}-tether-mode option[value="full"]`)).toHaveCount(0);
  if (provider === 'slack') await expect(card.getByRole('button', { name: 'Copy Slack app manifest' })).toBeVisible();
}

export async function verifyIncompleteTelegramSetupGuidance(page: Page): Promise<void> {
  await ensureProfileOpen(page);
  const card = channelCard(page, 'telegram');
  await card.locator('#telegram-channel_device_id').fill('');
  await card.getByRole('button', { name: 'Save & Connect' }).click();
  await expect(page.getByText(/Enter the Telegram chat\/device ID before connecting/i)).toBeVisible();
}

export async function replaceProviderCredentials(page: Page, provider: Provider, agentId: string, destination = providerDestination(provider)): Promise<void> {
  const card = channelCard(page, provider);
  await card.getByRole('button', { name: 'Replace credentials' }).click();
  await expect(page.getByText(new RegExp(`Paste the replacement ${provider}`, 'i'))).toBeVisible();
  await expect(card.locator(`#${provider}-bot_token`)).toHaveValue('');
  if (provider === 'slack') await expect(card.locator('#slack-app_token')).toHaveValue('');
  await card.locator(`#${provider}-bot_token`).fill(providerBotToken(provider));
  if (provider === 'slack') await card.locator('#slack-app_token').fill('xapp-acceptance-simulator');
  await card.locator(`#${provider}-channel_device_id`).fill(destination);
  await card.locator(`#${provider}-default-target`).selectOption(`agent:${agentId}`);
  await card.getByRole('button', { name: 'Save & Connect' }).click();
  await expect(card.getByText('Status: active')).toBeVisible();
}

export async function verifyProviderLifecycle(page: Page, provider: Provider, agentId: string): Promise<void> {
  const card = channelCard(page, provider);
  await card.getByRole('button', { name: 'Verify with Test Prompt' }).click();
  await expect(page.getByText(`${provider} test sent.`, { exact: false })).toBeVisible();
  await card.getByRole('button', { name: 'Suspend' }).click();
  await expect(card.getByText('Status: suspended')).toBeVisible();
  await expect(card.getByRole('button', { name: 'Verify with Test Prompt' })).toBeDisabled();
  await expect(page.getByText(/New inbound and outbound work is stopped/i)).toBeVisible();
  await card.getByRole('button', { name: 'Start recovery' }).click();
  await expect(page.getByText(/moved to recovery/i)).toBeVisible();
  await expect(card.getByRole('button', { name: 'Save & Connect' })).toBeEnabled();
  await card.getByRole('button', { name: 'Save & Connect' }).click();
  await expect(card.getByText('Status: active')).toBeVisible();
  await expect(card.locator(`#${provider}-default-target`)).toHaveValue(`agent:${agentId}`);
}
