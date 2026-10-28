import { expect } from '@playwright/test';
import { executeProviderApproval } from './provider-approval-lifecycle.js';
import { connectProvider, verifyProviderLifecycle } from './flows.js';
import { hasProviderOperation } from './provider-events.js';
import { replaceProviderCredentials, verifyIncompleteTelegramSetupGuidance, verifyProviderSetupGuidance } from './provider-flows.js';
import { verifyProviderRemoval } from './provider-removal.js';
import { browserApproval } from './narad-scenarios.js';
import type { AcceptanceScenario, ApprovalAction, Provider } from './model.js';

const publicSetup = (provider: Provider): AcceptanceScenario => ({
  id: `CRH-031-${provider.toUpperCase()}-SETUP`,
  title: `${provider} owner setup and registration journey`,
  tags: ['regression', 'full'],
  providers: [provider],
  checklistIds: ['L-01', 'L-02', 'L-03', 'L-04', 'L-09', 'L-10'],
  async execute({ page, baseUrl, simulator, seed, fixtures }) {
    await simulator.reset();
    await simulator.configure(provider, { scenarioId: `CRH-031-${provider.toUpperCase()}-SETUP`, seed, ownerReference: 'acceptance-owner' });
    await page.goto(new URL('/settings', baseUrl).toString());
    await expect(page).toHaveURL(/settings|login/);
    expect(fixtures.assistantId).not.toBe(fixtures.rollerId);
    await verifyProviderSetupGuidance(page, provider);
    if (provider === 'telegram') await verifyIncompleteTelegramSetupGuidance(page);
    await connectProvider(page, provider, fixtures.rollerId);
    await verifyProviderLifecycle(page, provider, fixtures.rollerId);
    await replaceProviderCredentials(page, provider, fixtures.rollerId);
    const evidence = await simulator.exportEvidence();
    expect(hasProviderOperation(evidence.transcript, provider), `${provider} verification must cross the provider transport boundary`).toBeTruthy();
  },
});

const publicRemove = (provider: Provider): AcceptanceScenario => ({
  id: `CRH-031-${provider.toUpperCase()}-REMOVE`,
  title: `${provider} permanent remove stops traffic and allows re-setup`,
  tags: ['regression', 'full'],
  providers: [provider],
  checklistIds: ['L-09'],
  async execute({ page, baseUrl, simulator, seed, fixtures }) {
    await simulator.reset();
    await simulator.configure(provider, { scenarioId: `CRH-031-${provider.toUpperCase()}-REMOVE`, seed, ownerReference: 'acceptance-owner' });
    await page.goto(new URL('/settings', baseUrl).toString());
    await connectProvider(page, provider, fixtures.rollerId);
    await verifyProviderRemoval(page, provider, fixtures.rollerId);
  },
});

const hitlParity = (provider: Provider, action: ApprovalAction): AcceptanceScenario => ({
  id: `CRH-033-${provider.toUpperCase()}-${action.toUpperCase()}`,
  title: `${provider} ${action} preserves Narad interrupt semantics at the provider boundary`,
  tags: ['full', 'release-gate'],
  providers: [provider],
  checklistIds: ['C-03', 'C-04', 'C-05', 'C-06', 'C-07', 'L-07'],
  async execute({ page, baseUrl, simulator, seed, fixtures }) {
    await simulator.reset();
    await simulator.configure(provider, {
      scenarioId: `CRH-033-${provider.toUpperCase()}-${action.toUpperCase()}`,
      seed, ownerReference: 'acceptance-owner', conversationReference: 'acceptance-conversation',
    });
    await executeProviderApproval(page, baseUrl, simulator, fixtures, provider, `CRH-033-${seed}`, action, false);
    const evidence = await simulator.exportEvidence();
    expect(evidence.transcript, `${provider} must record the visible channel interaction`).not.toEqual([]);
  },
});

export const baseAcceptanceScenarios: readonly AcceptanceScenario[] = [
  ...(['telegram', 'slack'] as const).map(publicSetup),
  ...(['telegram', 'slack'] as const).map(publicRemove),
  ...(['chat', 'approvals'] as const).flatMap((surface) =>
    (['approve', 'deny', 'stop-run'] as const).map((action) => browserApproval(surface, action))),
  ...(['telegram', 'slack'] as const).flatMap((provider) =>
    (['approve', 'deny', 'stop-run'] as const).map((action) => hitlParity(provider, action))),
];
