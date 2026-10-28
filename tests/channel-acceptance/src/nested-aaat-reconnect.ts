import { expect } from '@playwright/test';
import { assertDiceMentionVisible } from './dice-fixture.js';
import { resolveBrowserApproval } from './flows.js';
import { assertOneTerminalParentResult } from './nested-aaat-projection.js';
import { prepareNestedRun, recordApproval, waitForNestedApproval } from './nested-aaat-flows.js';
import type { AcceptanceScenario, Provider } from './model.js';

const provider: Provider = 'telegram';
const parentToolName = 'invoke_agent_roller';
const childToolName = 'run_skill_script';

export const nestedBrowserReconnectAtChildPause: AcceptanceScenario = {
  id: 'CRH-035-NESTED-BROWSER-RECONNECT-AT-CHILD-PAUSE',
  title: 'browser reload at a nested child pause retains the same approval request and continues exactly once',
  tags: ['full', 'release-gate'],
  providers: [provider],
  checklistIds: ['C-04', 'C-08', 'R-06'],
  async execute(context) {
    await prepareNestedRun(context, provider, 'reconnect-child');
    recordApproval(context, await waitForNestedApproval(context.page, parentToolName));
    await resolveBrowserApproval(context.page, 'approve');
    const child = await waitForNestedApproval(context.page, childToolName);
    recordApproval(context, child);

    const threadUrl = context.page.url();
    const replayConnection = context.page.waitForRequest((request) =>
      new URL(request.url()).pathname.includes('/api/narad/v1/sessions/')
      && new URL(request.url()).pathname.endsWith('/stream'));
    await context.page.reload();
    await replayConnection;
    expect(context.page.url()).toBe(threadUrl);

    const restored = context.page.locator(
      `[data-testid="hitl-approval"][data-tool-name="${childToolName}"][data-approval-request-id="${child.requestId}"]`,
    );
    await expect(restored, 'canonical replay must restore the same child approval request id').toBeVisible();
    await resolveBrowserApproval(context.page, 'approve');
    await assertDiceMentionVisible(context.page, 27);
    await assertOneTerminalParentResult(context.page, parentToolName, 'complete');
  },
};
