import { expect, type Page } from '@playwright/test';
import { connectProvider, openAgent, resolveBrowserApproval, sendPrompt } from './flows.js';
import { OwnedStackLifecycle } from './lifecycle.js';
import type { ProviderSimulatorControl } from './model.js';
import { waitForNestedApproval } from './nested-aaat-flows.js';
import { scenarioProviderDestination } from './provider-fixtures.js';
import type { AcceptanceSkillFixtures } from './skill-fixture-bootstrap.js';
import { measureRestart, type RestartObservation } from './restart-evidence.js';

const parentToolName = 'invoke_agent_roller';
const childToolName = 'run_skill_script';

export type NestedRestartBoundary = 'Gateway' | 'ChannelHost';

/** Nested AAAT child pause survives a process restart with the same request id. */
export async function runNestedChildPauseRestart(
  page: Page,
  baseUrl: URL,
  simulator: ProviderSimulatorControl,
  skillFixtures: AcceptanceSkillFixtures,
  seed: string,
  lifecycle: OwnedStackLifecycle,
  boundary: NestedRestartBoundary,
): Promise<Readonly<{ requestId: string; observation: RestartObservation }>> {
  const scenarioId = `${seed}-nested-${boundary.toLowerCase()}-restart-child`;
  const destination = scenarioProviderDestination('telegram', scenarioId);
  await simulator.reset();
  await simulator.configure('telegram', {
    scenarioId, seed, ownerReference: 'acceptance-owner', conversationReference: destination,
  });
  await page.reload();
  await connectProvider(page, 'telegram', skillFixtures.childAgentId, destination);
  await openAgent(page, baseUrl, skillFixtures.parentAgentId);
  await sendPrompt(page, `Use the Roller agent to roll a d27 for nested acceptance ${boundary}-restart-child. Do not calculate either result yourself.`);

  await waitForNestedApproval(page, parentToolName);
  await resolveBrowserApproval(page, 'approve');
  const child = await waitForNestedApproval(page, childToolName);

  const observation = await measureRestart(lifecycle, boundary, async () => {
    await page.reload();
    await expect(
      page.locator(
        `[data-testid="hitl-approval"][data-tool-name="${childToolName}"][data-approval-request-id="${child.requestId}"]`,
      ),
      `${boundary} restart at nested child pause must restore the same approval request`,
    ).toBeVisible();
  });

  return { requestId: child.requestId, observation };
}

export async function runNestedChildPauseGatewayRestart(
  page: Page,
  baseUrl: URL,
  simulator: ProviderSimulatorControl,
  skillFixtures: AcceptanceSkillFixtures,
  seed: string,
  lifecycle: OwnedStackLifecycle,
): Promise<Readonly<{ requestId: string; observation: RestartObservation }>> {
  return runNestedChildPauseRestart(page, baseUrl, simulator, skillFixtures, seed, lifecycle, 'Gateway');
}
