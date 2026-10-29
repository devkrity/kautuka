import { expect } from '@playwright/test';
import { assertDiceMentionVisible } from './dice-fixture.js';
import { openAgent, resolveBrowserApproval, sendPrompt } from './flows.js';
import { assertOneTerminalParentResult } from './nested-aaat-projection.js';
import { waitForNestedApproval, recordApproval } from './nested-aaat-flows.js';
import type { AcceptanceScenario } from './model.js';

const parentToolName = 'invoke_agent_roller';
const childToolName = 'run_skill_script';

/**
 * Two concurrent parent threads invoke the same child agent. Approvals and
 * terminal results must stay isolated to their own threads.
 */
export const sameChildConcurrencyScenario: AcceptanceScenario = {
  id: 'CRH-035-SAME-CHILD-CONCURRENCY',
  title: 'two concurrent parents invoking the same Roller child keep isolated approvals and results',
  tags: ['full', 'release-gate'],
  checklistIds: ['C-04', 'C-10', 'C-11'],
  async execute(context) {
    const browser = context.page.context().browser();
    expect(browser, 'same-child concurrency requires a Playwright browser').toBeTruthy();
    const second = await browser!.newPage();
    try {
      await openAgent(context.page, context.baseUrl, context.skillFixtures.parentAgentId);
      await openAgent(second, context.baseUrl, context.skillFixtures.parentAgentId);
      const firstThread = new URL(context.page.url()).searchParams.get('sessionId');
      const secondThread = new URL(second.url()).searchParams.get('sessionId');
      expect(firstThread).toBeTruthy();
      expect(secondThread).toBeTruthy();
      expect(firstThread).not.toBe(secondThread);

      await Promise.all([
        sendPrompt(context.page, 'Use the Roller agent to roll a d27 for same-child-a. Do not calculate the result yourself.'),
        sendPrompt(second, 'Use the Roller agent to roll a d31 for same-child-b. Do not calculate the result yourself.'),
      ]);

      const [firstParent, secondParent] = await Promise.all([
        waitForNestedApproval(context.page, parentToolName),
        waitForNestedApproval(second, parentToolName),
      ]);
      recordApproval(context, firstParent);
      recordApproval(context, secondParent);
      expect(firstParent.requestId).not.toBe(secondParent.requestId);

      await Promise.all([
        resolveBrowserApproval(context.page, 'approve'),
        resolveBrowserApproval(second, 'approve'),
      ]);

      const [firstChild, secondChild] = await Promise.all([
        waitForNestedApproval(context.page, childToolName),
        waitForNestedApproval(second, childToolName),
      ]);
      expect(firstChild.requestId).not.toBe(secondChild.requestId);
      expect(firstChild.requestId).not.toBe(firstParent.requestId);

      await Promise.all([
        resolveBrowserApproval(context.page, 'approve'),
        resolveBrowserApproval(second, 'approve'),
      ]);

      await assertDiceMentionVisible(context.page, 27);
      await assertDiceMentionVisible(second, 31);
      await assertOneTerminalParentResult(context.page, parentToolName, 'complete');
      await assertOneTerminalParentResult(second, parentToolName, 'complete');
      expect(new URL(context.page.url()).searchParams.get('sessionId')).toBe(firstThread);
      expect(new URL(second.url()).searchParams.get('sessionId')).toBe(secondThread);
    } finally {
      await second.close();
    }
  },
};
