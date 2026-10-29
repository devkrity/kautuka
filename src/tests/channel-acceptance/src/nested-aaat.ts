import { expect } from '@playwright/test';
import { assertDiceMentionVisible } from './dice-fixture.js';
import { injectProviderApproval, resolveBrowserApproval, waitForProviderAction } from './flows.js';
import { assertNestedProjection, assertOneTerminalParentResult } from './nested-aaat-projection.js';
import { prepareNestedRun, recordApproval, waitForNestedApproval } from './nested-aaat-flows.js';
import { nestedBrowserReconnectAtChildPause } from './nested-aaat-reconnect.js';
import { sameChildConcurrencyScenario } from './same-child-concurrency.js';
import type { AcceptanceScenario, Provider } from './model.js';

const provider: Provider = 'telegram';
const parentToolName = 'invoke_agent_roller';
const childToolName = 'run_skill_script';

const nestedSuccessScenario: AcceptanceScenario = {
  id: 'CRH-035-NESTED-SEQUENTIAL-MIXED-RESOLUTION',
  title: 'nested Roller invocation resolves parent in browser and child in Telegram without a second projection',
  tags: ['full', 'release-gate'], providers: [provider],
  checklistIds: ['C-03', 'C-04', 'C-05', 'C-06', 'C-07', 'C-08', 'C-11'],
  async execute(context) {
    const destination = await prepareNestedRun(context, provider, 'mixed');
    const parent = await waitForNestedApproval(context.page, parentToolName);
    recordApproval(context, parent);
    await resolveBrowserApproval(context.page, 'approve');
    const child = await waitForNestedApproval(context.page, childToolName);
    recordApproval(context, child);
    expect(child.requestId, 'each sequential approval has its immutable request id').not.toBe(parent.requestId);
    const attachWithinFiveSeconds = context.page.waitForRequest(
      (request) =>
        request.method() === 'GET'
        && new URL(request.url()).pathname.includes('/api/narad/v1/sessions/')
        && new URL(request.url()).pathname.endsWith('/stream'),
      { timeout: 5_000 },
    );
    await injectProviderApproval(context.simulator, provider, await waitForProviderAction(context.simulator, provider, 'approve'), destination);
    await attachWithinFiveSeconds;
    await expect(
      context.page.locator(`[data-testid="hitl-approval"][data-approval-request-id="${child.requestId}"]`),
      'channel resolution must clear the first child approval before the next nested pause',
    ).toHaveCount(0);
    // Durable multi-surface contract: after Telegram resolves while chat is open, attach within
    // 5s and within 30s show a real wait or progress — never a zombie Running child tool.
    await expect.poll(async () => {
      const nextHitl = await context.page.locator('[data-testid="hitl-approval"]').count();
      const zombieChild = await context.page
        .locator(`[data-testid="copilot-tool-render"][data-tool-name="${childToolName}"][data-status="executing"], [data-testid="copilot-tool-render"][data-tool-name="${childToolName}"][data-status="inProgress"]`)
        .count();
      return nextHitl > 0 || zombieChild === 0;
    }, {
      message: 'after channel resolve with chat open, nested chat must show a real wait or leave no zombie Running tools within 30s',
      timeout: 30_000,
    }).toBeTruthy();
    const secondChild = await waitForNestedApproval(context.page, childToolName, child.requestId);
    recordApproval(context, secondChild);
    expect(secondChild.requestId, 'sequential child approvals must never reuse the first child request id').not.toBe(child.requestId);
    await resolveBrowserApproval(context.page, 'approve');
    await assertDiceMentionVisible(context.page, 27);
    await assertDiceMentionVisible(context.page, 31);
    await assertOneTerminalParentResult(context.page, parentToolName, 'complete');
    const projection = await assertNestedProjection(context.page, context.baseUrl, parentToolName, childToolName, 2);
    context.evidence.addIdentifier({ kind: 'invocation', value: projection.parentId });
    for (const childId of projection.childIds) context.evidence.addIdentifier({ kind: 'invocation', value: childId });
  },
};

const nestedCancelScenario: AcceptanceScenario = {
  id: 'CRH-035-NESTED-CHILD-CANCEL',
  title: 'cancelling the child approval terminates exactly one parent invocation and leaves chat responsive',
  tags: ['full', 'release-gate'], providers: [provider], checklistIds: ['C-03', 'C-05', 'C-07', 'C-11'],
  async execute(context) {
    await prepareNestedRun(context, provider, 'cancel');
    recordApproval(context, await waitForNestedApproval(context.page, parentToolName));
    await resolveBrowserApproval(context.page, 'approve');
    recordApproval(context, await waitForNestedApproval(context.page, childToolName));
    await resolveBrowserApprovalForTool(context.page, childToolName, 'cancel');
    await expect(context.page.getByText(/cancelled|cancell?ed/i).last()).toBeVisible();
    await assertOneTerminalParentResult(context.page, parentToolName, 'cancelled');
  },
};

const nestedStopScenario: AcceptanceScenario = {
  id: 'CRH-035-NESTED-CHILD-STOP',
  title: 'stopping the child approval through Telegram terminates exactly one parent invocation and leaves chat responsive',
  tags: ['full', 'release-gate'], providers: [provider], checklistIds: ['C-03', 'C-05', 'C-07', 'C-08', 'C-11'],
  async execute(context) {
    const destination = await prepareNestedRun(context, provider, 'stop');
    recordApproval(context, await waitForNestedApproval(context.page, parentToolName));
    await resolveBrowserApproval(context.page, 'approve');
    recordApproval(context, await waitForNestedApproval(context.page, childToolName));
    await injectProviderApproval(context.simulator, provider, await waitForProviderAction(context.simulator, provider, 'stop-run'), destination);
    await expect(context.page.getByText(/stopped/i).last()).toBeVisible();
    await assertOneTerminalParentResult(context.page, parentToolName, 'stopped');
  },
};

/** Browser/provider-only AAAT coverage. No runtime event synthesis or store access. */
export const nestedAaatScenarios: readonly AcceptanceScenario[] = [
  nestedSuccessScenario,
  nestedCancelScenario,
  nestedStopScenario,
  nestedBrowserReconnectAtChildPause,
  sameChildConcurrencyScenario,
];

async function resolveBrowserApprovalForTool(page: import('@playwright/test').Page, toolName: string, action: 'cancel'): Promise<void> {
  const panel = page.locator(`[data-testid="hitl-approval"][data-tool-name="${toolName}"]`).last();
  await panel.locator(`[data-approval-action="${action}"]`).click();
  await expect(panel).toBeHidden();
}
