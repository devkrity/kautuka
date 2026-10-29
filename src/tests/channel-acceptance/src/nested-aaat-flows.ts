import { expect, type Page } from '@playwright/test';
import { connectProvider, openAgent, sendPrompt } from './flows.js';
import type { AcceptanceScenario, Provider } from './model.js';
import { scenarioProviderDestination } from './provider-fixtures.js';

export async function prepareNestedRun(
  context: Parameters<AcceptanceScenario['execute']>[0], provider: Provider, suffix: string,
): Promise<string> {
  const scenarioId = `${context.seed}-nested-${suffix}`;
  const destination = scenarioProviderDestination(provider, scenarioId);
  await context.simulator.reset();
  await context.simulator.configure(provider, { scenarioId, seed: context.seed, ownerReference: 'acceptance-owner', conversationReference: destination });
  // Fixture creation happens through the public API after the browser's initial
  // catalogue query. Refresh before using the normal settings form so its target
  // dropdown reflects the same public catalogue an owner sees on a new visit.
  await context.page.reload();
  await connectProvider(context.page, provider, context.skillFixtures.childAgentId, destination);
  await openAgent(context.page, context.baseUrl, context.skillFixtures.parentAgentId);
  const dice = suffix === 'mixed' ? 'a d27 and a d31' : 'a d27';
  await sendPrompt(context.page, `Use the Roller agent to roll ${dice} for nested acceptance ${suffix}. Do not calculate either result yourself.`);
  return destination;
}

export async function waitForNestedApproval(
  page: Page,
  toolName: string,
  previousRequestId?: string,
): Promise<Readonly<{ requestId: string }>> {
  const panels = page.locator(`[data-testid="hitl-approval"][data-tool-name="${toolName}"]`);
  let requestId: string | null = null;
  await expect.poll(async () => {
    const unexpected = await page.locator('[data-testid="hitl-approval"]').evaluateAll((elements, expectedTool) => elements
      .filter((element) => element.getClientRects().length > 0)
      .map((element) => ({
        toolName: element.getAttribute('data-tool-name'),
        requestId: element.getAttribute('data-approval-request-id'),
      }))
      .filter((entry) => entry.toolName && entry.toolName !== expectedTool), toolName);
    if (unexpected.length > 0) {
      throw new Error(
        `Expected the next ${toolName} approval, but visible HITL cards were ${unexpected
          .map((entry) => `${entry.toolName}:${entry.requestId ?? '?'}`)
          .join(', ')}.`,
      );
    }

    const visibleRequestIds = await panels.evaluateAll((elements) => elements
      .filter((element) => element.getClientRects().length > 0)
      .map((element) => element.getAttribute('data-approval-request-id'))
      .filter((value): value is string => Boolean(value)));
    const nextRequestId = visibleRequestIds.find((value) => value !== previousRequestId) ?? null;
    // Sticky: overlapping poll callbacks must not clear a previously found id.
    if (nextRequestId) {
      requestId = nextRequestId;
    }
    return requestId;
  }, {
    message: `${toolName} must expose the next immutable approval request id`,
    timeout: 90_000,
  }).not.toBeNull();
  return { requestId: requestId! };
}

export function recordApproval(context: Parameters<AcceptanceScenario['execute']>[0], approval: Readonly<{ requestId: string }>): void {
  context.evidence.addIdentifier({ kind: 'approval', value: approval.requestId });
}
