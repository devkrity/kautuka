import { expect, type Page } from '@playwright/test';

/**
 * Acceptance asserts lifecycle and that the requested die was addressed.
 * It does not assert a specific numeric roll — even if the fixture skill
 * currently prints a fixed formula for stable script execution.
 */
export async function assertDiceMentionVisible(page: Page, sides: number): Promise<void> {
  await expect(
    page.getByText(new RegExp(`d${sides}`, 'i')).last(),
    `chat must surface the d${sides} roll after the approved tool sequence`,
  ).toBeVisible({ timeout: 30_000 });
}
